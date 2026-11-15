using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SysWidget
{
    /// <summary>
    /// Замеры нагрузки. Короткая история — кольцо по секундам (15 минут): из него берутся
    /// графики плашек и окна мониторинга за 2 и 15 минут. Длинная — кольцо по минутам
    /// (сутки): каждая минута хранится как среднее и пик, сохраняется в history.txt в папке
    /// данных, поэтому после перезапуска виджета графики за час и сутки не пустые.
    /// Минуты, когда виджет не работал, остаются пустыми (NaN) — график рвётся, а не врёт.
    /// </summary>
    public sealed class History
    {
        public const int Capacity = 900;
        public const int LongMinutes = 1440;

        // ключи рядов
        public const int Cpu = 0;
        public const int Ram = 1;
        public const int Gpu = 2;
        public const int Net = 3;          // приём + передача, МБ/с
        public const int DiskAll = 4;      // сводка по всем дискам

        /// <summary>Ряд конкретного физического диска по его номеру в системе.</summary>
        public static int DiskKey(int index) { return 10 + index; }

        readonly Dictionary<int, double[]> _buf = new Dictionary<int, double[]>();
        int _head, _count;

        // --- длинная история: по минутам ---
        sealed class Acc { public double Sum, Max; public int N; }
        readonly Dictionary<int, Acc> _acc = new Dictionary<int, Acc>();      // накопление текущей минуты
        readonly Dictionary<int, float[]> _lAvg = new Dictionary<int, float[]>();
        readonly Dictionary<int, float[]> _lMax = new Dictionary<int, float[]>();
        readonly long[] _stamp = new long[LongMinutes];   // какая абсолютная минута лежит в ячейке (0 — пусто)
        long _curMin;                                     // абсолютная минута (UTC), которую сейчас накапливаем

        public int Count { get { return _count; } }

        static long NowMinute() { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute; }

        double[] Buf(int key)
        {
            double[] b;
            if (!_buf.TryGetValue(key, out b))
            {
                b = new double[Capacity];     // раньше диска не было — нули
                _buf[key] = b;
            }
            return b;
        }

        float[] LongBuf(Dictionary<int, float[]> map, int key)
        {
            float[] b;
            if (!map.TryGetValue(key, out b))
            {
                b = new float[LongMinutes];
                for (int i = 0; i < b.Length; i++) b[i] = float.NaN;
                map[key] = b;
            }
            return b;
        }

        void Put(int key, double v)
        {
            Buf(key)[_head] = v;
            Acc a;
            if (!_acc.TryGetValue(key, out a)) { a = new Acc(); _acc[key] = a; }
            a.Sum += v;
            if (v > a.Max) a.Max = v;
            a.N++;
        }

        public void Add(SysStats s)
        {
            long m = NowMinute();
            if (_curMin == 0) _curMin = m;
            else if (m != _curMin) { FlushMinute(); _curMin = m; }

            Put(Cpu, s.Cpu);
            Put(Ram, s.RamPct);
            Put(Gpu, s.HasGpu ? s.Gpu : 0);
            Put(Net, s.NetRxMBs + s.NetTxMBs);
            Put(DiskAll, s.DiskPct);

            // диски, которых в этом замере нет (отключили), пишем нулём,
            // чтобы в кольце не осталось чужих значений; в длинную историю они не идут
            var seen = new HashSet<int>();
            for (int i = 0; i < s.Disks.Count; i++)
            {
                int key = DiskKey(s.Disks[i].Index);
                seen.Add(key);
                Put(key, s.Disks[i].Pct);
            }
            foreach (KeyValuePair<int, double[]> kv in _buf)
                if (kv.Key >= 10 && !seen.Contains(kv.Key)) kv.Value[_head] = 0;

            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }

        /// <summary>Законченную минуту — в длинное кольцо (среднее и пик по каждому ряду).</summary>
        void FlushMinute()
        {
            if (_curMin == 0 || _acc.Count == 0) { _acc.Clear(); return; }
            int idx = (int)(_curMin % LongMinutes);
            foreach (float[] b in _lAvg.Values) b[idx] = float.NaN;
            foreach (float[] b in _lMax.Values) b[idx] = float.NaN;
            foreach (KeyValuePair<int, Acc> kv in _acc)
            {
                if (kv.Value.N == 0) continue;
                LongBuf(_lAvg, kv.Key)[idx] = (float)(kv.Value.Sum / kv.Value.N);
                LongBuf(_lMax, kv.Key)[idx] = (float)kv.Value.Max;
            }
            _stamp[idx] = _curMin;
            _acc.Clear();
        }

        /// <summary>Последние n замеров ряда, от старого к новому.</summary>
        public double[] Series(int key, int n)
        {
            int take = Math.Min(n, _count);
            var res = new double[take];
            double[] b;
            if (!_buf.TryGetValue(key, out b)) return res;
            for (int i = 0; i < take; i++)
                res[i] = b[(_head - take + i + Capacity * 2) % Capacity];
            return res;
        }

        /// <summary>
        /// Последние minutes минут ряда, от старой к новой: среднее по минуте и пик.
        /// Минуты без данных — NaN. Последняя точка — текущая, ещё не законченная минута.
        /// </summary>
        public double[] LongSeries(int key, int minutes, out double[] peaks)
        {
            int n = Math.Max(1, Math.Min(minutes, LongMinutes));
            var avg = new double[n];
            peaks = new double[n];
            long now = Math.Max(_curMin, NowMinute());
            float[] a, p;
            _lAvg.TryGetValue(key, out a);
            _lMax.TryGetValue(key, out p);
            Acc cur;
            bool hasCur = _acc.TryGetValue(key, out cur) && cur.N > 0;
            for (int i = 0; i < n; i++)
            {
                long mm = now - (n - 1 - i);
                avg[i] = double.NaN;
                peaks[i] = double.NaN;
                if (mm == _curMin && hasCur)
                {
                    avg[i] = cur.Sum / cur.N;
                    peaks[i] = cur.Max;
                }
                else if (mm > 0 && a != null)
                {
                    int idx = (int)(mm % LongMinutes);
                    if (_stamp[idx] == mm && !float.IsNaN(a[idx]))
                    {
                        avg[i] = a[idx];
                        peaks[i] = p != null && !float.IsNaN(p[idx]) ? p[idx] : a[idx];
                    }
                }
            }
            return avg;
        }

        // ------------------------------------------------------------ файл

        static string Num(float v)
        {
            return float.IsNaN(v) ? "-" : v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>Сохранить законченные минуты за последние сутки (текст: ключ, первая минута, средние, пики).</summary>
        public void Save(string path)
        {
            long now = Math.Max(_curMin, NowMinute());
            long first = now - (LongMinutes - 1);
            var sb = new StringBuilder();
            sb.Append("SWH1\n");
            foreach (KeyValuePair<int, float[]> kv in _lAvg)
            {
                float[] p;
                _lMax.TryGetValue(kv.Key, out p);
                var avg = new StringBuilder();
                var mx = new StringBuilder();
                int have = 0;
                for (int i = 0; i < LongMinutes; i++)
                {
                    long mm = first + i;
                    int idx = (int)(mm % LongMinutes);
                    bool ok = mm > 0 && _stamp[idx] == mm && !float.IsNaN(kv.Value[idx]);
                    if (i > 0) { avg.Append(','); mx.Append(','); }
                    if (ok)
                    {
                        have++;
                        avg.Append(Num(kv.Value[idx]));
                        mx.Append(Num(p != null ? p[idx] : kv.Value[idx]));
                    }
                    else { avg.Append('-'); mx.Append('-'); }
                }
                if (have == 0) continue;
                sb.Append("K|").Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(first.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(avg).Append('|').Append(mx).Append('\n');
            }
            AtomicFile.WriteAllText(path, sb.ToString());
        }

        /// <summary>Прочитать сохранённую историю; всё старше суток и испорченное отбрасывается.</summary>
        public void Load(string path)
        {
            if (!File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != "SWH1") return;
            long now = NowMinute();
            long oldest = now - (LongMinutes - 1);
            for (int li = 1; li < lines.Length; li++)
            {
                string[] f = lines[li].Split('|');
                if (f.Length != 5 || f[0] != "K") continue;
                int key;
                long first;
                if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out key)) continue;
                if (!long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out first)) continue;
                string[] av = f[3].Split(',');
                string[] mx = f[4].Split(',');
                if (av.Length != mx.Length) continue;
                float[] a = LongBuf(_lAvg, key), p = LongBuf(_lMax, key);
                for (int i = 0; i < av.Length; i++)
                {
                    long mm = first + i;
                    if (mm < oldest || mm > now || av[i] == "-") continue;
                    float va, vm;
                    if (!float.TryParse(av[i], NumberStyles.Float, CultureInfo.InvariantCulture, out va)) continue;
                    if (!float.TryParse(mx[i], NumberStyles.Float, CultureInfo.InvariantCulture, out vm)) vm = va;
                    int idx = (int)(mm % LongMinutes);
                    if (_stamp[idx] != mm)
                    {
                        // ячейка занята другой минутой — освобождаем её у всех рядов
                        foreach (float[] b in _lAvg.Values) b[idx] = float.NaN;
                        foreach (float[] b in _lMax.Values) b[idx] = float.NaN;
                        _stamp[idx] = mm;
                    }
                    a[idx] = va;
                    p[idx] = vm;
                }
            }
        }

        public static double Max(double[] v)
        {
            double m = 0;
            for (int i = 0; i < v.Length; i++) if (!double.IsNaN(v[i]) && v[i] > m) m = v[i];
            return m;
        }

        public static double Avg(double[] v)
        {
            double s = 0;
            int n = 0;
            for (int i = 0; i < v.Length; i++) if (!double.IsNaN(v[i])) { s += v[i]; n++; }
            return n == 0 ? 0 : s / n;
        }

        /// <summary>Сколько точек ряда не пустые.</summary>
        public static int Valid(double[] v)
        {
            int n = 0;
            for (int i = 0; i < v.Length; i++) if (!double.IsNaN(v[i])) n++;
            return n;
        }
    }
}
