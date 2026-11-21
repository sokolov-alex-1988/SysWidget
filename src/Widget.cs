using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfPath = System.Windows.Shapes.Path;

namespace SysWidget
{
    /// <summary>
    /// Карточка виджета: прозрачное окно без рамки, перетаскивание с зажимом по
    /// монитору, «посадка» на панель задач и всплывающая подсказка «освобождено».
    /// Все координаты — физические пиксели (Win32). Так Window.Left/Top в DIP и
    /// координаты мониторов не расходятся при разном масштабе на мониторах.
    /// </summary>
    public partial class Widget
    {
        /// <summary>Строки карточки слева направо; порядок важен для отступов.</summary>
        public enum CardRow { Ram = 0, Cpu = 1, Ssd = 2, Gpu = 3, Net = 4, Periph = 5 }

        const string CardXaml = @"
<Border xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
        BorderBrush=""@@CARD_STROKE@@"" BorderThickness=""1"" CornerRadius=""@@RADIUS_CARD@@"" Padding=""@@CARD_PAD@@"">
  <Border.Background>
    <LinearGradientBrush StartPoint=""0,0"" EndPoint=""0,1"">
      <GradientStop Color=""@@BG1@@"" Offset=""0""/>
      <GradientStop Color=""@@BG2@@"" Offset=""1""/>
    </LinearGradientBrush>
  </Border.Background>
  <StackPanel x:Name=""root"" Orientation=""Horizontal"" VerticalAlignment=""Center"">

    <Border x:Name=""grpRam"" CornerRadius=""@@GRP_RADIUS@@"" Background=""Transparent"" BorderBrush=""@@GRP_STROKE@@"" BorderThickness=""1"" Padding=""@@GRP_PAD@@"">
      <StackPanel Orientation=""Horizontal"">
        <Border Width=""3"" Height=""16"" CornerRadius=""@@BAR_RADIUS@@"" Background=""@@TRACK_RAM@@"" VerticalAlignment=""Center"" Margin=""0,0,@@TRACK_GAP@@,0"">
          <Border x:Name=""barRam"" Height=""16"" CornerRadius=""@@BAR_RADIUS@@"" Background=""@@RAM@@"" VerticalAlignment=""Bottom""/>
        </Border>
        <TextBlock x:Name=""lblRam"" Text=""ОЗУ"" FontFamily=""@@FONT_UI@@"" FontSize=""@@FS_LABEL@@"" Foreground=""@@TEXT_LABEL@@"" VerticalAlignment=""Center"" Margin=""0,@@LABEL_DROP@@,@@LABEL_GAP@@,0""/>
        <TextBlock x:Name=""valRam"" Text=""—"" FontFamily=""@@FONT_NUM@@"" FontSize=""@@FS_VALUE@@"" FontWeight=""SemiBold""
                   Typography.NumeralAlignment=""Tabular"" Foreground=""@@TEXT_HI@@"" Width=""@@VAL_W@@"" TextAlignment=""Right"" VerticalAlignment=""Center""/>
      </StackPanel>
    </Border>

    <Border x:Name=""grpCpu"" CornerRadius=""@@GRP_RADIUS@@"" Background=""Transparent"" BorderBrush=""@@GRP_STROKE@@"" BorderThickness=""1"" Padding=""@@GRP_PAD@@"">
      <StackPanel Orientation=""Horizontal"">
        <Border Width=""3"" Height=""16"" CornerRadius=""@@BAR_RADIUS@@"" Background=""@@TRACK_CPU@@"" VerticalAlignment=""Center"" Margin=""0,0,@@TRACK_GAP@@,0"">
          <Border x:Name=""barCpu"" Height=""16"" CornerRadius=""@@BAR_RADIUS@@"" Background=""@@CPU@@"" VerticalAlignment=""Bottom""/>
        </Border>
        <TextBlock x:Name=""lblCpu"" Text=""ЦП"" FontFamily=""@@FONT_UI@@"" FontSize=""@@FS_LABEL@@"" Foreground=""@@TEXT_LABEL@@"" VerticalAlignment=""Center"" Margin=""0,@@LABEL_DROP@@,@@LABEL_GAP@@,0""/>
        <TextBlock x:Name=""valCpu"" Text=""—"" FontFamily=""@@FONT_NUM@@"" FontSize=""@@FS_VALUE@@"" FontWeight=""SemiBold""
                   Typography.NumeralAlignment=""Tabular"" Foreground=""@@TEXT_HI@@"" Width=""@@VAL_W@@"" TextAlignment=""Right"" VerticalAlignment=""Center""/>
      </StackPanel>
    </Border>

    <Border x:Name=""grpGpu"" CornerRadius=""@@GRP_RADIUS@@"" Background=""Transparent"" BorderBrush=""@@GRP_STROKE@@"" BorderThickness=""1"" Padding=""@@GRP_PAD@@"">
      <StackPanel Orientation=""Horizontal"">
        <Border Width=""3"" Height=""16"" CornerRadius=""@@BAR_RADIUS@@"" Background=""@@TRACK_GPU@@"" VerticalAlignment=""Center"" Margin=""0,0,@@TRACK_GAP@@,0"">
          <Border x:Name=""barGpu"" Height=""16"" CornerRadius=""@@BAR_RADIUS@@"" Background=""@@GPU@@"" VerticalAlignment=""Bottom""/>
        </Border>
        <TextBlock x:Name=""lblGpu"" Text=""ГП"" FontFamily=""@@FONT_UI@@"" FontSize=""@@FS_LABEL@@"" Foreground=""@@TEXT_LABEL@@"" VerticalAlignment=""Center"" Margin=""0,@@LABEL_DROP@@,@@LABEL_GAP@@,0""/>
        <TextBlock x:Name=""valGpu"" Text=""—"" FontFamily=""@@FONT_NUM@@"" FontSize=""@@FS_VALUE@@"" FontWeight=""SemiBold""
                   Typography.NumeralAlignment=""Tabular"" Foreground=""@@TEXT_HI@@"" Width=""@@VAL_W@@"" TextAlignment=""Right"" VerticalAlignment=""Center""/>
      </StackPanel>
    </Border>

    <Border x:Name=""grpNet"" CornerRadius=""@@GRP_RADIUS@@"" Background=""Transparent"" BorderBrush=""@@GRP_STROKE@@"" BorderThickness=""1"" Padding=""@@GRP_PAD@@"">
      <StackPanel Orientation=""Horizontal"">
        <Border Width=""3"" Height=""16"" CornerRadius=""@@BAR_RADIUS@@"" Background=""@@TRACK_NET@@"" VerticalAlignment=""Center"" Margin=""0,0,@@TRACK_GAP@@,0"">
          <Border x:Name=""barNet"" Height=""16"" CornerRadius=""@@BAR_RADIUS@@"" Background=""@@NET@@"" VerticalAlignment=""Bottom""/>
        </Border>
        <TextBlock x:Name=""lblNet"" Text=""Сеть"" FontFamily=""@@FONT_UI@@"" FontSize=""@@FS_LABEL@@"" Foreground=""@@TEXT_LABEL@@"" VerticalAlignment=""Center"" Margin=""0,@@LABEL_DROP@@,@@LABEL_GAP@@,0""/>
        <TextBlock x:Name=""valNet"" Text=""—"" FontFamily=""@@FONT_NUM@@"" FontSize=""@@FS_VALUE@@"" FontWeight=""SemiBold""
                   Typography.NumeralAlignment=""Tabular"" Foreground=""@@TEXT_HI@@"" Width=""@@NET_W@@"" TextAlignment=""Right"" VerticalAlignment=""Center""/>
      </StackPanel>
    </Border>

    <Border x:Name=""btn"" Width=""@@BTN_W@@"" Height=""@@BTN_H@@"" CornerRadius=""@@GRP_RADIUS@@"" Background=""Transparent""
            BorderBrush=""@@GRP_STROKE@@"" BorderThickness=""1""
            Margin=""@@BTN_GAP@@,0,0,0"" Cursor=""Hand"">
      <TextBlock x:Name=""btnGlyph"" Text=""@@CLEAN_GLYPH@@"" FontFamily=""@@CLEAN_FONT@@"" FontSize=""14""
                 Foreground=""@@BTN_TEXT@@"" HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
    </Border>

    <Border x:Name=""btnDash"" Width=""@@BTN_W@@"" Height=""@@BTN_H@@"" CornerRadius=""@@GRP_RADIUS@@"" Background=""Transparent""
            BorderBrush=""@@GRP_STROKE@@"" BorderThickness=""1""
            Margin=""@@BTN_GAP@@,0,0,0"" Cursor=""Hand"">
      <TextBlock x:Name=""btnDashGlyph"" Text=""▦"" FontFamily=""Segoe UI Symbol"" FontSize=""13""
                 Foreground=""@@BTN_TEXT@@"" HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
    </Border>

    <Border x:Name=""btnCfg"" Width=""@@BTN_W@@"" Height=""@@BTN_H@@"" CornerRadius=""@@GRP_RADIUS@@"" Background=""Transparent""
            BorderBrush=""@@GRP_STROKE@@"" BorderThickness=""1""
            Margin=""@@BTN_GAP@@,0,0,0"" Cursor=""Hand"">
      <TextBlock x:Name=""btnCfgGlyph"" Text=""⚙"" FontFamily=""Segoe UI Symbol"" FontSize=""13""
                 Foreground=""@@BTN_TEXT@@"" HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
    </Border>

  </StackPanel>
</Border>
";

        static readonly IFormatProvider Inv = CultureInfo.InvariantCulture;

        // Значок кнопки очистки памяти — молния. В Windows 11 это глиф шрифта
        // Segoe Fluent Icons (того же рисунка, что и остальные значки системы);
        // там, где шрифта нет (Windows 10), — символ ⚡ из Segoe UI Symbol.
        static readonly bool HasFluentIcons = FontInstalled("Segoe Fluent Icons");
        static string CleanGlyph { get { return HasFluentIcons ? "\uE945" : "\u26A1"; } }
        static string CleanFont
        {
            get { return HasFluentIcons ? "Segoe Fluent Icons, Segoe UI" : "Segoe UI Symbol, Segoe UI"; }
        }

