using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;

namespace SysWidget
{
    /// <summary>Снимок сетевого состояния для подсказки строки «Сеть».</summary>
    public sealed class NetSnapshot
    {
        public string Kind = "";        // «Wi-Fi» или «Ethernet»; пусто — нет подключения
        public string Name = "";        // имя адаптера
        public string Ssid = "";        // имя Wi-Fi сети, если удалось узнать
        public string Target = "";      // кому пингуем: шлюз или свой узел
        public long SpeedBps;           // скорость канала по данным адаптера
        public double PingMs = -1;      // последний ответ, мс; -1 — нет ответа
        public int Loss = -1;           // потери за последние замеры, %; -1 — ещё не мерили
        public double DayRx, DayTx;     // байт за сегодня
        public double MonRx, MonTx;     // байт за месяц
        public List<DayTraffic> Days = new List<DayTraffic>();   // история по дням, сегодняшний — последним
    }

    /// <summary>Трафик за один день (байт).</summary>
    public sealed class DayTraffic
    {
        public string Day = "";         // yyyy-MM-dd
        public double Rx, Tx;
    }

    /// <summary>
    /// Сведения о сети, которых нет в счётчиках PDH: к чему подключены (Wi-Fi
    /// или кабель, имя сети), задержка до шлюза и трафик за день и месяц.
    /// Обновляется раз в 5 секунд в своём потоке. Прав администратора и
    /// драйверов не нужно: стандартные сетевые интерфейсы .NET, ping и netsh.
    ///
    /// Трафик считается по приросту счётчиков физических адаптеров (Ethernet и
    /// Wi-Fi, без виртуальных и VPN — иначе байты посчитались бы дважды) и
    /// хранится в traffic.json. Пока виджет не запущен, трафик не учитывается.
    ///
    /// Ping идёт к шлюзу — это ничего не выдаёт наружу. Свой узел можно
    /// задать в net.json: {"PingHost":"example.org"}.
    /// </summary>
    public sealed class NetInfo : IDisposable
    {
        const int TickMs = 5000;
        const int PingWindow = 12;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly string _trafficFile;
        string _pingHost = "";

        Thread _thread;
        volatile bool _stop;
        readonly ManualResetEvent _wake = new ManualResetEvent(false);

        volatile NetSnapshot _current = new NetSnapshot();
        public NetSnapshot Current { get { return _current; } }

        // трафик
        string _day = "", _month = "";
        double _dayRx, _dayTx, _monRx, _monTx;
        bool _dirty;
        DateTime _lastSave = DateTime.Now;
        readonly Dictionary<string, long[]> _last = new Dictionary<string, long[]>();

        // завершённые дни (до 60) — для графика; сегодняшний добавляется при снимке
        readonly List<DayTraffic> _hist = new List<DayTraffic>();
        const int HistoryDays = 60;

        /// <summary>Какое предупреждение о лимите уже показано: «2026-11:80». Хранится в traffic.json.</summary>
        string _warned = "";
        public string Warned
        {
            get { return _warned; }
            set { _warned = value ?? ""; _dirty = true; }
        }

        // ssid и ping
        string _ssid = "", _ssidFor = "";
        DateTime _ssidAt = DateTime.MinValue;
        readonly Queue<bool> _pings = new Queue<bool>();
        double _lastPingMs = -1;

        public NetInfo(string trafficFile, string settingsFile)
        {
            _trafficFile = trafficFile;
            LoadSettings(settingsFile);
            LoadTraffic();
        }

        public void Start()
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "SysWidgetNet";
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            try { _wake.Set(); } catch { }
            try { if (_thread != null) _thread.Join(1500); } catch { }
            SaveTraffic();
        }

        void Loop()
        {
            while (!_stop)
            {
                try { Tick(); }
                catch (Exception ex) { ErrorLog.Write("netinfo", ex); }
                try { _wake.WaitOne(TickMs); } catch { }
            }
        }

        // ------------------------------------------------------------------ замер

