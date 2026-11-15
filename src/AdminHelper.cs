using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace SysWidget
{
    /// <summary>Состояние накопителя по данным SMART (читается только с правами администратора).</summary>
    public sealed class DiskReliability
    {
        public int Index;
        public int TempC = -1;          // −1 — диск не отдаёт
        public int TempMaxC = -1;
        public int WearPct = -1;        // израсходованный ресурс SSD, %; −1 — неизвестно
        public long Hours = -1;         // наработка, часов
        public long ReadErrors = -1, WriteErrors = -1;
    }

    /// <summary>
    /// Необязательный режим «расширенные данные». Основной виджет работает без прав администратора
    /// и не требует их. Для данных, которые Windows отдаёт только администратору (температура и
    /// износ накопителя), пользователь по кнопке в настройках запускает ЭТОТ ЖЕ exe ещё раз с
    /// правами администратора (запрос UAC) в режиме «--helper». Помощник невидим, ничего не пишет
    /// на диск и не выполняет чужих команд: он только отвечает на три запроса по именованному
    /// каналу — PING, DISKS и QUIT. Канал доступен только вашей учётной записи (ACL по SID) и
    /// не принимает ничего, кроме этих слов. Помощник закрывается вместе с виджетом.
    ///
    /// ВАЖНО: раз повышенный процесс запускается из exe-файла, папка с программой должна быть
    /// недоступна для записи чужим программам (иначе вредоносная подмена файла получила бы
    /// права администратора при вашем согласии на UAC). Для переносной копии на флешке или
    /// в профиле пользователя режим включайте осознанно.
    /// </summary>
    public static class AdminHelper
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ============================================================ сервер (с правами администратора)

        /// <summary>Точка входа режима «--helper»; возвращает код выхода процесса.</summary>
        public static int RunServer(string[] args)
        {
            string pipe = Arg(args, "--pipe"), sid = Arg(args, "--sid"), ppid = Arg(args, "--ppid");
            int parent;
            if (pipe.Length == 0 || sid.Length == 0 || !int.TryParse(ppid, out parent)) return 2;

            // закрываемся вместе с виджетом
            var watch = new Thread(delegate()
            {
                try { using (Process p = Process.GetProcessById(parent)) p.WaitForExit(); } catch { }
                Environment.Exit(0);
            });
            watch.IsBackground = true;
            watch.Start();

            PipeSecurity ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));

            while (true)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None, 8192, 8192, ps))
                    {
                        server.WaitForConnection();
                        string cmd = ReadLine(server, 32);
                        string reply;
                        bool quit = false;
                        switch (cmd)
                        {
                            case "PING": reply = "OK"; break;
                            case "DISKS": reply = DisksReply(); break;
                            case "QUIT": reply = "BYE"; quit = true; break;
                            default: reply = "ERR"; break;
                        }
                        byte[] bytes = Encoding.UTF8.GetBytes(reply);
                        server.Write(bytes, 0, bytes.Length);
                        server.Flush();
                        try { server.WaitForPipeDrain(); } catch { }
                        try { server.Disconnect(); } catch { }
                        if (quit) return 0;
                    }
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("helper loop", ex);
                    Thread.Sleep(500);
                }
            }
        }

        static string Arg(string[] a, string key)
        {
            for (int i = 0; i + 1 < a.Length; i++) if (a[i] == key) return a[i + 1];
            return "";
        }

        /// <summary>Читает одну строку (до перевода строки), не больше max символов.</summary>
        static string ReadLine(Stream s, int max)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (sb.Length < max)
            {
                if (s.Read(one, 0, 1) <= 0) break;
                char c = (char)one[0];
                if (c == '\n') break;
                if (c != '\r') sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Строки «D|номер|темп|макс.темп|износ|часы|ошибки чтения|ошибки записи» по каждому диску.
        /// Класс MSFT_StorageReliabilityCounter читается только администратором.
        /// </summary>
        static string DisksReply()
        {
            var sb = new StringBuilder();
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();
                // счётчики надёжности отдаются через связь «диск → счётчик» (как у Get-StorageReliabilityCounter);
                // прямой перебор класса на многих машинах пуст
                using (var disks = new ManagementObjectSearcher(scope, new ObjectQuery(
                    "SELECT * FROM MSFT_PhysicalDisk")))
                using (var found = disks.Get())
                {
                    foreach (ManagementObject disk in found)
                    {
                        string id = Convert.ToString(disk["DeviceId"], Inv);
                        using (ManagementObjectCollection rel = disk.GetRelated("MSFT_StorageReliabilityCounter"))
                        {
                            foreach (ManagementBaseObject o in rel)
                            {
                                sb.Append("D|").Append(id).Append('|')
                                  .Append(Num(o, "Temperature")).Append('|').Append(Num(o, "TemperatureMax")).Append('|')
                                  .Append(Num(o, "Wear")).Append('|').Append(Num(o, "PowerOnHours")).Append('|')
                                  .Append(Num(o, "ReadErrorsTotal")).Append('|').Append(Num(o, "WriteErrorsTotal")).Append('\n');
                                break;
                            }
                        }
                    }
                }
                if (sb.Length == 0) sb.Append("E|no reliability data\n");
            }
            catch (Exception ex) { sb.Append("E|").Append(ex.Message.Replace('\n', ' ').Replace('|', '/')).Append('\n'); }
            return sb.ToString();
        }

        static string Num(ManagementBaseObject o, string name)
        {
            try
            {
                object v = o[name];
                if (v == null) return "-1";
                return Convert.ToInt64(v, Inv).ToString(Inv);
            }
            catch { return "-1"; }
        }

        // ============================================================ клиент (обычный виджет)

        static string _pipe = "";
        static volatile bool _connected;
        static string _lastError = "";
        static readonly object Gate = new object();
        static readonly Dictionary<int, DiskReliability> _disks = new Dictionary<int, DiskReliability>();
        static DateTime _disksAt = DateTime.MinValue;
        static bool _disksBusy;

        public static bool Connected { get { return _connected; } }
        public static string LastError { get { return _lastError; } }

        /// <summary>
        /// Запустить помощника с правами администратора (покажет запрос UAC) и дождаться ответа.
        /// Возвращает пустую строку при успехе, иначе причину. Вызывать из фонового потока.
        /// </summary>
        public static string Launch()
        {
            if (_connected) return "";
            try
            {
                string pipe = "SysWidgetHelper." + Guid.NewGuid().ToString("N");
                string sid = WindowsIdentity.GetCurrent().User.Value;
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var psi = new ProcessStartInfo(exe,
                    "--helper --pipe " + pipe + " --sid " + sid + " --ppid " + Process.GetCurrentProcess().Id.ToString(Inv));
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                try { Process.Start(psi); }
                catch (Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 1223) return _lastError = Lang.S("запрос UAC отклонён", "UAC prompt declined");
                    return _lastError = ex.Message;
                }

                _pipe = pipe;
                for (int i = 0; i < 40; i++)     // до 10 секунд
                {
                    Thread.Sleep(250);
                    if (Ask("PING", 400) == "OK") { _connected = true; _lastError = ""; return ""; }
                }
                return _lastError = Lang.S("помощник не ответил", "helper did not answer");
            }
            catch (Exception ex)
            {
                ErrorLog.Write("admin helper launch", ex);
                return _lastError = ex.Message;
            }
        }

        public static void Stop()
        {
            if (!_connected) return;
            try { Ask("QUIT", 800); } catch { }
            _connected = false;
            lock (Gate) { _disks.Clear(); _disksAt = DateTime.MinValue; }
        }

        static string Ask(string cmd, int timeoutMs)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut))
                {
                    c.Connect(timeoutMs);
                    byte[] b = Encoding.ASCII.GetBytes(cmd + "\n");
                    c.Write(b, 0, b.Length);
                    c.Flush();
                    using (var r = new StreamReader(c, Encoding.UTF8)) return r.ReadToEnd().TrimEnd();
                }
            }
            catch { return ""; }
        }

        /// <summary>Данные SMART диска (по номеру PhysicalDisk) или null. Обновляются в фоне раз в 30 секунд.</summary>
        public static DiskReliability GetDisk(int index)
        {
            if (!_connected) return null;
            RefreshDisks();
            lock (Gate)
            {
                DiskReliability d;
                return _disks.TryGetValue(index, out d) ? d : null;
            }
        }

        static void RefreshDisks()
        {
            lock (Gate)
            {
                if (_disksBusy || (DateTime.Now - _disksAt).TotalSeconds < 30) return;
                _disksBusy = true;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                string reply = Ask("DISKS", 4000);
                var fresh = new Dictionary<int, DiskReliability>();
                foreach (string line in reply.Split('\n'))
                {
                    string[] p = line.Trim().Split('|');
                    if (p.Length < 8 || p[0] != "D") continue;
                    long idx = L(p[1]);
                    if (idx < 0) continue;
                    fresh[(int)idx] = new DiskReliability
                    {
                        Index = (int)idx, TempC = (int)L(p[2]), TempMaxC = (int)L(p[3]), WearPct = (int)L(p[4]),
                        Hours = L(p[5]), ReadErrors = L(p[6]), WriteErrors = L(p[7])
                    };
                }
                lock (Gate)
                {
                    if (fresh.Count > 0) { _disks.Clear(); foreach (var kv in fresh) _disks[kv.Key] = kv.Value; }
                    _disksAt = DateTime.Now;      // и при неудаче — не опрашиваем каждую секунду
                    _disksBusy = false;
                }
            });
        }

        static long L(string s)
        {
            long v;
            return long.TryParse(s, NumberStyles.Integer, Inv, out v) ? v : -1;
        }
    }
}
