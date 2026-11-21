using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SysWidget
{
    /// <summary>Одна запись журнала тревог.</summary>
    public sealed class AlertEntry
    {
        public DateTime Time;
        public int Level;       // 1 — предупреждение, 2 — серьёзно
        public string Text = "";
    }

    /// <summary>
    /// Журнал сработавших тревог (порог ОЗУ/ЦП/диска/ГП, мало места, низкий заряд периферии).
    /// Хранится в alertlog.txt рядом с остальными данными, последние 100 записей: строка —
    /// «время в тиках, уровень, текст» через табуляцию. Только для показа в окне мониторинга.
    /// </summary>
    public static class AlertLog
    {
        const int Max = 100;
        static readonly object Gate = new object();
        static List<AlertEntry> _items;         // старые первыми
        public static volatile int Version;     // растёт при каждой записи: по нему окно понимает, что список изменился

        static string FilePath { get { return AppPaths.File_("alertlog.txt"); } }

        static void EnsureLoaded()
        {
            if (_items != null) return;
            _items = new List<AlertEntry>();
            try
            {
                string p = FilePath;
                if (!File.Exists(p)) return;
                foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    long ticks; int level;
                    if (f.Length < 3) continue;
                    if (!long.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) continue;
                    if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out level)) level = 1;
                    _items.Add(new AlertEntry { Time = new DateTime(ticks), Level = level, Text = f[2] });
                }
            }
            catch (Exception ex) { ErrorLog.Write("alert log load", ex); }
        }

        public static void Add(int level, string text)
        {
            try
            {
                lock (Gate)
                {
                    EnsureLoaded();
                    text = (text ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                    _items.Add(new AlertEntry { Time = DateTime.Now, Level = level, Text = text });
                    while (_items.Count > Max) _items.RemoveAt(0);

                    var sb = new StringBuilder();
                    foreach (AlertEntry e in _items)
                        sb.Append(e.Time.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                          .Append(e.Level.ToString(CultureInfo.InvariantCulture)).Append('\t')
                          .Append(e.Text).Append('\n');
                    AtomicFile.WriteAllText(FilePath, sb.ToString());
                    Version++;
                }
            }
            catch (Exception ex) { ErrorLog.Write("alert log add", ex); }
        }

        /// <summary>Последние записи, новые первыми.</summary>
        public static List<AlertEntry> Recent(int count)
        {
            lock (Gate)
            {
                EnsureLoaded();
                var res = new List<AlertEntry>();
                for (int i = _items.Count - 1; i >= 0 && res.Count < count; i--) res.Add(_items[i]);
                return res;
            }
        }
    }
}