        void Tick()
        {
            NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();

            RollDate();
            AccumulateTraffic(all);

            var snap = new NetSnapshot();
            NetworkInterface active = FindActive(all, out snap.Target);
            if (active != null)
            {
                snap.Name = active.Name;
                snap.SpeedBps = active.Speed;
                bool wifi = active.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                snap.Kind = wifi ? "Wi-Fi" : "Ethernet";
                if (wifi) snap.Ssid = ReadSsid(active.Name);
            }

            string host = _pingHost.Length > 0 ? _pingHost : snap.Target;
            if (host.Length > 0 && active != null) { snap.Target = host; DoPing(host); }
            else { _pings.Clear(); _lastPingMs = -1; }

            snap.PingMs = _lastPingMs;
            if (_pings.Count > 0)
            {
                int lost = 0;
                foreach (bool ok in _pings) if (!ok) lost++;
                snap.Loss = (int)Math.Round(lost * 100.0 / _pings.Count);
            }

            snap.DayRx = _dayRx; snap.DayTx = _dayTx;
            snap.MonRx = _monRx; snap.MonTx = _monTx;
            lock (_hist)
            {
                foreach (DayTraffic d in _hist) snap.Days.Add(d);
            }
            snap.Days.Add(new DayTraffic { Day = _day, Rx = _dayRx, Tx = _dayTx });
            _current = snap;

            if (_dirty && (DateTime.Now - _lastSave).TotalSeconds >= 60) SaveTraffic();
        }

        static bool Physical(NetworkInterface n)
        {
            NetworkInterfaceType t = n.NetworkInterfaceType;
            if (t != NetworkInterfaceType.Ethernet && t != NetworkInterfaceType.Wireless80211 &&
                t != NetworkInterfaceType.GigabitEthernet && t != NetworkInterfaceType.FastEthernetT &&
                t != NetworkInterfaceType.FastEthernetFx)
                return false;
            string d = ((n.Description ?? "") + " " + (n.Name ?? "")).ToLowerInvariant();
            return !(d.Contains("virtual") || d.Contains("vethernet") || d.Contains("hyper-v") ||
                     d.Contains("vmware") || d.Contains("virtualbox") || d.Contains("tap-windows") ||
                     d.Contains("wi-fi direct") || d.Contains("bluetooth") || d.Contains("loopback") ||
                     d.Contains("npcap") || d.Contains("wan miniport") || d.Contains("vpn"));
        }

