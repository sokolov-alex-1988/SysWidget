using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace SysWidget
{
    public enum AlertKind { Ram = 0, Cpu = 1, Disk = 2, Gpu = 3 }

    /// <summary>
    /// Пороговые оповещения. Показатель должен держаться выше порога несколько
    /// секунд подряд — короткие пики при запуске программы не считаются. Повтор
    /// по той же метрике не чаще, чем раз в Cooldown секунд.
    /// Настройки читаются из alerts.json рядом с exe (его будет писать окно
    /// настроек); если файла нет — действуют значения по умолчанию.
    /// </summary>
    public sealed class Alerts
    {
        static readonly IFormatProvider Inv = CultureInfo.InvariantCulture;

        public bool Enabled = true;
        public double RamLimit = 90;
        public double CpuLimit = 90;
        public double DiskLimit = 90;
        public double GpuLimit = 90;
        /// <summary>Свободное место на диске, %: тревога, когда его меньше.</summary>
        public double FreeLimit = 10;
        /// <summary>Заряд периферии, %: тревога, когда он ниже.</summary>
        public double PeriphLimit = 20;

        /// <summary>Сколько секунд подряд выше порога, чтобы сработать.</summary>
        public int Sustain = 10;
        /// <summary>Повторное напоминание по той же метрике, секунд.</summary>
        public int Cooldown = 600;
        /// <summary>Пауза после запуска: не пугаем сразу при старте системы.</summary>
        public int Warmup = 20;

        /// <summary>
        /// Запас гистерезиса, п. п.: показатель, чуть опустившийся ниже порога
        /// (в пределах запаса), не сбрасывает отсчёт «держится выше порога».
        /// Без него нагрузка, прыгающая вокруг 90 %, никогда бы не сработала.
        /// </summary>
        public double Hysteresis = 5;

        /// <summary>Тихие часы: в это время никаких плашек и уведомлений.</summary>
        public bool QuietOn = false;
        /// <summary>Начало и конец тихих часов, минут от полуночи. Может идти через полночь.</summary>
        public int QuietFrom = 23 * 60;
        public int QuietTo = 7 * 60;

        /// <summary>Сейчас тихие часы.</summary>
        public bool InQuiet()
        {
            if (!QuietOn || QuietFrom == QuietTo) return false;
            int now = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
            if (QuietFrom < QuietTo) return now >= QuietFrom && now < QuietTo;
            return now >= QuietFrom || now < QuietTo;    // через полночь
        }

        sealed class Watch
        {
            public int Above;      // секунд подряд выше порога
            public DateTime Last;  // когда срабатывало
            public bool Fired;
        }

        readonly DateTime _start = DateTime.Now;
        readonly Watch[] _w = new Watch[4];
        readonly Dictionary<string, Watch> _periphW =
            new Dictionary<string, Watch>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Watch> _diskW =
            new Dictionary<string, Watch>(StringComparer.OrdinalIgnoreCase);
        DateTime _lastDiskCheck = DateTime.MinValue;

        public Alerts()
        {
            for (int i = 0; i < _w.Length; i++) _w[i] = new Watch();
        }

        /// <summary>Порог метрики, %. Окно настроек меняет пороги через это свойство.</summary>
        public double Limit(AlertKind k)
        {
            if (k == AlertKind.Ram) return RamLimit;
            if (k == AlertKind.Cpu) return CpuLimit;
            if (k == AlertKind.Disk) return DiskLimit;
            return GpuLimit;
        }

        public void SetLimit(AlertKind k, double value)
        {
            if (k == AlertKind.Ram) RamLimit = value;
            else if (k == AlertKind.Cpu) CpuLimit = value;
            else if (k == AlertKind.Disk) DiskLimit = value;
            else GpuLimit = value;
        }

        public event Action<AlertKind, double> Raised;
        /// <summary>Низкий заряд подключённого устройства (заряд уже ниже порога).</summary>
        public event Action<Peripheral> PeripheralLow;
        /// <summary>Мало свободного места: буква тома, свободно ГБ, свободно %.</summary>
        public event Action<string, double, double> DiskFreeLow;

        public void Update(SysStats s)
        {
            if ((DateTime.Now - _start).TotalSeconds < Warmup) return;
            if (InQuiet())
            {
                // тихие часы: отсчёт не копим, иначе сразу после них сработает старое
                for (int i = 0; i < _w.Length; i++) _w[i].Above = 0;
                return;
            }
            Step(AlertKind.Ram, s.RamPct, true);
            Step(AlertKind.Cpu, s.Cpu, true);
            Step(AlertKind.Disk, s.DiskPct, true);
            Step(AlertKind.Gpu, s.Gpu, s.HasGpu);
            CheckDiskFree();
        }

        /// <summary>
        /// Низкий заряд периферии. Устройства приходят с фонового обхода раз в
        /// 10 секунд, заряд обновляется реже; проверяем при каждом новом списке.
        /// </summary>
        public void UpdatePeripherals(List<Peripheral> list)
        {
            if (!Enabled || (DateTime.Now - _start).TotalSeconds < Warmup || list == null || InQuiet())
                return;

            foreach (Peripheral p in list)
            {
                if (p.Battery < 0) continue;

                Watch w;
                if (!_periphW.TryGetValue(p.Key, out w))
                {
                    w = new Watch();
                    _periphW[p.Key] = w;
                }

                // зарядили — в следующий раз тревога снова сработает
                if (p.Battery >= PeriphLimit)
                {
                    w.Fired = false;
                    continue;
                }
                if (w.Fired && (DateTime.Now - w.Last).TotalSeconds < Cooldown) continue;

                w.Fired = true;
                w.Last = DateTime.Now;
                Action<Peripheral> h = PeripheralLow;
                if (h != null) h(p);
            }
        }

        /// <summary>
        /// Свободное место проверяем раз в минуту: оно меняется медленно, а
        /// DriveInfo на всех томах незачем дёргать каждую секунду.
        /// </summary>
        void CheckDiskFree()
        {
            if (!Enabled || (DateTime.Now - _start).TotalSeconds < Warmup || InQuiet()) return;
            if ((DateTime.Now - _lastDiskCheck).TotalSeconds < 60) return;
            _lastDiskCheck = DateTime.Now;

            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return; }

            foreach (DriveInfo d in drives)
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    long total = d.TotalSize;
                    long free = d.AvailableFreeSpace;
                    if (total <= 0) continue;

                    double pct = free * 100.0 / total;
                    Watch w;
                    if (!_diskW.TryGetValue(d.Name, out w))
                    {
                        w = new Watch();
                        _diskW[d.Name] = w;
                    }

                    if (pct >= FreeLimit)
                    {
                        w.Fired = false;
                        continue;
                    }
                    if (w.Fired && (DateTime.Now - w.Last).TotalSeconds < Cooldown) continue;

                    w.Fired = true;
                    w.Last = DateTime.Now;
                    Action<string, double, double> h = DiskFreeLow;
                    if (h != null)
                        h(d.Name.TrimEnd('\\'), free / 1073741824.0, pct);
                }
                catch { }
            }
        }

        void Step(AlertKind kind, double value, bool available)
        {
            Watch w = _w[(int)kind];
            if (!Enabled || !available || double.IsNaN(value))
            {
                w.Above = 0;
                return;
            }

            if (value < Limit(kind))
            {
                // в пределах запаса отсчёт не сбрасываем, но и не продолжаем
                if (value < Limit(kind) - Hysteresis) w.Above = 0;
                return;
            }

            w.Above++;
            if (w.Above < Sustain) return;
            if (w.Fired && (DateTime.Now - w.Last).TotalSeconds < Cooldown) return;

            w.Fired = true;
            w.Last = DateTime.Now;
            w.Above = 0;   // повтор — только после новых Sustain секунд выше порога

            Action<AlertKind, double> h = Raised;
            if (h != null) h(kind, value);
        }

        // ---------------------------------------------------------- настройки

        public void Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                string t = File.ReadAllText(path);

                Match en = Regex.Match(t, "\"Enabled\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                if (en.Success) Enabled = en.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);

                RamLimit = Num(t, "Ram", RamLimit);
                CpuLimit = Num(t, "Cpu", CpuLimit);
                DiskLimit = Num(t, "Disk", DiskLimit);
                GpuLimit = Num(t, "Gpu", GpuLimit);
                FreeLimit = Num(t, "Free", FreeLimit);
                PeriphLimit = Num(t, "Periph", PeriphLimit);

                Sustain = (int)Math.Round(Num(t, "Sustain", Sustain));
                Cooldown = (int)Math.Round(Num(t, "Cooldown", Cooldown));
                Warmup = (int)Math.Round(Num(t, "Warmup", Warmup));
                Hysteresis = Math.Max(0, Num(t, "Hyst", Hysteresis));

                Match q = Regex.Match(t, "\"QuietOn\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                if (q.Success) QuietOn = q.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                QuietFrom = ClampDay((int)Math.Round(Num(t, "QuietFrom", QuietFrom)));
                QuietTo = ClampDay((int)Math.Round(Num(t, "QuietTo", QuietTo)));
            }
            catch { }
        }

        static int ClampDay(int minutes)
        {
            return Math.Max(0, Math.Min(24 * 60 - 1, minutes));
        }

        public void Save(string path)
        {
            try
            {
                AtomicFile.WriteAllText(path, string.Format(Inv,
                    "{{\"Enabled\":{0},\"Ram\":{1},\"Cpu\":{2},\"Disk\":{3},\"Gpu\":{4}," +
                    "\"Free\":{5},\"Periph\":{6}," +
                    "\"Sustain\":{7},\"Cooldown\":{8},\"Warmup\":{9}," +
                    "\"Hyst\":{10},\"QuietOn\":{11},\"QuietFrom\":{12},\"QuietTo\":{13}}}",
                    Enabled ? "true" : "false",
                    RamLimit, CpuLimit, DiskLimit, GpuLimit, FreeLimit, PeriphLimit,
                    Sustain, Cooldown, Warmup,
                    Hysteresis, QuietOn ? "true" : "false", QuietFrom, QuietTo));
            }
            catch (Exception ex) { ErrorLog.Write("save alerts", ex); }
        }

        static double Num(string text, string key, double def)
        {
            Match m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
            if (!m.Success) return def;
            double v;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, Inv, out v)) return def;
            return v;
        }
    }
}
