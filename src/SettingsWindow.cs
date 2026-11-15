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
using System.Windows.Threading;

namespace SysWidget
{
    /// <summary>
    /// Окно настроек. Оформление то же, что у карточки:
    /// тот же фон, рамка, скругления, чипы и шрифты. Изменения применяются
    /// сразу — без кнопки «Сохранить», как в остальном приложении.
    ///
    /// Всё, что настраивается, уже умеет сохраняться: строки карточки и
    /// «поверх всех окон» — в state.json, пороги — в alerts.json, автозапуск —
    /// ярлыком в папке «Автозагрузка». Окно лишь переключает эти настройки.
    /// </summary>
    public sealed partial class SettingsWindow
    {
        const double WidthDip = 370;
        const double MinHeightDip = 260;
        double _winW = WidthDip;           // ширина окна, как её оставил пользователь
        double _winH;                      // высота; 0 — по содержимому
        const double Pad = 14;
        const double Step = 5;       // шаг порога, %
        const double MinLimit = 50;
        const double MaxLimit = 99;

        static readonly IFormatProvider Inv = CultureInfo.InvariantCulture;

        /// <summary>Метка «этот элемент нажимается, за него окно не тащим».</summary>
        const string NoDragTag = "nodrag";

        static readonly AlertKind[] Kinds = { AlertKind.Ram, AlertKind.Cpu, AlertKind.Disk, AlertKind.Gpu };
        static readonly string[] KindColors = { Theme.RamColor, Theme.CpuColor, Theme.SsdColor, Theme.GpuColor };

        /// <summary>Название порога: ОЗУ, ЦП, Диск, ГП — по текущему языку.</summary>
        static string KindName(int i)
        {
            if (i == 1) return Lang.Cpu;
            if (i == 2) return Lang.Disks;
            if (i == 3) return Lang.Gpu;
            return Lang.Ram;
        }

        static readonly Widget.CardRow[] Rows =
        {
            Widget.CardRow.Ram, Widget.CardRow.Cpu, Widget.CardRow.Ssd, Widget.CardRow.Gpu,
            Widget.CardRow.Net, Widget.CardRow.Periph
        };

        /// <summary>Название строки карточки — по текущему языку.</summary>
        string RowName(int i)
        {
            if (i == 1) return Lang.Cpu;
            if (i == 2) return Lang.Disk;
            if (i == 3) return Lang.Gpu;
            if (i == 4) return Lang.Net;
            if (i == 5) return Lang.S("Периф.", "Periph");
            return Lang.Ram;
        }

        readonly Widget _widget;
        readonly Func<bool> _getAutostart;
        readonly Action<bool> _setAutostart;
        readonly Action<bool> _setLanguage;
        readonly string _posFile;          // где окно стояло в прошлый раз

        Window _win;
        TextBlock _hint;

        bool _hasPos;
        int _posX, _posY;

        // перетаскивание окна
        bool _dragging, _dragMoved;
        System.Drawing.Point _dragStart;
        Native.RECT _dragRect;
        HwndSource _src;
        IntPtr _hwnd;

        readonly Border[] _rowChips = new Border[6];
        readonly TextBlock[] _rowTexts = new TextBlock[6];
        readonly TextBlock[] _limits = new TextBlock[4];
        TextBlock _freeLimit, _periphLimit;
        Border _chipQuiet;
        TextBlock _txtQuiet, _quietFrom, _quietTo;

        // по чипу на каждый физический диск (показываются, когда их два и больше)
        readonly List<Border> _diskChips = new List<Border>();
        readonly List<TextBlock> _diskChipTexts = new List<TextBlock>();
        StackPanel _diskChipHost;
        FrameworkElement _diskLine;

        Border _chipAlerts, _chipTop, _chipAuto, _chipTray;
        TextBlock _txtAlerts, _txtTop, _txtAuto, _txtTray;

        Border _chipRu, _chipEn;
        TextBlock _txtRu, _txtEn;

        // раскладка карточки
        Border _chipHoriz, _chipVert, _chip90, _chip100, _chip110, _chip125;
        TextBlock _txtHoriz, _txtVert, _txt90, _txt100, _txt110, _txt125;
        readonly TextBlock[] _ordText = new TextBlock[5];

        public SettingsWindow(Widget widget, Func<bool> getAutostart, Action<bool> setAutostart,
            Action<bool> setLanguage)
        {
            _widget = widget;
            _getAutostart = getAutostart;
            _setAutostart = setAutostart;
            _setLanguage = setLanguage;
            _posFile = AppPaths.File_("window.json");
            LoadPos();
        }

        public bool IsOpen { get { return _win != null && _win.IsVisible; } }

        public void Show()
        {
            if (_win == null) Build();
            Refresh();

            // Окно настроек — самостоятельный диалог, а не дочернее окно карточки:
            // карточку можно спрятать в трей, а настройки останутся на экране.
            // Поэтому держим их поверх остальных окон сами, без владельца.
            _win.Topmost = true;
            _win.Opacity = 0;          // покажемся только когда встанем на место
            _win.Show();

            IntPtr h = new WindowInteropHelper(_win).Handle;
            _hwnd = h;
            if (_src == null)
            {
                _src = HwndSource.FromHwnd(h);
                if (_src != null) _src.AddHook(DragHook);
            }
            Native.RaiseTopmost(h);

            Place();
            // SizeToContent доводит размер уже после показа — центрируем ещё раз
            _win.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
            {
                Place();
                _win.Opacity = 1;
            });

            _win.Activate();
        }

        public void Hide()
        {
            if (_win != null) _win.Hide();
        }

        public void Close()
        {
            try { if (_src != null) { _src.RemoveHook(DragHook); _src = null; } } catch { }
            if (_win != null) { _win.Close(); _win = null; }
        }