        static bool FontInstalled(string name)
        {
            try
            {
                foreach (FontFamily f in Fonts.SystemFontFamilies)
                    if (string.Equals(f.Source, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// XAML карточки с подставленной палитрой. Токены вроде @@RAM@@ живут
        /// в константе выше, чтобы тема менялась одним набором значений из
        /// Theme, а не правкой пятидесяти литералов.
        /// </summary>
        static string ThemedXaml()
        {
            return CardXaml
                .Replace("@@CARD_STROKE@@", Theme.CardStroke)
                .Replace("@@BG1@@", Theme.BgTop)
                .Replace("@@BG2@@", Theme.BgBottom)
                .Replace("@@TRACK_RAM@@", Theme.Fade(Theme.RamColor, 0x66))
                .Replace("@@TRACK_CPU@@", Theme.Fade(Theme.CpuColor, 0x66))
                .Replace("@@TRACK_GPU@@", Theme.Fade(Theme.GpuColor, 0x66))
                .Replace("@@TRACK_NET@@", Theme.Fade(Theme.NetColor, 0x66))
                .Replace("@@RAM@@", Theme.RamColor)
                .Replace("@@CPU@@", Theme.CpuColor)
                .Replace("@@GPU@@", Theme.GpuColor)
                .Replace("@@NET@@", Theme.NetColor)
                .Replace("@@TEXT_LABEL@@", Theme.TextLabel)
                .Replace("@@TEXT_HI@@", Theme.TextHi)
                .Replace("@@BTN_TEXT@@", Theme.BtnText)
                .Replace("@@FONT_UI@@", Theme.FontUi)
                .Replace("@@FONT_NUM@@", Theme.FontNum)
                .Replace("@@RADIUS_CARD@@", Theme.RadiusCard.ToString(Inv))
                .Replace("@@BAR_RADIUS@@", Theme.BarRadius.ToString(Inv))
                .Replace("@@GRP_RADIUS@@", Theme.GroupRadius.ToString(Inv))
                .Replace("@@GRP_STROKE@@", Theme.GroupStroke)
                .Replace("@@VAL_W@@", Theme.ValueWidth.ToString(Inv))
                .Replace("@@CARD_PAD@@", Theme.CardPadX.ToString(Inv) + "," + Theme.CardPadY.ToString(Inv))
                .Replace("@@GRP_PAD@@", Theme.GroupPadX.ToString(Inv) + "," + Theme.GroupPadY.ToString(Inv))
                .Replace("@@TRACK_GAP@@", Theme.TrackGap.ToString(Inv))
                .Replace("@@LABEL_GAP@@", Theme.LabelGap.ToString(Inv))
                .Replace("@@LABEL_DROP@@", Theme.LabelDrop.ToString(Inv))
                .Replace("@@FS_LABEL@@", Theme.FsLabel.ToString(Inv))
                .Replace("@@FS_VALUE@@", Theme.FsValue.ToString(Inv))
                .Replace("@@BTN_GAP@@", Theme.BtnGap.ToString(Inv))
                .Replace("@@BTN_W@@", Theme.BtnW.ToString(Inv))
                .Replace("@@BTN_H@@", Theme.BtnH.ToString(Inv))
                .Replace("@@CLEAN_GLYPH@@", CleanGlyph)
                .Replace("@@CLEAN_FONT@@", CleanFont)
                .Replace("@@NET_W@@", Theme.NetValueWidth.ToString(Inv));
        }

        readonly Sampler _sampler;
        readonly Program _host;
        readonly string _stateFile;
        readonly string _iconPath;

        Window _win;
        Border _card, _btn, _btnCfg, _btnDash;
        StackPanel _root;
        TextBlock _valRam, _valCpu, _valGpu, _valNet, _btnGlyph, _btnCfgGlyph;
        TextBlock _lblRam, _lblCpu, _lblGpu, _lblNet;
        TextBlock _btnTipText;
        TextBlock _cfgTipText, _dashTipText;
        Border _grpRam, _grpCpu, _grpGpu, _grpNet;
        // заполнение шкал: высота от 0 до 16 px — та же доля, что в процентах
        Border _barRam, _barCpu, _barGpu, _barNet;
        Border[] _barBlocks;

        // Диски: на каждый физический накопитель — своя группа на карточке,
        // своё значение и своя подсказка. Список собирается, когда приходит
        // Hardware (WMI читается в фоне), поэтому группы создаются не в XAML.
        readonly List<DiskInfo> _disks = new List<DiskInfo>();
        readonly List<Border> _diskGroups = new List<Border>();
        readonly List<Border> _diskBars = new List<Border>();
        readonly List<TextBlock> _diskLabels = new List<TextBlock>();
        readonly List<TextBlock> _diskVals = new List<TextBlock>();
        readonly List<ToolTip> _diskTips = new List<ToolTip>();
        readonly List<TextBlock> _diskTipTexts = new List<TextBlock>();
        bool[] _diskOn = new bool[0];
        string _diskSig = "";
        string _diskState = null;      // «0,1» из state.json: какие диски видны
        string _diskKnown = null;      // «0,1» — какие диски были в прошлый раз
        SysStats _lastStats;

        // Периферия: на каждое подключённое устройство — своя строка карточки,
        // как у дисков. Список приходит из фонового обхода (Program), поэтому
        // строки создаются по месту, а не в XAML.
        readonly List<Peripheral> _periph = new List<Peripheral>();
        readonly List<Border> _periphGroups = new List<Border>();
        readonly List<TextBlock> _periphVals = new List<TextBlock>();
        readonly List<ToolTip> _periphTips = new List<ToolTip>();
        readonly List<TextBlock> _periphTipTexts = new List<TextBlock>();
        string _periphSig = "";        // всё, включая заряд
        string _periphDevSig = "";     // только состав устройств

        // Скрытый владелец, который WPF заводит окну с ShowInTaskbar = false:
        // окно с владельцем не попадает на панель задач. Свой владелец (панель
        // задач) назначается вместо него, а не вместо «нуля» — иначе WPF-овский
        // владелец теряется и карточка выскакивает на панель задач.
        IntPtr _ownOwner;
        bool _ownOwnerKnown;

        // какие строки показывать на карточке: ОЗУ, ЦП, диски, ГП, сеть,
        // периферия (периферия — одна «строка» на каждое устройство)
        readonly bool[] _rowOn = { true, true, true, true, true, true };

        // раскладка карточки: порядок строк (индексы 0-4: ОЗУ, ЦП, диски, ГП,
        // сеть), вертикальный столбик вместо строки и выбранный масштаб.
        // Периферия в порядке не участвует: её строки всегда идут после метрик.
        readonly int[] _order = { 0, 1, 2, 3, 4 };
        bool _vertical;
        bool _trayAnim = true;
        double _userScale = 1.0;
        const double MinUserScale = 0.9;
        const double MaxUserScale = 1.25;

        // Высота шкалы: заполнение 100 % = 16 DIP. Строка с текстом 14 px
        // занимает по высоте ~19 DIP, поэтому шкала выше 10 px читается
        // лучше, но карточку не растягивает. В XAML те же 16 — держать
        // в согласии с этим значением.
        const double BarH = 16;

        // ширина колонки значений: проценты и строка сети со стрелками;
        // в неоновой теме цифры моноширинные и шире, поэтому значения берём
        // из палитры. 33 DIP — это «100 %» впритык: в строке проценты
        // центрируются, чтобы пустой хвост колонки не висел между подписью
        // и числом, а в столбике выравниваются вправо.
        static double ValueWidth { get { return Theme.ValueWidth; } }
        // Сеть — отдельная колонка: 72 DIP в тёмной теме и 86 в неоне
        // (Consolas шире); меньше нельзя — «↓100.0 ↑0.0» начнёт обрезаться.
        static double NetValueWidth { get { return Theme.NetValueWidth; } }

        ScaleTransform _scaleXf;
        Popup _toast;
        TextBlock _toastText;
        DateTime? _toastUntil;

        Popup _alert;
        TextBlock _alertText;
        DateTime? _alertUntil;

        SolidColorBrush _brNormal, _brWarn, _brBtnIdle, _brBtnHover, _brBtnBusy, _brDim;
        Brush _brNetVal;
        Brush[] _valBrushes;

        // плавная смена чисел: для каждой метрики — цель и текущее показанное
        // значение; таймер работает только пока числа едут
        TextBlock[] _valBlocks;
        double[] _animTarget;
        double[] _animShown;
        bool[] _animOn;
        int[] _lastPct;            // какой процент уже нарисован в блоке
        DispatcherTimer _animTimer;

        // Мельче этого значения (в процентах) число не анимируем, а ставим
        // сразу: при обновлении раз в секунду «набегание» на 0,8 % незаметно,
        // а каждый кадр — полная перерисовка прозрачного окна.
        const double AnimSnap = 1.5;

        // Масштаб карточки: 1 — обычный вид, меньше единицы — «посаженная» на
        // панель задач карточка. LayoutTransform меняет и размер окна при
        // SizeToContent, поэтому текст остаётся резким.
        double _scale = 1.0;
        double _naturalW, _naturalH;

        Point? _settleTo;          // отложенная физическая позиция
        bool _settleScheduled;
        Native.RECT? _pinBand;     // панель задач, с которой карточка «съехала» при смене раскладки
        bool _autoHidden;          // спрятана сама из-за полного экрана (не пользователем)
        bool _firstLoad = true;
        Point? _lastPx;            // последняя физическая позиция (для Show после Hide)

        // перетаскивание
        System.Drawing.Point? _dragFrom;
        Native.RECT _dragRect;
        bool _hasDragRect;

        bool _hasState;
        double _stateX, _stateY;

        public event Action MenuRequested;
        public event Action VisibilityChanged;
        public event Action SettingsRequested;
        /// <summary>Карточка сдвинулась (перетаскивание, «посадка» после смены размера).</summary>
        public event Action PositionChanged;
        /// <summary>Windows сообщила о подключении или отключении устройства.</summary>
        public event Action DeviceChanged;

        readonly Alerts _alerts = new Alerts();
        string _alertsFile;

        public Widget(Sampler sampler, Program host)
        {
            _sampler = sampler;
            _host = host;
            _stateFile = AppPaths.File_("state.json");
            _iconPath = Path.Combine(AppPaths.AppDir, "icon.ico");

            // язык читаем до сборки окна: подписи сразу строятся нужные
            Lang.Load(_stateFile);

            _alertsFile = AppPaths.File_("alerts.json");
            _alerts.Load(_alertsFile);
            _net = new NetInfo(AppPaths.File_("traffic.json"), AppPaths.File_("net.json"));
            PeriphLog.Load(AppPaths.File_("periph.json"));
            _net.Start();
            _alerts.Raised += OnAlert;
            _alerts.PeripheralLow += OnPeripheralLow;
            _alerts.DiskFreeLow += OnDiskFreeLow;

            BuildWindow();
            PositionChanged += delegate { RepositionPills(); };
        }

        public Window Win { get { return _win; } }
        public Alerts Alerts { get { return _alerts; } }

        NetInfo _net;

        /// <summary>Остановить фоновую службу сети и сохранить счётчики трафика.</summary>
        public void Shutdown()
        {
            try { if (_net != null) _net.Dispose(); } catch { }
            try { if (_dash != null) _dash.Close(); } catch { }
            SaveHistory();
        }

        // История нагрузки на диск (history.txt): раз в 5 минут и при выходе — график за сутки
        // переживает перезапуск. Читается при первом замере.
        bool _histLoaded;
        DateTime _histSaved = DateTime.UtcNow;

        void LoadHistory()
        {
            _histLoaded = true;
            try { _hist.Load(AppPaths.File_("history.txt")); }
            catch (Exception ex) { ErrorLog.Write("history load", ex); }
        }

        void SaveHistory()
        {
            _histSaved = DateTime.UtcNow;
            if (!_histLoaded) return;      // ничего не читали — перезаписать нечем
            try { _hist.Save(AppPaths.File_("history.txt")); }
            catch (Exception ex) { ErrorLog.Write("history save", ex); }
        }

        /// <summary>Перезапустить приложение (применить настройки, читаемые при старте).</summary>
        public void RestartApp()
        {
            _host.Restart();
        }

        /// <summary>Оповещения включены; переключатель живёт в меню и в alerts.json.</summary>
        public bool AlertsEnabled
        {
            get { return _alerts.Enabled; }
            set
            {
                if (_alerts.Enabled == value) return;
                _alerts.Enabled = value;
                if (!value)
                {
                    _alert.IsOpen = false;
                    _alertUntil = null;
                }
                _alerts.Save(_alertsFile);
            }
        }
        public bool IsVisible { get { return _win != null && _win.IsVisible; } }

        /// <summary>Текущий масштаб карточки: блок периферии подстраивается под него.</summary>
        public double Scale { get { return _scale; } }

        public IntPtr Handle
        {
            get
            {
                try { return new WindowInteropHelper(_win).Handle; }
                catch { return IntPtr.Zero; }
            }
        }

        public bool GetRect(out Native.RECT r)
        {
            r = new Native.RECT();
            IntPtr h = Handle;
            return h != IntPtr.Zero && Native.GetWindowRect(h, out r);
        }

        // ---------------------------------------------------------------- окно

        void BuildWindow()
        {
            _brNormal = Theme.B(Theme.TextHi);
            _brWarn = Theme.B(Theme.WarnColor);
            _brBtnIdle = Brushes.Transparent;
            _brBtnHover = Theme.B(Theme.BtnHover);
            _brBtnBusy = Theme.B(Theme.BtnBusy);
            _brDim = Theme.B(Theme.TextMid);
            // в неоне и пиксель-арте цифры красятся в цвет своей метрики; в тёмной и светлой — общий цвет
            _brNetVal = Theme.ColorByMetric ? Theme.B(Theme.NetColor) : _brNormal;

            _card = (Border)XamlReader.Parse(ThemedXaml());
            _scaleXf = new ScaleTransform(1, 1);
            _card.LayoutTransform = _scaleXf;

            _root = (StackPanel)_card.FindName("root");

            _valCpu = (TextBlock)_card.FindName("valCpu");
            _valRam = (TextBlock)_card.FindName("valRam");
            _valGpu = (TextBlock)_card.FindName("valGpu");
            _valNet = (TextBlock)_card.FindName("valNet");
            _lblRam = (TextBlock)_card.FindName("lblRam");
            _lblCpu = (TextBlock)_card.FindName("lblCpu");
            _lblGpu = (TextBlock)_card.FindName("lblGpu");
            _lblNet = (TextBlock)_card.FindName("lblNet");
            _grpRam = (Border)_card.FindName("grpRam");
            _grpCpu = (Border)_card.FindName("grpCpu");
            _grpGpu = (Border)_card.FindName("grpGpu");
            _grpNet = (Border)_card.FindName("grpNet");
            _barRam = (Border)_card.FindName("barRam");
            _barCpu = (Border)_card.FindName("barCpu");
            _barGpu = (Border)_card.FindName("barGpu");
            _barNet = (Border)_card.FindName("barNet");
            // неон и пиксель-арт: шкала из сегментов, а не сплошная полоса
            if (Theme.ColorByMetric)
            {
                _barRam.Background = BarFill(Theme.RamColor);
                _barCpu.Background = BarFill(Theme.CpuColor);
                _barGpu.Background = BarFill(Theme.GpuColor);
                _barNet.Background = BarFill(Theme.NetColor);
            }
            _btn = (Border)_card.FindName("btn");
            _btnGlyph = (TextBlock)_card.FindName("btnGlyph");
            _btnCfg = (Border)_card.FindName("btnCfg");
            _btnCfgGlyph = (TextBlock)_card.FindName("btnCfgGlyph");
            _btnDash = (Border)_card.FindName("btnDash");

            _animTimer = new DispatcherTimer();
            _animTimer.Interval = TimeSpan.FromMilliseconds(30);
            _animTimer.Tick += delegate { AnimTick(); };

            // диски нужны до BuildTips: у каждого своя группа и подсказка
            Hardware.WarmUp();
            BuildDiskGroups(Hardware.Current);

            BuildToast();
            BuildAlert();
            BuildTips();

            _win = new Window();
            _win.WindowStyle = WindowStyle.None;
            _win.AllowsTransparency = true;
            _win.Background = Brushes.Transparent;
            _win.ShowInTaskbar = false;
            _win.Title = Lang.S("Виджет системы", "System widget");
            _win.ResizeMode = ResizeMode.NoResize;
            _win.SizeToContent = SizeToContent.WidthAndHeight;
            _win.WindowStartupLocation = WindowStartupLocation.Manual;
            _win.Topmost = true;
            _win.Opacity = 0;          // покажемся только когда встанем на место
            _win.Content = _card;

            LoadState();
            ApplyLayout();
            if (_hasState) _win.Topmost = _stateTopmost;

            if (File.Exists(_iconPath))
            {
                try { _win.Icon = new BitmapImage(new Uri(_iconPath)); } catch { }
            }

            HookMouse();
            HookButton();
            HookGroups();

            _win.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                Action h = MenuRequested;
                if (h != null) h();
            };

            _win.Loaded += OnLoaded;
            _win.DpiChanged += OnDpiChanged;

            ApplyLanguage();
        }

        /// <summary>
        /// К чему прижимать плашку по горизонтали. Подсказки выравниваем по краям
        /// самой карточки, а не по элементу, на который навели: длинная подсказка
        /// кнопки иначе уезжала бы сильно правее панели.
        /// </summary>
        enum PillAlign { Left, Center, Right }

        /// <summary>
        /// Плашка сообщения. Один вид на всё: и уведомление «Освобождено …»,
        /// и подсказки при наведении на карточку и на кнопку очистки.
        /// </summary>
        Border MakePill(out TextBlock text)
        {
            return Theme.Pill(out text);
        }

        void BuildToast()
        {
            Border body = MakePill(out _toastText);

            _toast = new Popup();
            _toast.AllowsTransparency = true;
            _toast.StaysOpen = true;
            _toast.PlacementTarget = _card;
            _toast.Placement = PlacementMode.Top;
            _toast.VerticalOffset = -6;
            _toast.PopupAnimation = PopupAnimation.Fade;
            _toast.Child = body;
            _toast.Opened += delegate { FixPopupDeferred((Visual)_toast.Child, PillAlign.Right); };
        }

        /// <summary>
        /// Плашка тревоги. Геометрия та же, что у остальных плашек: над карточкой,
        /// прижата к её правому краю.
        /// </summary>
        void BuildAlert()
        {
            TextBlock text;
            Border body = Theme.AlertPill(out text);
            _alertText = text;

            _alert = new Popup();
            _alert.AllowsTransparency = true;
            _alert.StaysOpen = true;
            _alert.PlacementTarget = _card;
            _alert.Placement = PlacementMode.Top;
            _alert.VerticalOffset = -6;
            _alert.PopupAnimation = PopupAnimation.Fade;
            _alertBody = body;
            _alert.Child = body;
            _alert.Opened += delegate { FixPopupDeferred((Visual)_alert.Child, PillAlign.Right); };

            // Тревога по ЦП или ОЗУ кликабельна: открывает «топ процессов».
            body.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (!_alertKind.HasValue) return;
                ProcessPanel.Kind k = _alertKind.Value == AlertKind.Cpu
                    ? ProcessPanel.Kind.Cpu : ProcessPanel.Kind.Ram;
                _alert.IsOpen = false;
                _alertUntil = null;
                // уже открытую плашку того же вида не закрываем — человек хотел её видеть
                if (_procPopup == null || !_procPopup.IsOpen || _procKind != k) ToggleProcPanel(k);
            };
        }

        // Какая тревога сейчас на плашке (null — не по ЦП/ОЗУ, клик ничего не делает)
        AlertKind? _alertKind;
        Border _alertBody;

        void OnAlert(AlertKind kind, double value)
        {
            string msg;
            if (kind == AlertKind.Ram)
                msg = string.Format(Inv, Lang.S("ОЗУ {0:0}% · память почти заполнена",
                                                "RAM {0:0}% · memory almost full"), value);
            else if (kind == AlertKind.Cpu)
                msg = string.Format(Inv, Lang.S("ЦП {0:0}% · процессор перегружен",
                                                "CPU {0:0}% · processor overloaded"), value);
            else if (kind == AlertKind.Disk)
                msg = string.Format(Inv, Lang.S("Диски {0:0}% · накопитель перегружен",
                                                "Disks {0:0}% · drive overloaded"), value);
            else
                msg = string.Format(Inv, Lang.S("ГП {0:0}% · видеокарта перегружена",
                                                "GPU {0:0}% · graphics card overloaded"), value);

            bool clickable = kind == AlertKind.Cpu || kind == AlertKind.Ram;
            if (clickable)
                msg += Lang.S(" · клик — что грузит", " · click for top processes");

            ShowAlert(msg);
            if (clickable && _win.IsVisible)
            {
                _alertKind = kind;
                _alertBody.Cursor = Cursors.Hand;
                // времени на то, чтобы навести курсор, нужно чуть больше
                if (_alertUntil.HasValue) _alertUntil = DateTime.Now.AddSeconds(8);
            }
        }

        /// <summary>Заряд устройства ушёл ниже порога из alerts.json.</summary>
        void OnPeripheralLow(Peripheral p)
        {
            string msg = string.Format(Inv,
                Lang.S("{0} {1}% · низкий заряд", "{0} {1}% · low battery"),
                p.KindName, p.Battery);
            ShowAlert(msg);
        }

        /// <summary>На томе почти не осталось свободного места.</summary>
        void OnDiskFreeLow(string drive, double freeGB, double pct)
        {
            string msg = string.Format(Inv,
                Lang.S("Диск {0} · свободно {1:0.#} ГБ ({2:0}%)",
                       "Disk {0} · {1:0.#} GB free ({2:0}%)"),
                drive, freeGB, pct);
            ShowAlert(msg);
        }

        /// <summary>
        /// Показать тревогу. Виджет на экране — плашка над карточкой в общем стиле;
        /// виджет свёрнут в трей — уведомление от значка, иначе его просто не увидят.
        /// </summary>
        public void ShowAlert(string msg)
        {
            // прямой вызов — тревога не по ЦП/ОЗУ; OnAlert потом выставит вид сам
            _alertKind = null;
            if (_alertBody != null) _alertBody.ClearValue(FrameworkElement.CursorProperty);

            if (!_win.IsVisible)
            {
                // карточка скрыта: уведомление от значка — но не поверх игры,
                // презентации или режима фокусировки
                if (!_autoHidden && !Native.UserBusy()) _host.ShowBalloon(msg);
                return;
            }

            _toast.IsOpen = false;        // две плашки одновременно не нужны
            _toastUntil = null;

            _alertText.Text = msg;
            _alert.IsOpen = true;
            _alertUntil = DateTime.Now.AddSeconds(5);
        }

        /// <summary>Нейтральная плашка об итоге действия — не тревога, без красного.</summary>
        void ShowNote(string msg)
        {
            if (!_win.IsVisible)
            {
                _host.ShowBalloon(msg);
                return;
            }
            _alert.IsOpen = false;
            _alertUntil = null;
            _toastText.Text = msg;
            _toast.IsOpen = true;
            _toastUntil = DateTime.Now.AddSeconds(3);
        }

        ToolTip _cardTip;
        TextBlock _cardTipText;
        StackPanel[] _groupHeads;      // шапка плашки по слоту: текст + график + строка «за N мин»
        TextBlock[] _groupTipTexts;

        static readonly double Gb = 1073741824.0;

        /// <summary>
        /// Подсказки делаем объектом, а не строкой в ToolTip. Стандартная подсказка
        /// открывается у курсора — то есть прямо в полосе панели задач, и панель
        /// (она тоже topmost) её закрывает. Показываем над карточкой, а окно
        /// подсказки делаем owned от карточки: owned-окно всегда выше владельца.
        /// Оформление — та же плашка, что у уведомления об очистке памяти.
        /// </summary>
        void BuildTips()
        {
            _cardTip = MakeTip(_card, PillAlign.Left);
            _cardTip.Content = MakePill(out _cardTipText);
            _cardTipText.Text = CardHint();
            _card.ToolTip = _cardTip;
            // Общая подсказка карточки показывается и над строками, у которых своей подсказки
            // больше нет (вся информация — в плашке по клику): над строками её гасим,
            // остаётся только лёгкая подсветка при наведении
            _card.ToolTipOpening += delegate(object s, ToolTipEventArgs e)
            {
                DependencyObject o = Mouse.DirectlyOver as DependencyObject;
                while (o != null && !ReferenceEquals(o, _card))
                {
                    var b = o as Border;
                    if (b != null && (ReferenceEquals(b, _grpRam) || ReferenceEquals(b, _grpCpu)
                        || ReferenceEquals(b, _grpGpu) || ReferenceEquals(b, _grpNet)
                        || _diskGroups.Contains(b) || _periphGroups.Contains(b)))
                    {
                        e.Handled = true;
                        return;
                    }
                    o = o is Visual || o is System.Windows.Media.Media3D.Visual3D
                        ? System.Windows.Media.VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o);
                }
            };

            // у каждой метрики своя подсказка: при наведении на группу видно
            // именно её, а не общую сводку по всей карточке
            BuildMetricTips();

            TextBlock btnText;
            ToolTip btnTip = MakeTip(_btn, PillAlign.Right);
            btnTip.Content = MakePill(out btnText);
            _btnTipText = btnText;
            btnText.Text = Lang.S("Освободить оперативную память", "Free up RAM");
            _btn.ToolTip = btnTip;

            TextBlock cfgText;
            ToolTip cfgTip = MakeTip(_btnCfg, PillAlign.Right);
            cfgTip.Content = MakePill(out cfgText);
            _cfgTipText = cfgText;
            cfgText.Text = Lang.S("Настройки", "Settings");
            _btnCfg.ToolTip = cfgTip;

            TextBlock dashText;
            ToolTip dashTip = MakeTip(_btnDash, PillAlign.Right);
            dashTip.Content = MakePill(out dashText);
            _dashTipText = dashText;
            dashText.Text = Lang.S("Окно мониторинга", "Monitor window");
            _btnDash.ToolTip = dashTip;
        }

        /// <summary>
        /// Подсказки метрик в порядке слотов: 0 — ОЗУ, 1 — ЦП, дальше по одному
        /// на диск, последний — ГП. Так индексы совпадают с _valBlocks.
        /// </summary>
        void BuildMetricTips()
        {
            int n = 4 + _diskGroups.Count;
            _groupHeads = new StackPanel[n];
            _groupTipTexts = new TextBlock[n];
            _groupSparks = new Sparkline[n];
            _groupSparkNotes = new TextBlock[n];

            MakeMetricTip(0, _grpRam, Theme.RamColor);
            MakeMetricTip(1, _grpCpu, Theme.CpuColor);
            for (int d = 0; d < _diskGroups.Count; d++)
                MakeMetricTip(2 + d, _diskGroups[d], Theme.SsdColor);
            MakeMetricTip(GpuSlot, _grpGpu, Theme.GpuColor);
            MakeMetricTip(NetSlot, _grpNet, Theme.NetColor);
        }

        // История замеров для мини-графиков в подсказках метрик
        readonly History _hist = new History();
        SysStats _histLast;
        Sparkline[] _groupSparks;
        TextBlock[] _groupSparkNotes;

        void MakeMetricTip(int slot, Border target, string color)
        {
            if (target == null) return;
            TextBlock text;
            Border pill = MakePill(out text);

            // Подсказка при наведении больше не показывается: тот же текст, график и строка
            // «за N мин: ср … · макс …» теперь шапка плашки по клику (ProcessPanel.SetHeader).
            // При наведении строка лишь слегка подсвечивается (HookGroup).
            pill.Child = null;
            var sp = new StackPanel();
            sp.Children.Add(text);

            var spark = new Sparkline(color);
            spark.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(spark);

            var note = new TextBlock();
            note.Foreground = Theme.B(Theme.TextMid);
            note.FontFamily = new FontFamily(Theme.FontUi);
            note.FontSize = Theme.SizeText - 1;
            note.Margin = new Thickness(0, 3, 0, 0);
            note.Visibility = Visibility.Collapsed;
            sp.Children.Add(note);

            text.Text = "…";
            _groupHeads[slot] = sp;
            _groupTipTexts[slot] = text;
            _groupSparks[slot] = spark;
            _groupSparkNotes[slot] = note;
        }

        /// <summary>Ключ ряда истории для слота подсказки.</summary>
        int SparkKey(int slot)
        {
            if (slot == 0) return History.Ram;
            if (slot == 1) return History.Cpu;
            if (slot == GpuSlot) return History.Gpu;
            if (slot == NetSlot) return History.Net;
            int d = slot - 2;
            if (d >= 0 && d < _disks.Count && _disks[d].Index >= 0)
                return History.DiskKey(_disks[d].Index);
            return History.DiskAll;
        }

        void RefreshSpark(int slot)
        {
            if (_groupSparks == null || slot < 0 || slot >= _groupSparks.Length) return;
            Sparkline spark = _groupSparks[slot];
            if (spark == null) return;

            int key = SparkKey(slot);
            bool net = key == History.Net;

            double[] recent = _hist.Series(key, Sparkline.Window);
            double max = net ? Math.Max(0.1, History.Max(recent) * 1.15) : 100.0;
            spark.Set(recent, max);

            TextBlock note = _groupSparkNotes[slot];
            if (note == null) return;
            double[] all = _hist.Series(key, History.Capacity);
            if (all.Length < 30) { note.Visibility = Visibility.Collapsed; return; }

            int mins = (int)Math.Ceiling(all.Length / 60.0);
            double avg = History.Avg(all), peak = History.Max(all);
            string t = net
                ? string.Format(Inv, Lang.S("за {0} мин: ср {1:0.0} · макс {2:0.0} МБ/с",
                                            "last {0} min: avg {1:0.0} · max {2:0.0} MB/s"), mins, avg, peak)
                : string.Format(Inv, Lang.S("за {0} мин: ср {1:0}% · макс {2:0}%",
                                            "last {0} min: avg {1:0}% · max {2:0}%"), mins, avg, peak);
            if (note.Text != t) note.Text = t;
            note.Visibility = Visibility.Visible;
        }

        int GpuSlot { get { return 2 + _diskGroups.Count; } }
        int NetSlot { get { return 3 + _diskGroups.Count; } }

        // --------------------------------------------------------------- диски

        /// <summary>
        /// Создать по группе на каждый физический диск. Пока Hardware не
        /// прочитан, стоит одна запасная группа «Диск» — её потом заменит
        /// EnsureDisks, когда фоновая загрузка закончится.
        /// </summary>
        void BuildDiskGroups(Hardware h)
        {
            var oldOn = new Dictionary<int, bool>();
            for (int i = 0; i < _disks.Count && i < _diskOn.Length; i++)
                oldOn[_disks[i].Index] = _diskOn[i];

            _disks.Clear();
            if (h != null && h.Disks != null && h.Disks.Count > 0)
            {
                _disks.AddRange(h.Disks);
            }
            else
            {
                var fb = new DiskInfo();
                fb.Index = -1;
                fb.Model = h == null ? "" : h.DiskModel;
                _disks.Add(fb);
            }

            var on = new bool[_disks.Count];
            bool any = false;
            bool hasState = !string.IsNullOrEmpty(_diskState);
            string mask = hasState ? "," + _diskState + "," : "";
            // какие диски были в прошлый раз: диск, появившийся позже, не был
            // «выключен» пользователем — показываем его сразу
            string known = !string.IsNullOrEmpty(_diskKnown) ? "," + _diskKnown + "," : "";
            for (int i = 0; i < _disks.Count; i++)
            {
                bool saved;
                if (oldOn.TryGetValue(_disks[i].Index, out saved))
                    on[i] = saved;
                else if (known.Length > 0)
                    on[i] = known.IndexOf("," + _disks[i].Index + ",", StringComparison.Ordinal) < 0 ||
                            mask.IndexOf("," + _disks[i].Index + ",", StringComparison.Ordinal) >= 0;
                else
                    on[i] = !hasState ||
                            mask.IndexOf("," + _disks[i].Index + ",", StringComparison.Ordinal) >= 0;
                if (on[i]) any = true;
            }
            if (!any) for (int i = 0; i < on.Length; i++) on[i] = true;
            _diskOn = on;

            _diskGroups.Clear();
            _diskLabels.Clear();
            _diskVals.Clear();
            _diskBars.Clear();
            for (int i = 0; i < _disks.Count; i++)
            {
                TextBlock label, value;
                Border bar;
                Border g = MakeDiskGroup(_disks[i], out label, out value, out bar);
                _diskGroups.Add(g);
                _diskLabels.Add(label);
                _diskVals.Add(value);
                _diskBars.Add(bar);
            }

            BuildValBlocks();
            HookDiskGroups();
            UpdateDiskLabels(null);
        }

        Border MakeDiskGroup(DiskInfo d, out TextBlock label, out TextBlock value, out Border bar)
        {
            var g = new Border();
            g.CornerRadius = new CornerRadius(Theme.GroupRadius);
            g.Background = Brushes.Transparent;
            g.BorderBrush = Theme.B(Theme.GroupStroke);
            g.BorderThickness = new Thickness(1);
            g.Padding = new Thickness(Theme.GroupPadX, Theme.GroupPadY, Theme.GroupPadX, Theme.GroupPadY);

            var row = new StackPanel();
            row.Orientation = Orientation.Horizontal;

            // шкала: полупрозрачный фон во всю высоту, поверх — заполнение
            // по загрузке диска (16 px = 100 %)
            var track = new Border();
            track.Width = 3;
            track.Height = BarH;
            track.CornerRadius = new CornerRadius(Theme.BarRadius);
            track.Background = Theme.B(Theme.Fade(DiskAccent(d), 0x66));
            track.VerticalAlignment = VerticalAlignment.Center;
            track.Margin = new Thickness(0, 0, Theme.TrackGap, 0);

            bar = new Border();
            bar.Height = BarH;
            bar.CornerRadius = new CornerRadius(Theme.BarRadius);
            bar.Background = BarFill(DiskAccent(d));
            bar.VerticalAlignment = VerticalAlignment.Bottom;
            track.Child = bar;
            row.Children.Add(track);

            label = new TextBlock();
            label.Text = DiskKindLabel(d);
            label.FontFamily = new FontFamily(Theme.FontUi);
            label.FontSize = Theme.FsLabel;
            label.Foreground = Theme.B(Theme.TextLabel);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, Theme.LabelDrop, Theme.LabelGap, 0);
            row.Children.Add(label);

            value = new TextBlock();
            value.Text = "—";
            value.FontFamily = new FontFamily(Theme.FontNum);
            value.FontSize = Theme.FsValue;
            value.FontWeight = FontWeights.SemiBold;
            Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
            value.Foreground = Theme.B(Theme.TextHi);
            value.Width = ValueWidth;
            value.TextAlignment = TextAlignment.Right;
            value.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(value);

            g.Child = row;
            return g;
        }

