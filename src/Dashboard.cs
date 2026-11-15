using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace SysWidget
{
    /// <summary>
    /// Столбики загрузки по логическим ядрам: тонкий ряд под графиком ЦП.
    /// </summary>
    public sealed class CoreBars : FrameworkElement
    {
        readonly Brush _track, _fill;
        double[] _v = new double[0];

        public CoreBars(string color)
        {
            Height = 30;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            IsHitTestVisible = false;
            _track = Theme.B(Theme.Fade(color, 0x33));
            _fill = Theme.B(color);
        }

        public void Set(double[] v)
        {
            _v = v ?? new double[0];
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            int n = _v.Length;
            if (n == 0) return;
            double w = ActualWidth, h = ActualHeight;
            double gap = n > 24 ? 1 : 2;
            double bw = Math.Max(1, (w - gap * (n - 1)) / n);
            for (int i = 0; i < n; i++)
            {
                double x = i * (bw + gap);
                double f = Math.Min(1.0, Math.Max(0.0, _v[i] / 100.0));
                dc.DrawRectangle(_track, null, new Rect(x, 0, bw, h));
                double fh = Math.Max(f > 0 ? 1 : 0, f * h);
                dc.DrawRectangle(_fill, null, new Rect(x, h - fh, bw, fh));
            }
        }
    }

    /// <summary>
    /// Главное окно мониторинга: строки метрик с графиками и подробностями.
    /// Компактная карточка остаётся как есть, а это — «развёрнутый» вид для случаев,
    /// когда нужно видеть больше: график за 2 минуты, 15 минут, час или сутки,
    /// значение и время под курсором, подробности из тех же текстов, что в плашках.
    /// </summary>
    public sealed class Dashboard
    {
        static readonly IFormatProvider Inv = CultureInfo.InvariantCulture;

        const double DefW = 920, DefH = 660, MinW = 700, MinH = 280;   // высоту дальше ведёт UpdateMinHeight

        sealed class Tile
        {
            public Border Root;
            public TextBlock Title, Value, Unit, Note, Detail, AxisL, AxisM, AxisR;
            public Border Bar;
            public Panel SparkHost;         // где стоит график: средняя колонка
            public Sparkline Spark;
            public CoreBars Cores;
            public DashTileData Data;       // последние показанные данные — для подсказки под курсором
        }

        readonly Widget _w;
        readonly string _file;

        Window _win;
        ScrollViewer _scroll;
        Panel _host;
        readonly List<Tile> _tiles = new List<Tile>();

        int _period;       // период графика: 0 — 2 мин, 1 — 15 мин, 2 — 1 ч, 3 — 24 ч
        // положение — в физических пикселях экрана, как у окна настроек: так оно не зависит
        // от масштаба монитора. Window.Left/Top (DIP) при разных масштабах на двух мониторах
        // сдвигали окно при каждом открытии.
        int _posX, _posY;
        bool _hasPos;
        double _width = DefW, _height = DefH;      // размер — в DIP, как задаёт Window.Width/Height

        readonly Border[] _chips = new Border[4];
        readonly TextBlock[] _chipTexts = new TextBlock[4];
        TextBlock _title;
        FrameworkElement _head;

        public Dashboard(Widget w)
        {
            _w = w;
            _file = AppPaths.File_("dashboard.json");
            Load();
        }

        public bool IsOpen { get { return _win != null && _win.IsVisible; } }

        /// <summary>Сколько точек ряда укладывается по ширине графика: секунды у коротких периодов, минуты у длинных.</summary>
        int Points { get { return Widget.DashPeriodSec[_period] / (_period >= 2 ? 60 : 1); } }

        // ------------------------------------------------------------ показ

        public void Show()
        {
            if (_win == null) Build();

            // показываем невидимым, ставим на место и только потом проявляем —
            // иначе окно мелькнуло бы в углу экрана
            _win.Opacity = 0;
            _win.Show();
            if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
            SysStats s = _w.LastStats;
            if (s != null) Refresh(s);
            _win.UpdateLayout();
            UpdateMinHeight();      // в списке окно сразу получает высоту по строкам, до расстановки на место
            _win.UpdateLayout();
            Place();
            _win.Opacity = 1;
            _win.Activate();
        }

        public void Hide()
        {
            if (_win != null) _win.Close();
        }

        public void Close()
        {
            if (_win != null) { try { _win.Close(); } catch { } }
        }

        // ------------------------------------------------------------ сборка

        void Build()
        {
            _win = new Window();
            _win.Title = Lang.S("SysWidget — мониторинг", "SysWidget — monitor");
            _win.WindowStyle = WindowStyle.None;
            _win.ResizeMode = ResizeMode.CanResize;
            _win.ShowInTaskbar = false;      // открывается кнопкой на карточке или из трея, отдельный значок на панели задач не нужен
            _win.Background = Theme.B(Theme.Bg);
            _win.MinWidth = MinW;
            _win.MinHeight = MinH;
            _win.Width = _width;
            _win.Height = _height;
            _win.UseLayoutRounding = true;
            _win.SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(_win, TextFormattingMode.Display);

            try
            {
                string ico = Path.Combine(AppPaths.AppDir, "icon.ico");
                if (File.Exists(ico)) _win.Icon = BitmapFrame.Create(new Uri(ico));
            }
            catch { }

            WindowChrome.SetWindowChrome(_win, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),     // иначе WPF обрезает окно регионом со скруглёнными углами и рамка в углах рвётся
                UseAeroCaptionButtons = false
            });

            _win.WindowStartupLocation = WindowStartupLocation.Manual;     // место ставит Place()
            // До Place() окно показывается за пределами экранов: Opacity у окна без прозрачности
            // не действует, и иначе оно на 40–50 мс мелькало бы в углу первого монитора
            // (WPF ставит окно в (100;100) основного экрана), а потом прыгало на своё место.
            _win.Left = -20000;
            _win.Top = -20000;

            var frame = new Border();
            frame.BorderBrush = Theme.B(Theme.CardStroke);
            frame.BorderThickness = new Thickness(1);

            // Windows 11: скруглённые углы и рамка по скруглению от самой системы;
            // собственная прямоугольная рамка в углах рвалась. Где система этого не умеет
            // (Windows 10), остаётся наша прямоугольная рамка.
            _win.SourceInitialized += delegate
            {
                Color stroke = (Color)ColorConverter.ConvertFromString(Theme.CardStroke);
                Color bg = (Color)ColorConverter.ConvertFromString(Theme.Bg);
                double a = stroke.A / 255.0;
                int r = (int)Math.Round(stroke.R * a + bg.R * (1 - a));
                int g = (int)Math.Round(stroke.G * a + bg.G * (1 - a));
                int bl = (int)Math.Round(stroke.B * a + bg.B * (1 - a));
                if (Native.RoundCorners(new WindowInteropHelper(_win).Handle, r | (g << 8) | (bl << 16)))
                    frame.BorderThickness = new Thickness(0);
            };

            var shell = new Grid();
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });      // вердикт
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // под заголовком — тонкая линия: при прокрутке плитки уходят под неё,
            // а не обрезаются вплотную к чипам
            var headBox = new StackPanel();
            headBox.Children.Add(BuildHeader());
            var sep = new Border();
            sep.Height = 1;
            sep.Background = Theme.B(Theme.StrokeSoft);
            headBox.Children.Add(sep);
            Grid.SetRow(headBox, 0);
            shell.Children.Add(headBox);

            // под заголовком — полоса вердикта «почему тормозит»
            Grid.SetRow(BuildVerdict(), 1);
            shell.Children.Add(_vBox);

            _scroll = new ScrollViewer();
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.Padding = new Thickness(14, 8, 4, 4);
            _scroll.Focusable = false;
            _scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), SettingsWindow.ScrollBarStyle());
            _scroll.SizeChanged += delegate { ScheduleFit(); };
            // верхний край области прокрутки мягко затухает в цвет фона
            var fade = new System.Windows.Shapes.Rectangle();
            fade.Height = 8;
            fade.VerticalAlignment = VerticalAlignment.Top;
            fade.IsHitTestVisible = false;
            fade.Margin = new Thickness(0, 0, 10, 0);       // справа оставляем полосу прокрутки
            fade.Fill = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString(Theme.Bg),
                (Color)ColorConverter.ConvertFromString(Theme.Fade(Theme.Bg, 0)),
                90);

            var body = new Grid();
            body.Children.Add(_scroll);
            body.Children.Add(fade);
            Grid.SetRow(body, 2);
            shell.Children.Add(body);

            frame.Child = shell;
            _win.Content = frame;

            _win.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Hide(); };
            _win.Closing += delegate { Save(); };      // пока окно живо, положение ещё можно прочитать
            _win.Closed += delegate { _win = null; _tiles.Clear(); _vSampler = null; _vProcs = null; _vKey = null; };

            UpdateChips();
            BuildTiles();
        }

        FrameworkElement BuildHeader()
        {
            var grid = new Grid();
            grid.Background = Brushes.Transparent;      // чтобы принимать мышь по всей полосе
            grid.Margin = new Thickness(14, 10, 10, 8);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _head = grid;

            _title = new TextBlock();
            _title.Text = Lang.S("Мониторинг", "Monitor");
            _title.FontFamily = new FontFamily(Theme.FontUi);
            _title.FontSize = Theme.Fs(12);
            _title.FontWeight = FontWeights.SemiBold;
            _title.Foreground = Theme.B(Theme.TextHi);
            _title.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_title, 0);
            grid.Children.Add(_title);

            var chips = new StackPanel();
            chips.Orientation = Orientation.Horizontal;
            Grid.SetColumn(chips, 1);

            string[] labels = { Lang.S("2 мин", "2 min"), Lang.S("15 мин", "15 min"), Lang.S("1 ч", "1 h"), Lang.S("24 ч", "24 h") };
            for (int i = 0; i < 4; i++)
            {
                int period = i;
                _chips[i] = Chip(labels[i], delegate { SetPeriod(period); }, out _chipTexts[i]);
                chips.Children.Add(_chips[i]);
            }

            TextBlock closeText;
            Border close = Chip("✕", delegate { Hide(); }, out closeText);
            close.Margin = new Thickness(14, 0, 0, 0);
            closeText.FontSize = Theme.Fs(11);
            chips.Children.Add(close);
            grid.Children.Add(chips);

            // окно без рамки — тащим за свободное место полосы
            grid.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (!ReferenceEquals(e.OriginalSource, grid) && !ReferenceEquals(e.OriginalSource, _title)) return;
                if (e.ClickCount > 1)
                {
                    _win.WindowState = _win.WindowState == WindowState.Maximized
                        ? WindowState.Normal : WindowState.Maximized;
                    return;
                }
                try { _win.DragMove(); } catch { }
            };
            return grid;
        }

        /// <summary>Чип в стиле окна настроек: мягкая заливка, при наведении — рамка.</summary>
        Border Chip(string label, Action click, out TextBlock text)
        {
            var t = new TextBlock();
            t.Text = label;
            t.FontFamily = new FontFamily(Theme.FontUi);
            t.FontSize = Theme.Fs(10.5);
            t.Foreground = Theme.B(Theme.TextMid);
            t.VerticalAlignment = VerticalAlignment.Center;

            var b = new Border();
            b.CornerRadius = new CornerRadius(Theme.GroupRadius);
            b.Padding = new Thickness(9, 4, 9, 4);
            b.Margin = new Thickness(6, 0, 0, 0);
            b.BorderThickness = new Thickness(1);
            b.BorderBrush = Brushes.Transparent;
            b.Background = Theme.B(Theme.StrokeSoft);
            b.Cursor = Cursors.Hand;
            b.Child = t;
            b.Uid = "0";
            b.MouseEnter += delegate
            {
                b.Background = Theme.B(b.Uid == "1" ? Theme.SurfaceActive : Theme.BtnHover);
                b.BorderBrush = Theme.B(Theme.Stroke);
            };
            b.MouseLeave += delegate
            {
                b.Background = Theme.B(b.Uid == "1" ? Theme.SurfaceActive : Theme.StrokeSoft);
                b.BorderBrush = Brushes.Transparent;
            };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; click(); };
            text = t;
            return b;
        }

        static void Mark(Border chip, TextBlock text, bool on)
        {
            chip.Uid = on ? "1" : "0";
            chip.Background = Theme.B(on ? Theme.SurfaceActive : Theme.StrokeSoft);
            text.Foreground = Theme.B(on ? Theme.TextHi : Theme.TextMid);
        }

        void UpdateChips()
        {
            for (int i = 0; i < _chips.Length; i++) Mark(_chips[i], _chipTexts[i], i == _period);
        }

        void SetPeriod(int period)
        {
            if (_period == period) return;
            _period = period;
            UpdateChips();
            SysStats s = _w.LastStats;
            if (s != null) Refresh(s);
            Save();
        }

        // ------------------------------------------------------------ строки

        void BuildTiles()
        {
            _tiles.Clear();
            var sp = new StackPanel();
            sp.Orientation = Orientation.Vertical;
            _host = sp;
            _scroll.Content = _host;
            if (_win != null) _win.MinWidth = MinW;

            int n = _w.DashSlots;
            for (int i = 0; i < n; i++)
            {
                Tile t = MakeTile();
                _tiles.Add(t);
                _host.Children.Add(t.Root);
            }
            ScheduleFit();
        }

        /// <summary>Пересчитать высоту окна после раскладки: высота строк зависит от переносов текста.</summary>
        void ScheduleFit()
        {
            if (_win != null)
                _win.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)UpdateMinHeight);
        }

        /// <summary>
        /// Высоту окна ведут сами строки — ровно заголовок и все строки, ни пустого места под
        /// последней, ни обрезанной строки. Если строки не помещаются на экране, окно упирается
        /// в рабочую область, остальное прокручивается. Высоту берём суммой строк, а не высотой
        /// панели: панель растягивается на всю область прокрутки, и окно не могло бы уменьшиться.
        /// </summary>
        void UpdateMinHeight()
        {
            if (_win == null || _scroll == null || _tiles.Count == 0) return;

            // отступы области прокрутки (8 сверху, 4 снизу)
            double chrome = _win.ActualHeight - _scroll.ActualHeight;
            double rows = 0;
            for (int i = 0; i < _tiles.Count; i++)
                rows += _tiles[i].Root.ActualHeight + _tiles[i].Root.Margin.Top + _tiles[i].Root.Margin.Bottom;
            if (rows <= 20) return;                         // ещё не измерены

            double content = Math.Ceiling(chrome + rows + 12);
            double target = Math.Max(160, Math.Min(content, SystemParameters.WorkArea.Height));
            _win.MinHeight = target;
            _win.MaxHeight = target;
            if (Math.Abs(_win.Height - target) > 1) _win.Height = target;
        }

        TextBlock Small(double size, string color)
        {
            var tb = new TextBlock();
            tb.FontFamily = new FontFamily(Theme.FontUi);
            tb.FontSize = Theme.Fs(size);
            tb.Foreground = Theme.B(color);
            return tb;
        }

        /// <summary>
        /// Строка метрики: слева название, значение и «за N: ср · макс», в середине график со
        /// шкалой времени (у ЦП ещё столбики ядер), справа подробности — первая строка крупнее.
        /// </summary>
        Tile MakeTile()
        {
            var t = new Tile();

            t.Root = new Border();
            t.Root.Background = Theme.B(Theme.Surface);
            t.Root.BorderBrush = Theme.B(Theme.GroupStroke);
            t.Root.BorderThickness = new Thickness(1);
            t.Root.CornerRadius = new CornerRadius(Theme.RadiusCard);
            t.Root.Padding = new Thickness(14, 10, 14, 10);
            t.Root.Margin = new Thickness(0, 0, 10, 8);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star), MinWidth = 220 });
            t.Root.Child = grid;

            // колонка 0: название, значение, строка «за N: ср · макс»
            var left = new StackPanel();
            left.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(left, 0);

            var head = new StackPanel();
            head.Orientation = Orientation.Horizontal;
            t.Bar = new Border();
            t.Bar.Width = 3;
            t.Bar.Height = 13;
            t.Bar.CornerRadius = new CornerRadius(Theme.BarRadius);
            t.Bar.VerticalAlignment = VerticalAlignment.Center;
            t.Bar.Margin = new Thickness(0, 0, 8, 0);
            t.Title = Small(11.5, Theme.TextLabel);
            t.Title.FontWeight = FontWeights.SemiBold;
            t.Title.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(t.Bar);
            head.Children.Add(t.Title);
            left.Children.Add(head);

            var val = new StackPanel();
            val.Orientation = Orientation.Horizontal;
            val.Margin = new Thickness(0, 3, 0, 0);
            t.Value = Small(20, Theme.TextHi);
            t.Value.FontFamily = new FontFamily(Theme.FontNum);
            t.Value.FontWeight = FontWeights.SemiBold;
            t.Value.VerticalAlignment = VerticalAlignment.Center;
            Typography.SetNumeralAlignment(t.Value, FontNumeralAlignment.Tabular);
            t.Unit = Small(9.5, Theme.TextMid);
            t.Unit.VerticalAlignment = VerticalAlignment.Center;
            t.Unit.Margin = new Thickness(5, 4, 0, 0);
            val.Children.Add(t.Value);
            val.Children.Add(t.Unit);
            left.Children.Add(val);

            t.Note = Small(9.5, Theme.TextMid);
            t.Note.TextWrapping = TextWrapping.Wrap;
            t.Note.Margin = new Thickness(0, 3, 8, 0);
            left.Children.Add(t.Note);
            grid.Children.Add(left);

            // колонка 1: график (создаётся в EnsureSpark), под ним шкала времени, у ЦП — ядра
            var mid = new StackPanel();
            mid.VerticalAlignment = VerticalAlignment.Center;
            mid.Margin = new Thickness(0, 0, 18, 0);
            Grid.SetColumn(mid, 1);
            t.SparkHost = mid;

            var axis = new Grid();
            axis.Margin = new Thickness(0, 3, 0, 0);
            for (int i = 0; i < 3; i++) axis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            t.AxisL = Small(9, Theme.TextMid);
            t.AxisM = Small(9, Theme.TextMid);
            t.AxisM.HorizontalAlignment = HorizontalAlignment.Center;
            t.AxisR = Small(9, Theme.TextMid);
            t.AxisR.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(t.AxisL, 0);
            Grid.SetColumn(t.AxisM, 1);
            Grid.SetColumn(t.AxisR, 2);
            axis.Children.Add(t.AxisL);
            axis.Children.Add(t.AxisM);
            axis.Children.Add(t.AxisR);
            mid.Children.Add(axis);
            grid.Children.Add(mid);

            // колонка 2: подробности
            t.Detail = Small(10.5, Theme.TextLabel);
            t.Detail.TextWrapping = TextWrapping.Wrap;
            t.Detail.LineHeight = Theme.Fs(10.5) * 1.45;
            t.Detail.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            t.Detail.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(t.Detail, 2);
            grid.Children.Add(t.Detail);
            return t;
        }

        /// <summary>График и столбики ядер создаём по цвету метрики — один раз.</summary>
        void EnsureSpark(Tile t, DashTileData d)
        {
            if (t.Spark == null)
            {
                t.Spark = new Sparkline(d.Color);
                t.Spark.Width = double.NaN;
                t.Spark.HorizontalAlignment = HorizontalAlignment.Stretch;
                t.Spark.Height = 46;
                t.Spark.MidGrid = !d.Net;
                t.Spark.Interactive = true;
                Tile row = t;
                t.Spark.Describe = delegate(int i, int n) { return DescribePoint(row, i); };
                t.SparkHost.Children.Insert(0, t.Spark);
                t.Bar.Background = Theme.B(d.Color);
            }
            if (d.Cores != null && d.Cores.Length > 0 && t.Cores == null)
            {
                t.Cores = new CoreBars(d.Color);
                t.Cores.Height = 14;
                t.Cores.Margin = new Thickness(0, 6, 0, 0);
                t.SparkHost.Children.Add(t.Cores);
            }
        }

        /// <summary>Подпись момента: у коротких периодов «−45 с», у длинных — время на часах.</summary>
        string When(int back)
        {
            if (_period < 2)
            {
                if (back <= 0) return Lang.S("сейчас", "now");
                if (back < 60) return "−" + back + Lang.S(" с", " s");
                return "−" + (back / 60) + Lang.S(" мин ", " min ") + (back % 60) + Lang.S(" с", " s");
            }
            DateTime at = DateTime.Now.AddMinutes(-back);
            string clock = at.ToString("HH:mm", CultureInfo.CurrentCulture);
            if (at.Date != DateTime.Now.Date) clock = Lang.S("вчера ", "yesterday ") + clock;
            return clock;
        }

        /// <summary>Текст под курсором на графике: момент, значение и (для часа и суток) пик той минуты.</summary>
        string DescribePoint(Tile t, int i)
        {
            DashTileData d = t.Data;
            if (d == null || i < 0 || i >= d.Series.Length) return null;
            string when = When(d.Series.Length - 1 - i);
            double v = d.Series[i];
            if (double.IsNaN(v)) return when + "  ·  " + Lang.S("нет данных", "no data");
            string val = d.Net
                ? v.ToString("0.0", Inv) + Lang.S(" МБ/с", " MB/s")
                : v.ToString("0", Inv) + "%";
            if (d.Peaks != null && i < d.Peaks.Length && !double.IsNaN(d.Peaks[i]) && d.Peaks[i] > v + 0.5)
                val += Lang.S("  (пик ", "  (peak ") + (d.Net ? d.Peaks[i].ToString("0.0", Inv) : d.Peaks[i].ToString("0", Inv)) + ")";
            return when + "  ·  " + val;
        }

        /// <summary>Подписи шкалы времени под графиком.</summary>
        void SetAxis(Tile t)
        {
            string l, m;
            switch (_period)
            {
                case 0: l = Lang.S("−2 мин", "−2 min"); m = Lang.S("−1 мин", "−1 min"); break;
                case 1: l = Lang.S("−15 мин", "−15 min"); m = Lang.S("−8 мин", "−8 min"); break;
                case 2: l = Lang.S("−1 ч", "−1 h"); m = Lang.S("−30 мин", "−30 min"); break;
                default: l = Lang.S("−24 ч", "−24 h"); m = Lang.S("−12 ч", "−12 h"); break;
            }
            SetText(t.AxisL, l);
            SetText(t.AxisM, m);
            SetText(t.AxisR, Lang.S("сейчас", "now"));
        }

        /// <summary>Цвет значения по порогам из настроек (Warn/Crit); у метрик не в процентах — обычный.</summary>
        static string SeverityColor(double pct)
        {
            if (pct < 0) return Theme.TextHi;
            if (pct >= Prefs.CritAt) return Theme.WarnColor;
            if (pct >= Prefs.WarnAt) return Theme.BattMidColor;
            return Theme.TextHi;
        }

        /// <summary>Подробности: первая строка — сводка, выделена цветом и весом, остальные — обычным текстом.</summary>
        static void SetDetail(TextBlock tb, string text)
        {
            if (ReferenceEquals(tb.Tag, text) || (tb.Tag as string) == text) return;
            tb.Tag = text;
            tb.Inlines.Clear();
            string[] lines = (text ?? "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) tb.Inlines.Add(new LineBreak());
                var run = new Run(lines[i]);
                if (i == 0 && lines.Length > 1)
                {
                    run.FontWeight = FontWeights.SemiBold;
                    run.Foreground = Theme.B(Theme.TextHi);
                }
                tb.Inlines.Add(run);
            }
        }

        // ------------------------------------------------------------ вердикт

        Border _vBox;
        System.Windows.Shapes.Ellipse _vDot;
        TextBlock _vText;
        Border _vBtn;
        TextBlock _vBtnText;
        string _vKey;
        ProcessSampler _vSampler;
        volatile ProcessInfo[] _vProcs;
        int _vBusy;
        DateTime _vAt = DateTime.MinValue;

        /// <summary>Полоса под заголовком: цветная точка и одна-две строки «что сейчас нагружает ПК».</summary>
        FrameworkElement BuildVerdict()
        {
            _vBox = new Border();
            _vBox.CornerRadius = new CornerRadius(Theme.GroupRadius);
            _vBox.BorderThickness = new Thickness(1);
            _vBox.Padding = new Thickness(10, 6, 10, 6);
            _vBox.Margin = new Thickness(14, 8, 14, 0);
            _vBox.MinHeight = 30;

            var row = new StackPanel();
            row.Orientation = Orientation.Horizontal;

            _vDot = new System.Windows.Shapes.Ellipse();
            _vDot.Width = 8;
            _vDot.Height = 8;
            _vDot.VerticalAlignment = VerticalAlignment.Top;
            _vDot.Margin = new Thickness(0, 5, 9, 0);
            row.Children.Add(_vDot);

            // ширину текста ограничиваем окном: строка переносится, а не уезжает за край
            _vText = new TextBlock();
            _vText.FontFamily = new FontFamily(Theme.FontUi);
            _vText.FontSize = Theme.Fs(11);
            _vText.Foreground = Theme.B(Theme.TextHi);
            _vText.TextWrapping = TextWrapping.Wrap;
            _vText.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(_vText);

            // «Закрыть <процесс>»: два клика (второй подтверждает), только для процессов, которые можно завершать
            _vBtnText = new TextBlock();
            _vBtnText.FontFamily = new FontFamily(Theme.FontUi);
            _vBtnText.FontSize = Theme.Fs(10.5);
            _vBtnText.Foreground = Theme.B(Theme.TextHi);
            _vBtn = new Border();
            _vBtn.CornerRadius = new CornerRadius(5);
            _vBtn.Padding = new Thickness(9, 3, 9, 3);
            _vBtn.Margin = new Thickness(10, 0, 0, 0);
            _vBtn.VerticalAlignment = VerticalAlignment.Center;
            _vBtn.Cursor = Cursors.Hand;
            _vBtn.BorderThickness = new Thickness(1);
            _vBtn.BorderBrush = Theme.B(Theme.StrokeSoft);
            _vBtn.Background = Theme.B(Theme.Fade(Theme.TextHi, 0x14));
            _vBtn.Child = _vBtnText;
            _vBtn.Visibility = Visibility.Collapsed;
            _vBtn.MouseLeftButtonUp += delegate { OnVerdictClose(); };
            _vBtn.MouseEnter += delegate { _vBtn.Background = Theme.B(Theme.Fade(Theme.TextHi, 0x2A)); };
            _vBtn.MouseLeave += delegate { _vBtn.Background = Theme.B(Theme.Fade(Theme.TextHi, 0x14)); };
            row.Children.Add(_vBtn);
            _vBox.Child = row;

            _vBox.SizeChanged += delegate { FitVerdictText(); };
            _vBtn.SizeChanged += delegate { FitVerdictText(); };
            SetVerdictLook(0);
            _vText.Text = Lang.S("Анализирую нагрузку…", "Analysing load…");
            return _vBox;
        }

        void FitVerdictText()
        {
            double btn = _vBtn.Visibility == Visibility.Visible ? _vBtn.ActualWidth + 10 : 0;
            _vText.MaxWidth = Math.Max(100, _vBox.ActualWidth - 20 - 17 - btn);
        }

        ProcessInfo _vCulprit;
        string _vConfirm;               // имя процесса, ожидающего подтверждения
        DateTime _vConfirmAt;

        void ShowCloseButton()
        {
            if (_vCulprit == null) { _vBtn.Visibility = Visibility.Collapsed; return; }
            bool ask = _vConfirm == _vCulprit.Name && (DateTime.Now - _vConfirmAt).TotalSeconds < 5;
            if (!ask) _vConfirm = null;
            _vBtnText.Text = ask
                ? string.Format(Lang.S("Точно закрыть {0}?", "Really close {0}?"), _vCulprit.Name)
                : string.Format(Lang.S("Закрыть {0}", "Close {0}"), _vCulprit.Name);
            _vBtn.ToolTip = _vCulprit.Count > 1
                ? string.Format(Lang.S("Завершит все процессы {0} ({1}). Несохранённые данные пропадут.",
                                       "Ends all {0} processes ({1}). Unsaved data is lost."), _vCulprit.Name, _vCulprit.Count)
                : Lang.S("Завершит процесс. Несохранённые данные пропадут.", "Ends the process. Unsaved data is lost.");
            _vBtn.Visibility = Visibility.Visible;
            FitVerdictText();
        }

        void OnVerdictClose()
        {
            ProcessInfo g = _vCulprit;
            if (g == null) return;
            if (_vConfirm != g.Name || (DateTime.Now - _vConfirmAt).TotalSeconds >= 5)
            {
                _vConfirm = g.Name;
                _vConfirmAt = DateTime.Now;
                ShowCloseButton();
                return;
            }
            _vConfirm = null;
            int failed = ProcessPanel.Kill(g);
            _vProcs = null;                 // старый замер называл завершённый процесс
            _vAt = DateTime.MinValue;
            _vKey = null;
            if (failed > 0)
            {
                _vCulprit = null;
                _vBtn.Visibility = Visibility.Collapsed;
                _vText.Text = string.Format(Lang.S("{0}: не удалось завершить {1} из {2}", "{0}: could not end {1} of {2}"),
                                            g.Name, failed, g.Pids.Count);
                _vKey = "note";
            }
            else
            {
                _vCulprit = null;
                _vBtn.Visibility = Visibility.Collapsed;
                SetVerdictLook(0);
                _vText.Text = string.Format(Lang.S("{0} закрыт", "{0} closed"), g.Name);
            }
        }

        void SetVerdictLook(int level)
        {
            string c = level >= 2 ? Theme.WarnColor : level == 1 ? Theme.BattMidColor : Theme.RamColor;
            _vDot.Fill = Theme.B(c);
            _vBox.Background = Theme.B(level == 0 ? Theme.StrokeSoft : Theme.Fade(c, 0x22));
            _vBox.BorderBrush = level == 0 ? (Brush)Brushes.Transparent : Theme.B(Theme.Fade(c, 0x66));
        }

        /// <summary>Замер процессов для вердикта: раз в 5 секунд в фоне, пока окно открыто.</summary>
        void SampleProcesses()
        {
            if ((DateTime.UtcNow - _vAt).TotalSeconds < 5) return;
            if (System.Threading.Interlocked.CompareExchange(ref _vBusy, 1, 0) != 0) return;
            _vAt = DateTime.UtcNow;
            ProcessSampler sampler = _vSampler;
            if (sampler == null) { sampler = new ProcessSampler(); _vSampler = sampler; }
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ProcessInfo[] res = sampler.Sample();
                    if (sampler.Samples >= 2) _vProcs = res;      // загрузка ЦП осмысленна со второго замера
                }
                catch { }
                finally { _vBusy = 0; }
            });
        }

        void UpdateVerdict(SysStats s)
        {
            if (_vBox == null) return;
            SampleProcesses();
            DashVerdict v = _w.Diagnose(s, _vProcs);
            _vCulprit = v.Culprit;
            ShowCloseButton();
            string key = v.Level + "|" + v.Text;
            if (key == _vKey) return;
            _vKey = key;
            SetVerdictLook(v.Level);
            _vText.Text = v.Text;
        }

        // ------------------------------------------------------------ обновление

        public void Refresh(SysStats s)
        {
            if (_win == null || s == null) return;

            // накопитель подключили или отключили — число строк изменилось
            if (_tiles.Count != _w.DashSlots) BuildTiles();

            int pts = Points;
            for (int i = 0; i < _tiles.Count; i++)
            {
                Tile t = _tiles[i];
                DashTileData d = _w.DashTile(i, s, _period);
                t.Data = d;
                EnsureSpark(t, d);

                SetText(t.Title, d.Title);
                SetText(t.Value, d.Value);
                string col = SeverityColor(d.Pct);
                if ((t.Value.Tag as string) != col) { t.Value.Tag = col; t.Value.Foreground = Theme.B(col); }
                SetText(t.Unit, d.Unit);
                t.Unit.Visibility = d.Unit.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                SetText(t.Note, d.Note);
                t.Note.Visibility = d.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                SetDetail(t.Detail, d.Detail);
                SetAxis(t);

                t.Spark.Points = pts;
                t.Spark.SetPeaks(d.Peaks);
                t.Spark.Set(d.Series, d.Max);
                if (t.Cores != null) t.Cores.Set(d.Cores);
            }
            UpdateVerdict(s);
            UpdateMinHeight();
        }
        static void SetText(TextBlock tb, string text)
        {
            if (tb.Text != text) tb.Text = text;
        }

        // ------------------------------------------------------------ положение

        /// <summary>
        /// Поставить окно на сохранённое место (в физических пикселях), подтянув его к
        /// рабочей области монитора, а если места ещё нет — по центру монитора карточки.
        /// </summary>
        void Place()
        {
            if (_win == null || !_win.IsVisible) return;
            try
            {
                IntPtr h = new WindowInteropHelper(_win).Handle;
                Native.RECT pr;
                if (!Native.GetWindowRect(h, out pr)) return;

                Native.RECT wa = new Native.RECT();
                bool placed = _hasPos && Native.GetWorkAreaAt(
                    _posX + pr.Width / 2, _posY + pr.Height / 2, out wa);

                int x, y;
                if (!placed)
                {
                    if (!Native.GetWorkArea(_w.Handle, out wa)) return;
                    x = wa.Left + (wa.Width - pr.Width) / 2;
                    y = wa.Top + (wa.Height - pr.Height) / 2;
                }
                else
                {
                    x = _posX;
                    y = _posY;
                }

                if (x + pr.Width > wa.Right) x = wa.Right - pr.Width;
                if (y + pr.Height > wa.Bottom) y = wa.Bottom - pr.Height;
                if (x < wa.Left) x = wa.Left;
                if (y < wa.Top) y = wa.Top;
                Native.MoveTo(h, x, y);
            }
            catch (Exception ex) { ErrorLog.Write("dashboard place", ex); }
        }
        void Load()
        {
            try
            {
                if (!File.Exists(_file)) return;
                string t = File.ReadAllText(_file);
                double l = Num(t, "Left", double.NaN), tp = Num(t, "Top", double.NaN);
                if (!double.IsNaN(l) && !double.IsNaN(tp))
                {
                    _posX = (int)Math.Round(l);
                    _posY = (int)Math.Round(tp);
                    _hasPos = true;
                }
                _width = Math.Max(MinW, Num(t, "Width", DefW));
                _height = Math.Max(MinH, Num(t, "Height", DefH));
                // период графика; раньше был флажок «Long» (15 минут вместо 2)
                _period = (int)Num(t, "Period", Flag(t, "Long") ? 1 : 0);
                if (_period < 0 || _period >= Widget.DashPeriodSec.Length) _period = 0;
            }
            catch (Exception ex) { ErrorLog.Write("load dashboard", ex); }
        }

        void Save()
        {
            try
            {
                if (_win != null && _win.WindowState == WindowState.Normal)
                {
                    Native.RECT r;
                    if (Native.GetWindowRect(new WindowInteropHelper(_win).Handle, out r))
                    {
                        _posX = r.Left;
                        _posY = r.Top;
                        _hasPos = true;
                    }
                    _width = _win.ActualWidth;
                    _height = _win.ActualHeight;
                }
                if (!_hasPos) return;
                AtomicFile.WriteAllText(_file, string.Format(Inv,
                    "{{\"Left\":{0:0},\"Top\":{1:0},\"Width\":{2:0},\"Height\":{3:0},\"Period\":{4}}}",
                    _posX, _posY, _width, _height, _period));
            }
            catch (Exception ex) { ErrorLog.Write("save dashboard", ex); }
        }

        static double Num(string t, string key, double def)
        {
            Match m = Regex.Match(t, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+)");
            double v;
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, Inv, out v) ? v : def;
        }

        static bool Flag(string t, string key)
        {
            return Regex.IsMatch(t, "\"" + key + "\"\\s*:\\s*true", RegexOptions.IgnoreCase);
        }
    }
}