        /// <summary>
        /// Язык сменился: окно пересобирается заново — подписи в нём создаются
        /// один раз при сборке. Пересборку откладываем на конец клика, чтобы не
        /// закрывать окно внутри обработчика его же чипа.
        /// </summary>
        public void ApplyLanguage()
        {
            if (_win == null) return;
            bool open = IsOpen;
            _win.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
            {
                Close();
                if (open) Show();
            });
        }

        // ------------------------------------------------------------------ вид

        void Build()
        {
            _repaints.Clear();
            var root = new StackPanel();        // шапка и вкладки; страницы — в прокручиваемой области ниже

            // оболочка: шапка/вкладки — по содержимому, страницы — на всё свободное место, плашка перезапуска внизу
            var shell = new Grid();
            shell.Margin = new Thickness(Pad);
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(root, 0);
            shell.Children.Add(root);
            _shell = shell;

            // --- заголовок и крестик
            var head = new DockPanel();
            head.LastChildFill = false;

            var title = new TextBlock();
            title.Text = Lang.S("Настройки", "Settings");
            title.FontFamily = new FontFamily(Theme.FontUi);
            title.FontSize = Theme.Fs(12);
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = Theme.B(Theme.TextHi);
            title.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(title, Dock.Left);
            head.Children.Add(title);

            TextBlock closeText;
            Border close = Toggle("✕", out closeText);
            close.Margin = new Thickness(0);
            close.MouseLeftButtonUp += delegate { Hide(); };
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);

            root.Children.Add(head);

            // --- вкладки: «Карточка», «Вид», «Данные», «Тревоги», «Система»
            root.Children.Add(BuildTabBar(root));
            FrameworkElement scrollArea = BuildScrollArea();
            Grid.SetRow(scrollArea, 1);
            shell.Children.Add(scrollArea);
            StackPanel page = _pages[0];

            // --- карточка: какие строки показывать
            page.Children.Add(Section(Lang.S("КАРТОЧКА", "CARD")));

            // Сетка 3×2: шесть чипов в одну строку не влезают, а перенос
            // WrapPanel'ом оставлял «Периф.» одиноким и не по линии. В сетке
            // ряды выровнены между собой и прижаты вправо, как остальные
            // управления окна.
            var rowGrid = new Grid();
            rowGrid.HorizontalAlignment = HorizontalAlignment.Right;
            rowGrid.VerticalAlignment = VerticalAlignment.Center;
            for (int c = 0; c < 3; c++)
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int r = 0; r < 2; r++)
                rowGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < Rows.Length; i++)
            {
                int index = i;                        // в замыкание — копию счётчика
                _rowChips[i] = Toggle(RowName(i), out _rowTexts[i]);
                // первый чип в ряду прижат к краю колонки, остальные — с отступом
                _rowChips[i].Margin = new Thickness(i % 3 == 0 ? 0 : 6, i >= 3 ? 4 : 0, 0, 0);
                _rowChips[i].MouseLeftButtonUp += delegate { ToggleRow(index); };
                Grid.SetColumn(_rowChips[i], i % 3);
                Grid.SetRow(_rowChips[i], i / 3);
                rowGrid.Children.Add(_rowChips[i]);
            }
            page.Children.Add(Line(Lang.S("Строки карточки", "Card rows"), rowGrid));

            // Отдельно — какие из физических дисков видны. Строка появляется,
            // только когда дисков два и больше: при одном хватает чипа выше.
            _diskChipHost = new StackPanel();
            _diskChipHost.Orientation = Orientation.Horizontal;
            _diskChipHost.HorizontalAlignment = HorizontalAlignment.Right;
            _diskLine = Line(Lang.Disks, _diskChipHost);
            page.Children.Add(_diskLine);
            EnsureDiskChips();

            // --- раскладка карточки
            page.Children.Add(Section(Lang.S("РАСКЛАДКА", "LAYOUT")));

            var orientRow = new StackPanel();
            orientRow.Orientation = Orientation.Horizontal;
            orientRow.HorizontalAlignment = HorizontalAlignment.Right;

            _chipHoriz = Toggle(Lang.S("Горизонтально", "Horizontal"), out _txtHoriz);
            _chipHoriz.Margin = new Thickness(0);
            _chipHoriz.MouseLeftButtonUp += delegate { _widget.SetVertical(false); Refresh(); };
            _chipVert = Toggle(Lang.S("Вертикально", "Vertical"), out _txtVert);
            _chipVert.MouseLeftButtonUp += delegate { _widget.SetVertical(true); Refresh(); };
            orientRow.Children.Add(_chipHoriz);
            orientRow.Children.Add(_chipVert);
            page.Children.Add(Line(Lang.S("Ориентация", "Orientation"), orientRow));

            var scaleRow = new StackPanel();
            scaleRow.Orientation = Orientation.Horizontal;
            scaleRow.HorizontalAlignment = HorizontalAlignment.Right;

            _chip90 = ScaleChip(0.9, out _txt90);
            _chip90.Margin = new Thickness(0);
            _chip100 = ScaleChip(1.0, out _txt100);
            _chip110 = ScaleChip(1.1, out _txt110);
            _chip125 = ScaleChip(1.25, out _txt125);
            scaleRow.Children.Add(_chip90);
            scaleRow.Children.Add(_chip100);
            scaleRow.Children.Add(_chip110);
            scaleRow.Children.Add(_chip125);
            page.Children.Add(Line(Lang.S("Масштаб", "Scale"), scaleRow));

            // порядок: «‹» и «›» прокручивают последовательность метрик
            var orderRow = new StackPanel();
            orderRow.Orientation = Orientation.Horizontal;
            orderRow.HorizontalAlignment = HorizontalAlignment.Right;

            Border prev = Mini("‹");
            prev.MouseLeftButtonUp += delegate { _widget.ShiftOrder(false); Refresh(); };
            orderRow.Children.Add(prev);

            for (int i = 0; i < _ordText.Length; i++)
            {
                Border chip = Toggle("", out _ordText[i]);
                chip.Cursor = Cursors.Arrow;
                chip.Margin = new Thickness(4, 0, 0, 0);
                orderRow.Children.Add(chip);
            }

            Border next = Mini("›");
            next.Margin = new Thickness(4, 0, 0, 0);
            next.MouseLeftButtonUp += delegate { _widget.ShiftOrder(true); Refresh(); };
            orderRow.Children.Add(next);

            page.Children.Add(Line(Lang.S("Порядок", "Order"), orderRow));

            BuildLookTab(_pages[1]);
            BuildDataTab(_pages[2]);
            page = _pages[3];

            // --- оповещения: включение — в строке заголовка раздела, справа,
            // как и все остальные управления окна
            _chipAlerts = Toggle(Lang.S("Включены", "On"), out _txtAlerts);
            _chipAlerts.Margin = new Thickness(0);
            _chipAlerts.MouseLeftButtonUp += delegate
            {
                _widget.AlertsEnabled = !_widget.AlertsEnabled;
                Refresh();
            };
            page.Children.Add(SectionRow(Lang.S("ОПОВЕЩЕНИЯ", "ALERTS"), _chipAlerts));

            for (int i = 0; i < Kinds.Length; i++)
            {
                AlertKind kind = Kinds[i];
                TextBlock value = LimitValue();
                _limits[i] = value;

                var right = new StackPanel();
                right.Orientation = Orientation.Horizontal;
                right.HorizontalAlignment = HorizontalAlignment.Right;

                Border minus = Mini("−");
                minus.MouseLeftButtonUp += delegate { StepLimit(kind, -Step); };
                right.Children.Add(minus);

                value.Margin = new Thickness(4, 0, 4, 0);
                right.Children.Add(value);

                Border plus = Mini("+");
                plus.MouseLeftButtonUp += delegate { StepLimit(kind, Step); };
                right.Children.Add(plus);

                page.Children.Add(Line(KindName(i), right, KindColors[i]));
            }

            // Отдельные пороги: свободное место на диске и заряд периферии.
            // Тревога срабатывает, когда значение НИЖЕ порога, а не выше.
            _freeLimit = LimitValue();
            page.Children.Add(Line(Lang.S("Свободно на диске", "Free disk space"),
                LimitEditor(_freeLimit,
                    delegate { StepFree(-1); }, delegate { StepFree(1); })));

            _periphLimit = LimitValue();
            page.Children.Add(Line(Lang.S("Заряд периферии", "Peripheral battery"),
                LimitEditor(_periphLimit,
                    delegate { StepPeriph(-Step); }, delegate { StepPeriph(Step); })));

            // Тихие часы: в это время плашки и уведомления не показываются.
            _chipQuiet = Toggle(Lang.S("Выключены", "Off"), out _txtQuiet);
            _chipQuiet.Margin = new Thickness(0);
            _chipQuiet.MouseLeftButtonUp += delegate
            {
                _widget.Alerts.QuietOn = !_widget.Alerts.QuietOn;
                _widget.SaveAlerts();
                Refresh();
            };
            page.Children.Add(Line(Lang.S("Тихие часы", "Quiet hours"), _chipQuiet));

            _quietFrom = LimitValue();
            page.Children.Add(Line(Lang.S("Тихо с", "Quiet from"),
                LimitEditor(_quietFrom,
                    delegate { StepQuiet(true, -30); }, delegate { StepQuiet(true, 30); })));

            _quietTo = LimitValue();
            page.Children.Add(Line(Lang.S("Тихо до", "Quiet until"),
                LimitEditor(_quietTo,
                    delegate { StepQuiet(false, -30); }, delegate { StepQuiet(false, 30); })));

            _hint = new TextBlock();
            _hint.TextWrapping = TextWrapping.Wrap;
            _hint.FontFamily = new FontFamily(Theme.FontUi);
            _hint.FontSize = Theme.Fs(10);
            _hint.LineHeight = Theme.Fs(14);
            _hint.Foreground = Theme.B(Theme.TextMid);
            _hint.Margin = new Thickness(0, 8, 0, 0);
            page.Children.Add(_hint);

            page = _pages[4];

            // --- система
            page.Children.Add(Section(Lang.S("СИСТЕМА", "SYSTEM")));

            _chipTop = Toggle(Lang.S("Выключено", "Off"), out _txtTop);
            _chipTop.MouseLeftButtonUp += delegate
            {
                bool on = !_widget.Win.Topmost;
                _widget.Win.Topmost = on;
                _widget.SaveState();
                if (on) Native.RaiseTopmost(_widget.Handle);
                Refresh();
            };
            page.Children.Add(Line(Lang.S("Поверх всех окон", "Always on top"), _chipTop));

            _chipAuto = Toggle(Lang.S("Выключено", "Off"), out _txtAuto);
            _chipAuto.MouseLeftButtonUp += delegate
            {
                if (_setAutostart != null)
                {
                    bool on = _getAutostart != null && _getAutostart();
                    _setAutostart(!on);
                }
                Refresh();
            };
            page.Children.Add(Line(Lang.S("Запускать при входе", "Run at sign-in"), _chipAuto));

            _chipTray = Toggle(Lang.S("Включено", "On"), out _txtTray);
            _chipTray.MouseLeftButtonUp += delegate
            {
                _widget.TrayAnim = !_widget.TrayAnim;
                Refresh();
            };
            page.Children.Add(Line(Lang.S("Анимация значка", "Tray animation"), _chipTray));

            // --- язык
            page.Children.Add(Section(Lang.S("ЯЗЫК", "LANGUAGE")));

            _chipRu = Toggle("Русский", out _txtRu);
            _chipRu.Margin = new Thickness(0);
            _chipRu.MouseLeftButtonUp += delegate
            {
                if (_setLanguage != null) _setLanguage(false);
            };

            _chipEn = Toggle("English", out _txtEn);
            _chipEn.MouseLeftButtonUp += delegate
            {
                if (_setLanguage != null) _setLanguage(true);
            };

            var langRow = new StackPanel();
            langRow.Orientation = Orientation.Horizontal;
            langRow.HorizontalAlignment = HorizontalAlignment.Right;
            langRow.Children.Add(_chipRu);
            langRow.Children.Add(_chipEn);
            page.Children.Add(Line(Lang.S("Интерфейс", "Interface"), langRow));

            BuildSystemExtras(page);
            for (int pi = 0; pi < _pages.Length; pi++)
            {
                try { GroupSections(_pages[pi]); } catch (Exception ex) { ErrorLog.Write("settings group", ex); }
            }
            FrameworkElement restart = BuildRestartBar();
            Grid.SetRow(restart, 2);
            shell.Children.Add(restart);

            var card = new Border();
            card.Background = Theme.B(Theme.Bg);
            card.BorderBrush = Theme.B(Theme.StrokeStrong);
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(Theme.RadiusCard);
            card.Child = shell;

            _win = new Window();
            _win.WindowStyle = WindowStyle.None;
            _win.AllowsTransparency = true;
            _win.Background = Brushes.Transparent;
            _win.ShowInTaskbar = false;
            _win.ResizeMode = ResizeMode.NoResize;      // размер меняем своими ручками по краям (BuildResizeLayer)
            _win.Width = Math.Max(WidthDip, _winW);
            _win.MinWidth = WidthDip;
            if (_winH > 0) { _win.SizeToContent = SizeToContent.Manual; _win.Height = _winH; }
            else _win.SizeToContent = SizeToContent.Height;
            _win.WindowStartupLocation = WindowStartupLocation.Manual;
            _win.Title = Lang.S("Настройки", "Settings");
            _win.Content = BuildResizeLayer(card);
            _win.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Hide();
            };

            // Окно без рамки — тащим его мышью за любое свободное место, а на
            // чипах и кнопках «−/+» обычный клик, их не трогаем.
            //
            // Двигаем по сообщениям мыши самого окна: пока кнопка нажата, оно
            // захватывает мышь в Win32, и Windows шлёт ему WM_MOUSEMOVE, где бы
            // ни был курсор. Таймер и события WPF для этого не годятся — они
            // приходят с задержкой, а отпускание кнопки окно может и не увидеть
            // (окно уезжает из-под курсора).
            _win.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (NoDrag(e.OriginalSource as DependencyObject)) return;
                StartDrag();
                e.Handled = true;
            };

            _win.PreviewMouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                EndDrag();
            };
        }

        void StartDrag()
        {
            IntPtr hw = new WindowInteropHelper(_win).Handle;
            Native.RECT r;
            if (!Native.GetWindowRect(hw, out r)) return;

            _dragStart = System.Windows.Forms.Cursor.Position;
            _dragRect = r;
            _dragging = true;
            _dragMoved = false;
            try { Native.SetCapture(hw); } catch { }
        }

        /// <summary>Сообщения окна: движение мыши и отпускание кнопки.</summary>
        IntPtr DragHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (!_dragging) return IntPtr.Zero;

            if (msg == 0x0200)                       // WM_MOUSEMOVE
            {
                DragTick();
            }
            else if (msg == 0x0202)                  // WM_LBUTTONUP
            {
                DragTick();                          // последняя точка курсора
                EndDrag();
                handled = true;
            }
            else if (msg == 0x0215)                  // WM_CAPTURECHANGED
            {
                EndDrag();
            }
            return IntPtr.Zero;
        }

        /// <summary>Шаг перетаскивания: окно идёт за курсором, пока кнопка нажата.</summary>
        void DragTick()
        {
            if (!_dragging || _win == null) return;

            System.Drawing.Point p = System.Windows.Forms.Cursor.Position;
            int x = _dragRect.Left + (p.X - _dragStart.X);
            int y = _dragRect.Top + (p.Y - _dragStart.Y);
            if (p.X != _dragStart.X || p.Y != _dragStart.Y) _dragMoved = true;
            Native.MoveTo(new WindowInteropHelper(_win).Handle, x, y);

            if (!Native.LeftButtonDown()) EndDrag();
        }

        void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            try { Native.ReleaseCapture(); } catch { }
            if (_dragMoved) SavePos();
        }

        /// <summary>
        /// Лежит ли точка нажатия внутри нажимаемого элемента. Идём вверх по
        /// дереву: чипы и мелкие кнопки помечены меткой NoDrag.
        /// </summary>
        static bool NoDrag(DependencyObject d)
        {
            try
            {
                while (d != null)
                {
                    if (d is System.Windows.Controls.Primitives.ScrollBar) return true;     // тянем ползунок, а не окно
                    FrameworkElement fe = d as FrameworkElement;
                    if (fe != null && object.Equals(fe.Tag, NoDragTag)) return true;
                    if (!(d is Visual) && !(d is System.Windows.Media.Media3D.Visual3D)) return false;
                    d = VisualTreeHelper.GetParent(d);
                }
            }
            catch { }
            return false;
        }

        /// <summary>Заголовок раздела: мелкий, приглушённый, с воздухом сверху.</summary>
        TextBlock Section(string label)
        {
            var t = new TextBlock();
            t.Text = label;
            t.FontFamily = new FontFamily(Theme.FontUi);
            t.FontSize = Theme.Fs(10);
            t.Foreground = Theme.B(Theme.TextMid);
            t.Margin = new Thickness(0, 16, 0, 2);
            t.Tag = SecTag;
            return t;
        }

        const string SecTag = "section";

        /// <summary>
        /// Каждый раздел вкладки — «модуль» в рамке, как группа ОЗУ/ЦП на карточке: тонкая рамка
        /// GroupStroke, скругление GroupRadius, цветная метка у заголовка, при наведении рамка
        /// светлеет и появляется лёгкая заливка. Дети страницы между заголовками переносятся в рамку.
        /// </summary>
        void GroupSections(StackPanel page)
        {
            string[] accents = { Theme.RamColor, Theme.CpuColor, Theme.SsdColor, Theme.GpuColor, Theme.NetColor, Theme.HddColor };
            var items = new List<UIElement>();
            foreach (UIElement c in page.Children) items.Add(c);
            page.Children.Clear();

            StackPanel body = null;
            int n = 0;
            foreach (UIElement c in items)
            {
                FrameworkElement fe = c as FrameworkElement;
                if (fe != null && SecTag.Equals(fe.Tag))
                {
                    body = new StackPanel();
                    page.Children.Add(MakeModule(body));
                    fe.Margin = new Thickness(0, 0, 0, 2);
                    TextBlock title = fe as TextBlock;
                    DockPanel dock = fe as DockPanel;
                    if (title != null)
                    {
                        body.Children.Add(Titled(title, accents[n % accents.Length], null));
                    }
                    else
                    {
                        if (dock != null && dock.Children.Count > 0 && dock.Children[0] is TextBlock)
                        {
                            TextBlock lt = (TextBlock)dock.Children[0];
                            dock.Children.RemoveAt(0);
                            FrameworkElement left = Titled(lt, accents[n % accents.Length], null);
                            DockPanel.SetDock(left, Dock.Left);
                            dock.Children.Insert(0, left);
                        }
                        body.Children.Add(fe);
                    }
                    n++;
                }
                else if (body != null) body.Children.Add(c);
                else page.Children.Add(c);
            }
        }

        /// <summary>Заголовок раздела с цветной полоской слева (как шкала метрики на карточке).</summary>
        static FrameworkElement Titled(TextBlock t, string color, object unused)
        {
            t.Margin = new Thickness(0);
            t.VerticalAlignment = VerticalAlignment.Center;
            var bar = new Border();
            bar.Width = 3;
            bar.Height = 12;
            bar.CornerRadius = new CornerRadius(Theme.BarRadius);
            bar.Background = Theme.B(color);
            bar.Margin = new Thickness(0, 0, 7, 0);
            bar.VerticalAlignment = VerticalAlignment.Center;
            var row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Children.Add(bar);
            row.Children.Add(t);
            return row;
        }

        static Border MakeModule(UIElement body)
        {
            var b = new Border();
            b.Margin = new Thickness(0, 8, 0, 0);
            b.Padding = new Thickness(9, 8, 9, 9);
            b.CornerRadius = new CornerRadius(Theme.GroupRadius);
            b.BorderThickness = new Thickness(1);
            b.BorderBrush = Theme.B(Theme.GroupStroke);
            b.Background = Brushes.Transparent;     // прозрачный, но принимает мышь — для MouseEnter
            b.Child = body;
            b.MouseEnter += delegate
            {
                b.BorderBrush = Theme.B(Theme.Stroke);
                b.Background = Theme.B(Theme.Fade(Theme.BtnHover, 0x0C));
            };
            b.MouseLeave += delegate
            {
                b.BorderBrush = Theme.B(Theme.GroupStroke);
                b.Background = Brushes.Transparent;
            };
            return b;
        }

        /// <summary>Заголовок раздела с управлением справа: «ОПОВЕЩЕНИЯ — Вкл».</summary>
        DockPanel SectionRow(string label, UIElement control)
        {
            var row = new DockPanel();
            row.LastChildFill = false;
            row.Margin = new Thickness(0, 16, 0, 2);
            row.Tag = SecTag;

            TextBlock t = Section(label);
            t.Margin = new Thickness(0);
            t.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(t, Dock.Left);
            row.Children.Add(t);

            var right = new Border();
            right.HorizontalAlignment = HorizontalAlignment.Right;
            right.VerticalAlignment = VerticalAlignment.Center;
            right.Child = control;
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);

            return row;
        }

        /// <summary>Строка «подпись слева — управление справа».</summary>
        DockPanel Line(string label, UIElement control)
        {
            return Line(label, control, null);
        }

        /// <summary>То же, но с цветной точкой перед подписью (как на карточке).</summary>
        DockPanel Line(string label, UIElement control, string dotColor)
        {
            var row = new DockPanel();
            row.LastChildFill = false;
            row.Margin = new Thickness(0, 7, 0, 0);

            var left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            if (dotColor != null)
            {
                var dot = new System.Windows.Shapes.Ellipse();
                dot.Width = 6;
                dot.Height = 6;
                dot.Fill = Theme.B(dotColor);
                dot.VerticalAlignment = VerticalAlignment.Center;
                dot.Margin = new Thickness(0, 0, 7, 0);
                left.Children.Add(dot);
            }

            var text = new TextBlock();
            text.Text = label;
            text.FontFamily = new FontFamily(Theme.FontUi);
            text.FontSize = Theme.Fs(11);
            text.Foreground = Theme.B(Theme.TextMid);
            text.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(text);

            DockPanel.SetDock(left, Dock.Left);
            row.Children.Add(left);

            // Border, а не StackPanel: панель с горизонтальной ориентацией
            // меряет детей бесконечной шириной, и WrapPanel внутри неё никогда
            // бы не переносил чипы. Border передаёт ограничение дальше.
            var right = new Border();
            right.HorizontalAlignment = HorizontalAlignment.Right;
            right.VerticalAlignment = VerticalAlignment.Center;
            right.Child = control;
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);

            return row;
        }

        /// <summary>Чип-переключатель: включённый — светлее и с ярким текстом.</summary>
        Border Toggle(string label, out TextBlock text)
        {
            text = new TextBlock();
            text.Text = label;
            text.FontFamily = new FontFamily(Theme.FontUi);
            text.FontSize = Theme.Fs(10.5);
            text.Foreground = Theme.B(Theme.TextMid);
            text.VerticalAlignment = VerticalAlignment.Center;

            var b = new Border();
            b.CornerRadius = new CornerRadius(Theme.GroupRadius);
            b.Padding = new Thickness(8, 4, 8, 4);
            b.Background = Theme.B(Theme.StrokeSoft);
            b.BorderThickness = new Thickness(1);   // место под красную рамку отказа
            b.BorderBrush = Brushes.Transparent;
            b.Child = text;
            b.Cursor = Cursors.Hand;
            b.Margin = new Thickness(6, 0, 0, 0);
            b.Tag = NoDragTag;
            b.MouseEnter += delegate { Hover(b, true); };
            b.MouseLeave += delegate { Hover(b, false); };
            return b;
        }

        /// <summary>
        /// Цветной отклик при наведении, как у групп на карточке: заливка BtnHover и светлая рамка.
        /// Включённое состояние запоминается в Uid (его ставит Paint), чтобы вернуть как было.
        /// </summary>
        static void Hover(Border chip, bool over)
        {
            bool on = chip.Uid == "1";
            if (over)
            {
                chip.Background = Theme.B(on ? Theme.SurfaceActive : Theme.BtnHover);
                chip.BorderBrush = Theme.B(Theme.Stroke);
            }
            else
            {
                chip.Background = Theme.B(on ? Theme.SurfaceActive : Theme.StrokeSoft);
                chip.BorderBrush = Brushes.Transparent;
            }
        }

        /// <summary>Чип масштаба: «90%» — нажатие сразу применяет его к карточке.</summary>
        Border ScaleChip(double scale, out TextBlock text)
        {
            Border b = Toggle(string.Format(Inv, "{0:0}%", scale * 100), out text);
            b.MouseLeftButtonUp += delegate { _widget.SetUserScale(scale); Refresh(); };
            return b;
        }

        /// <summary>Кнопка «−» / «+» рядом с порогом.</summary>
        Border Mini(string glyph)
        {
            var text = new TextBlock();
            text.Text = glyph;
            text.FontFamily = new FontFamily(Theme.FontUi);
            text.FontSize = Theme.Fs(13);
            text.Foreground = Theme.B(Theme.TextHi);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;

            var b = new Border();
            b.Width = 22;
            b.Height = 22;
            b.CornerRadius = new CornerRadius(Theme.GroupRadius);
            b.Background = Theme.B(Theme.StrokeSoft);
            b.Child = text;
            b.Cursor = Cursors.Hand;
            b.Tag = NoDragTag;

            b.MouseEnter += delegate { b.Background = Theme.B(Theme.BtnHover); b.BorderBrush = Theme.B(Theme.Stroke); };
            b.MouseLeave += delegate { b.Background = Theme.B(Theme.StrokeSoft); b.BorderBrush = Brushes.Transparent; };
            b.BorderThickness = new Thickness(1);
            return b;
        }

        /// <summary>Число порога — та же гарнитура, что и значения на карточке.</summary>
        TextBlock LimitValue()
        {
            var t = new TextBlock();
            t.FontFamily = new FontFamily(Theme.FontNum);
            t.FontSize = Theme.Fs(13);
            t.FontWeight = FontWeights.SemiBold;
            t.Foreground = Theme.B(Theme.TextHi);
            t.MinWidth = 46;
            t.TextAlignment = TextAlignment.Center;
            Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        // ------------------------------------------------------------ действия

        void ToggleRow(int index)
        {
            Widget.CardRow row = Rows[index];
            bool on = !_widget.CardRowVisible(row);
            // последнюю строку выключить нельзя — показываем это красной рамкой
            if (!_widget.SetCardRowVisible(row, on)) Deny(_rowChips[index]);
            Refresh();
        }

        /// <summary>Создать/обновить чипы дисков: у каждого своя подпись и флаг.</summary>
        void EnsureDiskChips()
        {
            if (_diskChipHost == null) return;
            int n = _widget.DiskCount;

            if (n <= 1)
            {
                if (_diskLine != null) _diskLine.Visibility = Visibility.Collapsed;
                return;
            }
            if (_diskLine != null) _diskLine.Visibility = Visibility.Visible;

            if (_diskChips.Count != n)
            {
                _diskChipHost.Children.Clear();
                _diskChips.Clear();
                _diskChipTexts.Clear();
                for (int i = 0; i < n; i++)
                {
                    int idx = i;
                    TextBlock text;
                    Border chip = Toggle(_widget.DiskName(i), out text);
                    if (i == 0) chip.Margin = new Thickness(0);
                    chip.MouseLeftButtonUp += delegate { ToggleDisk(idx); };
                    _diskChips.Add(chip);
                    _diskChipTexts.Add(text);
                    _diskChipHost.Children.Add(chip);
                }
            }

            for (int i = 0; i < n && i < _diskChips.Count; i++)
            {
                _diskChipTexts[i].Text = _widget.DiskName(i);
                Paint(_diskChips[i], _diskChipTexts[i], _widget.DiskOn(i));
            }
        }

        void ToggleDisk(int i)
        {
            bool on = !_widget.DiskOn(i);
            if (!_widget.SetDiskOn(i, on)) Deny(_diskChips[i]);
            Refresh();
        }

        void StepLimit(AlertKind kind, double delta)
        {
            double v = _widget.Alerts.Limit(kind) + delta;
            if (v < MinLimit) v = MinLimit;
            if (v > MaxLimit) v = MaxLimit;
            _widget.Alerts.SetLimit(kind, v);
            _widget.SaveAlerts();
            PaintLimits();
        }

        /// <summary>Порог свободного места: ниже него — тревога, %.</summary>
        void StepFree(double delta)
        {
            double v = _widget.Alerts.FreeLimit + delta;
            if (v < 1) v = 1;
            if (v > 50) v = 50;
            _widget.Alerts.FreeLimit = v;
            _widget.SaveAlerts();
            PaintLimits();
        }

        /// <summary>Граница тихих часов: шаг 30 минут по кругу суток.</summary>
        void StepQuiet(bool from, int deltaMin)
        {
            const int Day = 24 * 60;
            Alerts a = _widget.Alerts;
            int v = (from ? a.QuietFrom : a.QuietTo) + deltaMin;
            v = ((v % Day) + Day) % Day;
            if (from) a.QuietFrom = v; else a.QuietTo = v;
            _widget.SaveAlerts();
            PaintLimits();
        }

        static string Hm(int minutes)
        {
            return string.Format(Inv, "{0:00}:{1:00}", minutes / 60, minutes % 60);
        }

        /// <summary>Порог заряда периферии: ниже него — тревога, %.</summary>
        void StepPeriph(double delta)
        {
            double v = _widget.Alerts.PeriphLimit + delta;
            if (v < 5) v = 5;
            if (v > 50) v = 50;
            _widget.Alerts.PeriphLimit = v;
            _widget.SaveAlerts();
            PaintLimits();
        }

        /// <summary>Короткий отказ: рамка на миг краснеет — понятно и без текста.</summary>
        void Deny(Border chip)
        {
            chip.BorderBrush = Theme.B(Theme.WarnColor);
            var timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(700);
            timer.Tick += delegate
            {
                timer.Stop();
                chip.BorderBrush = Brushes.Transparent;
            };
            timer.Start();
        }

        // ---------------------------------------------------------- отображение

        void Refresh()
        {
            if (_win == null) return;

            for (int i = 0; i < Rows.Length; i++)
            {
                _rowTexts[i].Text = RowName(i);
                Paint(_rowChips[i], _rowTexts[i], _widget.CardRowVisible(Rows[i]));
            }
            EnsureDiskChips();

            Paint(_chipAlerts, _txtAlerts, _widget.AlertsEnabled);
            _txtAlerts.Text = _widget.AlertsEnabled
                ? Lang.S("Включены", "On")
                : Lang.S("Выключены", "Off");

            Paint(_chipTop, _txtTop, _widget.Win.Topmost);
            _txtTop.Text = _widget.Win.Topmost
                ? Lang.S("Включено", "On")
                : Lang.S("Выключено", "Off");

            bool auto = _getAutostart != null && _getAutostart();
            Paint(_chipAuto, _txtAuto, auto);
            _txtAuto.Text = auto
                ? Lang.S("Включено", "On")
                : Lang.S("Выключено", "Off");

            Paint(_chipTray, _txtTray, _widget.TrayAnim);
            _txtTray.Text = _widget.TrayAnim
                ? Lang.S("Включено", "On")
                : Lang.S("Выключено", "Off");

            Paint(_chipRu, _txtRu, !Lang.En);
            Paint(_chipEn, _txtEn, Lang.En);

            Paint(_chipHoriz, _txtHoriz, !_widget.Vertical);
            Paint(_chipVert, _txtVert, _widget.Vertical);

            Paint(_chip90, _txt90, ScaleOn(0.9));
            Paint(_chip100, _txt100, ScaleOn(1.0));
            Paint(_chip110, _txt110, ScaleOn(1.1));
            Paint(_chip125, _txt125, ScaleOn(1.25));

            for (int i = 0; i < _ordText.Length; i++)
            {
                if (_ordText[i] == null) continue;
                int m = _widget.OrderAt(i);
                if (m >= 0) _ordText[i].Text = RowName(m);
            }

            PaintLimits();

            RunRepaints();

            if (_hint != null)
            {
                Alerts a = _widget.Alerts;
                _hint.Text = string.Format(Inv,
                    Lang.S("Плашка появляется, если показатель держится выше порога {0:0} секунд " +
                           "(небольшие провалы ниже порога не в счёт). " +
                           "Напоминание — не чаще раза в {1:0} мин. Для свободного места " +
                           "и заряда периферии — та же пауза после запуска и напоминание. " +
                           "В тихие часы плашек и уведомлений нет.",
                           "The notice appears when a metric stays above its threshold " +
                           "for {0:0} seconds (short dips below it do not count). " +
                           "Reminders — at most once every {1:0} min. " +
                           "Free disk space and battery level use the same startup delay " +
                           "and reminder interval. No notices during quiet hours."),
                    a.Sustain, a.Cooldown / 60.0);
            }
        }

        /// <summary>Масштаб карточки совпадает с этим чипом.</summary>
        bool ScaleOn(double s)
        {
            return Math.Abs(_widget.UserScale - s) < 0.01;
        }

        void PaintLimits()
        {
            for (int i = 0; i < Kinds.Length; i++)
            {
                if (_limits[i] == null) continue;
                _limits[i].Text = string.Format(Inv, "{0:0}%", _widget.Alerts.Limit(Kinds[i]));
            }
            if (_freeLimit != null)
                _freeLimit.Text = string.Format(Inv, "{0:0}%", _widget.Alerts.FreeLimit);
            if (_periphLimit != null)
                _periphLimit.Text = string.Format(Inv, "{0:0}%", _widget.Alerts.PeriphLimit);

            if (_quietFrom != null) _quietFrom.Text = Hm(_widget.Alerts.QuietFrom);
            if (_quietTo != null) _quietTo.Text = Hm(_widget.Alerts.QuietTo);
            if (_chipQuiet != null)
            {
                bool on = _widget.Alerts.QuietOn;
                Paint(_chipQuiet, _txtQuiet, on);
                _txtQuiet.Text = on ? Lang.S("Включены", "On") : Lang.S("Выключены", "Off");
            }
        }

        /// <summary>Кнопки «− значение +» для порога.</summary>
        FrameworkElement LimitEditor(TextBlock value, Action dec, Action inc)
        {
            var right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.HorizontalAlignment = HorizontalAlignment.Right;

            Border minus = Mini("−");
            minus.MouseLeftButtonUp += delegate { dec(); };
            right.Children.Add(minus);

            value.Margin = new Thickness(4, 0, 4, 0);
            right.Children.Add(value);

            Border plus = Mini("+");
            plus.MouseLeftButtonUp += delegate { inc(); };
            right.Children.Add(plus);

            return right;
        }

        static void Paint(Border chip, TextBlock text, bool on)
        {
            if (chip == null || text == null) return;
            chip.Uid = on ? "1" : "0";
            chip.Background = Theme.B(on ? Theme.SurfaceActive : Theme.StrokeSoft);
            text.Foreground = Theme.B(on ? Theme.TextHi : Theme.TextMid);
        }

        // ------------------------------------------------------------ положение

        /// <summary>
        /// Окно помнит, где его оставили. Первый раз (или если прошлое место
        /// больше не на экране — например, отключили монитор) — по центру того
        /// монитора, на котором стоит карточка.
        /// </summary>
        void Place()
        {
            if (!_win.IsVisible) return;
            try
            {
                IntPtr h = new WindowInteropHelper(_win).Handle;

                // страницы длиннее экрана прокручиваются: сначала ограничиваем их высоту
                Native.RECT r0, w0;
                if (Native.GetWindowRect(h, out r0) &&
                    Native.GetWorkAreaAt(r0.Left + r0.Width / 2, r0.Top + r0.Height / 2, out w0))
                    FitScroll(w0.Height);

                Native.RECT pr;
                if (!Native.GetWindowRect(h, out pr)) return;

                Native.RECT wa = new Native.RECT();
                bool placed = _hasPos && Native.GetWorkAreaAt(
                    _posX + pr.Width / 2, _posY + pr.Height / 2, out wa);

                int x, y;
                if (!placed)
                {
                    // своего места ещё нет — по центру монитора карточки
                    if (!Native.GetWorkArea(_widget.Handle, out wa)) return;
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
                // Запомненное место подтягиваем к границам экрана, а «центр по
                // умолчанию» хранить не нужно: пока окно не двигали, его каждый
                // раз считаем заново (размер окна к этому моменту уже точный).
                if (_hasPos)
                {
                    _posX = x;
                    _posY = y;
                }
            }
            catch { }
        }

        void LoadPos()
        {
            try
            {
                if (!File.Exists(_posFile)) return;
                string t = File.ReadAllText(_posFile);

                // размер (необязательный): ширина не меньше обычной, высота 0 — «по содержимому»
                Match mw = Regex.Match(t, "\"SettingsWidth\"\\s*:\\s*(\\d+)");
                Match mh = Regex.Match(t, "\"SettingsHeight\"\\s*:\\s*(\\d+)");
                int sw, sh;
                if (mw.Success && int.TryParse(mw.Groups[1].Value, NumberStyles.Integer, Inv, out sw) && sw >= WidthDip)
                    _winW = Math.Min(sw, 2400);
                if (mh.Success && int.TryParse(mh.Groups[1].Value, NumberStyles.Integer, Inv, out sh) && sh >= MinHeightDip)
                    _winH = Math.Min(sh, 2400);

                Match mx = Regex.Match(t, "\"SettingsLeft\"\\s*:\\s*(-?\\d+)");
                Match my = Regex.Match(t, "\"SettingsTop\"\\s*:\\s*(-?\\d+)");
                if (!mx.Success || !my.Success) return;

                int x, y;
                if (int.TryParse(mx.Groups[1].Value, NumberStyles.Integer, Inv, out x) &&
                    int.TryParse(my.Groups[1].Value, NumberStyles.Integer, Inv, out y))
                {
                    _posX = x;
                    _posY = y;
                    _hasPos = true;
                }
            }
            catch { }
        }

        /// <summary>
        /// Где окно остановилось — туда его и вернём в следующий раз. Если его
        /// отпустили за краем экрана, окно сначала подтянется к его границам.
        /// </summary>
        void SavePos()
        {
            try
            {
                IntPtr h = new WindowInteropHelper(_win).Handle;
                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return;

                Native.RECT wa;
                if (Native.GetWorkAreaAt(r.Left + r.Width / 2, r.Top + r.Height / 2, out wa))
                {
                    int x = r.Left;
                    int y = r.Top;
                    if (x + r.Width > wa.Right) x = wa.Right - r.Width;
                    if (y + r.Height > wa.Bottom) y = wa.Bottom - r.Height;
                    if (x < wa.Left) x = wa.Left;
                    if (y < wa.Top) y = wa.Top;
                    if (x != r.Left || y != r.Top) Native.MoveTo(h, x, y);
                    r.Left = x;
                    r.Top = y;
                }

                _posX = r.Left;
                _posY = r.Top;
                _hasPos = true;

                AtomicFile.WriteAllText(_posFile, string.Format(Inv,
                    "{{\"SettingsLeft\":{0},\"SettingsTop\":{1},\"SettingsWidth\":{2},\"SettingsHeight\":{3}}}",
                    r.Left, r.Top, (int)Math.Round(_winW), (int)Math.Round(_winH)));
            }
            catch (Exception ex) { ErrorLog.Write("save settings pos", ex); }
        }
    }
}
