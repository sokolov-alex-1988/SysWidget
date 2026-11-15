using System;
using System.IO;
using System.Text.RegularExpressions;

namespace SysWidget
{
    /// <summary>
    /// Язык интерфейса: русский (по умолчанию) или английский.
    ///
    /// Подписи не собираются в отдельный словарь, а пишутся парами прямо на
    /// месте: <c>Lang.S("ОЗУ", "RAM")</c>. Так строка всегда рядом с тем, что
    /// подписывает, и её не приходится искать по ключу в другом файле.
    /// </summary>
    public static class Lang
    {
        /// <summary>Включён английский.</summary>
        public static bool En { get; private set; }

        public static void Set(bool en)
        {
            En = en;
        }

        /// <summary>Русский вариант строки — или английский, если включён английский.</summary>
        public static string S(string ru, string en)
        {
            return En ? en : ru;
        }

        // Часто повторяющиеся названия метрик — короткие геттеры, чтобы не
        // дублировать пары внутри каждого окна.
        public static string Ram { get { return S("ОЗУ", "RAM"); } }
        public static string Cpu { get { return S("ЦП", "CPU"); } }
        public static string Ssd { get { return S("SSD", "SSD"); } }
        public static string Gpu { get { return S("ГП", "GPU"); } }
        public static string Net { get { return S("Сеть", "NET"); } }
        public static string Disk { get { return S("Диск", "Disk"); } }
        public static string Disks { get { return S("Диски", "Disks"); } }

        /// <summary>Прочитать выбранный язык из state.json. По умолчанию русский.</summary>
        public static void Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                string t = File.ReadAllText(path);
                Match m = Regex.Match(t, "\"Lang\"\\s*:\\s*\"(ru|en)\"");
                if (m.Success) En = m.Groups[1].Value == "en";
            }
            catch { }
        }
    }
}