        static string DiskAccent(DiskInfo d)
        {
            if (d != null && d.Kind == "HDD") return Theme.HddColor;
            if (d != null && d.Kind == "SSD") return Theme.SsdColor;
            return Theme.TextMid;
        }

        static string DiskKindLabel(DiskInfo d)
        {
            if (d != null && d.Kind == "HDD") return "HDD";
            if (d != null && d.Kind == "SSD") return "SSD";
            return Lang.Disk;
        }

        /// <summary>Подписи дисков: при двух и более дисках добавляем букву тома.</summary>
        void UpdateDiskLabels(SysStats s)
        {
            bool many = _disks.Count > 1;
            for (int i = 0; i < _disks.Count && i < _diskLabels.Count; i++)
            {
                string text = DiskKindLabel(_disks[i]);
                DiskStat st = FindDisk(s, _disks[i].Index);
                string letter = many && st != null ? FirstLetter(st.Letters) : "";
                if (letter.Length > 0) text += " " + letter;
                if (_diskLabels[i].Text != text) _diskLabels[i].Text = text;
            }
        }

        static DiskStat FindDisk(SysStats s, int index)
        {
            if (s == null || s.Disks == null) return null;
            for (int i = 0; i < s.Disks.Count; i++)
                if (s.Disks[i].Index == index) return s.Disks[i];
            return null;
        }

        static string FirstLetter(string letters)
        {
            if (string.IsNullOrEmpty(letters)) return "";
            int sp = letters.IndexOf(' ');
            return sp < 0 ? letters : letters.Substring(0, sp);
        }

        /// <summary>
        /// Если набор дисков изменился (пришло Hardware), пересобрать группы,
        /// подсказки и раскладку. Пока данных нет — ничего не трогаем.
        /// </summary>
        void EnsureDisks()
        {
            Hardware h = Hardware.Current;
            if (h == null) { _diskSig = "none"; return; }

            string sig = h.Disks == null || h.Disks.Count == 0 ? "empty" : "";
            if (h.Disks != null)
                for (int i = 0; i < h.Disks.Count; i++)
                    sig += h.Disks[i].Index + "|" + h.Disks[i].Model + "|" + h.Disks[i].Kind + ";";

            if (sig == _diskSig) return;
            _diskSig = sig;

            BuildDiskGroups(h);
            BuildMetricTips();
            ApplyLayout();
        }

        // ------------------------------------------------------------ периферия

        /// <summary>
        /// Новый список подключённых устройств с фонового обхода. Вызывать на
        /// UI-потоке. Строки пересобираются, только когда меняется состав
        /// устройств; смена заряда обновляет текст на месте — иначе карточка
        /// раз в минуту перекладывалась бы и дёргалась.
        /// </summary>
        public void SetPeripherals(List<Peripheral> list)
        {
            if (list == null) list = new List<Peripheral>();
            _alerts.UpdatePeripherals(list);
            PeriphLog.Observe(list);

            string sig = "", dev = "";
            foreach (Peripheral p in list)
            {
                sig += p.Key + "|" + p.Kind + "|" + p.HasKeyboard + "|" + p.Name
                     + "|" + p.Manufacturer + "|" + p.Link + "|" + p.Battery + ";";
                dev += p.Key + "|" + p.Kind + "|" + p.HasKeyboard + "|" + p.Name
                     + "|" + p.Link + ";";
            }
            if (sig == _periphSig) return;
            _periphSig = sig;

            _periph.Clear();
            _periph.AddRange(list);

            if (dev != _periphDevSig)
            {
                _periphDevSig = dev;
                BuildPeriphGroups();
                ApplyLayout();
            }
            else
            {
                UpdatePeriphValues();
            }
        }

        void BuildPeriphGroups()
        {
            _periphGroups.Clear();
            _periphVals.Clear();
            _periphTips.Clear();
            _periphTipTexts.Clear();

            for (int i = 0; i < _periph.Count; i++)
            {
                TextBlock value, tipText;
                ToolTip tip;
                Border g = MakePeriphGroup(_periph[i], out value, out tip, out tipText);
                _periphGroups.Add(g);
                _periphVals.Add(value);
                _periphTips.Add(tip);
                _periphTipTexts.Add(tipText);
            }

            for (int i = 0; i < _periphGroups.Count; i++)
            {
                HookGroup(_periphGroups[i]);
                // у периферии нет отдельного пункта в настройках — скрывается
                // вся строка периферии, как чипом «Периф.»
                HookRowMenu(_periphGroups[i], delegate { HideRow(CardRow.Periph); });
            }
            UpdatePeriphValues();
        }

        /// <summary>
        /// Строка устройства — та же рамка, что у метрик, но без модели: только
        /// значок типа (мышь, клавиатура, наушники, колонка) и заряд. Модель,
        /// способ подключения и производитель — в подсказке при наведении.
        /// </summary>
        Border MakePeriphGroup(Peripheral p, out TextBlock value,
            out ToolTip tip, out TextBlock tipText)
        {
            var g = new Border();
            g.CornerRadius = new CornerRadius(Theme.GroupRadius);
            g.Background = Brushes.Transparent;
            g.BorderBrush = Theme.B(Theme.GroupStroke);
            g.BorderThickness = new Thickness(1);
            g.Padding = new Thickness(Theme.GroupPadX, Theme.GroupPadY, Theme.GroupPadX, Theme.GroupPadY);

            var row = new StackPanel();
            row.Orientation = Orientation.Horizontal;

            FrameworkElement icon = KindIcon(p.Kind);
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, Theme.IconGap, 0);
            row.Children.Add(icon);

            // общий USB-приёмник 2,4 ГГц: за ним и мышь, и клавиатура —
            // показываем оба значка, иначе клавиатура «терялась»
            if (p.HasKeyboard && p.Kind == PeriphKind.Mouse)
            {
                FrameworkElement kbd = KindIcon(PeriphKind.Keyboard);
                kbd.VerticalAlignment = VerticalAlignment.Center;
                kbd.Margin = new Thickness(0, 0, Theme.IconGap, 0);
                row.Children.Add(kbd);
            }

            value = new TextBlock();
            value.FontFamily = new FontFamily(Theme.FontNum);
            value.FontSize = Theme.FsValue;
            value.FontWeight = FontWeights.SemiBold;
            Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
            value.Foreground = Theme.B(Theme.TextHi);
            value.Width = ValueWidth;
            value.TextAlignment = TextAlignment.Right;
            value.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(value);

            g.Child = row;

            // подсказки при наведении нет: всё — в плашке по клику (ProcessPanel, Kind.Periph);
            // tip и tipText остаются для совместимости с UpdatePeriphValues
            tip = MakeTip(g, PillAlign.Left);
            Border pill = MakePill(out tipText);
            tip.Content = pill;

            return g;
        }

        /// <summary>Заряд, «USB» или «BT» в колонке значения; цвета — как у метрик.</summary>
        void UpdatePeriphValues()
        {
            for (int i = 0; i < _periph.Count && i < _periphVals.Count; i++)
            {
                Peripheral p = _periph[i];
                TextBlock v = _periphVals[i];
                string text;
                string color;
                if (p.Battery >= 0)
                {
                    text = p.Battery.ToString(Inv) + "%";
                    // 100–50 % — зелёный, 49–20 % — оранжевый, 19–0 % — красный
                    color = p.Battery >= 50 ? Theme.RamColor
                          : p.Battery >= 20 ? Theme.BattMidColor : Theme.WarnColor;
                }
                else
                {
                    text = p.LinkShort;
                    color = Theme.TextMid;
                }
                if (v.Text != text) v.Text = text;
                v.Foreground = Theme.B(color);

                if (i < _periphTipTexts.Count) _periphTipTexts[i].Text = PeriphTipText(p);
            }
        }

