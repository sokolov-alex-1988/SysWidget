using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SysWidget
{
    /// <summary>Одно событие периферии: подключили или отключили.</summary>
    public sealed class PeriphEvent
    {
        public DateTime Time;
        public string Key = "";
        public string Name = "";        // «Мышь · FBK27CAS»
        public bool Connected;
    }

    /// <summary>
    /// Журнал периферии (periph.json в папке данных): когда устройство подключилось и отключилось
    /// и как менялся его заряд. Из истории заряда считается прогноз «хватит ещё на …».
    ///
    /// Вызывается только из UI-потока. Файл мал: до 300 событий и 14 дней замеров заряда
    /// (замер пишется, когда заряд изменился или прошёл час).
    /// </summary>
    public static class PeriphLog
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const int MaxEvents = 300;
        const int KeepDays = 14;
        const int WarmupSeconds = 60;       // первые секунды обход ещё собирает список — событий не пишем

        static readonly List<PeriphEvent> Events = new List<PeriphEvent>();
        static readonly Dictionary<string, List<int[]>> Samples = new Dictionary<string, List<int[]>>();   // ключ → [минуты эпохи, заряд]
        static Dictionary<string, string> _prev;                  // что было подключено в прошлый раз
        static readonly DateTime Started = DateTime.Now;
        static string _file;
        static bool _loaded;

        // ------------------------------------------------------------ вход

        public static void Load(string file)
        {
            _file = file;
            _loaded = true;
            try
            {
                if (!File.Exists(file)) return;
                string t = File.ReadAllText(file);

                foreach (Match m in Regex.Matches(t,
                    "\\{\"t\":\"([^\"]*)\",\"k\":\"([A-Za-z0-9_]*)\",\"n\":\"([^\"]*)\",\"e\":\"(on|off)\"\\}"))
                {
                    DateTime dt;
                    if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm", Inv, DateTimeStyles.None, out dt)) continue;
                    Events.Add(new PeriphEvent
                    {
                        Time = dt, Key = m.Groups[2].Value, Name = m.Groups[3].Value,
                        Connected = m.Groups[4].Value == "on"
                    });
                }

                foreach (Match m in Regex.Matches(t,
                    "\\{\"k\":\"([A-Za-z0-9_]+)\",\"s\":\\[((?:\\[\\d+,\\d+\\],?)*)\\]\\}"))
                {
                    var list = new List<int[]>();
                    foreach (Match s in Regex.Matches(m.Groups[2].Value, "\\[(\\d+),(\\d+)\\]"))
                        list.Add(new[] { int.Parse(s.Groups[1].Value, Inv), int.Parse(s.Groups[2].Value, Inv) });
                    Samples[m.Groups[1].Value] = list;
                }
            }
            catch (Exception ex) { ErrorLog.Write("load periph log", ex); }
        }

        /// <summary>Ключ устройства для файла: только безопасные символы.</summary>
        public static string K(string key)
        {
            string k = Regex.Replace(key ?? "", "[^A-Za-z0-9]", "_");
            return k.Length > 80 ? k.Substring(k.Length - 80) : k;
        }

        /// <summary>Свежий список устройств с обхода: пишем подключения, отключения и заряд.</summary>
        public static void Observe(List<Peripheral> list)
        {
            if (!_loaded || list == null) return;
            bool changed = false;
            DateTime now = DateTime.Now;
            bool warm = (now - Started).TotalSeconds < WarmupSeconds;

            var cur = new Dictionary<string, string>();
            foreach (Peripheral p in list) cur[K(p.Key)] = p.KindName + " · " + p.Name;

            if (_prev != null && !warm)
            {
                foreach (KeyValuePair<string, string> kv in cur)
                    if (!_prev.ContainsKey(kv.Key)) { AddEvent(now, kv.Key, kv.Value, true); changed = true; }
                foreach (KeyValuePair<string, string> kv in _prev)
                    if (!cur.ContainsKey(kv.Key)) { AddEvent(now, kv.Key, kv.Value, false); changed = true; }
            }
            // в период прогрева «прошлое» обновляем всегда, чтобы не считать старые устройства новыми
            _prev = cur;

            int minutes = (int)(now.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMinutes;
            foreach (Peripheral p in list)
            {
                if (p.Battery < 0) continue;
                string k = K(p.Key);
                List<int[]> s;
                if (!Samples.TryGetValue(k, out s)) { s = new List<int[]>(); Samples[k] = s; }
                int[] last = s.Count > 0 ? s[s.Count - 1] : null;
                if (last == null || last[1] != p.Battery || minutes - last[0] >= 60)
                {
                    s.Add(new[] { minutes, p.Battery });
                    changed = true;
                }
                int cut = minutes - KeepDays * 24 * 60;
                while (s.Count > 0 && s[0][0] < cut) s.RemoveAt(0);
            }

            if (changed) Save();
        }

        static void AddEvent(DateTime t, string key, string name, bool connected)
        {
            Events.Add(new PeriphEvent { Time = t, Key = key, Name = name, Connected = connected });
            while (Events.Count > MaxEvents) Events.RemoveAt(0);
        }

        // ---------------------------------------------------------- запросы

        /// <summary>Последние события устройства (свежие первыми).</summary>
        public static List<PeriphEvent> EventsFor(string key, int max)
        {
            string k = K(key);
            var res = new List<PeriphEvent>();
            for (int i = Events.Count - 1; i >= 0 && res.Count < max; i--)
                if (Events[i].Key == k) res.Add(Events[i]);
            return res;
        }

        /// <summary>Замеры заряда: [минуты от эпохи, заряд %], по возрастанию времени.</summary>
        public static List<int[]> SamplesFor(string key)
        {
            List<int[]> s;
            return Samples.TryGetValue(K(key), out s) ? new List<int[]>(s) : new List<int[]>();
        }

        public static DateTime FromMinutes(int minutes)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minutes).ToLocalTime();
        }

        /// <summary>
        /// Прогноз по последнему непрерывному разряду (без зарядки): через сколько часов будет 0%.
        /// Нужно не меньше 3 часов наблюдения и падения на 3 пункта; иначе −1.
        /// </summary>
        public static double HoursLeft(string key, out double basisHours)
        {
            basisHours = 0;
            List<int[]> s = SamplesFor(key);
            if (s.Count < 2) return -1;

            int end = s.Count - 1, start = end;
            while (start > 0 && s[start - 1][1] >= s[start][1] - 1) start--;   // назад, пока заряд не рос (допуск 1 пункт)

            double hours = (s[end][0] - s[start][0]) / 60.0;
            double drop = s[start][1] - s[end][1];
            basisHours = hours;
            if (hours < 3 || drop < 3) return -1;
            return s[end][1] / (drop / hours);
        }

        // ---------------------------------------------------------- запись

        static void Save()
        {
            if (_file == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"Events\":[");
                for (int i = 0; i < Events.Count; i++)
                {
                    PeriphEvent e = Events[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"t\":\"").Append(e.Time.ToString("yyyy-MM-dd HH:mm", Inv))
                      .Append("\",\"k\":\"").Append(e.Key)
                      .Append("\",\"n\":\"").Append(e.Name.Replace("\\", " ").Replace("\"", " "))
                      .Append("\",\"e\":\"").Append(e.Connected ? "on" : "off").Append("\"}");
                }
                sb.Append("],\"Samples\":[");
                bool first = true;
                foreach (KeyValuePair<string, List<int[]>> kv in Samples)
                {
                    if (kv.Value.Count == 0) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"k\":\"").Append(kv.Key).Append("\",\"s\":[");
                    for (int i = 0; i < kv.Value.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('[').Append(kv.Value[i][0].ToString(Inv)).Append(',').Append(kv.Value[i][1].ToString(Inv)).Append(']');
                    }
                    sb.Append("]}");
                }
                sb.Append("]}");
                AtomicFile.WriteAllText(_file, sb.ToString());
            }
            catch (Exception ex) { ErrorLog.Write("save periph log", ex); }
        }
    }
}
