using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SysWidget
{
    /// <summary>Раздел «Диагностика» вкладки «Система»: автозагрузка (и дальше — крупные папки, события).</summary>
    public sealed partial class SettingsWindow
    {
        // ------------------------------------------------------------ прокрутка страниц

        ScrollViewer _scroll;
        Grid _shell;

        // ------------------------------------------------------------ размер окна

        /// <summary>Карточка окна с ручками по правому и нижнему краям и в правом нижнем углу.</summary>
        FrameworkElement BuildResizeLayer(Border card)
        {
            var g = new Grid();
            g.Children.Add(card);
            g.Children.Add(Grip(HorizontalAlignment.Right, VerticalAlignment.Stretch, 7, double.NaN, Cursors.SizeWE, true, false));
            g.Children.Add(Grip(HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 7, Cursors.SizeNS, false, true));
            g.Children.Add(Grip(HorizontalAlignment.Right, VerticalAlignment.Bottom, 16, 16, Cursors.SizeNWSE, true, true));
            return g;
        }

        Thumb Grip(HorizontalAlignment ha, VerticalAlignment va, double w, double h, Cursor cursor, bool horiz, bool vert)
        {
            var t = new Thumb();
            t.HorizontalAlignment = ha;
            t.VerticalAlignment = va;
            if (!double.IsNaN(w)) t.Width = w;
            if (!double.IsNaN(h)) t.Height = h;
            t.Background = Brushes.Transparent;
            t.BorderThickness = new Thickness(0);
            t.Focusable = false;
            t.FocusVisualStyle = null;
            // свой пустой шаблон: у системного Thumb есть светлая заливка, она давала белый контур по краям
            var hit = new FrameworkElementFactory(typeof(Border));
            hit.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            t.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = hit };
            t.Cursor = cursor;
            t.Tag = NoDragTag;                          // тянем размер, а не окно
            t.DragDelta += delegate(object s, DragDeltaEventArgs e)
            {
                ResizeBy(horiz ? e.HorizontalChange : 0, vert ? e.VerticalChange : 0);
            };
            t.DragCompleted += delegate
            {
                SavePos();
                if (_win != null)
                    _win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                        (Action)delegate { Place(); });
            };
            if (vert)
                t.MouseDoubleClick += delegate           // двойной клик по нижнему краю — высота снова по содержимому
                {
                    _winH = 0;
                    _win.SizeToContent = SizeToContent.Height;
                    SavePos();
                    _win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                        (Action)delegate { Place(); });
                };
            return t;
        }

        void ResizeBy(double dx, double dy)
        {
            if (_win == null) return;
            IntPtr h = new System.Windows.Interop.WindowInteropHelper(_win).Handle;
            Native.RECT r, wa;
            if (!Native.GetWindowRect(h, out r) || !Native.GetWorkAreaAt(r.Left + r.Width / 2, r.Top + r.Height / 2, out wa)) return;
            double scale = DipScale();

            // не дальше края рабочей области
            double maxW = (wa.Right - r.Left) / scale;
            double maxH = (wa.Bottom - r.Top) / scale;

            if (dx != 0)
            {
                _winW = Math.Max(WidthDip, Math.Min(maxW, _win.ActualWidth + dx));
                _win.Width = _winW;
            }
            if (dy != 0)
            {
                double nh = Math.Max(MinHeightDip, Math.Min(maxH, _win.ActualHeight + dy));
                _winH = nh;
                _win.SizeToContent = SizeToContent.Manual;
                _win.Height = nh;
            }
        }

        double DipScale()
        {
            double scale = 1;
            PresentationSource src = _win == null ? null : PresentationSource.FromVisual(_win);
            if (src != null && src.CompositionTarget != null) scale = src.CompositionTarget.TransformToDevice.M22;
            return scale <= 0 ? 1 : scale;
        }

        /// <summary>Страницы вкладок в ScrollViewer: шапка и вкладки стоят, содержимое прокручивается.</summary>
        FrameworkElement BuildScrollArea()
        {
            var host = new StackPanel();
            for (int i = 0; i < TabCount; i++) host.Children.Add(_pages[i]);

            _scroll = new ScrollViewer();
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.Focusable = false;
            _scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), ScrollBarStyle());
            _scroll.Content = host;
            return _scroll;
        }

        /// <summary>Подогнать высоту прокручиваемой части под рабочую область монитора (в пикселях).</summary>
        void FitScroll(int workHeightPx)
        {
            if (_scroll == null || _shell == null || _win == null) return;
            double scale = DipScale();

            // высота задана вручную — страницы занимают отведённое место; только следим, чтобы не вылезти за экран
            if (_winH > 0)
            {
                if (!double.IsPositiveInfinity(_scroll.MaxHeight)) _scroll.MaxHeight = double.PositiveInfinity;
                double cap = workHeightPx / scale - 8;
                if (_win.Height > cap) { _winH = Math.Max(MinHeightDip, cap); _win.Height = _winH; }
                return;
            }

            // всё окно = шапка + вкладки + плашка перезапуска + поля + рамка; на странице остаётся остальное
            double chrome = _shell.ActualHeight - _scroll.ActualHeight + Pad * 2 + 2;
            double max = Math.Max(180, workHeightPx / scale - 16 - chrome);
            if (Math.Abs(_scroll.MaxHeight - max) > 0.5)
            {
                _scroll.MaxHeight = max;
                _win.UpdateLayout();
            }
        }

        /// <summary>Тонкая тёмная полоса прокрутки вместо светлой системной.</summary>
        internal static Style ScrollBarStyle()
        {
            string empty = "<RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>";
            string xaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ScrollBar}'>" +
                "<Setter Property='Width' Value='6'/><Setter Property='MinWidth' Value='6'/><Setter Property='Background' Value='Transparent'/>" +
                "<Setter Property='Margin' Value='4,0,0,0'/>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ScrollBar}'><Grid Background='Transparent'>" +
                "<Track x:Name='PART_Track' IsDirectionReversed='True'>" +
                "<Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Focusable='False'>" + empty + "</RepeatButton></Track.DecreaseRepeatButton>" +
                "<Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Focusable='False'>" + empty + "</RepeatButton></Track.IncreaseRepeatButton>" +
                "<Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'>" +
                "<Border CornerRadius='3' Background='" + Theme.TextMid + "' Opacity='0.45'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
                "</Track></Grid></ControlTemplate></Setter.Value></Setter></Style>";
            return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
        }


        const int AutoCap = 8;          // сколько записей показывать, пока не нажато «Все»

        StackPanel _autoList;
        TextBlock _autoStatus;
        bool _autoAll;

        void BuildDiagSections(StackPanel page)
        {
            BuildAutostartSection(page);
            BuildDiskMapSection(page);
            BuildEventsSection(page);
        }

        // ------------------------------------------------------------ события Windows

        StackPanel _evList;
        TextBlock _evStatus;

        sealed class EvGroup
        {
            public int Cat;
            public string Title = "", Detail = "";
            public int N24, N7;
            public DateTime Last;
        }

        void BuildEventsSection(StackPanel page)
        {
            page.Children.Add(Section(Lang.S("СОБЫТИЯ WINDOWS", "WINDOWS EVENTS")));

            Border scan = ActionChip(Lang.S("Обновить", "Refresh"), delegate { ScanEvents(); });
            scan.Margin = new Thickness(0);
            var chips = new StackPanel();
            chips.Orientation = Orientation.Horizontal;
            chips.HorizontalAlignment = HorizontalAlignment.Right;
            chips.Children.Add(scan);
            page.Children.Add(Line(Lang.S("Сбои и ошибки за 7 дней", "Crashes and errors, 7 days"), chips));

            _evStatus = SmallNote();
            _evStatus.Margin = new Thickness(0, 8, 0, 0);
            page.Children.Add(_evStatus);

            _evList = new StackPanel();
            page.Children.Add(_evList);

            if (WinEvents.Checked == DateTime.MinValue) ScanEvents();
            else ShowEvents();
        }

        void ScanEvents()
        {
            if (WinEvents.Busy) return;
            WinEvents.Run(delegate
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke((Action)delegate { ShowEvents(); });
            });
            ShowEvents();
        }

        static string CatName(int cat)
        {
            switch (cat)
            {
                case WinEvents.CatReboot: return Lang.S("ПЕРЕЗАГРУЗКИ И СИНИЕ ЭКРАНЫ", "RESTARTS AND BLUE SCREENS");
                case WinEvents.CatHardware: return Lang.S("ОБОРУДОВАНИЕ И ДРАЙВЕРЫ", "HARDWARE AND DRIVERS");
                case WinEvents.CatDisk: return Lang.S("ДИСК", "DISK");
                case WinEvents.CatService: return Lang.S("СЛУЖБЫ", "SERVICES");
                default: return Lang.S("СБОИ ПРИЛОЖЕНИЙ", "APP CRASHES");
            }
        }

        static string CatShort(int cat)
        {
            switch (cat)
            {
                case WinEvents.CatReboot: return Lang.S("перезагрузки", "restarts");
                case WinEvents.CatHardware: return Lang.S("оборудование", "hardware");
                case WinEvents.CatDisk: return Lang.S("диск", "disk");
                case WinEvents.CatService: return Lang.S("службы", "services");
                default: return Lang.S("приложения", "apps");
            }
        }

        void ShowEvents()
        {
            if (_evList == null) return;
            _evList.Children.Clear();
            if (WinEvents.Busy)
            {
                _evStatus.Text = Lang.S("Читаю журналы Windows…", "Reading Windows logs…");
                return;
            }
            if (WinEvents.Checked == DateTime.MinValue) { _evStatus.Text = ""; return; }

            _evStatus.Text = Lang.S("Проверено в ", "Checked at ") + WinEvents.Checked.ToString("HH:mm")
                + Lang.S(" · журналы «Приложение» и «Система», только чтение", " · Application and System logs, read-only")
                + (WinEvents.Error.Length > 0 ? "\n⚠ " + WinEvents.Error : "");

            DateTime day = DateTime.Now.AddHours(-24);

            // свернуть одинаковые события: (категория, название) → счётчики и последнее
            var groups = new Dictionary<string, EvGroup>();
            var order = new List<EvGroup>();
            int[] n24 = new int[5];
            int[] n7 = new int[5];
            foreach (WinEvent e in WinEvents.Items)          // уже от новых к старым
            {
                string key = e.Cat + "|" + e.Title;
                EvGroup g;
                if (!groups.TryGetValue(key, out g))
                {
                    g = new EvGroup();
                    g.Cat = e.Cat; g.Title = e.Title; g.Detail = e.Detail; g.Last = e.Time;
                    groups[key] = g;
                    order.Add(g);
                }
                g.N7++;
                n7[e.Cat]++;
                if (e.Time >= day) { g.N24++; n24[e.Cat]++; }
            }

            // --- итог: цвет по тяжести за сутки
            int total24 = 0;
            bool heavy = false;
            var parts = new List<string>();
            foreach (int c in new int[] { WinEvents.CatReboot, WinEvents.CatHardware, WinEvents.CatDisk, WinEvents.CatService, WinEvents.CatApp })
            {
                total24 += n24[c];
                if (n24[c] > 0)
                {
                    parts.Add(CatShort(c) + ": " + n24[c]);
                    if (c != WinEvents.CatApp && c != WinEvents.CatService) heavy = true;
                }
            }
            if (WinEvents.Items.Count == 0)
                _evList.Children.Add(UpdNote(Lang.S("✓ За 7 дней сбоев, перезагрузок без завершения и ошибок диска не найдено.",
                                                    "✓ No crashes, unclean restarts or disk errors in 7 days."), GoodColor));
            else if (total24 == 0)
                _evList.Children.Add(UpdNote(Lang.S("✓ За последние сутки сбоев нет. За 7 дней событий: ",
                                                    "✓ Nothing in the last 24 hours. Events in 7 days: ") + WinEvents.Items.Count, GoodColor));
            else
                _evList.Children.Add(UpdNote(Lang.S("● За сутки: ", "● Last 24 hours: ") + string.Join(", ", parts.ToArray()),
                                             heavy ? Theme.WarnColor : NoteColor));

            // --- по категориям
            foreach (int c in new int[] { WinEvents.CatReboot, WinEvents.CatHardware, WinEvents.CatDisk, WinEvents.CatService, WinEvents.CatApp })
            {
                var mine = new List<EvGroup>();
                foreach (EvGroup g in order) if (g.Cat == c) mine.Add(g);
                if (mine.Count == 0) continue;
                mine.Sort(delegate(EvGroup a, EvGroup b)
                {
                    if (a.N24 != b.N24) return b.N24.CompareTo(a.N24);
                    return b.Last.CompareTo(a.Last);
                });

                var rows = new List<FrameworkElement>();
                int shown = 0;
                foreach (EvGroup g in mine)
                {
                    if (shown >= 6) break;
                    rows.Add(EventRow(g, c));
                    shown++;
                }
                if (mine.Count > shown)
                {
                    var more = SmallNote();
                    more.Text = Lang.S("и ещё ", "and ") + (mine.Count - shown) + Lang.S(" видов", " more kinds");
                    more.Margin = new Thickness(0, 6, 0, 0);
                    rows.Add(more);
                }

                string badge = n24[c] > 0 ? Lang.S("сутки ", "24 h ") + n24[c] + " · " + n7[c] : Lang.S("7 дн. ", "7 d ") + n7[c];
                string color = n24[c] > 0 ? (c == WinEvents.CatApp || c == WinEvents.CatService ? NoteColor : Theme.WarnColor) : null;
                _evList.Children.Add(UpdGroup(CatName(c), badge, color, rows.ToArray()));
            }

            if (_win != null)
                _win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)delegate { Place(); });
        }

        FrameworkElement EventRow(EvGroup g, int cat)
        {
            var card = new StackPanel();
            card.Margin = new Thickness(0, 8, 0, 0);

            var l1 = new DockPanel();
            l1.LastChildFill = true;

            string pillText = g.N24 > 0 ? "×" + g.N24 + Lang.S(" за сутки", " in 24 h") : "×" + g.N7 + Lang.S(" за 7 дн.", " in 7 d");
            string pillColor = g.N24 > 0 ? (cat == WinEvents.CatApp || cat == WinEvents.CatService ? NoteColor : Theme.WarnColor) : null;
            Border pill = Pill(pillText, pillColor);
            pill.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(pill, Dock.Right);
            l1.Children.Add(pill);

            var name = new TextBlock();
            name.Text = g.Title;
            name.TextWrapping = TextWrapping.Wrap;
            name.FontFamily = new FontFamily(Theme.FontUi);
            name.FontSize = Theme.Fs(11);
            name.Foreground = Theme.B(Theme.TextHi);
            name.VerticalAlignment = VerticalAlignment.Center;
            l1.Children.Add(name);
            card.Children.Add(l1);

            var sub = SmallNote();
            sub.Margin = new Thickness(0, 2, 0, 0);
            sub.Text = Lang.S("последний ", "last ") + g.Last.ToString("dd.MM HH:mm")
                + (g.Detail.Length > 0 ? " · " + g.Detail : "")
                + (g.N7 > 1 && g.N24 > 0 && g.N7 != g.N24 ? " · " + Lang.S("за 7 дн. ", "7 d: ") + g.N7 : "");
            card.Children.Add(sub);
            return card;
        }

        // ------------------------------------------------------------ крупные папки

        StackPanel _mapList;
        TextBlock _mapStatus;
        System.Windows.Threading.DispatcherTimer _mapTimer;
        readonly HashSet<string> _mapOpen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void BuildDiskMapSection(StackPanel page)
        {
            page.Children.Add(Section(Lang.S("КРУПНЫЕ ПАПКИ", "LARGE FOLDERS")));

            string drive = System.IO.Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\');
            Border scan = ActionChip(Lang.S("Сканировать", "Scan"), delegate { ScanDiskMap(); });
            scan.Margin = new Thickness(0);
            var chips = new StackPanel();
            chips.Orientation = Orientation.Horizontal;
            chips.HorizontalAlignment = HorizontalAlignment.Right;
            chips.Children.Add(scan);
            page.Children.Add(Line(Lang.S("Где занято место на ", "Where space goes on ") + drive, chips));

            _mapStatus = SmallNote();
            _mapStatus.Margin = new Thickness(0, 8, 0, 0);
            page.Children.Add(_mapStatus);

            _mapList = new StackPanel();
            page.Children.Add(_mapList);
            ShowDiskMap();
        }

        void ScanDiskMap()
        {
            if (DiskMap.Busy) return;
            DiskMap.Run(delegate
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke((Action)delegate
                {
                    if (_mapTimer != null) { _mapTimer.Stop(); _mapTimer = null; }
                    ShowDiskMap();
                });
            });

            // ход сканирования — раз в полсекунды
            _mapTimer = new System.Windows.Threading.DispatcherTimer();
            _mapTimer.Interval = TimeSpan.FromMilliseconds(500);
            _mapTimer.Tick += delegate { if (DiskMap.Busy) ShowDiskMapProgress(); };
            _mapTimer.Start();
            ShowDiskMap();
        }

        void ShowDiskMapProgress()
        {
            _mapStatus.Text = Lang.S("Сканирую… просмотрено ", "Scanning… ") + DiskMap.SeenFiles.ToString("N0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', ' ')
                + Lang.S(" файлов, ", " files, ") + DiskMap.Fmt(DiskMap.SeenBytes)
                + Lang.S(". Обычно 1–3 минуты, окно можно не держать открытым.", ". Usually 1–3 minutes; you can close this window.");
        }

        void ShowDiskMap()
        {
            if (_mapList == null) return;
            _mapList.Children.Clear();

            if (DiskMap.Busy) { ShowDiskMapProgress(); return; }
            if (DiskMap.Checked == DateTime.MinValue || DiskMap.Root == null)
            {
                _mapStatus.Text = Lang.S("Нажмите «Сканировать»: диск обходится в фоне (1–3 минуты), ничего не удаляется. Результат — самые большие папки (клик по строке раскрывает) и файлы; «↗» показывает в проводнике.",
                                         "Press “Scan”: the drive is walked in the background (1–3 minutes), nothing is deleted. You get the biggest folders (click a row to expand) and files; “↗” shows it in Explorer.");
                return;
            }

            DirNode root = DiskMap.Root;
            long used = DiskMap.DriveUsed;
            int pct = used > 0 ? (int)Math.Min(100, root.Size * 100 / used) : 100;
            string head = Lang.S("Занято ", "Used ") + DiskMap.Fmt(used) + Lang.S(" из ", " of ") + DiskMap.Fmt(DiskMap.DriveTotal)
                + Lang.S(" · просмотрено ", " · scanned ") + DiskMap.Fmt(root.Size) + " (" + pct + "%)"
                + (DiskMap.Denied > 0 ? Lang.S(" · без доступа: ", " · no access: ") + DiskMap.Denied : "")
                + " · " + DiskMap.Checked.ToString("HH:mm") + ", " + Math.Round(DiskMap.Seconds) + Lang.S(" с", " s");
            if (pct < 95)
                head += "\n" + Lang.S("Остальное — системные папки, недоступные без прав администратора, файл подкачки, теневые копии и «облачные» файлы OneDrive.",
                                      "The rest is system folders that need administrator rights, the page file, shadow copies and OneDrive cloud-only files.");
            _mapStatus.Text = head;

            // --- папки
            var rows = new List<FrameworkElement>();
            AddNodeRows(rows, root, 0);
            _mapList.Children.Add(UpdGroup(Lang.S("ПАПКИ", "FOLDERS"), DiskMap.Fmt(root.Size), null, rows.ToArray()));

            // --- файлы
            if (DiskMap.Files.Count > 0)
            {
                var frows = new List<FrameworkElement>();
                foreach (BigFile f in DiskMap.Files) frows.Add(MapFileRow(f));
                _mapList.Children.Add(UpdGroup(Lang.S("САМЫЕ БОЛЬШИЕ ФАЙЛЫ", "BIGGEST FILES"),
                    DiskMap.Files.Count.ToString(), null, frows.ToArray()));
            }

            if (_win != null)
                _win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)delegate { Place(); });
        }

        void AddNodeRows(List<FrameworkElement> rows, DirNode parent, int level)
        {
            foreach (DirNode n in parent.Kids)
            {
                bool has = n.Kids.Count > 0;
                bool open = has && _mapOpen.Contains(n.Path);
                rows.Add(MapRow(n, parent.Size, level, has, open));
                if (open) AddNodeRows(rows, n, level + 1);
            }
            if (parent.OtherCount > 0)
            {
                var note = SmallNote();
                note.Text = Lang.S("ещё ", "and ") + parent.OtherCount + Lang.S(" папок — ", " folders — ") + DiskMap.Fmt(parent.OtherSize);
                note.Margin = new Thickness(level * 12 + 14, 6, 0, 0);
                rows.Add(note);
            }
        }

        FrameworkElement MapRow(DirNode n, long parentSize, int level, bool has, bool open)
        {
            DirNode node = n;
            var box = new StackPanel();
            box.Margin = new Thickness(level * 12, 7, 0, 0);

            var line = new DockPanel();
            line.Background = Brushes.Transparent;
            line.LastChildFill = true;

            Border openBtn = OpenGlyph(n.Path, false);
            DockPanel.SetDock(openBtn, Dock.Right);
            line.Children.Add(openBtn);

            var size = new TextBlock();
            size.Text = DiskMap.Fmt(n.Size);
            size.FontFamily = new FontFamily(Theme.FontNum);
            size.FontSize = Theme.Fs(11);
            size.Foreground = Theme.B(Theme.TextHi);
            size.VerticalAlignment = VerticalAlignment.Center;
            size.Margin = new Thickness(8, 0, 8, 0);
            DockPanel.SetDock(size, Dock.Right);
            line.Children.Add(size);

            var name = new TextBlock();
            name.Text = (has ? (open ? "▾ " : "▸ ") : "   ") + n.Name;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.FontFamily = new FontFamily(Theme.FontUi);
            name.FontSize = Theme.Fs(11);
            name.Foreground = Theme.B(Theme.TextHi);
            name.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(name);

            if (has)
            {
                line.Cursor = Cursors.Hand;
                line.Tag = NoDragTag;
                line.MouseLeftButtonUp += delegate
                {
                    if (!_mapOpen.Remove(node.Path)) _mapOpen.Add(node.Path);
                    ShowDiskMap();
                };
            }
            box.Children.Add(line);

            // полоса доли: ширина — доля от родителя, цвет — доля от всего занятого места
            double share = parentSize > 0 ? (double)n.Size / parentSize : 0;
            double ofDrive = DiskMap.DriveUsed > 0 ? (double)n.Size / DiskMap.DriveUsed : 0;
            string color = ofDrive >= 0.2 ? NoteColor : GoodColor;
            var bar = new Grid();
            bar.Height = 3;
            bar.Margin = new Thickness(0, 4, 0, 0);
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.005, Math.Min(1, share)), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - Math.Min(1, share)), GridUnitType.Star) });
            var fill = new Border();
            fill.CornerRadius = new CornerRadius(Theme.BarRadius);
            fill.Background = Theme.B(Theme.Fade(color, 0xCC));
            Grid.SetColumn(fill, 0);
            bar.Children.Add(fill);
            var rest = new Border();
            rest.Background = Theme.B(Theme.StrokeSoft);
            Grid.SetColumn(rest, 1);
            bar.Children.Add(rest);
            box.Children.Add(bar);
            return box;
        }

        FrameworkElement MapFileRow(BigFile f)
        {
            var box = new StackPanel();
            box.Margin = new Thickness(0, 8, 0, 0);

            var l1 = new DockPanel();
            l1.LastChildFill = true;
            Border openBtn = OpenGlyph(f.Path, true);
            DockPanel.SetDock(openBtn, Dock.Right);
            l1.Children.Add(openBtn);

            var size = new TextBlock();
            size.Text = DiskMap.Fmt(f.Size);
            size.FontFamily = new FontFamily(Theme.FontNum);
            size.FontSize = Theme.Fs(11);
            size.Foreground = Theme.B(Theme.TextHi);
            size.VerticalAlignment = VerticalAlignment.Center;
            size.Margin = new Thickness(8, 0, 8, 0);
            DockPanel.SetDock(size, Dock.Right);
            l1.Children.Add(size);

            var name = new TextBlock();
            name.Text = System.IO.Path.GetFileName(f.Path);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.FontFamily = new FontFamily(Theme.FontUi);
            name.FontSize = Theme.Fs(11);
            name.Foreground = Theme.B(Theme.TextHi);
            name.VerticalAlignment = VerticalAlignment.Center;
            l1.Children.Add(name);
            box.Children.Add(l1);

            var dir = SmallNote();
            dir.Text = System.IO.Path.GetDirectoryName(f.Path);
            dir.TextTrimming = TextTrimming.CharacterEllipsis;
            dir.TextWrapping = TextWrapping.NoWrap;
            dir.Margin = new Thickness(0, 1, 0, 0);
            box.Children.Add(dir);
            return box;
        }

        /// <summary>Кнопка «↗»: показать папку или файл в проводнике.</summary>
        Border OpenGlyph(string path, bool file)
        {
            var t = new TextBlock();
            t.Text = "↗";
            t.FontFamily = new FontFamily(Theme.FontUi);
            t.FontSize = Theme.Fs(11);
            t.Foreground = Theme.B(Theme.TextMid);
            var b = new Border();
            b.Padding = new Thickness(6, 1, 6, 1);
            b.CornerRadius = new CornerRadius(Theme.GroupRadius);
            b.Background = Theme.B(Theme.StrokeSoft);
            b.Child = t;
            b.Cursor = Cursors.Hand;
            b.Tag = NoDragTag;                          // обычный клик, не перетаскивание окна
            b.BorderThickness = new Thickness(1);       // место под светлую рамку при наведении
            b.BorderBrush = Brushes.Transparent;
            b.MouseEnter += delegate { Hover(b, true); t.Foreground = Theme.B(Theme.TextHi); };
            b.MouseLeave += delegate { Hover(b, false); t.Foreground = Theme.B(Theme.TextMid); };
            b.VerticalAlignment = VerticalAlignment.Center;
            b.ToolTip = Lang.S("Показать в проводнике", "Show in Explorer");
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe",
                        file ? "/select,\"" + path + "\"" : "\"" + path + "\"");
                }
                catch (Exception ex) { ErrorLog.Write("open in explorer", ex); }
            };
            return b;
        }

        // ------------------------------------------------------------ автозагрузка

        void BuildAutostartSection(StackPanel page)
        {
            page.Children.Add(Section(Lang.S("АВТОЗАГРУЗКА", "STARTUP")));

            Border scan = ActionChip(Lang.S("Обновить", "Refresh"), delegate { ScanAutostart(); });
            scan.Margin = new Thickness(0);
            var chips = new StackPanel();
            chips.Orientation = Orientation.Horizontal;
            chips.HorizontalAlignment = HorizontalAlignment.Right;
            chips.Children.Add(scan);
            page.Children.Add(Line(Lang.S("Что запускается при входе", "Runs at sign-in"), chips));

            _autoStatus = SmallNote();
            _autoStatus.Margin = new Thickness(0, 8, 0, 0);
            page.Children.Add(_autoStatus);

            _autoList = new StackPanel();
            page.Children.Add(_autoList);

            if (Autostart.Checked == DateTime.MinValue) ScanAutostart();
            else ShowAutostart();
        }

        void ScanAutostart()
        {
            if (Autostart.Busy) return;
            Autostart.Run(delegate
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke((Action)delegate { ShowAutostart(); });
            });
            ShowAutostart();       // Busy выставлен — покажет «Проверяю…»
        }

        void ShowAutostart()
        {
            if (_autoList == null) return;
            _autoList.Children.Clear();
            if (Autostart.Busy)
            {
                _autoStatus.Text = Lang.S("Читаю автозагрузку…", "Reading startup entries…");
                return;
            }

            List<StartupItem> items = Autostart.Items;
            int on = 0;
            foreach (StartupItem it in items) if (it.Enabled) on++;

            _autoStatus.Text = Lang.S(
                "Отключение действует со следующего входа в Windows и не закрывает уже запущенную программу. Запись не удаляется — её можно включить обратно. «Влияние» — оценка по текущему потреблению памяти и ЦП процессами записи.",
                "Disabling takes effect at the next sign-in and does not close a running program. The entry is not deleted — you can turn it back on. “Impact” is an estimate from the current memory and CPU use of the entry's processes.");

            if (items.Count == 0)
            {
                _autoList.Children.Add(UpdGroup(Lang.S("ЗАПИСИ", "ENTRIES"), "✓ 0", GoodColor,
                    UpdNote(Lang.S("Автозагрузка пуста.", "Nothing runs at sign-in."), GoodColor)));
            }
            else
            {
                var rows = new List<FrameworkElement>();
                int shown = 0;
                foreach (StartupItem it in items)
                {
                    if (!_autoAll && shown >= AutoCap) break;
                    rows.Add(AutoRow(it));
                    shown++;
                }
                if (items.Count > shown || _autoAll && items.Count > AutoCap)
                {
                    Border more = ActionChip(_autoAll ? Lang.S("Свернуть", "Collapse")
                                                      : Lang.S("Все (", "All (") + items.Count + ")",
                        delegate { _autoAll = !_autoAll; ShowAutostart(); });
                    more.Margin = new Thickness(0, 8, 0, 0);
                    more.HorizontalAlignment = HorizontalAlignment.Left;
                    rows.Add(more);
                }
                _autoList.Children.Add(UpdGroup(Lang.S("ЗАПИСИ", "ENTRIES"),
                    on + " / " + items.Count + " " + Lang.S("включено", "enabled"), null, rows.ToArray()));
            }

            if (_win != null)
                _win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)delegate { Place(); });
        }

        FrameworkElement AutoRow(StartupItem it)
        {
            StartupItem item = it;
            var card = new StackPanel();
            card.Margin = new Thickness(0, 8, 0, 0);

            // линия 1: название и плашка влияния
            var l1 = new DockPanel();
            l1.LastChildFill = true;

            string impactText, impactColor;
            ImpactLook(it, out impactText, out impactColor);
            Border pill = Pill(impactText, impactColor);
            pill.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(pill, Dock.Right);
            l1.Children.Add(pill);

            var name = new TextBlock();
            name.Text = it.Name;
            name.TextWrapping = TextWrapping.Wrap;
            name.FontFamily = new FontFamily(Theme.FontUi);
            name.FontSize = Theme.Fs(11);
            name.Foreground = Theme.B(it.Enabled ? Theme.TextHi : Theme.TextMid);
            name.VerticalAlignment = VerticalAlignment.Center;
            l1.Children.Add(name);
            card.Children.Add(l1);

            // линия 2: откуда, сколько ест; справа кнопка
            var l2 = new DockPanel();
            l2.LastChildFill = true;
            l2.Margin = new Thickness(0, 2, 0, 0);

            if (it.CanToggle)
            {
                Border chip = ActionChip(it.Enabled ? Lang.S("Отключить", "Disable") : Lang.S("Включить", "Enable"),
                    delegate
                    {
                        string err = Autostart.SetEnabled(item, !item.Enabled);
                        if (err.Length > 0) _autoStatus.Text = Lang.S("Не удалось: ", "Failed: ") + err;
                        else ShowAutostart();       // процесс ещё работает — влияние остаётся прежним
                    });
                chip.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(chip, Dock.Right);
                l2.Children.Add(chip);
            }

            var sub = SmallNote();
            var parts = new List<string>();
            if (it.Company.Length > 0) parts.Add(it.Company);
            parts.Add(it.Source);
            if (it.Running)
                parts.Add(it.MemMB.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " " + Lang.S("МБ", "MB")
                        + ", ЦП " + it.CpuPct.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%");
            if (it.Lock.Length > 0) parts.Add(it.Lock);
            sub.Text = string.Join(" · ", parts.ToArray());
            l2.Children.Add(sub);
            card.Children.Add(l2);
            return card;
        }

        void ImpactLook(StartupItem it, out string text, out string color)
        {
            if (!it.Enabled) { text = Lang.S("отключено", "disabled"); color = null; return; }
            switch (it.Impact)
            {
                case 3: text = Lang.S("высокое", "high"); color = Theme.WarnColor; return;
                case 2: text = Lang.S("среднее", "medium"); color = NoteColor; return;
                case 1: text = Lang.S("низкое", "low"); color = GoodColor; return;
                default: text = Lang.S("не запущено", "not running"); color = null; return;
            }
        }

        Border Pill(string text, string color)
        {
            var t = new TextBlock();
            t.Text = text;
            t.FontFamily = new FontFamily(Theme.FontUi);
            t.FontSize = Theme.Fs(10);
            t.Foreground = color != null ? Theme.B(color) : Theme.B(Theme.TextMid);

            var pill = new Border();
            pill.CornerRadius = new CornerRadius(Theme.RadiusPill);
            pill.Padding = new Thickness(8, 2, 8, 2);
            pill.Background = Theme.B(color != null ? Theme.Fade(color, 0x26) : Theme.StrokeSoft);
            pill.Child = t;
            pill.VerticalAlignment = VerticalAlignment.Center;
            return pill;
        }

        /// <summary>Мелкий приглушённый текст с переносом (сноски и подписи строк).</summary>
        TextBlock SmallNote()
        {
            var t = new TextBlock();
            t.TextWrapping = TextWrapping.Wrap;
            t.FontFamily = new FontFamily(Theme.FontUi);
            t.FontSize = Theme.Fs(10);
            t.LineHeight = Theme.Fs(14);
            t.Foreground = Theme.B(Theme.TextMid);
            return t;
        }
    }
}
