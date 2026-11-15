using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SysWidget
{
    /// <summary>Один процесс (или группа одноимённых) в списке «топ».</summary>
    public sealed class ProcessInfo
    {
        public string Name;
        public double Cpu;      // % от всего процессора
        public double RamMb;    // рабочий набор, МБ
        public double ReadMBs, WriteMBs;   // ввод-вывод процесса, МБ/с (со второго замера)
        public double GpuPct, GpuMb;       // ГП: загрузка и видеопамять (подставляет плашка из SysStats)
        public int Conns;                  // сеть: установленных TCP-соединений (подставляет плашка)
        public List<string> Remotes = new List<string>();   // с кем (адрес:порт), без повторов
        public int Count;       // сколько экземпляров с этим именем
        public List<int> Pids = new List<int>();
    }

    /// <summary>
    /// Замер списка процессов. Загрузка ЦП процессом — это прирост его
    /// процессорного времени между двумя замерами, поделённый на прошедшее
    /// время и число ядер: так же считает диспетчер задач. Драйверов не нужно,
    /// всё через обычные счётчики процесса; защищённые процессы просто
    /// пропускаются (доступ к ним запрещён без прав администратора).
    ///
    /// Класс не потокобезопасен: вызывать Sample по одному, из любого потока.
    /// </summary>
    public sealed class ProcessSampler
    {
        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS c);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        readonly Dictionary<int, TimeSpan> _last = new Dictionary<int, TimeSpan>();
        // накопленные байты чтения и записи процесса при прошлом замере
        readonly Dictionary<int, ulong[]> _lastIo = new Dictionary<int, ulong[]>();
        DateTime _lastAt;

        /// <summary>Сколько замеров сделано. Загрузка ЦП осмысленна со второго.</summary>
        public int Samples { get; private set; }

        public ProcessInfo[] Sample()
        {
            DateTime now = DateTime.Now;
            double elapsed = _lastAt == DateTime.MinValue ? 0 : (now - _lastAt).TotalSeconds;
            int cores = Environment.ProcessorCount;
            if (cores < 1) cores = 1;

            var groups = new Dictionary<string, ProcessInfo>(StringComparer.OrdinalIgnoreCase);
            var next = new Dictionary<int, TimeSpan>();
            var nextIo = new Dictionary<int, ulong[]>();

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return new ProcessInfo[0]; }

            for (int i = 0; i < all.Length; i++)
            {
                Process p = all[i];
                try
                {
                    int pid = p.Id;
                    if (pid == 0) continue;                  // «Простой» процесс

                    string name = p.ProcessName;
                    double mem = p.WorkingSet64 / 1048576.0;

                    double cpu = 0;
                    TimeSpan pt;
                    if (TryTotal(p, out pt))
                    {
                        next[pid] = pt;
                        TimeSpan prev;
                        // при первом замере прироста нет — показываем 0
                        if (elapsed > 0.2 && _last.TryGetValue(pid, out prev))
                        {
                            double s = (pt - prev).TotalSeconds;
                            if (s > 0) cpu = s / elapsed / cores * 100.0;
                        }
                    }

                    // ввод-вывод: прирост накопленных байтов между замерами. Считается ВЕСЬ
                    // ввод-вывод процесса (файлы, сеть, каналы): точный дисковый учёт по
                    // процессам без прав администратора недоступен.
                    double rd = 0, wr = 0;
                    ulong[] io;
                    if (TryIo(pid, out io))
                    {
                        nextIo[pid] = io;
                        ulong[] prevIo;
                        if (elapsed > 0.2 && _lastIo.TryGetValue(pid, out prevIo))
                        {
                            if (io[0] >= prevIo[0]) rd = (io[0] - prevIo[0]) / elapsed / 1048576.0;
                            if (io[1] >= prevIo[1]) wr = (io[1] - prevIo[1]) / elapsed / 1048576.0;
                        }
                    }

                    ProcessInfo g;
                    if (!groups.TryGetValue(name, out g))
                    {
                        g = new ProcessInfo();
                        g.Name = name;
                        groups[name] = g;
                    }
                    g.Cpu += cpu;
                    g.RamMb += mem;
                    g.ReadMBs += rd;
                    g.WriteMBs += wr;
                    g.Count++;
                    g.Pids.Add(pid);
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }

            _lastIo.Clear();
            foreach (KeyValuePair<int, ulong[]> kv in nextIo) _lastIo[kv.Key] = kv.Value;
            _last.Clear();
            foreach (KeyValuePair<int, TimeSpan> kv in next) _last[kv.Key] = kv.Value;
            _lastAt = now;
            Samples++;

            var res = new ProcessInfo[groups.Count];
            groups.Values.CopyTo(res, 0);
            return res;
        }

        static bool TryIo(int pid, out ulong[] io)
        {
            io = null;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;           // чужой защищённый процесс
            try
            {
                IO_COUNTERS c;
                if (!GetProcessIoCounters(h, out c)) return false;
                io = new ulong[] { c.ReadTransferCount, c.WriteTransferCount };
                return true;
            }
            finally { CloseHandle(h); }
        }

        static bool TryTotal(Process p, out TimeSpan t)
        {
            t = TimeSpan.Zero;
            try { t = p.TotalProcessorTime; return true; }
            catch { return false; }
        }
    }
}
