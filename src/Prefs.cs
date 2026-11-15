using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace SysWidget
{
    /// <summary>
    /// Пользовательские настройки внешнего вида и поведения (prefs.json).
    /// state.json хранит положение и раскладку карточки, alerts.json — тревоги,
    /// здесь — всё остальное: тема, плотность, шрифт, цвета, прозрачность, пороги,
    /// имя Wi-Fi и лимит трафика. Горячая клавиша (Ctrl+Alt+W) не настраивается.
    ///
    /// Часть настроек (тема, плотность, шрифт, цвета) читается при создании
    /// окон, поэтому действует после перезапуска; остальные применяются сразу.
    /// RestartSignature позволяет понять, что перезапуск нужен.
    /// </summary>
    public static class Prefs
    {
        static readonly IFormatProvider Inv = CultureInfo.InvariantCulture;
        static string _file;

        // ---------------- вид (нужен перезапуск)
        public static bool Neon = true;
        public static bool ShowDashBtn = true;   // кнопка окна мониторинга на карточке
        public static bool Light = false;        // светлая тема; действует, только если Neon выключен
        public static int Density = 0;           // 0 компактно, 1 обычно, 2 свободно
        public static double FontScale = 1.0;    // 0.9 … 1.2
        public static string AccRam = "", AccCpu = "", AccSsd = "", AccHdd = "",
                             AccGpu = "", AccNet = "", AccWarn = "";

        // ---------------- вид (сразу)
        public static double Opacity = 1.0;      // 0.4 … 1.0
        public static bool FadeIdle = false;     // бледнеть, когда курсор далеко
        public static bool HideLabels = false;   // только цветная шкала и значение
        public static bool ColorByLoad = false;  // значение желтеет, а потом краснеет
        public static int WarnAt = 70, CritAt = 90;

        // ---------------- сеть
        public static bool ShowSsid = false;     // имя Wi-Fi в подсказке сети: запрашивает местоположение, поэтому выключено
        public static int TrafficLimitGB = 0;    // лимит трафика за месяц, ГБ; 0 — без предупреждений

        /// <summary>Снимок настроек, которые действуют только после перезапуска.</summary>
        public static string RestartSignature()
        {
            return string.Join("|", new string[]
            {
                Neon ? "n" : "d", Light ? "l" : "-", Density.ToString(Inv), FontScale.ToString("0.00", Inv),
                AccRam, AccCpu, AccSsd, AccHdd, AccGpu, AccNet, AccWarn
            });
        }

        static string _appliedSignature = "";

        /// <summary>Настройки изменены так, что без перезапуска они не вступят в силу.</summary>
        public static bool NeedsRestart { get { return RestartSignature() != _appliedSignature; } }

        // ------------------------------------------------------------ хранение

        /// <summary>
        /// Прочитать prefs.json. defaultNeon — тема по умолчанию: пока файла нет,
        /// её берут из старого поля Theme в state.json, чтобы выбор не потерялся.
        /// </summary>
        public static void Load(string file, bool defaultNeon)
        {
            _file = file;
            Neon = defaultNeon;
            try
            {
                if (File.Exists(file)) Parse(File.ReadAllText(file));
            }
            catch (Exception ex) { ErrorLog.Write("load prefs", ex); }
            Clamp();
            _appliedSignature = RestartSignature();
        }

        /// <summary>Разобрать JSON из строки (файл настроек или профиль).</summary>
        public static void Parse(string t)
        {
            Neon = Bool(t, "Neon", Neon);
            Light = Bool(t, "Light", Light);
            ShowDashBtn = Bool(t, "ShowDashBtn", ShowDashBtn);
            Density = Int(t, "Density", Density);
            FontScale = Dbl(t, "FontScale", FontScale);
            AccRam = Str(t, "AccRam", AccRam); AccCpu = Str(t, "AccCpu", AccCpu);
            AccSsd = Str(t, "AccSsd", AccSsd); AccHdd = Str(t, "AccHdd", AccHdd);
            AccGpu = Str(t, "AccGpu", AccGpu); AccNet = Str(t, "AccNet", AccNet);
            AccWarn = Str(t, "AccWarn", AccWarn);

            Opacity = Dbl(t, "Opacity", Opacity);
            FadeIdle = Bool(t, "FadeIdle", FadeIdle);
            HideLabels = Bool(t, "HideLabels", HideLabels);
            ColorByLoad = Bool(t, "ColorByLoad", ColorByLoad);
            WarnAt = Int(t, "WarnAt", WarnAt);
            CritAt = Int(t, "CritAt", CritAt);

            ShowSsid = Bool(t, "ShowSsid", ShowSsid);
            TrafficLimitGB = Int(t, "TrafficLimitGB", TrafficLimitGB);
        }

        static void Clamp()
        {
            Density = Math.Max(0, Math.Min(2, Density));
            FontScale = Math.Max(0.8, Math.Min(1.3, FontScale));
            Opacity = Math.Max(0.3, Math.Min(1.0, Opacity));
            WarnAt = Math.Max(30, Math.Min(95, WarnAt));
            TrafficLimitGB = Math.Max(0, Math.Min(100000, TrafficLimitGB));
            CritAt = Math.Max(WarnAt + 5, Math.Min(99, CritAt));
            AccRam = Hex(AccRam); AccCpu = Hex(AccCpu); AccSsd = Hex(AccSsd); AccHdd = Hex(AccHdd);
            AccGpu = Hex(AccGpu); AccNet = Hex(AccNet); AccWarn = Hex(AccWarn);
        }

        /// <summary>Только «#RRGGBB» — всё остальное означает «цвет темы».</summary>
        static string Hex(string s)
        {
            return s != null && Regex.IsMatch(s, "^#[0-9A-Fa-f]{6}$") ? s.ToUpperInvariant() : "";
        }

        public static string ToJson()
        {
            Clamp();
            return string.Format(Inv,
                "{{\"Neon\":{0},\"Density\":{1},\"FontScale\":{2:0.00}," +
                "\"AccRam\":\"{3}\",\"AccCpu\":\"{4}\",\"AccSsd\":\"{5}\",\"AccHdd\":\"{6}\"," +
                "\"AccGpu\":\"{7}\",\"AccNet\":\"{8}\",\"AccWarn\":\"{9}\"," +
                "\"Opacity\":{10:0.00},\"FadeIdle\":{11},\"HideLabels\":{12},\"ColorByLoad\":{13}," +
                "\"WarnAt\":{14},\"CritAt\":{15},\"ShowSsid\":{16},\"TrafficLimitGB\":{17},\"Light\":{18},\"ShowDashBtn\":{19}}}",
                B(Neon), Density, FontScale,
                AccRam, AccCpu, AccSsd, AccHdd, AccGpu, AccNet, AccWarn,
                Opacity, B(FadeIdle), B(HideLabels), B(ColorByLoad),
                WarnAt, CritAt, B(ShowSsid), TrafficLimitGB, B(Light), B(ShowDashBtn));
        }

        public static void Save()
        {
            try
            {
                if (_file == null) return;
                AtomicFile.WriteAllText(_file, ToJson());
            }
            catch (Exception ex) { ErrorLog.Write("save prefs", ex); }
        }

        /// <summary>Вернуть всё к значениям по умолчанию (тему оставляем как выбрана).</summary>
        public static void Reset()
        {
            bool neon = Neon, light = Light;
            Density = 0; FontScale = 1.0;
            AccRam = AccCpu = AccSsd = AccHdd = AccGpu = AccNet = AccWarn = "";
            Opacity = 1.0; FadeIdle = false; HideLabels = false; ColorByLoad = false; WarnAt = 70; CritAt = 90;
            ShowSsid = false;
            Neon = neon; Light = light;
        }

        static string B(bool v) { return v ? "true" : "false"; }

        static bool Bool(string t, string key, bool def)
        {
            Match m = Regex.Match(t, "\"" + key + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase) : def;
        }

        static int Int(string t, string key, int def)
        {
            return (int)Math.Round(Dbl(t, key, def));
        }

        static double Dbl(string t, string key, double def)
        {
            Match m = Regex.Match(t, "\"" + key + "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
            double v;
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, Inv, out v) ? v : def;
        }

        static string Str(string t, string key, string def)
        {
            Match m = Regex.Match(t, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : def;
        }
    }
}