        /// <summary>Адаптер с шлюзом IPv4 — через него и выходит интернет.</summary>
        static NetworkInterface FindActive(NetworkInterface[] all, out string gateway)
        {
            gateway = "";
            foreach (NetworkInterface n in all)
            {
                try
                {
                    if (n.OperationalStatus != OperationalStatus.Up || !Physical(n)) continue;
                    foreach (GatewayIPAddressInformation g in n.GetIPProperties().GatewayAddresses)
                    {
                        if (g.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (g.Address.Equals(IPAddress.Any)) continue;
                        gateway = g.Address.ToString();
                        return n;
                    }
                }
                catch { }
            }
            return null;
        }

        // ---------------------------------------------------------------- трафик

        void RollDate()
        {
            string day = DateTime.Now.ToString("yyyy-MM-dd", Inv);
            string month = DateTime.Now.ToString("yyyy-MM", Inv);
            if (day != _day)
            {
                // закончившийся день уходит в историю; пустые дни не храним
                if (_day.Length > 0 && (_dayRx > 0 || _dayTx > 0))
                {
                    lock (_hist)
                    {
                        _hist.Add(new DayTraffic { Day = _day, Rx = _dayRx, Tx = _dayTx });
                        while (_hist.Count > HistoryDays) _hist.RemoveAt(0);
                    }
                }
                _day = day; _dayRx = _dayTx = 0; _dirty = true;
            }
            if (month != _month) { _month = month; _monRx = _monTx = 0; _dirty = true; }
        }

        void AccumulateTraffic(NetworkInterface[] all)
        {
            var seen = new HashSet<string>();
            foreach (NetworkInterface n in all)
            {
                try
                {
                    if (n.OperationalStatus != OperationalStatus.Up || !Physical(n)) continue;
                    IPv4InterfaceStatistics st = n.GetIPv4Statistics();
                    long rx = st.BytesReceived, tx = st.BytesSent;
                    seen.Add(n.Id);

                    long[] prev;
                    if (_last.TryGetValue(n.Id, out prev))
                    {
                        // счётчик сбросился (адаптер переподключили) — берём всё, что накопилось
                        double dRx = rx >= prev[0] ? rx - prev[0] : rx;
                        double dTx = tx >= prev[1] ? tx - prev[1] : tx;
                        if (dRx > 0 || dTx > 0)
                        {
                            _dayRx += dRx; _monRx += dRx;
                            _dayTx += dTx; _monTx += dTx;
                            _dirty = true;
                        }
                    }
                    _last[n.Id] = new long[] { rx, tx };
                }
                catch { }
            }

            // адаптер пропал — при возвращении начнём с нового замера, а не с разницы через часы
            var gone = new List<string>();
            foreach (string id in _last.Keys) if (!seen.Contains(id)) gone.Add(id);
            foreach (string id in gone) _last.Remove(id);
        }

        void LoadTraffic()
        {
            try
            {
                if (!File.Exists(_trafficFile)) return;
                string t = File.ReadAllText(_trafficFile);
                _day = Str(t, "Day");
                _month = Str(t, "Month");
                _dayRx = Num(t, "DayRx"); _dayTx = Num(t, "DayTx");
                _monRx = Num(t, "MonRx"); _monTx = Num(t, "MonTx");
                _warned = Str(t, "Warned");
                foreach (Match m in Regex.Matches(t,
                    "\\{\"d\":\"(\\d{4}-\\d{2}-\\d{2})\",\"rx\":(\\d+),\"tx\":(\\d+)\\}"))
                {
                    double rx, tx;
                    if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, Inv, out rx)) continue;
                    if (!double.TryParse(m.Groups[3].Value, NumberStyles.Float, Inv, out tx)) continue;
                    _hist.Add(new DayTraffic { Day = m.Groups[1].Value, Rx = rx, Tx = tx });
                }
                while (_hist.Count > HistoryDays) _hist.RemoveAt(0);
            }
            catch { }
        }

        void SaveTraffic()
        {
            try
            {
                var days = new System.Text.StringBuilder();
                lock (_hist)
                {
                    foreach (DayTraffic d in _hist)
                    {
                        if (days.Length > 0) days.Append(',');
                        days.Append(string.Format(Inv, "{{\"d\":\"{0}\",\"rx\":{1:0},\"tx\":{2:0}}}", d.Day, d.Rx, d.Tx));
                    }
                }
                AtomicFile.WriteAllText(_trafficFile, string.Format(Inv,
                    "{{\"Day\":\"{0}\",\"DayRx\":{1:0},\"DayTx\":{2:0}," +
                    "\"Month\":\"{3}\",\"MonRx\":{4:0},\"MonTx\":{5:0},\"Warned\":\"{6}\",\"Days\":[{7}]}}",
                    _day, _dayRx, _dayTx, _month, _monRx, _monTx, _warned, days));
                _dirty = false;
                _lastSave = DateTime.Now;
            }
            catch (Exception ex) { ErrorLog.Write("save traffic", ex); }
        }

        void LoadSettings(string file)
        {
            try
            {
                if (!File.Exists(file)) return;
                string host = Str(File.ReadAllText(file), "PingHost").Trim();
                // только имя или адрес узла: без схемы, пути и пробелов
                if (Regex.IsMatch(host, @"^[A-Za-z0-9.\-:]{1,253}$")) _pingHost = host;
            }
            catch { }
        }

        static string Str(string text, string key)
        {
            Match m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        static double Num(string text, string key)
        {
            Match m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*(\\d+(?:\\.\\d+)?)");
            double v;
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, Inv, out v)) return v;
            return 0;
        }

        // ------------------------------------------------------------------ ping

        void DoPing(string host)
        {
            bool ok = false;
            double ms = -1;
            try
            {
                using (var p = new Ping())
                {
                    PingReply r = p.Send(host, 1000);
                    if (r != null && r.Status == IPStatus.Success)
                    {
                        ok = true;
                        ms = r.RoundtripTime;
                    }
                }
            }
            catch { }

            _pings.Enqueue(ok);
            while (_pings.Count > PingWindow) _pings.Dequeue();
            _lastPingMs = ms;
        }

        // ------------------------------------------------------------------ SSID

        /// <summary>
        /// Имя Wi-Fi сети. Прямого вызова без WinRT/WlanAPI нет, поэтому спрашиваем
        /// netsh — он есть на любой Windows и работает без прав администратора.
        /// Подпись «SSID» не локализуется, поэтому разбор не зависит от языка ОС.
        ///
        /// ВАЖНО: в новых Windows 11 чтение имени сети считается обращением к
        /// местоположению — на каждый запуск netsh в трее на секунду появляется
        /// значок геолокации, а в «Недавней активности» отображается «Оболочка
        /// сетевых команд». Поэтому по умолчанию имя НЕ запрашиваем (Prefs.ShowSsid
        /// выключен), а если включено — не чаще раза в 5 минут или при смене адаптера.
        /// </summary>
        string ReadSsid(string adapter)
        {
            if (!Prefs.ShowSsid) { _ssidFor = null; _ssid = ""; return ""; }
            if (adapter == _ssidFor && (DateTime.Now - _ssidAt).TotalSeconds < 300) return _ssid;
            _ssidFor = adapter;
            _ssidAt = DateTime.Now;
            _ssid = "";

            try
            {
                var psi = new ProcessStartInfo("netsh.exe", "wlan show interfaces");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return _ssid; }
                    Match m = Regex.Match(output, @"^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.Multiline);
                    if (m.Success) _ssid = m.Groups[1].Value;
                }
            }
            catch { }
            return _ssid;
        }
    }
}
