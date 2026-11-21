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
    public sealed partial class Dashboard
    {
        static readonly IFormatProvider Inv = CultureInfo.InvariantCulture;

        const double DefW = 920, DefH = 660, MinW = 700, MinH = 280;   // высоту дальше ведёт UpdateMinHeight

        // --- оформление окна целиком берётся из Theme: цвета, шрифты, радиусы и рамки те же,
        // что у карточки виджета, плашек и настроек, поэтому окно выглядит так же в неоне,
        // в тёмной и в светлой темах. Здесь только короткие имена для частых значений.
        static string cWin { get { return Theme.Bg; } }
        static string cWinTop { get { return Theme.BgTop; } }              // фон окна — тот же градиент, что у карточки
        static string cWinBottom { get { return Theme.BgBottom; } }
        static string cFrame { get { return Theme.CardStroke; } }          // рамка окна
        static string cEdge { get { return Theme.GroupStroke; } }          // рамки блоков, как у строк карточки
        static string cCard { get { return Theme.Surface; } }              // заливка блоков, как у плашек
        static string cHi { get { return Theme.TextHi; } }
        static string cMid { get { return Theme.TextMid; } }
        static string cLabel { get { return Theme.TextLabel; } }
        static string cAccent { get { return Theme.Accent; } }             // заголовки, активные вкладки, колокольчик
        static string cOk { get { return Theme.OkColor; } }                // «всё в порядке»

        static Brush Vertical(string top, string bottom)
        {
            var b = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString(top),
                (Color)ColorConverter.ConvertFromString(bottom), 90);
            b.Freeze();
            return b;
        }

        readonly Widget _w;
        readonly string _file;

        Window _win;
        ScrollViewer _scroll;
        Panel _host;
        bool _fitAuto = true;      // высоту ведёт содержимое, пока окно не меняли мышью
        double _lastAutoH;         // последняя высота, выставленная подгонкой: от неё отличаем ручное изменение

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
            _win.Background = Theme.B(cWin);
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
            frame.BorderBrush = Theme.B(cFrame);
            frame.BorderThickness = new Thickness(1);
            frame.Background = Vertical(cWinTop, cWinBottom);

            // Windows 11: скруглённые углы и рамка по скруглению от самой системы;
            // собственная прямоугольная рамка в углах рвалась. Где система этого не умеет
            // (Windows 10), остаётся наша прямоугольная рамка.
            _win.SourceInitialized += delegate
            {
                Color stroke = (Color)ColorConverter.ConvertFromString(cFrame);
                Color bg = (Color)ColorConverter.ConvertFromString(cWin);
                double a = stroke.A / 255.0;
                int r = (int)Math.Round(stroke.R * a + bg.R * (1 - a));
                int g = (int)Math.Round(stroke.G * a + bg.G * (1 - a));
                int bl = (int)Math.Round(stroke.B * a + bg.B * (1 - a));
                // пиксельная тема и «Терминал»: углы окна квадратные, рамку рисуем сами; иначе скругление и рамку даёт DWM
                IntPtr hw = new WindowInteropHelper(_win).Handle;
                if (Theme.Retro || Theme.Terminal) Native.SquareCorners(hw);
                else if (Native.RoundCorners(hw, r | (g << 8) | (bl << 16)))
                    frame.BorderThickness = new Thickness(0);

                // размеры окна при разворачивании на весь экран задаём сами (см. MaxHook)
                HwndSource src = HwndSource.FromHwnd(new WindowInteropHelper(_win).Handle);
                if (src != null) src.AddHook(MaxHook);
            };

            var shell = new Grid();
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });      // вердикт
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // под заголовком — тонкая линия: при прокрутке плитки уходят под неё,
            // а не обрезаются вплотную к чипам
            var headBox = new StackPanel();
            headBox.Children.Add(BuildHeaderDl());
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
            // поля уходят в отступ панели блоков, а не области прокрутки —
            // иначе она обрезала бы края блоков у самой границы
            _scroll.Padding = new Thickness(0, 0, 4, 0);
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
                (Color)ColorConverter.ConvertFromString(cWinTop),
                (Color)ColorConverter.ConvertFromString(Theme.Fade(cWinTop, 0)),
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
            _win.SizeChanged += delegate(object s, SizeChangedEventArgs e)
            {
                // высота изменилась не нами — пользователь тянет край: подгонку выключаем
                if (_fitAuto && e.HeightChanged && _lastAutoH > 0 && Math.Abs(e.NewSize.Height - _lastAutoH) > 3)
                    _fitAuto = false;
            };

            // Окно без рамки при разворачивании на весь экран выходит за рабочую область на толщину
            // невидимой рамки (около 9 пикселей с каждой стороны): край содержимого оказывался за экраном.
            // Возвращаем его внутрь полем, равным измеренному выходу.
            _win.StateChanged += delegate
            {
                if (_win.WindowState != WindowState.Maximized) { frame.Margin = new Thickness(0); return; }
                _win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate
                {
                    try
                    {
                        IntPtr hh = new WindowInteropHelper(_win).Handle;
                        Native.RECT pr;
                        Native.RECT wa = new Native.RECT();
                        if (!Native.GetWindowRect(hh, out pr)) return;
                        if (!Native.GetWorkAreaAt(pr.Left + pr.Width / 2, pr.Top + pr.Height / 2, out wa)) return;
                        double k = VisualTreeHelper.GetDpi(_win).DpiScaleX;
                        if (k <= 0) k = 1;
                        frame.Margin = new Thickness(
                            Math.Max(0, wa.Left - pr.Left) / k, Math.Max(0, wa.Top - pr.Top) / k,
                            Math.Max(0, pr.Right - wa.Right) / k, Math.Max(0, pr.Bottom - wa.Bottom) / k);
                    }
                    catch (Exception ex) { ErrorLog.Write("dashboard maximize margin", ex); }
                });
            };
            _win.Closed += delegate { StopClock(); _win = null; _vSampler = null; _vProcs = null; _vKey = null; };

            UpdateChips();
            BuildBodyDl();
        }

        /// <summary>Чип в стиле окна настроек: мягкая заливка, при наведении — рамка.</summary>
        Border Chip(string label, Action click, out TextBlock text)
        {
            var t = new TextBlock();
            t.Text = label;
            t.FontFamily = new FontFamily(Theme.FontUi);
            t.FontSize = Theme.Fs(10.5);
            t.Foreground = Theme.B(cMid);
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

        // ------------------------------------------------------------ разворачивание

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MmPoint { public int X, Y; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MinMaxInfo
        {
            public MmPoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
        }

        /// <summary>
        /// Окно без рамки WPF разворачивает с выходом за рабочую область (на 9 пикселей влево и
        /// вверх, а по высоте на столько же не доходит до низа), и снизу оставалась полоса. Размер и
        /// место развёрнутого окна задаём точно по рабочей области монитора, на котором оно стоит.
        /// </summary>
        static IntPtr MaxHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
            try
            {
                IntPtr mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
                var mi = new Native.MONITORINFO();
                mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO));
                if (mon == IntPtr.Zero || !Native.GetMonitorInfo(mon, ref mi)) return IntPtr.Zero;

                var mmi = (MinMaxInfo)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(MinMaxInfo));
                mmi.MaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                mmi.MaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
                mmi.MaxSize.X = mi.rcWork.Width;
                mmi.MaxSize.Y = mi.rcWork.Height;
                System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }
            catch { }
            return IntPtr.Zero;
        }

        // ------------------------------------------------------------ строки

        /// <summary>
        /// Высота рабочей области того монитора, где окно стоит (или будет стоять), в DIP.
        /// SystemParameters.WorkArea — это всегда основной монитор: у второго экрана другая
        /// высота и масштаб, и потолок окна получался слишком низким.
        /// </summary>
        double WorkHeightDip()
        {
            try
            {
                Native.RECT wa = new Native.RECT();
                bool ok = _hasPos
                    ? Native.GetWorkAreaAt(_posX + 50, _posY + 50, out wa)
                    : Native.GetWorkArea(_w.Handle, out wa);
                if (ok && wa.Height > 0)
                {
                    double s = VisualTreeHelper.GetDpi(_win).DpiScaleY;
                    return Math.Floor(wa.Height / (s > 0 ? s : 1));
                }
            }
            catch { }
            return SystemParameters.WorkArea.Height;
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
            if (_win == null || _scroll == null || _host == null) return;

            // отступы области прокрутки (8 сверху, 4 снизу)
            double chrome = _win.ActualHeight - _scroll.ActualHeight;
            if (_host != null)
            {
                // Высота свободная. Пока окно не меняли мышью, оно подстраивается под
                // содержимое (не выше рабочей области), чтобы ничего не пряталось: списки внизу
                // заполняются не сразу. После ручного изменения высоты (и на весь экран) подгонка
                // высоты выключается, но плотность вида (ApplyDensity) продолжает следить за местом:
                // не помещается — убираем второстепенное, места стало больше — возвращаем.
                // DesiredSize — высота содержимого без растяжения.
                double need = _host.DesiredSize.Height;
                if (need <= 20) return;                     // ещё не измерены
                if (_win.WindowState == WindowState.Minimized) return;
                if (_win.WindowState != WindowState.Normal) _fitAuto = false;
                double wa = WorkHeightDip();
                _win.MinHeight = MinH;
                _win.MaxHeight = wa;

                double view = _scroll.ActualHeight;         // место под содержимое сейчас
                if (view <= 50) return;

                // Строка вердикта лежит вне области прокрутки: при её скрытии освобождается место, а при
                // возврате пропадает. Поэтому сравниваем полную высоту окна (шапка + вердикт + содержимое),
                // а не только область прокрутки, иначе вид «мигает»: убрали — влезло — вернули — не влезло.
                bool vShown = _vBox != null && _vBox.Visibility == Visibility.Visible;
                if (vShown && _vBox.ActualHeight > 0) _vH = _vBox.ActualHeight + _vBox.Margin.Top + _vBox.Margin.Bottom;
                double head = chrome - (vShown ? _vH : 0);
                double total = need + head + (_density < 1 ? _vH : 0);      // сколько высоты окну нужно на этом уровне
                double availT = _fitAuto ? wa : _win.ActualHeight;          // пока высоту ведёт подгонка — вся рабочая область
                _totAt[_density] = total;
                _totW[_density] = _win.ActualWidth;      // при другой ширине текст переносится иначе, и замер устаревает
                if (total > availT + 1 && _density < 4)
                {
                    _density++;
                    _escAvail = availT;
                    ApplyDensity();
                    ScheduleFit();
                    return;
                }
                // вернуть убранное можно, только если места стало больше, чем когда его убрали (развернули окно,
                // перенесли на большой монитор), и прежний уровень с запасом помещается — иначе вид дёргается.
                // Замер прежнего уровня мог устареть (окно было уже, текст переносился и занимал больше места):
                // тогда пробуем вернуть уровень и смотрим на результат; не поместилось — _escAvail снова запомнит это место,
                // и новых попыток не будет, пока места не станет больше
                if (_density > 0 && availT > _escAvail + 8
                    && (_totAt[_density - 1] <= 0 || Math.Abs(_win.ActualWidth - _totW[_density - 1]) > 30 || _totAt[_density - 1] + 8 <= availT))
                {
                    _density--;
                    ApplyDensity();
                    ScheduleFit();
                    return;
                }
                if (_fitAuto)
                {
                    double fit = Math.Max(MinH, Math.Min(Math.Ceiling(chrome + need), wa));      // need уже с полями панели
                    if (Math.Abs(_win.Height - fit) > 1)
                    {
                        _lastAutoH = fit;
                        _win.Height = fit;
                    }
                }
                return;
            }
        }

        TextBlock Small(double size, string color)
        {
            var tb = new TextBlock();
            tb.FontFamily = new FontFamily(Theme.FontUi);
            tb.FontSize = Theme.Fs(size);
            tb.Foreground = Theme.B(color);
            return tb;
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

        /// <summary>
        /// Цвет значения по порогам из настроек (Warn/Crit); обычное значение — цвет самой метрики.
        /// </summary>
        static string SeverityColor(double pct, string metric)
        {
            string normal = metric;
            if (pct < 0) return normal;
            if (pct >= Prefs.CritAt) return Theme.WarnColor;
            if (pct >= Prefs.WarnAt) return Theme.BattMidColor;
            return normal;
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
                    run.Foreground = Theme.B(cHi);
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
            _vText.Foreground = Theme.B(cHi);
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
            string c = level >= 2 ? Theme.WarnColor : level == 1 ? Theme.BattMidColor : cOk;
            _vDot.Fill = Theme.B(c);
            // в норме — обычная плашка темы (заливка и рамка как у плашек), при замечаниях — цветная
            _vBox.Background = Theme.B(level == 0 ? cCard : Theme.Fade(c, 0x22));
            _vBox.BorderBrush = Theme.B(level == 0 ? cEdge : Theme.Fade(c, 0x66));
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
            RefreshDl(s);
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