        static string PeriphTipText(Peripheral p)
        {
            string t = p.Name;
            if (!string.Equals(p.Name, p.KindName, StringComparison.OrdinalIgnoreCase))
                t = p.KindName + " · " + p.Name;

            if (p.Manufacturer.Length > 0
                && !string.Equals(p.Manufacturer, p.Name, StringComparison.OrdinalIgnoreCase))
                t += "\n" + p.Manufacturer;

            t += "\n" + p.LinkFull;
            if (p.Battery >= 0)
                t += " · " + p.Battery.ToString(Inv) + "%";
            else if (p.Link != PeriphLink.Usb)
                t += " · " + Lang.S("заряд неизвестен", "battery unknown");
            return t;
        }

        /// <summary>
        /// Значок типа устройства рисуем сами: в системных шрифтах нет
        /// монохромных мыши/клавиатуры/наушников, а эмодзи выбиваются из темы.
        /// </summary>
        static FrameworkElement KindIcon(PeriphKind kind)
        {
            var canvas = new Canvas();
            canvas.Width = 14;
            canvas.Height = 14;

            Brush stroke = Theme.B(Theme.TextLabel);
            const double w = 1.1;

            if (kind == PeriphKind.Mouse)
            {
                canvas.Children.Add(Stroke(
                    "M1,4 A3,3 0 0 1 4,1 H6 A3,3 0 0 1 9,4 V9 A4,4 0 0 1 1,9 Z", stroke, w));
                canvas.Children.Add(Stroke("M5,1.5 V4.5", stroke, w));
            }
            else if (kind == PeriphKind.Keyboard)
            {
                canvas.Children.Add(Stroke(
                    "M2,4.5 H12 A1.5,1.5 0 0 1 13.5,6 V9 A1.5,1.5 0 0 1 12,10.5 " +
                    "H2 A1.5,1.5 0 0 1 0.5,9 V6 A1.5,1.5 0 0 1 2,4.5 Z", stroke, w));
                canvas.Children.Add(Stroke(
                    "M2.5,7 H4 M5,7 H6.5 M7.5,7 H9 M10,7 H11.5 M3.5,9 H10.5", stroke, 0.9));
            }
            else if (kind == PeriphKind.Speaker)
            {
                var body = new WpfPath();
                body.Data = Geometry.Parse("M1,5 H4 L7.5,1.5 V12.5 L4,9 H1 Z");
                body.Fill = stroke;
                canvas.Children.Add(body);
                canvas.Children.Add(Stroke("M9.5,4.5 A3.5,3.5 0 0 1 9.5,9.5", stroke, w));
            }
            else
            {
                // наушники (в том числе для «просто аудио»)
                canvas.Children.Add(Stroke("M1.5,9 V7 A5,5 0 0 1 11.5,7 V9", stroke, w));
                canvas.Children.Add(Stroke("M1,8.5 H3 V12 H1 Z M10,8.5 H12 V12 H10 Z", stroke, w));
            }
            return canvas;
        }

        static WpfPath Stroke(string data, Brush brush, double thickness)
        {
            var p = new WpfPath();
            p.Data = Geometry.Parse(data);
            p.Stroke = brush;
            p.StrokeThickness = thickness;
            p.StrokeStartLineCap = PenLineCap.Round;
            p.StrokeEndLineCap = PenLineCap.Round;
            p.StrokeLineJoin = PenLineJoin.Round;
            p.Fill = Brushes.Transparent;
            return p;
        }

        /// <summary>Пересобрать массивы анимации под текущее число дисков.</summary>
        void BuildValBlocks()
        {
            if (_animTimer != null) _animTimer.Stop();

            int n = 4 + _diskVals.Count;
            var blocks = new TextBlock[n];
            blocks[0] = _valRam;
            blocks[1] = _valCpu;
            for (int i = 0; i < _diskVals.Count; i++) blocks[2 + i] = _diskVals[i];
            blocks[n - 2] = _valGpu;
            blocks[n - 1] = _valNet;

            _valBlocks = blocks;

            // шкалы идут теми же слотами, что и значения
            var bars = new Border[n];
            bars[0] = _barRam;
            bars[1] = _barCpu;
            for (int i = 0; i < _diskBars.Count && 2 + i < n; i++) bars[2 + i] = _diskBars[i];
            bars[n - 2] = _barGpu;
            bars[n - 1] = _barNet;
            _barBlocks = bars;

            // цвет цифр по метрике — только в неоне и пиксель-арте
            var vbrushes = new Brush[n];
            for (int i = 0; i < n; i++) vbrushes[i] = _brNormal;
            if (Theme.ColorByMetric)
            {
                vbrushes[0] = Theme.B(Theme.RamColor);
                vbrushes[1] = Theme.B(Theme.CpuColor);
                for (int i = 0; i < _diskVals.Count && 2 + i < n; i++)
                    vbrushes[2 + i] = Theme.B(DiskAccent(i < _disks.Count ? _disks[i] : null));
                vbrushes[n - 2] = Theme.B(Theme.GpuColor);
                vbrushes[n - 1] = Theme.B(Theme.NetColor);
            }
            _valBrushes = vbrushes;

            _animTarget = new double[n];
            _animShown = new double[n];
            _animOn = new bool[n];
            _lastPct = new int[n];
            for (int i = 0; i < n; i++) { _animShown[i] = -1; _lastPct[i] = int.MinValue; }
        }

        string CardHint()
        {
            return Lang.S("Правый клик — меню",
                          "Right-click — menu");
        }

        // ------------------------------------------------ подсказки по метрикам

        void UpdateGroupTips(SysStats s)
        {
            SetTip(0, RamTip(s));
            SetTip(1, CpuTip(s));
            for (int d = 0; d < _diskGroups.Count; d++)
                SetTip(2 + d, DiskTip(s, d));
            SetTip(GpuSlot, GpuTip(s));
            SetTip(NetSlot, NetTip(s));

            // раз в секунду обновляем график открытой плашки (текст шапки обновлён выше);
            // ширина плашки меняется вместе с текстом — прижимаем её к левому краю карточки
            if (_procPopup != null && _procPopup.IsOpen && _procSlot >= 0)
            {
                RefreshSpark(_procSlot);
                FixPopupDeferred((Visual)_procPopup.Child, PillAlign.Left);
            }
        }

        void SetTip(int i, string text)
        {
            if (_groupTipTexts == null || i < 0 || i >= _groupTipTexts.Length) return;
            TextBlock t = _groupTipTexts[i];
            if (t != null && t.Text != text) t.Text = text;
        }

        /// <summary>ОЗУ: занято/всего, кэш и подтверждённая память, подозрения на утечку, слоты.</summary>
        string RamTip(SysStats s)
        {
            string line = string.Format(Inv, Lang.S("ОЗУ · занято {0:0.0} из {1:0.0} ГБ",
                                                    "RAM · used {0:0.0} of {1:0.0} GB"),
                                        s.RamUsedGB, s.RamTotalGB);
            if (s.CommitLimitGB > 0)
                line += "\n" + string.Format(Inv,
                    Lang.S("Кэш файлов {0:0.0} ГБ (освободится при нехватке)\nВыделено программам {1:0.0} из {2:0.0} ГБ (предел с файлом подкачки)",
                           "File cache {0:0.0} GB (freed when needed)\nAllocated to apps {1:0.0} of {2:0.0} GB (limit incl. page file)"),
                    s.CacheGB, s.CommitUsedGB, s.CommitLimitGB);

            List<MemoryGrowth> grow = MemoryWatch.Suspects();
            for (int i = 0; i < grow.Count && i < 2; i++)
                line += "\n" + string.Format(Inv,
                    Lang.S("↗ растёт: {0} +{1:0} МБ за {2:0} мин", "↗ growing: {0} +{1:0} MB in {2:0} min"),
                    grow[i].Name, grow[i].GrowthMb, grow[i].Minutes);

            Hardware h = Hardware.Current;
            if (h == null) return line;

            string spec = JoinParts(h.RamMaker, h.RamPart);
            if (h.RamSpeedMHz > 0)
            {
                string speed = h.RamType.Length > 0
                    ? h.RamType + "-" + h.RamSpeedMHz.ToString(Inv)
                    : h.RamSpeedMHz.ToString(Inv) + " " + Lang.S("МГц", "MHz");
                spec = JoinParts(spec, speed);
            }
            else if (h.RamType.Length > 0)
            {
                spec = JoinParts(spec, h.RamType);
            }
            return spec.Length > 0 ? line + "\n" + spec : line;
        }

        /// <summary>ЦП: загрузка, текущая частота, модель и ядра.</summary>
        string CpuTip(SysStats s)
        {
            Hardware h = Hardware.Current;
            string clock = "";
            if (h != null && h.CpuMaxMHz > 0 && s.CpuPerf > 1)
            {
                double ghz = h.CpuMaxMHz * s.CpuPerf / 100000.0;
                clock = string.Format(Inv, Lang.S(" · {0:0.00} ГГц", " · {0:0.00} GHz"), ghz);
            }
            string line = string.Format(Inv, Lang.S("ЦП · {0:0}%{1}", "CPU · {0:0}%{1}"),
                                        s.Cpu, clock);
            // загрузка по ядрам — столбиками под списком плашки, здесь не дублируем
            if (s.CpuPowerW >= 0)
                line += "\n" + string.Format(Inv, Lang.S("Потребление {0:0.0} Вт", "Power {0:0.0} W"), s.CpuPowerW)
                      + (s.CpuCoresPowerW >= 0 ? string.Format(Inv, Lang.S(" (ядра {0:0.0} Вт)", " (cores {0:0.0} W)"), s.CpuCoresPowerW) : "");
            if (s.Cpu >= 60 && s.CpuPerf > 1 && s.CpuPerf < 60)
                line += "\n" + Lang.S("⚠ частота ниже номинала при нагрузке (перегрев или энергосбережение?)",
                                      "⚠ clock below base under load (overheating or power saver?)");
            if (h == null || h.CpuName.Length == 0) return line;

            string spec = h.CpuName;
            if (h.CpuCores > 0 && h.CpuThreads > 0)
                spec += string.Format(Inv, Lang.S(" · ядра: {0} · потоки: {1}",
                                                  " · cores: {0} · threads: {1}"),
                                      h.CpuCores, h.CpuThreads);
            if (h.CpuMaxMHz > 0)
                spec += string.Format(Inv, Lang.S(" · база {0:0.00} ГГц", " · base {0:0.00} GHz"),
                                      h.CpuMaxMHz / 1000.0);
            return line + "\n" + spec;
        }

        /// <summary>
        /// Диск: занятость, текущее чтение/запись, тома с местом и модель.
        /// У каждого физического диска своя подсказка и свои счётчики.
        /// </summary>
        string DiskTip(SysStats s, int di)
        {
            DiskInfo d = _disks[di];
            DiskStat st = FindDisk(s, d.Index);
            string kind = DiskKindLabel(d);

            // железо ещё не прочитано: показываем общую сводку, как раньше
            if (d.Index < 0)
            {
                string line0 = string.Format(Inv,
                    Lang.S("{0} · {1:0}% · чтение {2:0.0} · запись {3:0.0} МБ/с",
                           "{0} · {1:0}% · read {2:0.0} · write {3:0.0} MB/s"),
                    kind, s.DiskPct, s.DiskReadMBs, s.DiskWriteMBs);
                Hardware h0 = Hardware.Current;
                string vol0 = "";
                if (h0 != null && h0.VolumeSize > 0)
                    vol0 = string.Format(Inv, Lang.S("том {0} · {1:0} ГБ · свободно {2:0} ГБ",
                                                     "volume {0} · {1:0} GB · {2:0} GB free"),
                                         h0.SystemDrive, h0.VolumeSize / Gb, h0.VolumeFree / Gb);
                string spec0 = JoinParts(h0 == null ? "" : h0.DiskModel, vol0);
                return spec0.Length > 0 ? line0 + "\n" + spec0 : line0;
            }

            string line = st == null
                ? kind
                : string.Format(Inv, Lang.S("{0} · {1:0}% · чтение {2:0.0} · запись {3:0.0} МБ/с",
                                            "{0} · {1:0}% · read {2:0.0} · write {3:0.0} MB/s"),
                                kind, st.Pct, st.ReadMBs, st.WriteMBs);

            string spec = JoinParts(d.Model, VolumeText(st));
            return spec.Length > 0 ? line + "\n" + spec : line;
        }

