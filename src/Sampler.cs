using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace SysWidget
{
    /// <summary>Загрузка и потоки одного физического диска.</summary>
    public sealed class DiskStat
    {
        public int Index;            // номер PhysicalDisk (0, 1, …)
        public string Letters = "";  // «C:» или «C: D:» — тома на этом диске
        public double Pct;           // занятость диска, %
        public double ReadMBs, WriteMBs;
    }

    public sealed class NetStat
    {
        public string Name = "";     // имя интерфейса из PDH
        public double RxMBs, TxMBs;  // приём / передача, МБ/с
    }

    /// <summary>Использование ГП одним процессом (по номеру pid).</summary>
    public sealed class GpuProc
    {
        public int Pid;
        public double Util;           // %, самый занятый движок процесса
        public double DedMb, SharedMb;   // видеопамять процесса, МБ
    }

    public sealed class SysStats
    {
        public double Cpu;
        public double CpuPerf;        // «% Processor Performance», 100 = номинал
        public double[] CpuCores = new double[0];   // загрузка каждого логического ядра, %
        public double[] CoreMHz = new double[0];    // текущая частота каждого логического ядра, МГц (без админа)
        public double CpuPowerW = -1, CpuCoresPowerW = -1;   // потребление процессора и его ядер, Вт; −1 — Windows не отдаёт (RAPL)
        public double RamPct, RamUsedGB, RamTotalGB;
        public double CacheGB;        // кэш файлов: освобождается, когда памяти не хватает
        public double CommitUsedGB, CommitLimitGB;   // подтверждённая память и её предел (ОЗУ + файл подкачки)
        public double DiskPct, DiskReadMBs, DiskWriteMBs;   // сводка по всем дискам
        public List<DiskStat> Disks = new List<DiskStat>();
        public double NetRxMBs, NetTxMBs;                   // суммарная сеть
        public List<NetStat> Nets = new List<NetStat>();    // активные интерфейсы
        public double Gpu;            // загрузка самого занятого движка, %
        public double VramUsedGB;     // занятая видеопамять (выделенная или общая)
        public double VramSharedGB;   // занятая общая видеопамять (для встроенных ГП)
        public bool HasGpu;           // Windows отдала счётчики GPU
        public List<GpuProc> GpuProcs = new List<GpuProc>();            // ГП по процессам (pid)
        public Dictionary<string, double> GpuEngines = new Dictionary<string, double>();   // тип движка → %
    }

    /// <summary>
    /// Замер загрузки через PDH: один запрос на все счётчики стоит доли миллисекунды.
    /// Get-Counter/PerformanceCounter здесь не годится — на русской локали пути
    /// счётчиков не совпадают, а на 500+ инстансах замер занимает больше секунды.
    /// </summary>
    public sealed class Sampler : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX s);

        [StructLayout(LayoutKind.Sequential)]
        struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
                           SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pi, uint size);

        [StructLayout(LayoutKind.Explicit)]
        struct PDH_FMT_COUNTERVALUE
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double doubleValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint PdhOpenQueryW(string src, IntPtr ud, out IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhAddEnglishCounterW(IntPtr q, string path, IntPtr ud, out IntPtr c);
        [DllImport("pdh.dll")]
        static extern uint PdhCollectQueryData(IntPtr q);
        [DllImport("pdh.dll")]
        static extern uint PdhGetFormattedCounterValue(IntPtr c, uint fmt, out uint type, out PDH_FMT_COUNTERVALUE v);
        [DllImport("pdh.dll")]
        static extern uint PdhCloseQuery(IntPtr q);

        // Счётчики с подстановкой «*» отдают массив инстансов — для них отдельный вызов
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint fmt,
            ref uint bufSize, out uint itemCount, IntPtr buffer);

        [StructLayout(LayoutKind.Sequential)]
        struct PDH_FMT_COUNTERVALUE_ITEM_W
        {
            public IntPtr szName;
            public PDH_FMT_COUNTERVALUE FmtValue;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("psapi.dll")]
        static extern bool EmptyWorkingSet(IntPtr hProcess);
        [DllImport("kernel32.dll")]
        static extern bool SetProcessInformation(IntPtr h, int cls, ref uint info, int size);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        const uint PROCESS_SET_INFORMATION = 0x0200;
        const int ProcessMemoryPriority = 0;
        const uint MemoryPriorityLow = 2;      // страницы таких процессов система отбирает первыми (по умолчанию 5)

        const uint PDH_FMT_DOUBLE = 0x00000200;
        const uint PDH_MORE_DATA = 0x800007D2;
        const uint PROCESS_QUERY_INFORMATION = 0x0400;
        const uint PROCESS_SET_QUOTA = 0x0100;

        IntPtr _q, _cpu, _cpuPerf, _cpuCores, _cpuFreq, _energy, _gpuEngine, _gpuMem, _gpuMemShared, _gpuProcDed, _gpuProcShared;
        IntPtr _diskPct, _diskRead, _diskWrite;
        IntPtr _netRx, _netTx;

        // 0 — покой, 1 — чистим, 2 — готово (результат забирает UI-поток)
        int _trimState;
        long _trimFreed;

        public int TrimState
        {
            get { return _trimState; }
            set { _trimState = value; }
        }

        public long TrimFreed { get { return _trimFreed; } }
        string _trimTop;
        /// <summary>«имя — N ГБ»: самый крупный держатель памяти после очистки (null — ничего заметного).</summary>
        public string TrimTop { get { return _trimTop; } }
        public string LastError = "";

        // ---------- фоновый замер ----------
        // PDH может задуматься (сброс драйвера GPU, пробуждение после сна):
        // синхронный вызов из таймера подвесил бы всю карточку. Поэтому замер
        // идёт в своём потоке, а UI забирает последний готовый снимок.
        volatile bool _run;
        Thread _worker;
        SysStats _latest;

        /// <summary>Последний готовый замер; null — первый ещё не готов.</summary>
        public SysStats Latest { get { return _latest; } }

        public void Start()
        {
            if (_worker != null) return;
            _run = true;
            _worker = new Thread(Work);
            _worker.IsBackground = true;
            _worker.Name = "SysWidgetSampler";
            _worker.Start();
        }

        void Work()
        {
            var clock = new Stopwatch();
            while (_run)
            {
                clock.Restart();
                try { _latest = Sample(); }
                catch (Exception ex) { LastError = ex.Message; ErrorLog.Write("sample", ex); }

                int wait = 1000 - (int)clock.ElapsedMilliseconds;
                if (wait < 50) wait = 50;
                // спим короткими кусками, чтобы Dispose не ждал целую секунду
                for (int slept = 0; slept < wait && _run; slept += 50)
                    Thread.Sleep(Math.Min(50, wait - slept));
            }
        }

        public Sampler()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) throw new Exception("PdhOpenQuery failed");
            _cpu = Add(@"\Processor(_Total)\% Processor Time");
            // текущая частота: процент от номинала × MaxClockSpeed из WMI
            _cpuPerf = Add(@"\Processor Information(_Total)\% Processor Performance");
            _cpuCores = Add(@"\Processor(*)\% Processor Time");
            // частота по ядрам (МГц) и потребление процессора (RAPL, мВт): счётчики Windows,
            // админ не нужен; на части машин «Energy Meter» нет — тогда значение −1
            _cpuFreq = Add(@"\Processor Information(*)\Processor Frequency");
            _energy = Add(@"\Energy Meter(*)\Power");
            _diskPct = Add(@"\PhysicalDisk(*)\% Disk Time");
            _diskRead = Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
            _diskWrite = Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");

            // сеть: скорость приёма/передачи по каждому интерфейсу. Счётчики
            // Windows, драйверы не нужны; виртуальные адаптеры отсеиваем по имени.
            _netRx = Add(@"\Network Interface(*)\Bytes Received/sec");
            _netTx = Add(@"\Network Interface(*)\Bytes Sent/sec");

            // GPU: счётчики Windows 10 1709+. Не зависят от производителя и не
            // требуют ни драйверов, ни прав администратора.
            _gpuEngine = Add(@"\GPU Engine(*)\Utilization Percentage");
            _gpuMem = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
            // у встроенной графики выделенной памяти почти нет — вся занятость в общей
            _gpuMemShared = Add(@"\GPU Adapter Memory(*)\Shared Usage");
            // то же по процессам: инстансы вида pid_1234_luid_0x…_phys_0
            _gpuProcDed = Add(@"\GPU Process Memory(*)\Dedicated Usage");
            _gpuProcShared = Add(@"\GPU Process Memory(*)\Shared Usage");
            PdhCollectQueryData(_q);   // прайминг: счётчикам-скорости нужны два замера
        }

        IntPtr Add(string path)
        {
            IntPtr h;
            uint st = PdhAddEnglishCounterW(_q, path, IntPtr.Zero, out h);
            if (st != 0) { LastError += path + " -> 0x" + st.ToString("X") + "; "; return IntPtr.Zero; }
            return h;
        }

        double Scalar(IntPtr h)
        {
            if (h == IntPtr.Zero) return 0;
            uint t;
            PDH_FMT_COUNTERVALUE v;
            if (PdhGetFormattedCounterValue(h, PDH_FMT_DOUBLE, out t, out v) != 0) return 0;
            if (v.CStatus != 0) return 0;
            return v.doubleValue;
        }

        /// <summary>
        /// Значения счётчика с подстановкой «*»: имена инстансов и числа.
        /// Буфер запрашиваем дважды — сначала размер, потом данные.
        /// </summary>
        double[] ArrayValues(IntPtr h, out string[] names)
        {
            names = new string[0];
            if (h == IntPtr.Zero) return new double[0];

            uint size = 0, count = 0;
            uint r = PdhGetFormattedCounterArrayW(h, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
            if (r != PDH_MORE_DATA || size == 0) return new double[0];

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                r = PdhGetFormattedCounterArrayW(h, PDH_FMT_DOUBLE, ref size, out count, buf);
                if (r != 0) return new double[0];

                int itemSize = Marshal.SizeOf(typeof(PDH_FMT_COUNTERVALUE_ITEM_W));
                var vals = new double[count];
                names = new string[count];
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = new IntPtr(buf.ToInt64() + (long)i * itemSize);
                    var it = (PDH_FMT_COUNTERVALUE_ITEM_W)Marshal.PtrToStructure(
                        p, typeof(PDH_FMT_COUNTERVALUE_ITEM_W));
                    names[i] = Marshal.PtrToStringUni(it.szName);
                    vals[i] = it.FmtValue.CStatus == 0 ? it.FmtValue.doubleValue : 0;
                }
                return vals;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>Частота по ядрам и потребление процессора (RAPL: пакет и ядра), если Windows их отдаёт.</summary>
        void ReadCpuExtra(SysStats s)
        {
            string[] names;
            double[] f = ArrayValues(_cpuFreq, out names);
            var list = new List<KeyValuePair<int, double>>();
            for (int i = 0; i < f.Length; i++)
            {
                // инстансы «группа,номер»: «0,3»; «0,_Total» и «_Total» пропускаем
                string n = names[i];
                if (n == null) continue;
                string[] p = n.Split(',');
                int g, idx;
                if (p.Length != 2 || !int.TryParse(p[0], out g) || !int.TryParse(p[1], out idx)) continue;
                list.Add(new KeyValuePair<int, double>(g * 64 + idx, f[i]));
            }
            list.Sort(delegate(KeyValuePair<int, double> a, KeyValuePair<int, double> b) { return a.Key.CompareTo(b.Key); });
            var mhz = new double[list.Count];
            for (int i = 0; i < mhz.Length; i++) mhz[i] = list[i].Value;
            s.CoreMHz = mhz;

            double[] e = ArrayValues(_energy, out names);
            for (int i = 0; i < e.Length; i++)
            {
                if (names[i] == null) continue;
                if (names[i].EndsWith("_PKG", StringComparison.OrdinalIgnoreCase)) s.CpuPowerW = e[i] / 1000.0;      // мВт → Вт
                else if (names[i].EndsWith("_PP0", StringComparison.OrdinalIgnoreCase)) s.CpuCoresPowerW = e[i] / 1000.0;
            }
        }

        /// <summary>Загрузка каждого логического ядра, по порядку номеров (инстанс «_Total» пропускаем).</summary>
        double[] ReadCores()
        {
            string[] names;
            double[] vals = ArrayValues(_cpuCores, out names);
            var list = new List<KeyValuePair<int, double>>();
            for (int i = 0; i < vals.Length; i++)
            {
                int idx;
                if (names[i] == null || !int.TryParse(names[i], out idx)) continue;
                list.Add(new KeyValuePair<int, double>(idx, Math.Min(100, Math.Max(0, vals[i]))));
            }
            list.Sort(delegate(KeyValuePair<int, double> a, KeyValuePair<int, double> b) { return a.Key.CompareTo(b.Key); });
            var res = new double[list.Count];
            for (int i = 0; i < res.Length; i++) res[i] = list[i].Value;
            return res;
        }

        /// <summary>
        /// Загрузка каждого физического диска. Инстансы PDH выглядят как
        /// «0 C:», «1 D: E:», «_Total»: номер — это тот же Index, что и у
        /// Win32_DiskDrive, поэтому диск, счётчик и его модель сходятся.
        /// </summary>
        void ReadDisks(SysStats s)
        {
            s.Disks.Clear();
            s.DiskPct = 0;
            s.DiskReadMBs = 0;
            s.DiskWriteMBs = 0;
            if (_diskPct == IntPtr.Zero) return;

            var byName = new Dictionary<string, DiskStat>(StringComparer.OrdinalIgnoreCase);
            string[] names;
            double[] vals = ArrayValues(_diskPct, out names);
            for (int i = 0; i < vals.Length; i++)
            {
                string n = names[i];
                if (n == null || string.Equals(n, "_Total", StringComparison.OrdinalIgnoreCase))
                    continue;

                var d = new DiskStat();
                ParseDiskInstance(n, out d.Index, out d.Letters);
                d.Pct = Math.Min(100, Math.Max(0, vals[i]));
                byName[n] = d;
            }

            FillDiskBytes(byName, _diskRead, false);
            FillDiskBytes(byName, _diskWrite, true);

            double maxPct = 0;
            foreach (var kv in byName)
            {
                DiskStat d = kv.Value;
                s.Disks.Add(d);
                if (d.Pct > maxPct) maxPct = d.Pct;
                s.DiskReadMBs += d.ReadMBs;
                s.DiskWriteMBs += d.WriteMBs;
            }
            s.Disks.Sort(delegate(DiskStat a, DiskStat b) { return a.Index.CompareTo(b.Index); });
            s.DiskPct = maxPct;
        }

        void FillDiskBytes(Dictionary<string, DiskStat> map, IntPtr counter, bool write)
        {
            string[] names;
            double[] vals = ArrayValues(counter, out names);
            for (int i = 0; i < vals.Length; i++)
            {
                DiskStat d;
                if (names[i] == null || !map.TryGetValue(names[i], out d)) continue;
                double mb = Math.Max(0, vals[i]) / 1048576.0;
                if (write) d.WriteMBs = mb;
                else d.ReadMBs = mb;
            }
        }

        /// <summary>
        /// Сетевая активность: сумма по физическим интерфейсам. Loopback,
        /// туннели и виртуальные адаптеры (Hyper-V, Wi-Fi Direct) не считаем,
        /// иначе одна и та же передача попала бы в сумму дважды.
        /// </summary>
        void ReadNet(SysStats s)
        {
            s.Nets.Clear();
            s.NetRxMBs = 0;
            s.NetTxMBs = 0;
            if (_netRx == IntPtr.Zero && _netTx == IntPtr.Zero) return;

            var map = new Dictionary<string, NetStat>(StringComparer.OrdinalIgnoreCase);
            AddNetValues(map, _netRx, false);
            AddNetValues(map, _netTx, true);

            // Считаем только один адаптер — тот, через который сейчас идёт
            // трафик (Wi-Fi или Ethernet). Складывать нельзя: виртуальный
            // адаптер дублировал бы байты физического, и скорость удваивалась.
            NetStat best = null;
            foreach (var kv in map)
            {
                NetStat d = kv.Value;
                if (d.RxMBs < 0.0005 && d.TxMBs < 0.0005) continue;   // простаивает
                if (best == null || d.RxMBs + d.TxMBs > best.RxMBs + best.TxMBs) best = d;
            }
            if (best == null) return;

            s.Nets.Add(best);
            s.NetRxMBs = best.RxMBs;
            s.NetTxMBs = best.TxMBs;
        }

        void AddNetValues(Dictionary<string, NetStat> map, IntPtr counter, bool tx)
        {
            string[] names;
            double[] vals = ArrayValues(counter, out names);
            for (int i = 0; i < vals.Length; i++)
            {
                string n = names[i];
                if (!UseNetInterface(n)) continue;

                NetStat d;
                if (!map.TryGetValue(n, out d))
                {
                    d = new NetStat();
                    d.Name = n;
                    map[n] = d;
                }
                double mb = Math.Max(0, vals[i]) / 1048576.0;
                if (tx) d.TxMBs = mb;
                else d.RxMBs = mb;
            }
        }

        static bool UseNetInterface(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            string t = n.ToLowerInvariant();
            if (t.Contains("loopback")) return false;
            if (t.Contains("isatap")) return false;
            if (t.Contains("teredo")) return false;
            if (t.Contains("pseudo-interface")) return false;
            if (t.Contains("vethernet")) return false;
            if (t.Contains("wi-fi direct")) return false;
            if (t.Contains("virtual adapter")) return false;
            if (t.Contains("bluetooth")) return false;
            if (t.Contains("tap-windows")) return false;
            if (t.Contains("npcap loopback")) return false;
            return true;
        }

        static void ParseDiskInstance(string name, out int index, out string letters)
        {
            index = -1;
            letters = "";
            int sp = name.IndexOf(' ');
            string head = sp < 0 ? name : name.Substring(0, sp);
            int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
            if (sp >= 0) letters = name.Substring(sp + 1).Trim();
        }

        /// <summary>
        /// Загрузка GPU. Инстансы выглядят как
        /// pid_1234_luid_0x…_phys_0_eng_3_engtype_3D, то есть отдельно на процесс,
        /// на движок и на адаптер. Складываем по «адаптер + тип движка» и берём
        /// самый нагруженный движок — это ближе всего к тому, что показывает
        /// диспетчер задач.
        /// </summary>
        void ReadGpu(SysStats s)
        {
            s.HasGpu = _gpuEngine != IntPtr.Zero;
            if (!s.HasGpu) return;

            string[] names;
            double[] vals = ArrayValues(_gpuEngine, out names);

            var byEngine = new System.Collections.Generic.Dictionary<string, double>();
            // по процессам: pid → (тип движка → сумма). У процесса берём самый занятый движок.
            var perPid = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.Dictionary<string, double>>();
            for (int i = 0; i < vals.Length; i++)
            {
                string n = names[i];
                if (n == null) continue;
                int et = n.IndexOf("_engtype_", StringComparison.Ordinal);
                if (et < 0) continue;
                string eng = n.Substring(et + 9);
                if (eng.Length == 0) continue;           // безымянные движки пропускаем

                int li = n.IndexOf("luid_", StringComparison.Ordinal);
                int e = li >= 0 ? n.IndexOf("_eng_", li, StringComparison.Ordinal) : -1;
                string adapter = (li >= 0 && e > li) ? n.Substring(li, e - li) : "";

                string key = adapter + "|" + eng;
                double cur;
                byEngine.TryGetValue(key, out cur);
                byEngine[key] = cur + vals[i];

                int pid = ParsePid(n);
                if (pid > 0)
                {
                    System.Collections.Generic.Dictionary<string, double> m;
                    if (!perPid.TryGetValue(pid, out m))
                    {
                        m = new System.Collections.Generic.Dictionary<string, double>();
                        perPid[pid] = m;
                    }
                    double pc;
                    m.TryGetValue(eng, out pc);
                    m[eng] = pc + vals[i];
                }
            }

            double best = 0;
            foreach (var kv in byEngine)
            {
                double v = Math.Min(100.0, kv.Value);    // движок не может быть занят больше 100%
                if (v > best) best = v;
            }
            s.Gpu = Math.Max(0, best);

            // нагрузка по типам движков (3D, видео, копирование…) — максимум по адаптерам
            foreach (var kv in byEngine)
            {
                int bar = kv.Key.IndexOf('|');
                string type = bar >= 0 ? kv.Key.Substring(bar + 1) : kv.Key;
                double cur;
                s.GpuEngines.TryGetValue(type, out cur);
                s.GpuEngines[type] = Math.Max(cur, Math.Min(100.0, kv.Value));
            }

            var procs = new System.Collections.Generic.Dictionary<int, GpuProc>();
            foreach (var kv in perPid)
            {
                double u = 0;
                foreach (var e2 in kv.Value) if (e2.Value > u) u = e2.Value;
                procs[kv.Key] = new GpuProc { Pid = kv.Key, Util = Math.Min(100.0, u) };
            }
            AddProcMem(procs, _gpuProcDed, false);
            AddProcMem(procs, _gpuProcShared, true);
            foreach (var gp in procs.Values) s.GpuProcs.Add(gp);

            double[] mem = ArrayValues(_gpuMem, out names);
            double bytes = 0;
            for (int i = 0; i < mem.Length; i++) bytes += mem[i];
            s.VramUsedGB = bytes / 1073741824.0;

            double[] shared = ArrayValues(_gpuMemShared, out names);
            bytes = 0;
            for (int i = 0; i < shared.Length; i++) bytes += shared[i];
            s.VramSharedGB = bytes / 1073741824.0;
        }

        /// <summary>Номер процесса из имени инстанса «pid_1234_luid_…»; 0 — не удалось.</summary>
        static int ParsePid(string name)
        {
            if (name == null || !name.StartsWith("pid_", StringComparison.Ordinal)) return 0;
            int end = name.IndexOf('_', 4);
            string num = end > 4 ? name.Substring(4, end - 4) : name.Substring(4);
            int pid;
            return int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) ? pid : 0;
        }

        /// <summary>Видеопамять по процессам: байты → МБ, складываем по pid.</summary>
        void AddProcMem(System.Collections.Generic.Dictionary<int, GpuProc> procs, IntPtr counter, bool shared)
        {
            string[] names;
            double[] v = ArrayValues(counter, out names);
            for (int i = 0; i < v.Length; i++)
            {
                int pid = ParsePid(names[i]);
                if (pid <= 0) continue;
                GpuProc gp;
                if (!procs.TryGetValue(pid, out gp)) { gp = new GpuProc { Pid = pid }; procs[pid] = gp; }
                double mb = Math.Max(0, v[i]) / 1048576.0;
                if (shared) gp.SharedMb += mb; else gp.DedMb += mb;
            }
        }

        static ulong AvailPhys()
        {
            var ms = new MEMORYSTATUSEX();
            ms.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            return GlobalMemoryStatusEx(ref ms) ? ms.ullAvailPhys : 0;
        }

        public SysStats Sample()
        {
            var s = new SysStats();
            PdhCollectQueryData(_q);

            s.Cpu = Math.Min(100, Math.Max(0, Scalar(_cpu)));
            s.CpuPerf = Math.Max(0, Scalar(_cpuPerf));
            s.CpuCores = ReadCores();
            ReadCpuExtra(s);

            var ms = new MEMORYSTATUSEX();
            ms.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref ms))
            {
                s.RamPct = ms.dwMemoryLoad;
                s.RamTotalGB = ms.ullTotalPhys / 1073741824.0;
                s.RamUsedGB = (ms.ullTotalPhys - ms.ullAvailPhys) / 1073741824.0;
            }

            // кэш и подтверждённая память — страницами; размер страницы отдаёт сама система
            PERFORMANCE_INFORMATION pi;
            if (GetPerformanceInfo(out pi, (uint)Marshal.SizeOf(typeof(PERFORMANCE_INFORMATION))))
            {
                double page = (double)(ulong)pi.PageSize;
                s.CacheGB = (double)(ulong)pi.SystemCache * page / 1073741824.0;
                s.CommitUsedGB = (double)(ulong)pi.CommitTotal * page / 1073741824.0;
                s.CommitLimitGB = (double)(ulong)pi.CommitLimit * page / 1073741824.0;
            }

            ReadDisks(s);

            ReadNet(s);

            ReadGpu(s);

            return s;
        }

        /// <summary>
        /// Просим процессы отдать неиспользуемые страницы из рабочего набора.
        /// Работает в фоне: окно не подвисает, результат забираем из TrimState/TrimFreed.
        /// </summary>
        public void TrimStart()
        {
            if (Interlocked.CompareExchange(ref _trimState, 1, 0) != 0) return;
            var t = new Thread(TrimWork);
            t.IsBackground = true;
            t.Name = "SysWidgetTrim";
            t.Start();
        }

        void TrimWork()
        {
            try
            {
                ulong before = AvailPhys();
                // Окно, с которым работают сейчас, не трогаем: после сброса его страницы пришлось бы
                // читать с диска, и программа подвисала бы. То же — для служебных процессов.
                uint fg = 0;
                try { GetWindowThreadProcessId(GetForegroundWindow(), out fg); } catch { }
                int self = Process.GetCurrentProcess().Id;
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id <= 4 || p.Id == self || (uint)p.Id == fg || p.WorkingSet64 < 20L * 1048576L) continue;
                        if (!ProcessPanel.CanClose(p.ProcessName)) continue;
                        IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA | PROCESS_SET_INFORMATION, false, p.Id);
                        if (h == IntPtr.Zero) continue;
                        // фоновым процессам — низкий приоритет памяти: освободившееся место
                        // система не вернёт им первым делом (Windows 8 и новее; иначе пропускаем)
                        try { uint pr = MemoryPriorityLow; SetProcessInformation(h, ProcessMemoryPriority, ref pr, 4); } catch { }
                        EmptyWorkingSet(h);
                        CloseHandle(h);
                    }
                    catch { }
                }
                Thread.Sleep(250);   // даём ОС пересчитать свободную память
                ulong after = AvailPhys();
                _trimFreed = after > before ? (long)(after - before) : 0;

                // кто держит больше всех памяти после сброса (процессы с одним именем складываем)
                var sums = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id <= 4 || !ProcessPanel.CanClose(p.ProcessName)) continue;
                        long w; sums.TryGetValue(p.ProcessName, out w);
                        sums[p.ProcessName] = w + p.WorkingSet64;
                    }
                    catch { }
                }
                string best = null; long bw = 0;
                foreach (var kv in sums) if (kv.Value > bw) { bw = kv.Value; best = kv.Key; }
                _trimTop = best == null || bw < 200L * 1048576L ? null
                    : best + " — " + (bw / 1073741824.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + Lang.S("ГБ", "GB");
            }
            catch { }
            _trimState = 2;
        }

        public void Dispose()
        {
            _run = false;
            if (_worker != null) { try { _worker.Join(2000); } catch { } _worker = null; }
            if (_q != IntPtr.Zero) { PdhCloseQuery(_q); _q = IntPtr.Zero; }
        }
    }
}
