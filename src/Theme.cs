using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SysWidget
{
    /// <summary>
    /// Единая палитра и типографика приложения. Любое окно, плашка и меню берут
    /// цвета и шрифты отсюда — так весь виджет выглядит как одно приложение,
    /// а не набор случайных функций.
    /// </summary>
    public static class Theme
    {
        /// <summary>Неоновая тема включена (state.json: "Theme":"neon").</summary>
        public static bool Neon { get; private set; }

        /// <summary>Светлая тема (prefs.json: "Light":true). Геометрия и шрифты — как у тёмной.</summary>
        public static bool Light { get; private set; }

        /// <summary>
        /// Светлая палитра поверх тёмной. Прозрачные «белые» рамки и заливки тёмной
        /// темы здесь заменены прозрачным чёрным, акценты метрик — глубже,
        /// чтобы читались на белом. Значок в трее остаётся на тёмной подложке.
        /// </summary>
        static void ApplyLight()
        {
            Bg = "#FFF3F5F8"; BgTop = "#FFFFFFFF"; BgBottom = "#FFEEF1F5";
            CardStroke = "#26000000";
            Surface = "#FFFFFFFF"; SurfaceHover = "#FFE6EAF0"; SurfaceActive = "#26000000";
            Stroke = "#33000000"; StrokeStrong = "#26000000"; StrokeSoft = "#0F000000";
            TextHi = "#FF111827"; TextMid = "#FF6B7280"; TextLabel = "#FF4B5563";
            BtnText = "#FF1F2937"; BtnHover = "#14000000"; BtnBusy = "#33D97706";
            GroupStroke = "#26000000";
            RamColor = "#059669"; CpuColor = "#2563EB"; SsdColor = "#B45309";
            HddColor = "#E11D48"; GpuColor = "#7C3AED"; NetColor = "#0891B2";
            WarnColor = "#DC2626"; BattMidColor = "#EA580C";
        }

        // --- поверхности ---
        public static string Bg = "#FF181B22";          // фон панели и настроек
        public static string BgTop = "#FF1E222B";       // верх градиента карточки
        public static string BgBottom = "#FF161920";    // низ градиента карточки
        public static string CardStroke = "#1FFFFFFF";  // рамка карточки
        public static string Surface = "#FF252A33";     // плашки, меню
        public static string SurfaceHover = "#FF2E343F";// наведение в меню
        public static string SurfaceActive = "#40FFFFFF";// выбранный/включённый чип
        public static string Stroke = "#33FFFFFF";      // рамка плашек
        public static string StrokeStrong = "#1FFFFFFF";// рамка панели, настроек
        public static string StrokeSoft = "#1AFFFFFF";  // мягкая заливка кнопок

        // --- текст ---
        public static string TextHi = "#F2FFFFFF";
        public static string TextMid = "#8CFFFFFF";
        public static string TextLabel = "#A6FFFFFF";   // подписи строк карточки
        public static string BtnText = "#E6FFFFFF";     // глифы кнопок карточки
        public static string BtnHover = "#1FFFFFFF";
        public static string BtnBusy = "#33FBBF24";
        public static string GroupStroke = "#26FFFFFF";  // рамка строки карточки

        // --- цвета метрик (они же цветные точки на карточке) ---
        public static string RamColor = "#34D399";
        public static string CpuColor = "#5B9CFF";
        public static string SsdColor = "#FBBF24";
        public static string HddColor = "#FB7185";
        public static string GpuColor = "#C084FC";
        public static string NetColor = "#22D3EE";
        public static string WarnColor = "#FF6B6B";
        public static string BattMidColor = "#FB923C";   // заряд 49–20 %

        // --- значок в трее (RGB; прозрачность задаёт TrayIconFactory) ---
        public static string TrayBg = "#181B22";
        public static string TrayStroke = "#FFFFFF";
        public static string TrayHead = "#C9FFE9";
        public static string TrayBody = "#34D399";

        // --- типографика и геометрия ---
        // Одна гарнитура на всё приложение. Цифры — та же семья с табличными
        // цифрами (Typography.NumeralAlignment), отдельного моноширинного
        // шрифта больше нет. В меню трея — обычный Segoe UI: GDI+ не умеет
        // вариативные имена, а рисунок у них один.
        public static string FontUi = "Segoe UI Variable Text, Segoe UI";
        public static string FontNum = FontUi;
        public static string FontMenu = "Segoe UI";
        public static double SizeText = 11;     // подписи
        public static double SizeValue = 13;    // значения
        public static double RadiusCard = 8;
        public static double RadiusPill = 8;
        public static double BarRadius = 1.5;   // скругление шкалы
        public static double GroupRadius = 6;   // скругление рамки строки
        public static double ValueWidth = 36;   // колонка значения; точное значение задаёт Apply под шрифт темы
        public static double NetValueWidth = 72;

        // --- плотность карточки (DIP): все отступы в одном месте, чтобы
        // XAML и строки, созданные кодом (диски, периферия), не расходились
        public static double Gap = 4;                        // единый зазор: поля карточки, блоки, кнопки
        public static double CardPadX { get { return Gap; } }
        public static double CardPadY { get { return Gap; } }
        public static double GroupPadX = 3, GroupPadY = 1;   // внутри рамки строки
        public static double TrackGap = 4;      // шкала — подпись
        public static double LabelGap = 1;      // подпись — значение
        // Подпись (10.5) и значение (14) стоят на одной линии: у более мелкого шрифта
        // основание букв выше, чем у цифр, на 1,5 DIP (замер по отрисовке). Верхний
        // отступ подписи вдвое больше сдвига, потому что подпись центрируется по высоте.
        public static double LabelDrop = 3;
        public static double IconGap = 2;       // значок периферии — следующий
        public static double RowGap { get { return Gap; } }    // между строками в ряду
        public static double RowGapV { get { return Gap; } }   // между строками в столбике
        public static double BtnGap { get { return Gap; } }    // перед кнопками «⟳» и «⚙»
        public static double BtnW = 26, BtnH = 24;
        public static double FsLabel = 10.5, FsValue = 14;   // шрифты подписи и значения карточки

        /// <summary>
        /// Переключить палитру. Вызывается один раз при старте — до создания
        /// окон, меню и значка трея: они читают Theme в конструкторах.
        /// </summary>
        public static void Apply(bool neon, bool light)
        {
            Neon = neon;
            Light = light && !neon;
            if (!neon)
            {
                Bg = "#FF181B22"; BgTop = "#FF1E222B"; BgBottom = "#FF161920";
                CardStroke = "#1FFFFFFF";
                Surface = "#FF252A33"; SurfaceHover = "#FF2E343F"; SurfaceActive = "#40FFFFFF";
                Stroke = "#33FFFFFF"; StrokeStrong = "#1FFFFFFF"; StrokeSoft = "#1AFFFFFF";
                TextHi = "#F2FFFFFF"; TextMid = "#8CFFFFFF"; TextLabel = "#A6FFFFFF";
                BtnText = "#E6FFFFFF"; BtnHover = "#1FFFFFFF"; BtnBusy = "#33FBBF24";
                GroupStroke = "#26FFFFFF";
                RamColor = "#34D399"; CpuColor = "#5B9CFF"; SsdColor = "#FBBF24";
                HddColor = "#FB7185"; GpuColor = "#C084FC"; NetColor = "#22D3EE";
                WarnColor = "#FF6B6B"; BattMidColor = "#FB923C";
                TrayBg = "#181B22"; TrayStroke = "#FFFFFF"; TrayHead = "#C9FFE9"; TrayBody = "#34D399";
                FontUi = "Segoe UI Variable Text, Segoe UI"; FontNum = FontUi;
                RadiusCard = 8; RadiusPill = 8; BarRadius = 1.5; GroupRadius = 6;
                ValueWidth = 36; NetValueWidth = 72;   // «100%» шрифтом Segoe: 35,1 DIP
                if (light) ApplyLight();
                return;
            }

            // Неон-нуар: почти чёрные поверхности, циановые рамки и акценты,
            // моноширинные цифры, острые углы.
            Bg = "#FF050810"; BgTop = "#FF0B2434"; BgBottom = "#FF03060A";
            CardStroke = "#8000F0FF";
            Surface = "#FF0A1622"; SurfaceHover = "#FF122436"; SurfaceActive = "#3300F0FF";
            Stroke = "#5900E5FF"; StrokeStrong = "#5900F0FF"; StrokeSoft = "#1F00F0FF";
            TextHi = "#FFF2FDFF"; TextMid = "#FF7FA6B8"; TextLabel = "#E6CFF9FF";
            BtnText = "#FFE6FBFF"; BtnHover = "#2200F0FF"; BtnBusy = "#33FFC400";
            GroupStroke = "#5900F0FF";
            RamColor = "#FF4DFFA8"; CpuColor = "#FF00F0FF"; SsdColor = "#FFFFD000";
            HddColor = "#FFFF3D7F"; GpuColor = "#FFC77DFF"; NetColor = "#FF00FFE8";
            WarnColor = "#FFFF3355"; BattMidColor = "#FFFF9F0A";
            TrayBg = "#05070C"; TrayStroke = "#00F0FF"; TrayHead = "#D9FFFA"; TrayBody = "#00FFD5";
            FontUi = "Bahnschrift, Segoe UI Variable Text, Segoe UI";
            FontNum = "Consolas, Segoe UI";
            RadiusCard = 3; RadiusPill = 4; BarRadius = 0; GroupRadius = 3;
            ValueWidth = 31; NetValueWidth = 86;   // Consolas: «100%» занимает 30,8 DIP
        }

        /// <summary>
        /// Накладывает настройки пользователя (Prefs) поверх палитры темы:
        /// плотность отступов, масштаб шрифтов карточки, свои цвета метрик.
        /// Вызывается один раз при старте, сразу после Apply, — до создания окон.
        /// </summary>
        public static void ApplyPrefs()
        {
            switch (Prefs.Density)
            {
                // Один зазор Gap: от рамки карточки до блоков со всех четырёх сторон,
                // между блоками (в ряду и в столбике) и между кнопками.
                case 1:   // обычно
                    GroupPadX = 4; GroupPadY = 2;
                    TrackGap = 6; LabelGap = 2; IconGap = 3;
                    Gap = 6; BtnW = 28; BtnH = 26;
                    break;
                case 2:   // свободно
                    GroupPadX = 5; GroupPadY = 3;
                    TrackGap = 7; LabelGap = 3; IconGap = 4;
                    Gap = 8; BtnW = 30; BtnH = 28;
                    break;
                default:  // компактно
                    GroupPadX = 3; GroupPadY = 1;
                    TrackGap = 4; LabelGap = 1; IconGap = 2;
                    Gap = 4; BtnW = 26; BtnH = 24;
                    break;
            }

            // Шрифты карточки: подпись и значение растут вместе, а колонки значений
            // и сдвиг подписи к общей линии основания — пропорционально.
            double f = Prefs.FontScale;
            FsLabel = 10.5 * f;
            FsValue = 14 * f;
            ValueWidth = Math.Ceiling(ValueWidth * f);
            NetValueWidth = Math.Ceiling(NetValueWidth * f);
            LabelDrop = Math.Round(3 * f * 2) / 2;

            if (Prefs.AccRam.Length > 0) RamColor = Prefs.AccRam;
            if (Prefs.AccCpu.Length > 0) CpuColor = Prefs.AccCpu;
            if (Prefs.AccSsd.Length > 0) SsdColor = Prefs.AccSsd;
            if (Prefs.AccHdd.Length > 0) HddColor = Prefs.AccHdd;
            if (Prefs.AccGpu.Length > 0) GpuColor = Prefs.AccGpu;
            if (Prefs.AccNet.Length > 0) NetColor = Prefs.AccNet;
            if (Prefs.AccWarn.Length > 0) WarnColor = Prefs.AccWarn;
        }
        /// <summary>
        /// Прочитать «Theme» из state.json. Основной вид — неон: поле появилось
        /// позже остальных настроек, поэтому его отсутствие значит «неон», а не
        /// «тёмная». Тёмную тему можно вернуть, записав "Theme":"dark".
        /// </summary>
        public static bool NeonFromState(string stateFile)
        {
            try
            {
                if (!File.Exists(stateFile)) return true;
                string t = File.ReadAllText(stateFile);
                Match m = Regex.Match(t, "\"Theme\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
                if (!m.Success) return true;
                string v = m.Groups[1].Value;
                return !v.Equals("dark", StringComparison.OrdinalIgnoreCase)
                    && !v.Equals("light", StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        /// <summary>
        /// Тот же цвет с прозрачностью — фон шкалы под заполнением. Строка
        /// вида «#RRGGBB» превращается в «#AARRGGBB».
        /// </summary>
        public static string Fade(string hex, byte alpha)
        {
            string h = hex.TrimStart('#');
            // на входе бывает и «#RRGGBB», и «#AARRGGBB» — берём только цвет
            if (h.Length > 6) h = h.Substring(h.Length - 6);
            return "#" + alpha.ToString("X2") + h;
        }

        /// <summary>Коэффициент шрифтов окна настроек: базовые 10–11 DIP читались плохо.</summary>
        public const double SettingsFontScale = 1.15;

        /// <summary>Размер шрифта окна настроек: базовый размер × коэффициент, с шагом 0,5 DIP.</summary>
        public static double Fs(double size)
        {
            return Math.Round(size * SettingsFontScale * 2) / 2;
        }

        /// <summary>Кисть WPF по строке цвета; замораживаем — так быстрее рисуется.</summary>
        public static SolidColorBrush B(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        /// <summary>Цвет для WinForms (меню в трее).</summary>
        public static System.Drawing.Color C(string hex)
        {
            return System.Drawing.ColorTranslator.FromHtml(hex);
        }

        /// <summary>
        /// Плашка сообщения/подсказки — один вид на всё приложение:
        /// уведомления, подсказки к карточке и кнопке.
        /// </summary>
        public static Border Pill(out TextBlock text)
        {
            text = new TextBlock();
            text.Foreground = B(TextHi);
            text.FontFamily = new FontFamily(FontUi);
            text.FontSize = SizeText;
            text.LineHeight = 15;

            var border = new Border();
            border.Background = B(Surface);
            border.BorderBrush = B(Stroke);
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(RadiusPill);
            border.Padding = new Thickness(9, 5, 9, 5);
            border.Child = text;
            return border;
        }

        /// <summary>
        /// Плашка предупреждения: та же форма, отступы и шрифт, что у обычной
        /// плашки, но с красной точкой и красноватой рамкой — чтобы тревогу было
        /// видно сразу, не выбиваясь из общего вида.
        /// </summary>
        public static Border AlertPill(out TextBlock text)
        {
            text = new TextBlock();
            text.Foreground = B(TextHi);
            text.FontFamily = new FontFamily(FontUi);
            text.FontSize = SizeText;
            text.LineHeight = 15;
            text.VerticalAlignment = VerticalAlignment.Center;

            var dot = new Ellipse();
            dot.Width = 6;
            dot.Height = 6;
            dot.Fill = B(WarnColor);
            dot.VerticalAlignment = VerticalAlignment.Center;
            dot.Margin = new Thickness(0, 0, 7, 0);

            var row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Children.Add(dot);
            row.Children.Add(text);

            var border = new Border();
            border.Background = B(Surface);
            border.BorderBrush = B(Fade(WarnColor, 0x66));
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(RadiusPill);
            border.Padding = new Thickness(9, 5, 9, 5);
            border.Child = row;
            return border;
        }
    }
}
