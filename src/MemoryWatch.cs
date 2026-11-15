using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SysWidget
{
    /// <summary>Процесс (все экземпляры с одним именем), чья память растёт без остановки.</summary>
    public sealed class MemoryGrowth
    {
        public string Name;
        public double GrowthMb;     // прирост за окно наблюдения
        public double Minutes;      // длительность окна
        public double NowMb;
    }

    /// <summary>
    /// Следит за ростом памяти процессов: раз в 30 секунд снимает рабочий набор каждой
    /// группы одноимённых процессов и хранит последние 20 минут. Если память группы
    /// растёт почти без остановок, а число экземпляров не меняется, — это признак
    /// утечки («подозрение», а не диагноз: приложение могло просто открыть большой файл).
    ///
    /// Нагрузка ничтожна: один перебор процессов раз в полминуты в пуле потоков.
    /// Прав администратора не нужно — недоступные процессы пропускаются.
    /// </summary>
    public static class MemoryWatch
    {
        const int PeriodMs = 30000;
        const int MaxSamples = 40;          // 20 минут
        const int MinSamples = 12;          // не раньше чем через ~6 минут
        const double MinGrowthMb = 100;     // меньшие приросты шум
        const double MinGrowthFrac = 0.10;  // и меньше 10% от начала окна

        sealed class Series
        {
            public readonly List<double> Mb = new List<double>();
            public readonly List<int> Count = new List<int>();
        }

        static readonly object Gate = new object();
        static readonly Dictionary<string, Series> Data = new Dictionary<string, Series>(StringComparer.OrdinalIgnoreCase);
        static List<MemoryGrowth> _suspects = new List<MemoryGrowth>();
        static Timer _timer;

        static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Idle", "System", "Registry", "Memory Compression", "MemCompression"
        };

        public static void Start()
        {
            if (_timer != null) return;
            _timer = new Timer(delegate { Tick(); }, null, 5000, PeriodMs);
        }

        /// <summary>Подозрительные процессы, самые «растущие» первыми.</summary>
        public static List<MemoryGrowth> Suspects()
        {
            lock (Gate) return new List<MemoryGrowth>(_suspects);
        }

        public static MemoryGrowth Find(string name)
        {
            lock (Gate)
            {
                foreach (MemoryGrowth g in _suspects)
                    if (string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) return g;
            }
            return null;
        }

        static void Tick()
        {
            try
            {
                var now = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                var cnt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                Process[] all = Process.GetProcesses();
                for (int i = 0; i < all.Length; i++)
                {
                    Process p = all[i];
                    try
                    {
                        if (p.Id == 0) continue;
                        string name = p.ProcessName;
                        if (Skip.Contains(name)) continue;
                        double mb = p.WorkingSet64 / 1048576.0;
                        double cur; now.TryGetValue(name, out cur);
                        now[name] = cur + mb;
                        int c; cnt.TryGetValue(name, out c);
                        cnt[name] = c + 1;
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }

                lock (Gate)
                {
                    foreach (KeyValuePair<string, double> kv in now)
                    {
                        Series s;
                        if (!Data.TryGetValue(kv.Key, out s)) { s = new Series(); Data[kv.Key] = s; }
                        s.Mb.Add(kv.Value);
                        s.Count.Add(cnt[kv.Key]);
                        while (s.Mb.Count > MaxSamples) { s.Mb.RemoveAt(0); s.Count.RemoveAt(0); }
                    }

                    // процессы, которых больше нет, забываем: иначе история копилась бы вечно
                    var gone = new List<string>();
                    foreach (string k in Data.Keys) if (!now.ContainsKey(k)) gone.Add(k);
                    foreach (string k in gone) Data.Remove(k);

                    var found = new List<MemoryGrowth>();
                    foreach (KeyValuePair<string, Series> kv in Data)
                    {
                        MemoryGrowth g = Evaluate(kv.Key, kv.Value);
                        if (g != null) found.Add(g);
                    }
                    found.Sort(delegate(MemoryGrowth a, MemoryGrowth b) { return b.GrowthMb.CompareTo(a.GrowthMb); });
                    _suspects = found;
                }
            }
            catch (Exception ex) { ErrorLog.Write("memory watch", ex); }
        }

        static MemoryGrowth Evaluate(string name, Series s)
        {
            int n = s.Mb.Count;
            if (n < MinSamples) return null;

            double first = s.Mb[0], last = s.Mb[n - 1];
            double growth = last - first;
            if (growth < MinGrowthMb || growth < first * MinGrowthFrac) return null;

            // число экземпляров должно быть почти постоянным: иначе это просто
            // открыли новые вкладки или окна, а не утечка
            int cMin = int.MaxValue, cMax = 0;
            for (int i = 0; i < n; i++) { cMin = Math.Min(cMin, s.Count[i]); cMax = Math.Max(cMax, s.Count[i]); }
            if (cMax - cMin > Math.Max(1, cMin / 10)) return null;

            // рост почти непрерывный: не меньше 80% шагов без заметного спада, не меньше
            // половины шагов — вверх, и ни один скачок не даёт больше 60% прироста
            // (разовая загрузка файла — не утечка)
            int ok = 0, up = 0;
            double maxStep = 0;
            for (int i = 1; i < n; i++)
            {
                double d = s.Mb[i] - s.Mb[i - 1];
                if (d >= -2.0) ok++;
                if (d >= 1.0) up++;
                if (d > maxStep) maxStep = d;
            }
            if (ok < (n - 1) * 0.8 || up < (n - 1) * 0.5 || maxStep > growth * 0.6) return null;

            return new MemoryGrowth
            {
                Name = name,
                GrowthMb = growth,
                Minutes = (n - 1) * PeriodMs / 60000.0,
                NowMb = last
            };
        }
    }
}