        /// <summary>Тома диска: буквы, общий объём и свободное место.</summary>
        static string VolumeText(DiskStat st)
        {
            if (st == null || st.Letters.Length == 0) return "";

            long total = 0, free = 0;
            string letters = "";
            string[] parts = st.Letters.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                string letter = parts[i].Trim();
                if (letter.Length == 0) continue;
                try
                {
                    var di = new DriveInfo(letter);
                    if (!di.IsReady || di.TotalSize <= 0) continue;
                    total += di.TotalSize;
                    free += di.TotalFreeSpace;
                    letters = JoinParts(letters, letter);
                }
                catch { }
            }
            if (total <= 0) return "";
            return string.Format(Inv, Lang.S("том {0} · {1:0} ГБ · свободно {2:0} ГБ",
                                            "volume {0} · {1:0} GB · {2:0} GB free"),
                                 letters, total / Gb, free / Gb);
        }

        /// <summary>ГП: загрузка, видеопамять с остатком и модель адаптера.</summary>
        string GpuTip(SysStats s)
        {
            if (!s.HasGpu)
                return Lang.S("ГП · счётчики Windows недоступны",
                              "GPU · Windows counters unavailable");

            Hardware h = Hardware.Current;
            double total = h == null ? 0 : h.GpuVramGB;
            // у дискретной карты важна выделенная память, у встроенной — общая
            double used = s.VramUsedGB >= 0.05 ? s.VramUsedGB : s.VramSharedGB;

            string mem;
            if (total > 0.05 && used <= total + 0.5)
            {
                double free = Math.Max(0, total - used);
                mem = string.Format(Inv, Lang.S("видеопамять {0:0.0} из {1:0.0} ГБ · свободно {2:0.0} ГБ",
                                                "VRAM {0:0.0} of {1:0.0} GB · {2:0.0} GB free"),
                                    used, total, free);
            }
            else
            {
                mem = string.Format(Inv, Lang.S("видеопамять занято {0:0.0} ГБ",
                                                "VRAM used {0:0.0} GB"), used);
            }
            string line = string.Format(Inv, Lang.S("ГП · {0:0}% · {1}", "GPU · {0:0}% · {1}"),
                                        s.Gpu, mem);
            if (h == null || h.GpuName.Length == 0) return line;

            string name = h.GpuName;
            if (h.GpuMaker.Length > 0 &&
                name.IndexOf(h.GpuMaker, StringComparison.OrdinalIgnoreCase) < 0)
                name = h.GpuMaker + " " + name;
            return line + "\n" + name;
        }

        static string JoinParts(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? "";
            if (string.IsNullOrEmpty(b)) return a;
            return a + " · " + b;
        }

        /// <summary>
        /// Сеть: суммарные скорости и разбивка по активным интерфейсам.
        /// </summary>
        string NetTip(SysStats s)
        {
            if (s == null) return Lang.Net;

            string line = string.Format(Inv,
                Lang.S("{0} · ↓ {1:0.0} · ↑ {2:0.0} МБ/с",
                       "{0} · ↓ {1:0.0} · ↑ {2:0.0} MB/s"),
                Lang.Net, s.NetRxMBs, s.NetTxMBs);

            NetSnapshot n = _net == null ? null : _net.Current;

            // Фоновая служба ещё не ответила: оставляем прежний вид — только имя адаптера
            if (n == null || (n.Kind.Length == 0 && n.DayRx + n.DayTx <= 0))
            {
                if (s.Nets.Count == 0) return line + "\n" + Lang.S("нет активности", "idle");
                return line + "\n" + ShortName(s.Nets[0].Name);
            }

            var sb = new System.Text.StringBuilder(line);

            // к чему подключены
            if (n.Kind.Length == 0)
            {
                sb.Append('\n').Append(Lang.S("нет подключения", "not connected"));
            }
            else
            {
                string conn = n.Kind;
                if (n.Ssid.Length > 0) conn += " «" + ShortName(n.Ssid) + "»";
                else if (n.Name.Length > 0 && n.Kind != "Wi-Fi") conn += " · " + ShortName(n.Name);
                if (n.SpeedBps > 0) conn += " · " + FormatLink(n.SpeedBps);
                sb.Append('\n').Append(conn);

                // задержка до шлюза или заданного узла
                if (n.PingMs >= 0)
                {
                    sb.Append('\n').Append(string.Format(Inv,
                        Lang.S("Пинг {0:0} мс · потери {1}%", "Ping {0:0} ms · loss {1}%"),
                        n.PingMs, Math.Max(0, n.Loss)));
                }
                else if (n.Loss >= 0)
                {
                    sb.Append('\n').Append(Lang.S("Нет ответа на пинг", "No ping reply"));
                }
            }

            // трафик за день и месяц
            sb.Append('\n').Append(string.Format(Inv,
                Lang.S("Сегодня ↓ {0} ↑ {1}", "Today ↓ {0} ↑ {1}"),
                FormatBytes(n.DayRx), FormatBytes(n.DayTx)));
            sb.Append('\n').Append(string.Format(Inv,
                Lang.S("Месяц ↓ {0} ↑ {1}", "Month ↓ {0} ↑ {1}"),
                FormatBytes(n.MonRx), FormatBytes(n.MonTx)));
            // лимит трафика — полосой и строкой в плашке под списком, здесь не дублируем
            return sb.ToString();
        }

        /// <summary>
        /// Предупреждение о лимите трафика за месяц: при 80% и при 100%, по одному разу в месяц
        /// (что уже показано — запоминает NetInfo в traffic.json, чтобы перезапуск не повторял).
        /// </summary>
        void CheckTrafficLimit()
        {
            if (Prefs.TrafficLimitGB <= 0 || _net == null) return;
            NetSnapshot n = _net.Current;
            double limit = Prefs.TrafficLimitGB * 1073741824.0;
            double pct = (n.MonRx + n.MonTx) / limit * 100.0;
            int level = pct >= 100 ? 100 : pct >= 80 ? 80 : 0;
            if (level == 0) return;

            string month = DateTime.Now.ToString("yyyy-MM", Inv);
            int shown = 0;
            string w = _net.Warned;
            if (w.StartsWith(month + ":", StringComparison.Ordinal))
                int.TryParse(w.Substring(month.Length + 1), out shown);
            if (level <= shown) return;

            _net.Warned = month + ":" + level;
            ShowAlert(string.Format(Inv,
                Lang.S("Трафик за месяц {0} из {1} ГБ ({2:0}%)", "Monthly traffic {0} of {1} GB ({2:0}%)"),
                FormatBytes(n.MonRx + n.MonTx), Prefs.TrafficLimitGB, pct));
        }

        string FormatBytes(double b)
        {
            double gb = b / 1073741824.0;
            if (gb >= 100) return gb.ToString("0", Inv) + Lang.S(" ГБ", " GB");
            if (gb >= 1) return gb.ToString("0.0", Inv) + Lang.S(" ГБ", " GB");
            return (b / 1048576.0).ToString("0", Inv) + Lang.S(" МБ", " MB");
        }

        string FormatLink(long bps)
        {
            if (bps >= 1000000000L) return (bps / 1e9).ToString("0.#", Inv) + Lang.S(" Гбит/с", " Gbit/s");
            return (bps / 1e6).ToString("0", Inv) + Lang.S(" Мбит/с", " Mbit/s");
        }

        static string ShortName(string s)
        {
            if (s == null) return "";
            s = s.Trim();
            return s.Length > 42 ? s.Substring(0, 41) + "…" : s;
        }

        /// <summary>
        /// Подписи карточки по текущему языку. Вызывается при сборке окна и при
        /// переключении языка в настройках; подсказка карточки пересоберётся на
        /// ближайшем тике Update.
        /// </summary>
        public void ApplyLanguage()
        {
            if (_win != null) _win.Title = Lang.S("Виджет системы", "System widget");
            if (_lblRam != null) _lblRam.Text = Lang.Ram;
            if (_lblCpu != null) _lblCpu.Text = Lang.Cpu;
            if (_lblGpu != null) _lblGpu.Text = Lang.Gpu;
            if (_lblNet != null) _lblNet.Text = Lang.Net;
            UpdateDiskLabels(_lastStats);
            if (_btnTipText != null)
                _btnTipText.Text = Lang.S("Освободить оперативную память", "Free up RAM");
            if (_cfgTipText != null)
                _cfgTipText.Text = Lang.S("Настройки", "Settings");
            if (_dashTipText != null)
                _dashTipText.Text = Lang.S("Окно мониторинга", "Monitor window");
            if (_cardTipText != null) _cardTipText.Text = CardHint();
        }

        /// <summary>Переключить язык интерфейса и запомнить выбор.</summary>
        public void SetLanguage(bool en)
        {
            Lang.Set(en);
            ApplyLanguage();
            SaveState();
            SaveLang();
        }

        /// <summary>
        /// Записать выбранный язык, даже если карточка сейчас скрыта (SaveState
        /// в этом случае не может снять прямоугольник окна и ничего не пишет).
        /// </summary>
        void SaveLang()
        {
            try
            {
                if (!File.Exists(_stateFile)) return;
                string t = File.ReadAllText(_stateFile);
                string lang = "\"Lang\":\"" + (Lang.En ? "en" : "ru") + "\"";
                if (Regex.IsMatch(t, "\"Lang\"\\s*:\\s*\"(ru|en)\""))
                    t = Regex.Replace(t, "\"Lang\"\\s*:\\s*\"(ru|en)\"", lang);
                else
                    t = t.TrimEnd().TrimEnd('}').TrimEnd() + "," + lang + "}";
                AtomicFile.WriteAllText(_stateFile, t);
            }
            catch (Exception ex) { ErrorLog.Write("save lang", ex); }
        }

        /// <summary>
        /// Голый ToolTip: своя команда рисует плашку, системную рамку (Aero2
        /// подкладывает под неё серую «тень») выкидываем шаблоном-заглушкой.
        /// </summary>
        ToolTip MakeTip(FrameworkElement target, PillAlign align)
        {
            var tip = new ToolTip();
            tip.PlacementTarget = target;
            tip.Placement = PlacementMode.Top;
            tip.VerticalOffset = -6;
            tip.HasDropShadow = false;

            var tpl = new ControlTemplate(typeof(ToolTip));
            tpl.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
            tip.Template = tpl;

            tip.Opened += delegate { FixPopupDeferred(tip, align); };
            return tip;
        }

        /// <summary>
        /// Плашки не должны отставать от карточки: при перетаскивании WPF
        /// пересчитывает положение всплывающего окна сам и ставит его по
        /// собственному выравниванию — возвращаем наше.
        /// </summary>
        void RepositionPills()
        {
            if (_toast != null && _toast.IsOpen) FixPopupDeferred((Visual)_toast.Child, PillAlign.Right);
            if (_alert != null && _alert.IsOpen) FixPopupDeferred((Visual)_alert.Child, PillAlign.Right);
        }

        /// <summary>
        /// Разместить плашку и повторить на следующем кадре. При первом показе
        /// окно всплывающего элемента ещё не имеет окончательной ширины —
        /// выравнивание по правому краю посчиталось бы по старому размеру и
        /// плашка уехала бы влево.
        /// </summary>
        void FixPopupDeferred(Visual popupVisual, PillAlign align)
        {
            FixPopup(popupVisual, align);

            var el = popupVisual as FrameworkElement;
            if (el == null) return;
            el.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
            {
                FixPopup(popupVisual, align);
            });
        }

        /// <summary>
        /// Всплывающее окно подсказки/сообщения: владелец — карточка, при включённом
        /// «поверх всех окон» поднимаем в topmost-слой. По горизонтали прижимаем к
        /// левому/правому краю карточки, по вертикали — зазор над карточкой считаем
        /// сами: WPF для разных целей (карточка/кнопка) ставит плашку по-разному, и
        /// подсказка кнопки наезжала на панель. Если сверху места нет — под карточку.
        /// </summary>
        void FixPopup(Visual popupVisual, PillAlign align)
        {
            try
            {
                var src = PresentationSource.FromVisual(popupVisual) as HwndSource;
                if (src == null) return;
                IntPtr popup = src.Handle;
                IntPtr card = Handle;
                if (popup == IntPtr.Zero || card == IntPtr.Zero) return;

                Native.SetOwner(popup, card);
                if (_win.Topmost) Native.RaiseTopmost(popup);

                Native.RECT pr, cr;
                if (!Native.GetWindowRect(popup, out pr)) return;
                if (!Native.GetWindowRect(card, out cr)) return;

                // зазор 6 DIP, как у стандартной подсказки WPF, — но одинаковый везде
                uint dpi = Native.GetDpiForWindow(card);
                if (dpi == 0) dpi = 96;
                int gap = (int)Math.Round(6.0 * dpi / 96.0);

                int x;
                if (align == PillAlign.Left) x = cr.Left;
                else if (align == PillAlign.Right) x = cr.Right - pr.Width;
                else x = (cr.Left + cr.Right - pr.Width) / 2;

                int y = cr.Top - pr.Height - gap;

                var bd = System.Windows.Forms.Screen.FromPoint(
                    new System.Drawing.Point((cr.Left + cr.Right) / 2, (cr.Top + cr.Bottom) / 2)).Bounds;
                if (y < bd.Top) y = cr.Bottom + gap;

                // плашка шире карточки (или карточка у края экрана) — не выпускаем за монитор
                if (x + pr.Width > bd.Right) x = bd.Right - pr.Width;
                if (x < bd.Left) x = bd.Left;

                if (x != pr.Left || y != pr.Top) Native.MoveTo(popup, x, y);
            }
            catch { }
        }

        static SolidColorBrush Brush(string hex)
        {
            return Theme.B(hex);
        }

        // ------------------------------------------------ состояние (state.json)

        bool _stateTopmost = true;

        void LoadState()
        {
            try
            {
                if (!File.Exists(_stateFile)) return;
                string t = File.ReadAllText(_stateFile);

                Match l = Regex.Match(t, "\"Left\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
                Match tp = Regex.Match(t, "\"Top\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
                if (l.Success && tp.Success)
                {
                    double x, y;
                    if (double.TryParse(l.Groups[1].Value, NumberStyles.Float, Inv, out x) &&
                        double.TryParse(tp.Groups[1].Value, NumberStyles.Float, Inv, out y) &&
                        !double.IsNaN(x) && !double.IsNaN(y))
                    {
                        _stateX = x;
                        _stateY = y;
                        _hasState = true;
                    }
                }

                Match tm = Regex.Match(t, "\"Topmost\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                if (tm.Success)
                {
                    _stateTopmost = tm.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }

                // «Rows»:"ram,cpu,ssd,gpu" — какие строки показывать; нет поля — все
                Match rm = Regex.Match(t, "\"Rows\"\\s*:\\s*\"([^\"]*)\"");
                if (rm.Success)
                {
                    string v = "," + rm.Groups[1].Value.ToLowerInvariant() + ",";
                    string[] keys = { "ram", "cpu", "ssd", "gpu", "net", "periph" };
                    bool any = false;
                    for (int i = 0; i < keys.Length; i++)
                    {
                        _rowOn[i] = v.IndexOf("," + keys[i] + ",", StringComparison.Ordinal) >= 0;
                        if (_rowOn[i]) any = true;
                    }
                    // пустой список оставил бы карточку совсем без метрик
                    if (!any) for (int i = 0; i < keys.Length; i++) _rowOn[i] = true;
                }

                // «V»:2 — версия схемы state. У состояния без неё сети ещё не было:
                // включаем новую строку один раз, дальше решает пользователь.
                if (!Regex.IsMatch(t, "\"V\"\\s*:\\s*\\d+"))
                    _rowOn[(int)CardRow.Net] = true;

                // До версии 3 периферия была отдельным окном, и её видимость
                // хранилась полем «Periph». Переводим его в строку карточки.
                if (!Regex.IsMatch(t, "\"V\"\\s*:\\s*[3-9]\\b"))
                {
                    bool legacy = true;
                    Match pm = Regex.Match(t, "\"Periph\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                    if (pm.Success)
                        legacy = pm.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    _rowOn[(int)CardRow.Periph] = legacy;
                }

                // «Order»:"cpu,ram,ssd,gpu" — порядок строк; пропущенные дополняем
                Match om = Regex.Match(t, "\"Order\"\\s*:\\s*\"([^\"]*)\"");
                if (om.Success) ParseOrder(om.Groups[1].Value);

                // «Disks»:"0,1" — какие физические диски видны на карточке
                Match dm = Regex.Match(t, "\"Disks\"\\s*:\\s*\"([^\"]*)\"");
                if (dm.Success) _diskState = dm.Groups[1].Value.ToLowerInvariant();

                // «DisksAll»:"0,1" — какие диски существовали при прошлом запуске
                Match km = Regex.Match(t, "\"DisksAll\"\\s*:\\s*\"([^\"]*)\"");
                if (km.Success) _diskKnown = km.Groups[1].Value.ToLowerInvariant();

                // «Vertical»:true — карточка столбиком
                Match vm = Regex.Match(t, "\"Vertical\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                if (vm.Success)
                    _vertical = vm.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);

                // «TrayAnim»:false — значок в трее без анимации (слабые машины)
                Match an = Regex.Match(t, "\"TrayAnim\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                if (an.Success)
                    _trayAnim = an.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);

                // «Scale»:1.1 — выбранный масштаб карточки
                Match sm = Regex.Match(t, "\"Scale\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
                if (sm.Success)
                {
                    double s;
                    if (double.TryParse(sm.Groups[1].Value, NumberStyles.Float, Inv, out s) &&
                        s >= MinUserScale - 0.001 && s <= MaxUserScale + 0.001)
                        _userScale = s;
                }
            }
            catch { }
        }

        /// <summary>
        /// Разобрать порядок строк: известные ключи — по порядку, повторные и
        /// неизвестные отбрасываем, недостающие дополняем обычным порядком.
        /// </summary>
        void ParseOrder(string list)
        {
            string[] keys = { "ram", "cpu", "ssd", "gpu", "net" };
            string[] parts = list.ToLowerInvariant().Split(',');
            bool[] used = new bool[keys.Length];
            int n = 0;
            for (int i = 0; i < parts.Length && n < keys.Length; i++)
            {
                int k = Array.IndexOf(keys, parts[i].Trim());
                if (k < 0 || used[k]) continue;
                used[k] = true;
                _order[n++] = k;
            }
            for (int k = 0; k < keys.Length && n < keys.Length; k++)
                if (!used[k]) _order[n++] = k;
        }

        public void SaveState()
        {
            Native.RECT r;
            if (!GetRect(out r)) return;
            try
            {
                AtomicFile.WriteAllText(_stateFile, string.Format(Inv,
                    "{{\"V\":3,\"Left\":{0},\"Top\":{1},\"Topmost\":{2},\"Rows\":\"{3}\"," +
                    "\"Order\":\"{4}\",\"Vertical\":{5},\"Scale\":{6},\"Disks\":\"{7}\"," +
                    "\"DisksAll\":\"{8}\",\"Lang\":\"{9}\",\"TrayAnim\":{10},\"Theme\":\"{11}\"}}",
                    r.Left, r.Top, _win.Topmost ? "true" : "false", RowMask(),
                    OrderMask(), _vertical ? "true" : "false",
                    _userScale.ToString("0.##", Inv), DiskMask(), DiskAllMask(),
                    Lang.En ? "en" : "ru", _trayAnim ? "true" : "false",
                    Theme.Neon ? "neon" : (Theme.Retro ? "retro" : (Theme.Terminal ? "terminal" : (Theme.Light ? "light" : "dark")))));
            }
            catch (Exception ex) { ErrorLog.Write("save state", ex); }
        }

        /// <summary>Список видимых строк для state.json, в порядке карточки.</summary>
        string RowMask()
        {
            string[] keys = { "ram", "cpu", "ssd", "gpu", "net", "periph" };
            string res = "";
            for (int i = 0; i < keys.Length; i++)
            {
                if (!_rowOn[i]) continue;
                if (res.Length > 0) res += ",";
                res += keys[i];
            }
            return res;
        }

        /// <summary>Порядок строк для state.json — все четыре ключа.</summary>
        string OrderMask()
        {
            string[] keys = { "ram", "cpu", "ssd", "gpu", "net" };
            string res = "";
            for (int k = 0; k < _order.Length; k++)
            {
                if (res.Length > 0) res += ",";
                res += keys[_order[k]];
            }
            return res;
        }

        /// <summary>Видимые физические диски для state.json — их номера.</summary>
        string DiskMask()
        {
            string res = "";
            for (int i = 0; i < _disks.Count && i < _diskOn.Length; i++)
            {
                if (!_diskOn[i]) continue;
                if (res.Length > 0) res += ",";
                res += _disks[i].Index.ToString(Inv);
            }
            return res;
        }

        /// <summary>Все найденные диски для state.json: по этому списку новый
        /// накопитель отличается от выключенного пользователем.</summary>
        string DiskAllMask()
        {
            string res = "";
            for (int i = 0; i < _disks.Count; i++)
            {
                if (res.Length > 0) res += ",";
                res += _disks[i].Index.ToString(Inv);
            }
            return res;
        }

        /// <summary>
        /// Переставить строки карточки под текущую раскладку: порядок, ориентация,
        /// отступы и масштаб. Скрытые строки остаются в дереве (Collapsed), чтобы
        /// настройка «показать строку» не теряла элемент.
        /// </summary>
        void ApplyLayout()
        {
            if (_root == null) return;

            // запоминаем панель, на которой стоит карточка: после пересчёта
            // размера (столбик стал выше/ниже) её центр может выйти за полосу
            // панели, и FitTaskbar решит, что она «вне панели»
            _pinBand = null;
            try
            {
                Native.RECT r0;
                if (GetRect(out r0)) _pinBand = GetTaskbarBand(r0);
            }
            catch { }

            Border[] groups = { _grpRam, _grpCpu, null, _grpGpu, _grpNet };
            _root.Orientation = _vertical ? Orientation.Vertical : Orientation.Horizontal;

            // сначала собираем ряды метрик в нужном порядке; каждая строка —
            // отдельная рамка, поэтому разделители между ними больше не нужны
            var rows = new List<FrameworkElement>();
            for (int k = 0; k < _order.Length; k++)
            {
                int r = _order[k];
                if (r == 2)
                {
                    // слот дисков — все группы подряд, по одной на накопитель
                    for (int d = 0; d < _diskGroups.Count; d++)
                        rows.Add(_diskGroups[d]);
                }
                else
                {
                    Border g = groups[r];
                    if (g != null) rows.Add(g);
                }
            }

            // периферия идёт после метрик: в порядке строк она не участвует —
            // устройств бывает разное число, и крутить их вместе с ОЗУ неудобно
            for (int d = 0; d < _periphGroups.Count; d++)
                rows.Add(_periphGroups[d]);

            _root.Children.Clear();
            for (int i = 0; i < rows.Count; i++)
                _root.Children.Add(rows[i]);
            _root.Children.Add(_btn);
            if (_btnDash != null) _root.Children.Add(_btnDash);
            if (_btnCfg != null) _root.Children.Add(_btnCfg);

            // в столбик подписи разной ширины — задаём общую, чтобы проценты
            // встали друг под другом
            var labels = new List<TextBlock>();
            labels.Add(_lblRam);
            labels.Add(_lblCpu);
            labels.AddRange(_diskLabels);
            labels.Add(_lblGpu);
            labels.Add(_lblNet);
            for (int i = 0; i < labels.Count; i++)
                if (labels[i] != null) labels[i].MinWidth = _vertical ? 28 : 0;

            // в столбике значения тоже встают в одну колонку: у сети строка
            // длиннее («↓11.4 ↑0.0»), и без общей ширины цифры съезжают
            var vals = new List<TextBlock>();
            vals.Add(_valRam);
            vals.Add(_valCpu);
            vals.AddRange(_diskVals);
            vals.Add(_valGpu);
            vals.Add(_valNet);
            vals.AddRange(_periphVals);
            for (int i = 0; i < vals.Count; i++)
            {
                if (vals[i] == null) continue;
                bool wide = _vertical || vals[i] == _valNet;
                vals[i].Width = wide ? NetValueWidth : ValueWidth;
                // Значения всегда прижаты вправо. Раньше проценты в строке стояли по
                // центру колонки, и зазор до правого края рамки гулял от 3 до 10 DIP
                // в зависимости от числа («5%» и «100%»), а у дисков и сети значение
                // было вправо, и рамки выглядели по-разному. Теперь правый край
                // везде на одном месте, как левый (там шкала), а колонка, как таблица.
                vals[i].TextAlignment = TextAlignment.Right;
            }

            ApplyRows();
            SetScale(_userScale);
            _naturalW = 0;
            _naturalH = 0;

            if (_win != null && _win.IsVisible)
            {
                FitTaskbar();
                Settle();
                // размер после смены раскладки WPF пересчитает следующим кадром —
                // посадку на панель повторяем, когда он уже известен
                _win.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
                {
                    if (!_win.IsVisible) return;
                    _naturalW = 0;
                    _naturalH = 0;
                    FitTaskbar();
                    Settle();
                    // отложенная посадка уже посчитана — «привязка» больше не нужна
                    _pinBand = null;
                });
            }
        }

        // ---------------------------------------------------------- раскладка

        public bool Vertical { get { return _vertical; } }
        public double UserScale { get { return _userScale; } }

        /// <summary>
        /// Живой дождь в значке трея. На слабых машинах его можно выключить:
        /// значок останется, но перестанет перерисовываться каждый кадр.
        /// </summary>
        public bool TrayAnim
        {
            get { return _trayAnim; }
            set
            {
                if (_trayAnim == value) return;
                _trayAnim = value;
                try { if (_host != null) _host.SetTrayAnimation(value); } catch { }
                SaveState();
            }
        }

        /// <summary>Сколько физических дисков нашлось (минимум один слот).</summary>
        public int DiskCount { get { return _disks.Count; } }

        /// <summary>Подпись диска для настроек: «SSD C:», «HDD D:».</summary>
        public string DiskName(int i)
        {
            if (i < 0 || i >= _disks.Count) return "";
            string text = DiskKindLabel(_disks[i]);
            DiskStat st = FindDisk(_lastStats, _disks[i].Index);
            string letter = st == null ? "" : FirstLetter(st.Letters);
            return letter.Length > 0 ? text + " " + letter : text;
        }

        public bool DiskOn(int i)
        {
            return i >= 0 && i < _diskOn.Length && _diskOn[i];
        }

        /// <summary>Показать/скрыть один диск. Последний скрыть нельзя.</summary>
        public bool SetDiskOn(int i, bool on)
        {
            if (i < 0 || i >= _diskOn.Length) return false;
            if (!on)
            {
                int left = 0;
                for (int k = 0; k < _diskOn.Length; k++) if (_diskOn[k]) left++;
                if (left <= 1) return false;
            }
            _diskOn[i] = on;
            ApplyRows();
            SaveState();
            return true;
        }

        /// <summary>Индекс строки, стоящей на месте i (0 — ОЗУ, 1 — ЦП, 2 — диски, 3 — ГП).</summary>
        public int OrderAt(int i)
        {
            return i >= 0 && i < _order.Length ? _order[i] : -1;
        }

        /// <summary>Карточка столбиком (true) или строкой (false).</summary>
        public void SetVertical(bool on)
        {
            if (_vertical == on) return;
            _vertical = on;
            ApplyLayout();
            SaveState();
        }

        /// <summary>Масштаб карточки: 0.9, 1.0, 1.1, 1.25.</summary>
        public void SetUserScale(double s)
        {
            if (s < MinUserScale - 0.001 || s > MaxUserScale + 0.001) return;
            if (Math.Abs(s - _userScale) < 0.004) return;
            _userScale = s;
            ApplyLayout();
            SaveState();
        }

        /// <summary>Сдвинуть порядок строк: right — первая уходит в конец.</summary>
        public void ShiftOrder(bool right)
        {
            int[] next = new int[_order.Length];
            for (int i = 0; i < _order.Length; i++)
                next[i] = _order[(i + (right ? 1 : _order.Length - 1)) % _order.Length];
            Array.Copy(next, _order, _order.Length);
            ApplyLayout();
            SaveState();
        }

        /// <summary>
        /// Показать/скрыть строки карточки. Отступы пересчитываем сами: скрытая
        /// группа забирает с собой и свой левый/верхний отступ, иначе у края
        /// карточки оставалась бы лишняя дырка.
        /// </summary>
        void ApplyRows()
        {
            _grpRam.Visibility = _rowOn[0] ? Visibility.Visible : Visibility.Collapsed;
            _grpCpu.Visibility = _rowOn[1] ? Visibility.Visible : Visibility.Collapsed;
            _grpGpu.Visibility = _rowOn[3] ? Visibility.Visible : Visibility.Collapsed;
            _grpNet.Visibility = _rowOn[4] ? Visibility.Visible : Visibility.Collapsed;

            for (int i = 0; i < _diskGroups.Count; i++)
            {
                bool on = _rowOn[2] && i < _diskOn.Length && _diskOn[i];
                _diskGroups[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }

            for (int i = 0; i < _periphGroups.Count; i++)
            {
                _periphGroups[i].Visibility = _rowOn[(int)CardRow.Periph]
                    ? Visibility.Visible : Visibility.Collapsed;
            }

            bool first = true;
            for (int k = 0; k < _order.Length; k++)
            {
                int r = _order[k];
                if (r == 2)
                {
                    for (int i = 0; i < _diskGroups.Count; i++)
                    {
                        bool on = _rowOn[2] && i < _diskOn.Length && _diskOn[i];
                        if (on) SetRowMargin(_diskGroups[i], ref first);
                    }
                }
                else
                {
                    Border g = r == 0 ? _grpRam : r == 1 ? _grpCpu : r == 3 ? _grpGpu : _grpNet;
                    if (g != null && _rowOn[r]) SetRowMargin(g, ref first);
                }
            }

            if (_rowOn[(int)CardRow.Periph])
            {
                for (int i = 0; i < _periphGroups.Count; i++)
                    SetRowMargin(_periphGroups[i], ref first);
            }
            if (_btn != null)
            {
                _btn.Margin = _vertical
                    ? new Thickness(0, first ? 0 : Theme.RowGapV, 0, 0)
                    : new Thickness(first ? 0 : Theme.BtnGap, 0, 0, 0);
            }
            if (_btnDash != null)
            {
                _btnDash.Margin = _vertical
                    ? new Thickness(0, Theme.RowGapV, 0, 0)
                    : new Thickness(Theme.BtnGap, 0, 0, 0);
            }
            if (_btnCfg != null)
            {
                _btnCfg.Margin = _vertical
                    ? new Thickness(0, Theme.RowGapV, 0, 0)
                    : new Thickness(Theme.BtnGap, 0, 0, 0);
            }
        }

        /// <summary>
        /// Отступы между самостоятельными рамками строк: в строке — Theme.RowGap DIP
        /// слева у всех, кроме первой, в столбике — 4 DIP сверху.
        /// </summary>
        void SetRowMargin(Border g, ref bool first)
        {
            g.Margin = _vertical
                ? new Thickness(0, first ? 0 : Theme.RowGapV, 0, 0)
                : new Thickness(first ? 0 : Theme.RowGap, 0, 0, 0);
            first = false;
        }

        /// <summary>Какая строка карточки сейчас на экране.</summary>
        public bool CardRowVisible(CardRow row)
        {
            int i = (int)row;
            return i >= 0 && i < _rowOn.Length && _rowOn[i];
        }

        /// <summary>
        /// Включить/выключить строку карточки. Последнюю строку выключить нельзя:
        /// пустая карточка бесполезна. Возвращает true, если настройка применена.
        /// </summary>
        public bool SetCardRowVisible(CardRow row, bool on)
        {
            int i = (int)row;
            if (i < 0 || i >= _rowOn.Length) return false;

            bool[] next = (bool[])_rowOn.Clone();
            next[i] = on;

            bool any = false;
            for (int k = 0; k < next.Length; k++) if (next[k]) any = true;
            if (!any) return false;

            for (int k = 0; k < next.Length; k++) _rowOn[k] = next[k];

            ApplyRows();
            SaveState();
            FitTaskbar();
            Settle();
            return true;
        }

        /// <summary>Сохранить пороги оповещений (окно настроек меняет их напрямую).</summary>
        public void SaveAlerts()
        {
            _alerts.Save(_alertsFile);
        }

        // ------------------------------------------------------------ геометрия

        void MoveTo(int x, int y)
        {
            IntPtr h = Handle;
            if (h == IntPtr.Zero) return;
            Native.MoveTo(h, x, y);
            _lastPx = new Point(x, y);
            if (PositionChanged != null) PositionChanged();
        }

        /// <summary>Панель задач, на которой стоит карточка: центр окна попал в её полосу.</summary>
        Native.RECT? GetTaskbarBand(Native.RECT r)
        {
            IntPtr tb = Native.TaskbarUnder(Handle);
            if (tb == IntPtr.Zero) return null;
            Native.RECT tr;
            if (!Native.GetWindowRect(tb, out tr)) return null;
            int cy = (r.Top + r.Bottom) / 2;
            if (cy >= tr.Top && cy < tr.Bottom) return tr;

            // столбик выше панели: серединой её не задевает, но низ прижат
            // к краю экрана — значит, он тоже стоит на панели
            if (Math.Abs(r.Bottom - tr.Bottom) <= 2) return tr;
            return null;
        }

        /// <summary>Натуральные размеры карточки в физических пикселях (при масштабе 1).</summary>
        void GetNatural(Native.RECT r, out double w, out double h)
        {
            if (Math.Abs(_scale - 1.0) < 0.004)
            {
                _naturalW = r.Width;
                _naturalH = r.Height;
            }
            if (_naturalW > 8 && _naturalH > 8) { w = _naturalW; h = _naturalH; return; }
            if (_scale <= 0.01) { w = r.Width; h = r.Height; return; }
            w = r.Width / _scale;
            h = r.Height / _scale;
        }

        void SetScale(double s)
        {
            if (s > MaxUserScale) s = MaxUserScale;
            if (s < 0.4) s = 0.4;
            if (Math.Abs(s - _scale) < 0.004) return;
            _scale = s;
            _scaleXf.ScaleX = s;
            _scaleXf.ScaleY = s;
        }

        /// <summary>
        /// Решает, «на панели» ли карточка, подбирает масштаб и отложенную позицию
        /// (после пересчёта размера её применит Settle).
        /// </summary>
        void FitTaskbar()
        {
            if (!_win.IsVisible) return;
            try
            {
                Native.RECT r;
                if (!GetRect(out r)) return;
                if (r.Width <= 8 || r.Height <= 8) return;

                Native.RECT? band = GetTaskbarBand(r);
                if (!band.HasValue) band = _pinBand;
                if (!band.HasValue)
                {
                    // не на панели — масштаб пользователя и то же место, где её положили
                    SetScale(_userScale);
                    _settleTo = new Point(r.Left, r.Top);
                    return;
                }

                double natW, natH;
                GetNatural(r, out natW, out natH);
                int tbH = band.Value.Height;
                if (natH <= 8 || tbH <= 12) return;

                // по 2 px отступа сверху и снизу, чтобы карточка не липла к краям панели
                double targetH = tbH - 4;

                // Столбик в полосу панели не влезает при любом читаемом
                // масштабе. Не давим его до нечитаемого: держим масштаб
                // пользователя и ставим низом на край экрана — карточка
                // встаёт на панель столбиком.
                if (_vertical && natH * _userScale > targetH)
                {
                    SetScale(_userScale);
                    int vw = (int)Math.Round(natW * _scale);
                    int vh = (int)Math.Round(natH * _scale);
                    int vx = Math.Min(Math.Max(r.Left, band.Value.Left), band.Value.Right - vw);
                    _settleTo = new Point(vx, band.Value.Bottom - vh);
                    return;
                }

                SetScale(Math.Min(_userScale, targetH / natH));

                int newW = (int)Math.Round(natW * _scale);
                int newH = (int)Math.Round(natH * _scale);
                int x = Math.Min(Math.Max(r.Left, band.Value.Left), band.Value.Right - newW);
                int y = band.Value.Top + (tbH - newH) / 2;
                _settleTo = new Point(x, y);
            }
            catch { }
        }

        /// <summary>
        /// Позицию ставим после того, как WPF пересчитал размер окна: иначе окно
        /// уже с новым размером останется со старым углом.
        /// </summary>
        void Settle()
        {
            if (_settleScheduled) return;
            _settleScheduled = true;
            _win.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
            {
                _settleScheduled = false;
                if (_settleTo.HasValue)
                {
                    MoveTo((int)Math.Round(_settleTo.Value.X), (int)Math.Round(_settleTo.Value.Y));
                    _settleTo = null;
                }
                KeepOnScreen();
                SyncTaskbarOwner();
                SaveState();
            });
        }

        /// <summary>
        /// Держим карточку в пределах монитора, но НЕ в рабочей области — поверх
        /// панели задач её тоже можно ставить. Именно WorkingArea раньше
        /// притягивала окно обратно над панелью.
        /// </summary>
        public void KeepOnScreen()
        {
            try
            {
                Native.RECT r;
                if (!GetRect(out r)) return;
                int w = r.Width, h = r.Height;
                if (w <= 0 || h <= 0) return;

                int cx = (r.Left + r.Right) / 2;
                int cy = (r.Top + r.Bottom) / 2;
                var bd = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cx, cy)).Bounds;
                int l = Math.Min(Math.Max(r.Left, bd.Left), bd.Right - w);
                int t = Math.Min(Math.Max(r.Top, bd.Top), bd.Bottom - h);
                if (l != r.Left || t != r.Top) MoveTo(l, t);
            }
            catch { }
        }

        /// <summary>
        /// WPF-окно не знает о владельце, назначенном через Win32, поэтому держим
        /// связь сами: если карточка пересекает панель задач, панель становится
        /// владельцем. Owned-окно всегда выше владельца, и панель не перекрывает
        /// карточку — не нужно по десять раз в секунду перетягивать z-order.
        /// </summary>
        public void SyncTaskbarOwner()
        {
            if (!_win.IsVisible) return;
            try
            {
                IntPtr hw = Handle;
                if (hw == IntPtr.Zero) return;

                // запоминаем «своего» владельца до первого вмешательства
                if (!_ownOwnerKnown)
                {
                    _ownOwner = Native.OwnerOf(hw);
                    _ownOwnerKnown = true;
                }

                IntPtr tb = Native.TaskbarUnder(hw);
                IntPtr want = tb != IntPtr.Zero ? tb : _ownOwner;
                if (want != Native.OwnerOf(hw)) Native.SetOwner(hw, want);
            }
            catch { }
        }

        /// <summary>
        /// Панель задач тоже topmost: клик по ней поднимает её выше карточки, и
        /// виджет «прячется за панель». Если она перекрыла окно — возвращаем наверх.
        /// </summary>
        public void KeepAboveTaskbar()
        {
            if (!_win.IsVisible || !_win.Topmost) return;
            try
            {
                IntPtr hw = Handle;
                if (hw != IntPtr.Zero && Native.CoveredByTaskbar(hw)) Native.RaiseTopmost(hw);
            }
            catch { }
        }

        // ---------------------------------------------------------- перетаскивание

        void HookMouse()
        {
            _win.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (_btn.IsMouseOver || _btnCfg.IsMouseOver || _btnDash.IsMouseOver) return;
                Native.RECT r;
                if (!GetRect(out r)) return;
                _dragFrom = System.Windows.Forms.Cursor.Position;
                _dragRect = r;
                _hasDragRect = true;
                try { _win.CaptureMouse(); } catch { }
                e.Handled = true;
            };

            _win.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!_dragFrom.HasValue) return;
                if (Mouse.LeftButton != MouseButtonState.Pressed)
                {
                    _dragFrom = null;
                    _hasDragRect = false;
                    return;
                }
                var p = System.Windows.Forms.Cursor.Position;
                MoveClamped(_dragRect.Left + (p.X - _dragFrom.Value.X),
                            _dragRect.Top + (p.Y - _dragFrom.Value.Y));
            };

            _win.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (!_dragFrom.HasValue) return;
                var from = _dragFrom.Value;
                var to = System.Windows.Forms.Cursor.Position;
                _dragFrom = null;
                _hasDragRect = false;
                try { _win.ReleaseMouseCapture(); } catch { }
                FitTaskbar();
                Settle();

                // почти без сдвига — это клик, а не перенос
                if (Math.Abs(to.X - from.X) <= 3 && Math.Abs(to.Y - from.Y) <= 3)
                    OnCardClick(e);
            };

            // захват мыши мог пропасть (Alt+Tab и т.п.) — доводим состояние до конца
            _win.LostMouseCapture += delegate(object s, MouseEventArgs e)
            {
                if (!_dragFrom.HasValue) return;
                _dragFrom = null;
                _hasDragRect = false;
                FitTaskbar();
                Settle();
            };
        }

        /// <summary>
        /// Перенос карточки мышью: не выпускаем её за монитор. Низ монитора — это
        /// и есть нижняя граница панели задач, ниже карточка не уезжает.
        /// </summary>
        void MoveClamped(int x, int y)
        {
            if (!_hasDragRect) return;
            int w = _dragRect.Width, h = _dragRect.Height;
            if (w <= 0 || h <= 0) return;
            try
            {
                var c = System.Windows.Forms.Cursor.Position;
                var bd = System.Windows.Forms.Screen.FromPoint(c).Bounds;
                x = Math.Min(Math.Max(x, bd.Left), bd.Right - w);
                y = Math.Min(Math.Max(y, bd.Top), bd.Bottom - h);
            }
            catch { }
            MoveTo(x, y);
        }

        // ------------------------------------------------------------ кнопка

        void HookButton()
        {
            _btn.MouseEnter += delegate
            {
                if (_sampler.TrimState == 0) _btn.Background = _brBtnHover;
            };
            _btn.MouseLeave += delegate
            {
                if (_sampler.TrimState == 0) _btn.Background = _brBtnIdle;
            };
            _btn.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                StartClean();
            };

            _btnDash.MouseEnter += delegate { _btnDash.Background = _brBtnHover; };
            _btnDash.MouseLeave += delegate { _btnDash.Background = _brBtnIdle; };
            _btnDash.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                ToggleDashboard();
            };
            ApplyDashButton();

            _btnCfg.MouseEnter += delegate { _btnCfg.Background = _brBtnHover; };
            _btnCfg.MouseLeave += delegate { _btnCfg.Background = _brBtnIdle; };
            _btnCfg.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                Action h = SettingsRequested;
                if (h != null) h();
            };
        }

        /// <summary>
        /// Наведение на метрику подсвечивает всю группу — цветную риску,
        /// подпись и значение — тем же фоном, что и кнопка очистки.
        /// </summary>
        void HookGroups()
        {
            HookGroup(_grpRam);
            HookRowMenu(_grpRam, delegate { HideRow(CardRow.Ram); });
            HookGroup(_grpCpu);
            HookRowMenu(_grpCpu, delegate { HideRow(CardRow.Cpu); });
            HookGroup(_grpGpu);
            HookRowMenu(_grpGpu, delegate { HideRow(CardRow.Gpu); });
            HookGroup(_grpNet);
            HookRowMenu(_grpNet, delegate { HideRow(CardRow.Net); });
        }

        void HookDiskGroups()
        {
            for (int i = 0; i < _diskGroups.Count; i++)
            {
                int idx = i;   // C# 5: в замыкании нужна своя копия
                HookGroup(_diskGroups[i]);
                HookRowMenu(_diskGroups[i], delegate { HideDisk(idx); });
            }
        }

        void HookGroup(Border g)
        {
            if (g == null) return;
            g.MouseEnter += delegate { g.Background = _brBtnHover; };
            g.MouseLeave += delegate { g.Background = _brBtnIdle; };
        }

        // ------------------------------------------------- топ процессов

        // Клик по строке ЦП или ОЗУ открывает плашку с самыми тяжёлыми процессами.
        // Пока она открыта, раз в 1,5 с идёт фоновый замер; когда курсор ушёл
        // и с плашки, и с карточки — через ~6 с она закрывается сама.
        ProcessPanel _procPanel;
        ProcessSampler _procSampler;
        Popup _procPopup;
        DispatcherTimer _procTimer;
        ProcessPanel.Kind _procKind;
        bool _procBusy;
        int _procIdle;

        void OnCardClick(MouseButtonEventArgs e)
        {
            try
            {
                if (HitGroup(_grpCpu, e)) ToggleProcPanel(ProcessPanel.Kind.Cpu);
                else if (HitGroup(_grpRam, e)) ToggleProcPanel(ProcessPanel.Kind.Ram);
                else if (HitGroup(_grpGpu, e)) ToggleProcPanel(ProcessPanel.Kind.Gpu);
                else if (HitGroup(_grpNet, e)) ToggleProcPanel(ProcessPanel.Kind.Net);
                else
                {
                    for (int i = 0; i < _diskGroups.Count; i++)
                        if (HitGroup(_diskGroups[i], e)) { ToggleProcPanel(ProcessPanel.Kind.Disk, i.ToString(Inv)); return; }
                    // строка периферии: плашка устройства (заряд, прогноз, журнал)
                    for (int i = 0; i < _periphGroups.Count && i < _periph.Count; i++)
                        if (HitGroup(_periphGroups[i], e)) { ToggleProcPanel(ProcessPanel.Kind.Periph, _periph[i].Key); return; }
                }
            }
            catch (Exception ex) { ErrorLog.Write("card click", ex); }
        }

        /// <summary>Попал ли курсор в строку. Через координаты: под захватом мыши IsMouseOver ненадёжен.</summary>
        static bool HitGroup(Border g, MouseButtonEventArgs e)
        {
            if (g == null || !g.IsVisible || g.ActualWidth <= 0) return false;
            return new Rect(0, 0, g.ActualWidth, g.ActualHeight).Contains(e.GetPosition(g));
        }

        string _procKey = "";      // какое устройство открыто в плашке периферии / какой диск (индекс в _diskGroups)
        int _procSlot = -1;        // слот шапки плашки (0 ОЗУ, 1 ЦП, 2+d диск, GpuSlot, NetSlot); −1 — шапки нет

        int SlotFor(ProcessPanel.Kind kind, string key)
        {
            switch (kind)
            {
                case ProcessPanel.Kind.Ram: return 0;
                case ProcessPanel.Kind.Cpu: return 1;
                case ProcessPanel.Kind.Gpu: return GpuSlot;
                case ProcessPanel.Kind.Net: return NetSlot;
                case ProcessPanel.Kind.Disk:
                    int d;
                    return int.TryParse(key, out d) && d >= 0 && d < _diskGroups.Count ? 2 + d : -1;
                default: return -1;
            }
        }

        void ToggleProcPanel(ProcessPanel.Kind kind) { ToggleProcPanel(kind, null); }

        void ToggleProcPanel(ProcessPanel.Kind kind, string key)
        {
            if (_procPopup != null && _procPopup.IsOpen && _procKind == kind && _procKey == (key ?? ""))
            {
                CloseProcPanel();
                return;
            }

            if (_procPopup == null)
            {
                _procPanel = new ProcessPanel();
                _procPopup = new Popup();
                _procPopup.AllowsTransparency = true;
                _procPopup.StaysOpen = true;
                _procPopup.PlacementTarget = _card;
                _procPopup.Placement = PlacementMode.Top;
                _procPopup.VerticalOffset = -6;
                _procPopup.PopupAnimation = PopupAnimation.Fade;
                _procPopup.Child = _procPanel.Root;
                _procPopup.Opened += delegate { FixPopupDeferred((Visual)_procPopup.Child, PillAlign.Left); };

                _procTimer = new DispatcherTimer();
                _procTimer.Interval = TimeSpan.FromMilliseconds(1500);
                _procTimer.Tick += delegate
                {
                    try { ProcTick(); }
                    catch (Exception ex) { ErrorLog.Write("proc tick", ex); }
                };
            }

            _procKind = kind;
            _procKey = key ?? "";
            _procSlot = SlotFor(kind, _procKey);
            if (kind == ProcessPanel.Kind.Disk) DiskHealth.Ensure(null);   // состояние дисков — в фоне, раз в 10 мин
            _procSampler = kind == ProcessPanel.Kind.Periph ? null : new ProcessSampler();
            _procPanel.SetKind(kind);
            _procPanel.DiskIndex = -1;
            if (kind == ProcessPanel.Kind.Disk)
            {
                int di;
                if (int.TryParse(_procKey, out di) && di >= 0 && di < _disks.Count) _procPanel.DiskIndex = _disks[di].Index;
            }
            // шапка: тот же текст и график, что раньше показывала подсказка при наведении
            _procPanel.SetHeader(_procSlot >= 0 && _groupHeads != null && _procSlot < _groupHeads.Length
                                 ? _groupHeads[_procSlot] : null);
            if (_procSlot >= 0) RefreshSpark(_procSlot);
            if (kind == ProcessPanel.Kind.Periph) ShowPeriphPanel();
            _procIdle = 0;

            SetTipsEnabled(false);
            if (_procPopup.IsOpen) FixPopupDeferred((Visual)_procPopup.Child, PillAlign.Left);
            else _procPopup.IsOpen = true;

            _procTimer.Start();
            RefreshProcs();
        }

        void CloseProcPanel()
        {
            if (_procTimer != null) _procTimer.Stop();
            if (_procPopup != null) _procPopup.IsOpen = false;
            _procSampler = null;
            SetTipsEnabled(true);
        }

        void ProcTick()
        {
            if (_procPopup == null || !_procPopup.IsOpen) { CloseProcPanel(); return; }
            if (!_win.IsVisible) { CloseProcPanel(); return; }

            bool over = _procPanel.Root.IsMouseOver || _card.IsMouseOver;
            _procIdle = over ? 0 : _procIdle + 1;
            if (_procIdle >= 4) { CloseProcPanel(); return; }

            RefreshProcs();
        }

        /// <summary>Плашка устройства: берём его из текущего списка (если отключили — по ключу из журнала).</summary>
        void ShowPeriphPanel()
        {
            Peripheral dev = null;
            foreach (Peripheral p in _periph) if (p.Key == _procKey) { dev = p; break; }
            _procPanel.DeviceKey = _procKey;
            _procPanel.Device = dev;
            _procPanel.Redraw();
        }

        /// <summary>Замер — в пуле потоков: перебор сотен процессов не должен вешать окно.</summary>
        void RefreshProcs()
        {
            if (_procKind == ProcessPanel.Kind.Periph) { ShowPeriphPanel(); return; }
            ProcessSampler sampler = _procSampler;
            if (sampler == null || _procBusy) return;
            _procBusy = true;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                ProcessInfo[] data = null;
                int n = 0;
                try { data = sampler.Sample(); n = sampler.Samples; }
                catch (Exception ex) { ErrorLog.Write("proc sample", ex); }

                _win.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
                {
                    _procBusy = false;
                    // за время замера плашку могли закрыть или переключить
                    if (data != null && sampler == _procSampler && _procPopup != null && _procPopup.IsOpen)
                    {
                        _procPanel.Stats = _lastStats;
                        _procPanel.Net = _net == null ? null : _net.Current;
                        _procPanel.Update(data, n);
                    }
                });
            });
        }

        /// <summary>
        /// Подсказки строк встают на то же место над карточкой, что и плашка
        /// процессов, и перекрывали бы её — пока плашка открыта, их выключаем.
        /// </summary>
        void SetTipsEnabled(bool on)
        {
            var targets = new List<FrameworkElement>();
            targets.Add(_card);
            targets.Add(_grpRam);
            targets.Add(_grpCpu);
            targets.Add(_grpGpu);
            targets.Add(_grpNet);
            targets.AddRange(_diskGroups.ConvertAll<FrameworkElement>(delegate(Border b) { return b; }));
            targets.AddRange(_periphGroups.ConvertAll<FrameworkElement>(delegate(Border b) { return b; }));

            foreach (FrameworkElement el in targets)
            {
                if (el == null) continue;
                ToolTipService.SetIsEnabled(el, on);
                var tip = el.ToolTip as ToolTip;
                if (!on && tip != null) tip.IsOpen = false;
            }
        }

        // ------------------------------------------------- меню строки карточки

        /// <summary>
        /// Правый клик по строке: скрыть её — быстрее, чем идти в окно
        /// настроек и снимать галочку. Настройки открываются шестернёй.
        /// </summary>
        void HookRowMenu(Border g, Action hide)
        {
            if (g == null) return;

            var menu = new ContextMenu();
            menu.PlacementTarget = g;
            menu.Placement = PlacementMode.MousePoint;
            menu.Background = Theme.B(Theme.Surface);
            menu.BorderBrush = Theme.B(Theme.Stroke);
            menu.BorderThickness = new Thickness(1);
            menu.Padding = new Thickness(3, 4, 3, 4);
            menu.HasDropShadow = true;
            menu.SnapsToDevicePixels = true;
            menu.Template = RowMenuTemplate();

            menu.Items.Add(RowMenuItem(Lang.S("Скрыть строку", "Hide row"), hide));

            // Меню карточки открывается по правому клику в пустом месте;
            // по строке должно открываться меню строки, а не оба сразу.
            // Открываем сами, а не через ContextMenuService: карточка —
            // окно без активации, и автоматическое открытие там не срабатывает.
            g.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                menu.PlacementTarget = g;
                menu.IsOpen = true;
            };
        }

        /// <summary>
        /// Стандартное меню красит фон системным светлым; шаблон оставляет
        /// только нашу рамку и список пунктов.
        /// </summary>
        static ControlTemplate RowMenuTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "B");
            border.SetValue(Border.BackgroundProperty,
                new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty,
                new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty,
                new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty,
                new TemplateBindingExtension(Control.PaddingProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.SnapsToDevicePixelsProperty, true);

            var items = new FrameworkElementFactory(typeof(ItemsPresenter));
            border.AppendChild(items);

            var t = new ControlTemplate(typeof(ContextMenu));
            t.VisualTree = border;
            return t;
        }

        /// <summary>Пункт меню строки в тёмном оформлении карточки.</summary>
        MenuItem RowMenuItem(string text, Action click)
        {
            var mi = new MenuItem();
            mi.Header = text;
            mi.FontFamily = new FontFamily(Theme.FontMenu);
            mi.FontSize = 12;
            mi.Foreground = Theme.B(Theme.TextHi);
            mi.Background = Brushes.Transparent;
            mi.Padding = new Thickness(10, 5, 14, 5);
            mi.Template = RowMenuItemTemplate();
            mi.Click += delegate { if (click != null) click(); };
            return mi;
        }

        /// <summary>
        /// Стандартный MenuItem красит наведение системным голубым; шаблон
        /// оставляет только текст и наш фон наведения.
        /// </summary>
        static ControlTemplate RowMenuItemTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "B");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.SnapsToDevicePixelsProperty, true);
            border.SetValue(Border.PaddingProperty,
                new TemplateBindingExtension(Control.PaddingProperty));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            border.AppendChild(content);

            var t = new ControlTemplate(typeof(MenuItem));
            t.VisualTree = border;

            var hover = new Trigger();
            hover.Property = MenuItem.IsHighlightedProperty;
            hover.Value = true;
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                Theme.B(Theme.SurfaceHover), "B"));
            t.Triggers.Add(hover);

            return t;
        }

        /// <summary>Скрыть строку; последнюю видимую скрыть нельзя.</summary>
        void HideRow(CardRow row)
        {
            if (SetCardRowVisible(row, false)) return;
            ShowNote(Lang.S("Последнюю строку скрыть нельзя",
                            "The last row can't be hidden"));
        }

        /// <summary>Скрыть один диск; последний видимый убирает строку целиком.</summary>
        void HideDisk(int i)
        {
            if (SetDiskOn(i, false)) return;
            // единственный видимый диск: прячем всю строку диска, как любую метрику
            if (!SetCardRowVisible(CardRow.Ssd, false))
                ShowNote(Lang.S("Последнюю строку скрыть нельзя",
                                "The last row can't be hidden"));
        }

        /// <summary>
        /// Пока идёт очистка памяти, молния переливается: цвет плавно ходит
        /// между обычным и жёлтым акцентом темы. Раньше вместо значка ставились
        /// три точки, которые в моноширинном шрифте превращались в крупные
        /// квадраты.
        /// </summary>
        void StartGlyphShimmer()
        {
            try
            {
                Color from = (Color)ColorConverter.ConvertFromString(Theme.BtnText);
                Color to = (Color)ColorConverter.ConvertFromString(Theme.SsdColor);
                var brush = new SolidColorBrush(from);
                _btnGlyph.Text = CleanGlyph;
                _btnGlyph.Foreground = brush;

                var anim = new System.Windows.Media.Animation.ColorAnimation(
                    from, to, TimeSpan.FromMilliseconds(380));
                anim.AutoReverse = true;
                anim.RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever;
                anim.EasingFunction = new System.Windows.Media.Animation.SineEase();
                brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            }
            catch (Exception ex) { ErrorLog.Write("glyph shimmer", ex); }
        }

        void StopGlyphShimmer()
        {
            try
            {
                var brush = _btnGlyph.Foreground as SolidColorBrush;
                if (brush != null && !brush.IsFrozen)
                    brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            }
            catch { }
            _btnGlyph.Text = CleanGlyph;
            _btnGlyph.Foreground = Theme.B(Theme.BtnText);
        }
        public void StartClean()
        {
            if (_sampler.TrimState != 0) return;
            _btn.Background = _brBtnBusy;
            StartGlyphShimmer();
            _toast.IsOpen = false;
            _toastUntil = null;
            _sampler.TrimStart();
        }

        // ------------------------------------------------------- показ/скрытие

        void InitialPlace()
        {
            Native.RECT r;
            if (!GetRect(out r)) return;

            if (_hasState)
            {
                MoveTo((int)_stateX, (int)_stateY);
            }
            else
            {
                var wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                MoveTo(wa.Right - r.Width - 16, wa.Bottom - r.Height - 10);
            }

            // сохранённая позиция могла оказаться под панелью задач или вообще
            // за пределами экрана (например, после смены состава мониторов)
            KeepOnScreen();
            FitTaskbar();
            Settle();
            ApplyLook();
        }

        // ---------------------------------------------------- настройки «Вид» (сразу)

        Brush _brAmber;
        bool _faded;

        /// <summary>Прозрачность, подписи и порог цвета из Prefs — применяется без перезапуска.</summary>
        public void ApplyLook()
        {
            if (_win == null) return;
            _faded = false;
            _win.Opacity = Prefs.Opacity;

            Visibility v = Prefs.HideLabels ? Visibility.Collapsed : Visibility.Visible;
            if (_lblRam != null) _lblRam.Visibility = v;
            if (_lblCpu != null) _lblCpu.Visibility = v;
            if (_lblGpu != null) _lblGpu.Visibility = v;
            if (_lblNet != null) _lblNet.Visibility = v;

            if (_lastStats != null) Update();
        }

        /// <summary>«Бледнеть в простое»: курсор вне карточки — окно прозрачнее.</summary>
        void UpdateFade()
        {
            if (_win == null || !_win.IsVisible) return;
            bool idle = false;
            if (Prefs.FadeIdle)
            {
                try
                {
                    var p = System.Windows.Forms.Cursor.Position;
                    Native.RECT r;
                    idle = GetRect(out r) && (p.X < r.Left || p.X > r.Right || p.Y < r.Top || p.Y > r.Bottom);
                }
                catch { idle = false; }
            }
            if (idle == _faded) return;
            _faded = idle;
            _win.Opacity = idle ? Math.Max(0.25, Prefs.Opacity * 0.45) : Prefs.Opacity;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_devHooked)
            {
                _devHooked = true;
                IntPtr hwnd = new WindowInteropHelper(_win).Handle;
                HwndSource src = HwndSource.FromHwnd(hwnd);
                if (src != null) src.AddHook(DeviceHook);
                RegisterHotkey(hwnd);
            }

            if (_firstLoad)
            {
                _firstLoad = false;
                Update();
                _win.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)InitialPlace);
            }
            else if (_lastPx.HasValue)
            {
                // после Hide/Show WPF ставит окно по своим Left/Top — возвращаем
                // физическую позицию, иначе на мониторе с другим масштабом прыжок
                MoveTo((int)_lastPx.Value.X, (int)_lastPx.Value.Y);
            }
        }

        void OnDpiChanged(object sender, DpiChangedEventArgs e)
        {
            // физический размер карточки на новом мониторе другой — сбрасываем
            // замер натурального размера и заново садимся на панель задач
            _naturalW = 0;
            _naturalH = 0;

            // посреди перетаскивания ничего не двигаем: карточку ведёт мышь,
            // а посадку на панель донастроим при отпускании
            if (_dragFrom.HasValue) return;

            // Background, а не Loaded: WPF должен успеть пересчитать размер окна
            // под новый DPI, иначе мерим ещё старый и центрируем неточно
            _win.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
            {
                if (!_win.IsVisible) return;

                // WPF при смене DPI переставляет окно по своим Left/Top, а они
                // устарели: двигаем карточку через Win32, и WPF о наших переносах
                // не знает. Возвращаем физический левый верхний угол — при смене
                // масштаба карточка должна расти/ужиматься от него же.
                if (_lastPx.HasValue) MoveTo((int)_lastPx.Value.X, (int)_lastPx.Value.Y);

                KeepOnScreen();
                FitTaskbar();
                Settle();
            });
        }

        const int WM_DEVICECHANGE = 0x0219;
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_HOTKEY = 0x0312;
        const int HotkeyId = 0x5357;          // «SW»
        bool _devHooked;
        bool _hotkeyOk;
        bool _displayPending;

        /// <summary>Глобальная клавиша показать/скрыть, или null, если её занял кто-то другой.</summary>
        public string HotkeyText { get { return _hotkeyOk ? "Ctrl+Alt+W" : null; } }

        /// <summary>
        /// WM_DEVICECHANGE приходит, когда воткнули или вынули устройство.
        /// Сообщаем наружу — служба периферии пересмотрит список сразу, а не
        /// через десять секунд. WM_DISPLAYCHANGE — сменилось разрешение или
        /// состав мониторов: карточку возвращаем на экран. WM_HOTKEY — Ctrl+Alt+W.
        /// </summary>
        IntPtr DeviceHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE)
            {
                Action h = DeviceChanged;
                if (h != null)
                {
                    try { h(); } catch { }
                }
            }
            else if (msg == WM_DISPLAYCHANGE)
            {
                OnDisplayChanged();
            }
            else if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                try { Toggle(); }
                catch (Exception ex) { ErrorLog.Write("hotkey", ex); }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Монитор отключили или сменили разрешение — карточка могла остаться
        /// за краем экрана. Ждём, пока Windows закончит перестройку (сообщения
        /// идут пачкой), и возвращаем окно в видимую область.
        /// </summary>
        void OnDisplayChanged()
        {
            if (_displayPending) return;
            _displayPending = true;

            var t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(800);
            t.Tick += delegate
            {
                t.Stop();
                _displayPending = false;
                try
                {
                    if (!_win.IsVisible) return;
                    KeepOnScreen();
                    FitTaskbar();
                    Settle();
                }
                catch (Exception ex) { ErrorLog.Write("display change", ex); }
            };
            t.Start();
        }

        void RegisterHotkey(IntPtr hwnd)
        {
            try
            {
                _hotkeyOk = Native.RegisterHotKey(hwnd, HotkeyId,
                    Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x57);   // W
                if (!_hotkeyOk) ErrorLog.Write("hotkey", "Ctrl+Alt+W уже занято другой программой");
            }
            catch (Exception ex) { ErrorLog.Write("hotkey", ex); }
        }

        public void ShowWidget()
        {
            if (_win.IsVisible) { _win.Activate(); return; }
            _autoHidden = false;
            _win.Show();
            if (_lastPx.HasValue) MoveTo((int)_lastPx.Value.X, (int)_lastPx.Value.Y);
            _win.Activate();
            // Show ставит окно по сохранённым WPF Left/Top, а те могут быть
            // в другой системе координат — после показа перепроверяем физически
            _win.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
            {
                KeepOnScreen();
                FitTaskbar();
                Settle();
            });
            Update();
            Action h = VisibilityChanged;
            if (h != null) h();
        }

        public void HideWidget()
        {
            _autoHidden = false;      // спрятал пользователь — из полного экрана не возвращаем
            _win.Hide();
            Action h = VisibilityChanged;
            if (h != null) h();
        }

        /// <summary>
        /// Игра или видео во весь экран на том же мониторе — карточка уходит,
        /// чтобы не торчать поверх. Возвращается сама, когда полный экран
        /// закончился. Пользовательское «скрыть» не трогаем: после ручного
        /// скрытия _autoHidden сброшен и карточка сама не появится.
        /// </summary>
        void KeepOutOfFullscreen()
        {
            try
            {
                IntPtr own = Handle;
                if (own == IntPtr.Zero) return;

                IntPtr mon = Native.FullscreenForegroundMonitor(own);
                bool over = mon != IntPtr.Zero &&
                            mon == Native.MonitorFromWindow(own, Native.MONITOR_DEFAULTTONEAREST);

                if (over)
                {
                    // посреди перетаскивания не дёргаем — донастроим после отпускания
                    if (!_win.IsVisible || _dragFrom.HasValue) return;
                    _autoHidden = true;
                    _win.Hide();
                    Action h = VisibilityChanged;
                    if (h != null) h();
                }
                else if (_autoHidden)
                {
                    _autoHidden = false;
                    _win.Show();
                    if (_lastPx.HasValue) MoveTo((int)_lastPx.Value.X, (int)_lastPx.Value.Y);
                    _win.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
                    {
                        KeepOnScreen();
                        FitTaskbar();
                        Settle();
                    });
                    Action h = VisibilityChanged;
                    if (h != null) h();
                }
            }
            catch { }
        }

        public void Toggle()
        {
            if (_win.IsVisible) HideWidget();
            else ShowWidget();
        }

        // ---------------------------------------------------------- обновление

        /// <summary>Применяет значения к таймеру и заодно доводит z-order.</summary>
        public void Update()
        {
            KeepAboveTaskbar();
            KeepOutOfFullscreen();
            EnsureDisks();
            UpdateFade();

            // замер делает фоновый поток Sampler: UI только забирает готовый
            // снимок и не ждёт PDH, если тот задумался
            SysStats s = _sampler.Latest;
            if (s == null) return;
            _lastStats = s;

            // каждый снимок — в историю один раз, даже если Update позвали чаще
            if (!_histLoaded) LoadHistory();
            if (!ReferenceEquals(s, _histLast)) { _histLast = s; _hist.Add(s); }
            if ((DateTime.UtcNow - _histSaved).TotalMinutes >= 5) SaveHistory();

            _alerts.Update(s);
            CheckTrafficLimit();

            SetValue(_valCpu, s.Cpu);
            SetValue(_valRam, s.RamPct);
            for (int d = 0; d < _diskVals.Count && d < _disks.Count; d++)
            {
                DiskStat st = FindDisk(s, _disks[d].Index);
                if (_disks[d].Index < 0)
                    SetValue(_diskVals[d], s.DiskPct, s.Disks.Count > 0);
                else
                    SetValue(_diskVals[d], st == null ? 0 : st.Pct, st != null);
            }
            SetValue(_valGpu, s.Gpu, s.HasGpu);
            SetNet(s);

            UpdateDiskLabels(s);

            // значок в трее: статичный поток, краснеет, когда любая метрика от 90%
            _host.UpdateTrayIcon(s.Cpu, s.RamPct, s.DiskPct, s.HasGpu ? s.Gpu : -1);

            UpdateGroupTips(s);
            RefreshDashboard(s);

            // состояние кнопки очистки
            if (_sampler.TrimState == 2)
            {
                _sampler.TrimState = 0;
                StopGlyphShimmer();
                _btn.Background = _brBtnIdle;

                double mb = _sampler.TrimFreed / 1048576.0;
                string msg = mb >= 1024
                    ? string.Format(Inv, Lang.S("Освобождено {0:0.0} ГБ", "Freed {0:0.0} GB"), mb / 1024)
                    : string.Format(Inv, Lang.S("Освобождено {0:0} МБ", "Freed {0:0} MB"), mb);
                if (_sampler.TrimTop != null)
                    msg += Lang.S(". Больше всего держит ", ". Biggest: ") + _sampler.TrimTop;

                if (_win.IsVisible)
                {
                    _alert.IsOpen = false;      // уведомление важнее тревоги
                    _alertUntil = null;
                    _toastText.Text = msg;
                    _toast.IsOpen = true;
                    _toastUntil = DateTime.Now.AddSeconds(3);
                }
                else
                {
                    // виджет свёрнут в трей — показываем уведомление от значка
                    _host.ShowBalloon(msg);
                }
            }

            if (_toastUntil.HasValue && DateTime.Now > _toastUntil.Value)
            {
                _toast.IsOpen = false;
                _toastUntil = null;
            }

            if (_alertUntil.HasValue && DateTime.Now > _alertUntil.Value)
            {
                _alert.IsOpen = false;
                _alertUntil = null;
            }
        }

        void SetValue(TextBlock t, double pct)
        {
            SetValue(t, pct, true);
        }

        /// <summary>available=false — Windows не отдала этот счётчик, показываем «—».</summary>
        /// <summary>
        /// Сеть: две скорости в одной строке. Проценты тут не нужны, поэтому
        /// анимация значений не используется — текст обновляется как есть.
        /// </summary>
        /// <summary>
        /// Заливка шкалы: сплошная, а в неоновой теме — вертикальные сегменты
        /// по 3 px с просветом 1 px. Рисуется кистью, поэтому высота заполнения
        /// по-прежнему задаётся одной Height и анимируется как обычно.
        /// </summary>
        static Brush BarFill(string hex)
        {
            var solid = Theme.B(hex);
            if (!Theme.ColorByMetric) return solid;

            var db = new DrawingBrush();
            db.TileMode = TileMode.Tile;
            db.Viewport = new Rect(0, 0, 3, 4);          // 3 px сегмент + 1 px просвет
            db.ViewportUnits = BrushMappingMode.Absolute;
            var seg = new GeometryDrawing(solid, null, new RectangleGeometry(new Rect(0, 0, 3, 3)));
            seg.Freeze();
            db.Drawing = seg;
            db.Freeze();
            return db;
        }

        /// <summary>Высота заполнения шкалы: BarH (16 px) — 100 %, минимум 1 px при любой нагрузке.</summary>
        static void SetBar(Border bar, double pct)
        {
            if (bar == null) return;
            if (double.IsNaN(pct) || pct <= 0) { if (bar.Height != 0) bar.Height = 0; return; }
            double h = Math.Min(100.0, pct) / 100.0 * BarH;
            // целые пиксели: дробная высота размазывает край по полупрозрачным
            // пикселям и шкала выглядит мутной
            h = Math.Max(1.0, Math.Round(h));
            if (bar.Height != h) bar.Height = h;
        }

        void SetNet(SysStats s)
        {
            if (_valNet == null) return;
            if (s == null)
            {
                _valNet.Text = "—";
                _valNet.Foreground = _brDim;
                SetBar(_barNet, 0);
                return;
            }
            double rx = s.NetRxMBs, tx = s.NetTxMBs;
            Brush fg = (rx >= 0.05 || tx >= 0.05) ? _brNetVal : _brDim;
            if (!ReferenceEquals(_valNet.Foreground, fg)) _valNet.Foreground = fg;
            string text = string.Format(Inv, "↓{0:0.0} ↑{1:0.0}", rx, tx);
            if (_valNet.Text != text) _valNet.Text = text;

            // у сети нет «процентов»: шкала показывает активность по
            // логарифму скорости — 50 МБ/с заполняет её целиком
            double speed = rx + tx;
            double lvl = speed <= 0.01 ? 0 : Math.Min(100.0,
                Math.Log10(1 + speed) / Math.Log10(51.0) * 100.0);
            SetBar(_barNet, lvl);
        }

        void SetValue(TextBlock t, double pct, bool available)
        {
            int i = _valBlocks == null ? -1 : Array.IndexOf(_valBlocks, t);

            if (!available)
            {
                if (i >= 0)
                {
                    _animOn[i] = false;
                    _animShown[i] = -1;
                    if (_lastPct != null && i < _lastPct.Length) _lastPct[i] = int.MinValue;
                }
                if (i >= 0 && _barBlocks != null && i < _barBlocks.Length) SetBar(_barBlocks[i], 0);
                t.Text = "—";
                t.Foreground = _brDim;
                return;
            }
            if (double.IsNaN(pct)) return;

            double p = Math.Max(0.0, Math.Min(100.0, pct));
            Brush normal = (i >= 0 && _valBrushes != null && i < _valBrushes.Length)
                ? _valBrushes[i] : _brNormal;
            Brush fg = normal;
            if (Prefs.ColorByLoad)
            {
                if (p >= Prefs.CritAt) fg = _brWarn;
                else if (p >= Prefs.WarnAt)
                    fg = _brAmber ?? (_brAmber = Theme.B(Theme.BattMidColor));
            }
            else if (p >= 90) fg = _brWarn;
            if (!ReferenceEquals(t.Foreground, fg)) t.Foreground = fg;

            if (i < 0)
            {
                t.Text = string.Format(Inv, "{0:0}%", p);
                return;
            }

            // мелкое колебание — сразу к цели, без кадров анимации
            if (_animShown[i] < 0 || Math.Abs(p - _animShown[i]) < AnimSnap)
            {
                _animShown[i] = p;
                _animOn[i] = false;
                RenderBlock(i, p);
                return;
            }

            _animTarget[i] = p;
            _animOn[i] = true;
            if (!_animTimer.IsEnabled) _animTimer.Start();
        }

        /// <summary>
        /// Показать значение блока: число и высоту шкалы. Присваиваем только
        /// изменившееся — прозрачное WPF-окно перерисовывается целиком на любое
        /// изменение, поэтому одинаковые значения трогать незачем.
        /// </summary>
        void RenderBlock(int i, double shown)
        {
            if (_valBlocks == null || i < 0 || i >= _valBlocks.Length) return;

            int pct = (int)Math.Round(shown);
            if (_lastPct == null || i >= _lastPct.Length || _lastPct[i] != pct)
            {
                if (_lastPct != null && i < _lastPct.Length) _lastPct[i] = pct;
                _valBlocks[i].Text = string.Format(Inv, "{0:0}%", shown);
            }
            if (_barBlocks != null && i < _barBlocks.Length)
                SetBar(_barBlocks[i], shown);
        }

        /// <summary>Доводит показанные числа к целям; останавливается, когда всё сошлось.</summary>
        void AnimTick()
        {
            bool moving = false;
            for (int i = 0; i < _animOn.Length; i++)
            {
                if (!_animOn[i]) continue;

                double diff = _animTarget[i] - _animShown[i];
                if (Math.Abs(diff) < 0.5)
                {
                    _animShown[i] = _animTarget[i];
                }
                else
                {
                    _animShown[i] += diff * 0.38;
                    moving = true;
                }
                RenderBlock(i, _animShown[i]);
            }
            if (!moving) _animTimer.Stop();
        }
    }
}

