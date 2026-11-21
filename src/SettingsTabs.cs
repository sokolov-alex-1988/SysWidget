using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SysWidget
{
    /// <summary>
    /// Вкладки окна настроек и общие кирпичики для новых настроек: ряд чипов
    /// «выбери одно», переключатель, шаговый регулятор. Каждый кирпичик сам
    /// перерисовывается из Refresh (список _repaints), поэтому состояние чипа
    /// всегда берётся из Prefs, а не хранится в окне.
    /// </summary>
    public sealed partial class SettingsWindow
    {
        const int TabCount = 5;

        StackPanel[] _pages;
        Border[] _tabChips;
        TextBlock[] _tabTexts;
        int _tab;

        // подсказки и плашка «нужен перезапуск» видны на всех вкладках
        Border _restartBar;

        readonly List<Action> _repaints = new List<Action>();

        // ------------------------------------------------------------- вкладки

        /// <summary>Панель вкладок; страницы создаются здесь же, содержимое наполняет Build.</summary>
        FrameworkElement BuildTabBar(StackPanel root)
        {
            string[] names =
            {
                Lang.S("Карточка", "Card"), Lang.S("Вид", "Look"), Lang.S("Данные", "Data"),
                Lang.S("Тревоги", "Alerts"), Lang.S("Система", "System")
            };

            var grid = new System.Windows.Controls.Primitives.UniformGrid();
            grid.Columns = TabCount;
            grid.Margin = new Thickness(0, 10, 0, 0);

            _tabChips = new Border[TabCount];
            _tabTexts = new TextBlock[TabCount];
            _pages = new StackPanel[TabCount];

            for (int i = 0; i < TabCount; i++)
            {
                int idx = i;
                Border chip = Toggle(names[i], out _tabTexts[i]);
                chip.Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0);
                chip.Padding = new Thickness(2, 5, 2, 5);
                _tabTexts[i].HorizontalAlignment = HorizontalAlignment.Center;
                chip.MouseLeftButtonUp += delegate { ShowTab(idx); };
                _tabChips[i] = chip;
                grid.Children.Add(chip);

                _pages[i] = new StackPanel();
                _pages[i].Visibility = i == _tab ? Visibility.Visible : Visibility.Collapsed;
            }
            return grid;
        }

        void ShowTab(int index)
        {
            if (_pages == null || index < 0 || index >= TabCount) return;
            _tab = index;
            if (_scroll != null) _scroll.ScrollToTop();
            for (int i = 0; i < TabCount; i++)
                _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            Refresh();

            // высота окна зависит от вкладки: после пересчёта размера подтягиваем
            // его к границам экрана
            if (_win != null)
                _win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)delegate { Place(); });
        }

        void PaintTabs()
        {
            if (_tabChips == null) return;
            for (int i = 0; i < TabCount; i++) Paint(_tabChips[i], _tabTexts[i], i == _tab);
        }

        // ---------------------------------------------------------- кирпичики

        /// <summary>Ряд чипов «выбери одно»: текущее значение берётся из get, выбор уходит в set.</summary>
        FrameworkElement Choice(string[] labels, Func<int> get, Action<int> set)
        {
            var host = new StackPanel();
            host.Orientation = Orientation.Horizontal;
            host.HorizontalAlignment = HorizontalAlignment.Right;

            var chips = new Border[labels.Length];
            var texts = new TextBlock[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                chips[i] = Toggle(labels[i], out texts[i]);
                if (i == 0) chips[i].Margin = new Thickness(0);
                chips[i].MouseLeftButtonUp += delegate { set(idx); Refresh(); };
                host.Children.Add(chips[i]);
            }

            _repaints.Add(delegate
            {
                int cur = get();
                for (int i = 0; i < chips.Length; i++) Paint(chips[i], texts[i], i == cur);
            });
            return host;
        }

        /// <summary>Переключатель «Вкл / Выкл».</summary>
        FrameworkElement OnOff(Func<bool> get, Action<bool> set)
        {
            TextBlock text;
            Border chip = Toggle(Lang.S("Выключено", "Off"), out text);
            chip.Margin = new Thickness(0);
            chip.MouseLeftButtonUp += delegate { set(!get()); Refresh(); };
            _repaints.Add(delegate
            {
                bool on = get();
                Paint(chip, text, on);
                text.Text = on ? Lang.S("Включено", "On") : Lang.S("Выключено", "Off");
            });
            return chip;
        }

        /// <summary>«− значение +»: текст значения собирает text(), кнопки зовут dec и inc.</summary>
        FrameworkElement Stepper(Func<string> text, Action dec, Action inc)
        {
            TextBlock value = LimitValue();
            _repaints.Add(delegate { value.Text = text(); });
            return LimitEditor(value, delegate { dec(); Refresh(); }, delegate { inc(); Refresh(); });
        }

        /// <summary>Кнопка-действие: нажал — сделалось (без состояния).</summary>
        Border ActionChip(string label, Action act)
        {
            TextBlock text;
            Border chip = Toggle(label, out text);
            chip.MouseLeftButtonUp += delegate { act(); };
            text.Foreground = Theme.B(Theme.TextHi);
            return chip;
        }

        /// <summary>Изменили настройку вида, которая действует сразу, — сохранить и применить.</summary>
        void LookChanged()
        {
            Prefs.Save();
            _widget.ApplyLook();
        }

        // ------------------------------------------------------------ вкладка «Вид»

        static readonly string[] Palette =
        {
            "#34D399", "#4DFFA8", "#A3E635", "#FBBF24", "#FFD000", "#FF9F0A",
            "#FB7185", "#FF3D7F", "#C084FC", "#5B9CFF", "#00F0FF", "#FFFFFF"
        };

        void BuildLookTab(StackPanel page)
        {
            // --- тема, плотность, шрифт: читаются при создании окон — нужен перезапуск
            page.Children.Add(Section(Lang.S("ТЕМА И РАЗМЕРЫ", "THEME AND SIZE")));

            page.Children.Add(Line(Lang.S("Тема", "Theme"),
                Choice(new[] { Lang.S("Неон", "Neon"), Lang.S("Тёмная", "Dark"), Lang.S("Светлая", "Light"), Lang.S("Пиксель", "Pixel"), Lang.S("Терминал", "Terminal") },
                    delegate { return Prefs.Neon ? 0 : (Prefs.Retro ? 3 : (Prefs.Terminal ? 4 : (Prefs.Light ? 2 : 1))); },
                    delegate(int i) { Prefs.Neon = i == 0; Prefs.Light = i == 2; Prefs.Retro = i == 3; Prefs.Terminal = i == 4; Prefs.Save(); })));

            page.Children.Add(Line(Lang.S("Плотность", "Density"),
                Choice(new[] { Lang.S("Компактно", "Compact"), Lang.S("Обычно", "Normal"), Lang.S("Свободно", "Roomy") },
                    delegate { return Prefs.Density; },
                    delegate(int i) { Prefs.Density = i; Prefs.Save(); })));

            double[] fonts = { 0.9, 1.0, 1.1, 1.2 };
            page.Children.Add(Line(Lang.S("Шрифт", "Font size"),
                Choice(new[] { "90%", "100%", "110%", "120%" },
                    delegate
                    {
                        int best = 0;
                        for (int i = 0; i < fonts.Length; i++)
                            if (Math.Abs(fonts[i] - Prefs.FontScale) < Math.Abs(fonts[best] - Prefs.FontScale)) best = i;
                        return best;
                    },
                    delegate(int i) { Prefs.FontScale = fonts[i]; Prefs.Save(); })));

            // --- сразу
            page.Children.Add(Section(Lang.S("ПРОЗРАЧНОСТЬ И ПОДПИСИ", "OPACITY AND LABELS")));

            page.Children.Add(Line(Lang.S("Прозрачность", "Opacity"),
                Stepper(
                    delegate { return string.Format(Inv, "{0:0}%", Prefs.Opacity * 100); },
                    delegate { Prefs.Opacity = Math.Max(0.4, Math.Round(Prefs.Opacity * 10 - 1) / 10); LookChanged(); },
                    delegate { Prefs.Opacity = Math.Min(1.0, Math.Round(Prefs.Opacity * 10 + 1) / 10); LookChanged(); })));

            page.Children.Add(Line(Lang.S("Бледнеть в простое", "Fade when idle"),
                OnOff(delegate { return Prefs.FadeIdle; },
                      delegate(bool v) { Prefs.FadeIdle = v; LookChanged(); })));

            page.Children.Add(Line(Lang.S("Подписи", "Labels"),
                Choice(new[] { Lang.S("Текст", "Text"), Lang.S("Скрыты", "Hidden") },
                    delegate { return Prefs.HideLabels ? 1 : 0; },
                    delegate(int i) { Prefs.HideLabels = i == 1; LookChanged(); })));

            page.Children.Add(Line(Lang.S("Кнопка «▦» на карточке", "“▦” button on the card"),
                Choice(new[] { Lang.S("Показана", "Shown"), Lang.S("Скрыта", "Hidden") },
                    delegate { return Prefs.ShowDashBtn ? 0 : 1; },
                    delegate(int i) { Prefs.ShowDashBtn = i == 0; Prefs.Save(); _widget.ApplyDashButton(); })));

            // --- цвета
            page.Children.Add(Section(Lang.S("ЦВЕТА МЕТРИК", "METRIC COLOURS")));
            page.Children.Add(BuildColorGrid());

            // --- цвет по нагрузке
            page.Children.Add(Section(Lang.S("ЦВЕТ ПО НАГРУЗКЕ", "COLOUR BY LOAD")));

            page.Children.Add(Line(Lang.S("Менять цвет числа", "Colour the number"),
                OnOff(delegate { return Prefs.ColorByLoad; },
                      delegate(bool v) { Prefs.ColorByLoad = v; LookChanged(); })));

            page.Children.Add(Line(Lang.S("Жёлтый от", "Amber from"),
                Stepper(
                    delegate { return Prefs.WarnAt + "%"; },
                    delegate { Prefs.WarnAt = Math.Max(30, Prefs.WarnAt - 5); LookChanged(); },
                    delegate { Prefs.WarnAt = Math.Min(Prefs.CritAt - 5, Prefs.WarnAt + 5); LookChanged(); })));

            page.Children.Add(Line(Lang.S("Красный от", "Red from"),
                Stepper(
                    delegate { return Prefs.CritAt + "%"; },
                    delegate { Prefs.CritAt = Math.Max(Prefs.WarnAt + 5, Prefs.CritAt - 5); LookChanged(); },
                    delegate { Prefs.CritAt = Math.Min(99, Prefs.CritAt + 5); LookChanged(); })));
        }

        /// <summary>Сетка кружков: клик меняет цвет по кругу палитры, правый клик — возвращает цвет темы.</summary>
        FrameworkElement BuildColorGrid()
        {
            string[] names =
            {
                Lang.Ram, Lang.Cpu, Lang.Gpu, Lang.Net, "SSD", "HDD", Lang.S("Тревога", "Alert")
            };
            Func<int, string> theme = delegate(int i)
            {
                switch (i)
                {
                    case 0: return Theme.RamColor;
                    case 1: return Theme.CpuColor;
                    case 2: return Theme.GpuColor;
                    case 3: return Theme.NetColor;
                    case 4: return Theme.SsdColor;
                    case 5: return Theme.HddColor;
                    default: return Theme.WarnColor;
                }
            };
            Func<int, string> getAcc = delegate(int i)
            {
                switch (i)
                {
                    case 0: return Prefs.AccRam;
                    case 1: return Prefs.AccCpu;
                    case 2: return Prefs.AccGpu;
                    case 3: return Prefs.AccNet;
                    case 4: return Prefs.AccSsd;
                    case 5: return Prefs.AccHdd;
                    default: return Prefs.AccWarn;
                }
            };
            Action<int, string> setAcc = delegate(int i, string v)
            {
                switch (i)
                {
                    case 0: Prefs.AccRam = v; break;
                    case 1: Prefs.AccCpu = v; break;
                    case 2: Prefs.AccGpu = v; break;
                    case 3: Prefs.AccNet = v; break;
                    case 4: Prefs.AccSsd = v; break;
                    case 5: Prefs.AccHdd = v; break;
                    default: Prefs.AccWarn = v; break;
                }
                Prefs.Save();
            };

            var wrap = new WrapPanel();
            wrap.Margin = new Thickness(0, 7, 0, 0);

            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                var dot = new System.Windows.Shapes.Ellipse();
                dot.Width = 12; dot.Height = 12;
                dot.VerticalAlignment = VerticalAlignment.Center;
                dot.Margin = new Thickness(0, 0, 6, 0);

                var label = new TextBlock();
                label.Text = names[i];
                label.FontFamily = new FontFamily(Theme.FontUi);
                label.FontSize = Theme.Fs(10.5);
                label.Foreground = Theme.B(Theme.TextHi);
                label.VerticalAlignment = VerticalAlignment.Center;

                var row = new StackPanel();
                row.Orientation = Orientation.Horizontal;
                row.Children.Add(dot);
                row.Children.Add(label);

                var chip = new Border();
                chip.CornerRadius = new CornerRadius(Theme.GroupRadius);
                chip.Padding = new Thickness(7, 4, 9, 4);
                chip.Margin = new Thickness(0, 0, 6, 6);
                chip.Background = Theme.B(Theme.StrokeSoft);
                chip.Cursor = Cursors.Hand;
                chip.Tag = NoDragTag;
                chip.Child = row;

                chip.MouseLeftButtonUp += delegate
                {
                    string cur = getAcc(idx);
                    if (cur.Length == 0) cur = ColorKey(theme(idx));
                    int at = Array.IndexOf(Palette, cur);
                    setAcc(idx, Palette[(at + 1) % Palette.Length]);
                    Refresh();
                };
                chip.MouseRightButtonUp += delegate { setAcc(idx, ""); Refresh(); };

                _repaints.Add(delegate
                {
                    string acc = getAcc(idx);
                    dot.Fill = Theme.B(acc.Length > 0 ? acc : theme(idx));
                });
                wrap.Children.Add(chip);
            }
            return wrap;
        }

        /// <summary>«#AARRGGBB» → «#RRGGBB» для сравнения с палитрой.</summary>
        static string ColorKey(string c)
        {
            string h = c.TrimStart('#');
            if (h.Length > 6) h = h.Substring(h.Length - 6);
            return "#" + h.ToUpperInvariant();
        }

        // -------------------------------------------------- плашка «нужен перезапуск»

        FrameworkElement BuildRestartBar()
        {
            var text = new TextBlock();
            text.Text = Lang.S("Тема, плотность, шрифт и цвета применятся после перезапуска",
                               "Theme, density, font and colours apply after a restart");
            text.TextWrapping = TextWrapping.Wrap;
            text.FontFamily = new FontFamily(Theme.FontUi);
            text.FontSize = Theme.Fs(10);
            text.Foreground = Theme.B(Theme.TextHi);
            text.VerticalAlignment = VerticalAlignment.Center;

            Border go = ActionChip(Lang.S("Перезапустить", "Restart"), delegate { _widget.RestartApp(); });
            go.Margin = new Thickness(8, 0, 0, 0);
            go.Background = Theme.B(Theme.SurfaceActive);

            var dock = new DockPanel();
            DockPanel.SetDock(go, Dock.Right);
            dock.Children.Add(go);
            dock.Children.Add(text);

            _restartBar = new Border();
            _restartBar.Margin = new Thickness(0, 12, 0, 0);
            _restartBar.Padding = new Thickness(8, 6, 6, 6);
            _restartBar.CornerRadius = new CornerRadius(Theme.GroupRadius);
            _restartBar.Background = Theme.B(Theme.StrokeSoft);
            _restartBar.BorderBrush = Theme.B(Theme.BtnBusy);
            _restartBar.BorderThickness = new Thickness(1);
            _restartBar.Child = dock;
            _restartBar.Visibility = Visibility.Collapsed;
            return _restartBar;
        }

        // -------------------------------------------- вкладки «Данные» и «Система»

        /// <summary>Где лежат данные и сброс настроек вида.</summary>
        void BuildDataTab(StackPanel page)
        {
            page.Children.Add(Section(Lang.S("ДАННЫЕ", "DATA")));

            page.Children.Add(Line(Lang.S("Имя Wi-Fi в подсказке", "Wi-Fi name in tooltip"),
                OnOff(delegate { return Prefs.ShowSsid; },
                      delegate(bool v) { Prefs.ShowSsid = v; Prefs.Save(); })));
            var ssidNote = new TextBlock();
            ssidNote.Text = Lang.S("Windows считает чтение имени сети обращением к местоположению: при включении раз в 5 минут в трее на секунду появляется значок геолокации («Оболочка сетевых команд»).",
                                   "Windows treats reading the network name as a location request: when on, a location icon flashes in the tray every 5 minutes (“Network Command Shell”).");
            ssidNote.TextWrapping = TextWrapping.Wrap;
            ssidNote.FontFamily = new FontFamily(Theme.FontUi);
            ssidNote.FontSize = Theme.Fs(10);
            ssidNote.LineHeight = Theme.Fs(14);
            ssidNote.Foreground = Theme.B(Theme.TextMid);
            ssidNote.Margin = new Thickness(0, 4, 0, 0);
            page.Children.Add(ssidNote);

            page.Children.Add(Line(Lang.S("Лимит трафика в месяц", "Monthly traffic limit"),
                Stepper(
                    delegate { return Prefs.TrafficLimitGB > 0 ? Prefs.TrafficLimitGB + Lang.S(" ГБ", " GB") : Lang.S("выкл", "off"); },
                    delegate { Prefs.TrafficLimitGB = Math.Max(0, Prefs.TrafficLimitGB - 10); Prefs.Save(); },
                    delegate { Prefs.TrafficLimitGB = Math.Min(100000, Prefs.TrafficLimitGB + 10); Prefs.Save(); })));

            page.Children.Add(Line(Lang.S("Папка данных", "Data folder"),
                ActionChip(Lang.S("Открыть", "Open"), delegate
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", "\"" + AppPaths.DataDir + "\""); }
                    catch (Exception ex) { ErrorLog.Write("open data dir", ex); }
                })));

            page.Children.Add(Line(Lang.S("Настройки вида", "Look settings"),
                ActionChip(Lang.S("Сбросить", "Reset"), delegate
                {
                    Prefs.Reset();
                    Prefs.Save();
                    _widget.ApplyLook();
                    Refresh();
                })));

            BuildCleanerSection(page);
        }

        // ------------------------------------------------------ очистка диска

        bool _cleanBusy, _cleanScanned;
        TextBlock _cleanStatus;

        /// <summary>
        /// Очистка мусора и безопасных кэшей. Порядок такой, чтобы случайно ничего не
        /// стёрлось: «Проверить» только считает, «Очистить» удаляет то, что отмечено.
        /// Работает в фоне — окно не замирает.
        /// </summary>
        void BuildCleanerSection(StackPanel page)
        {
            page.Children.Add(Section(Lang.S("ОЧИСТКА ДИСКА", "DISK CLEANUP")));

            foreach (DiskCleaner.Category cat in DiskCleaner.Categories)
            {
                DiskCleaner.Category c = cat;
                TextBlock size = LimitValue();
                size.Margin = new Thickness(0, 0, 8, 0);
                _repaints.Add(delegate
                {
                    size.Text = _cleanScanned ? DiskCleaner.Size(c.Bytes) : "";
                });

                var right = new StackPanel();
                right.Orientation = Orientation.Horizontal;
                right.HorizontalAlignment = HorizontalAlignment.Right;
                right.Children.Add(size);
                right.Children.Add(OnOff(delegate { return c.On; },
                                         delegate(bool v) { c.On = v; }));
                page.Children.Add(Line(c.Name, right));
            }

            _cleanStatus = new TextBlock();
            _cleanStatus.TextWrapping = TextWrapping.Wrap;
            _cleanStatus.FontFamily = new FontFamily(Theme.FontUi);
            _cleanStatus.FontSize = Theme.Fs(10);
            _cleanStatus.Foreground = Theme.B(Theme.TextMid);
            _cleanStatus.Margin = new Thickness(0, 8, 0, 0);
            _cleanStatus.Text = Lang.S(
                "Только то, что Windows и программы создают заново. Корзина, «Загрузки», пароли и куки не затрагиваются.",
                "Only what Windows and apps recreate on their own. Recycle Bin, Downloads, passwords and cookies are untouched.");

            Border scan = ActionChip(Lang.S("Проверить", "Scan"), delegate { StartScan(); });
            Border clean = ActionChip(Lang.S("Очистить", "Clean"), delegate { StartClean(); });
            var chips = new StackPanel();
            chips.Orientation = Orientation.Horizontal;
            chips.HorizontalAlignment = HorizontalAlignment.Right;
            scan.Margin = new Thickness(0);
            chips.Children.Add(scan);
            chips.Children.Add(clean);
            page.Children.Add(Line(Lang.S("Действие", "Action"), chips));
            page.Children.Add(_cleanStatus);
        }

        void StartScan()
        {
            if (_cleanBusy) return;
            _cleanBusy = true;
            _cleanStatus.Text = Lang.S("Идёт проверка…", "Scanning…");
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                long total = 0;
                try { total = DiskCleaner.Scan(); }
                catch (Exception ex) { ErrorLog.Write("cleaner scan", ex); }
                _win.Dispatcher.BeginInvoke((Action)delegate
                {
                    _cleanBusy = false;
                    _cleanScanned = true;
                    _cleanStatus.Text = Lang.S("Можно освободить: ", "Can free: ") + DiskCleaner.Size(total)
                        + Lang.S(". Нажмите «Очистить».", ". Press Clean.");
                    Refresh();
                });
            });
        }

        void StartClean()
        {
            if (_cleanBusy) return;
            if (!_cleanScanned) { StartScan(); return; }   // сначала покажем, что будет удалено
            _cleanBusy = true;
            _cleanStatus.Text = Lang.S("Очистка…", "Cleaning…");
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                DiskCleaner.Result r = null;
                try { r = DiskCleaner.Clean(); }
                catch (Exception ex) { ErrorLog.Write("cleaner clean", ex); }
                _win.Dispatcher.BeginInvoke((Action)delegate
                {
                    _cleanBusy = false;
                    _cleanScanned = false;
                    _cleanStatus.Text = r == null
                        ? Lang.S("Очистка не удалась — подробности в error.log.", "Cleanup failed — see error.log.")
                        : Lang.S("Освобождено: ", "Freed: ") + DiskCleaner.Size(r.Freed)
                          + Lang.S(" (файлов: ", " (files: ") + r.Deleted
                          + (r.Skipped > 0 ? Lang.S(", занято и пропущено: ", ", in use and skipped: ") + r.Skipped : "")
                          + ")";
                    Refresh();
                });
            });
        }
        void BuildSystemExtras(StackPanel page)
        {
            // сочетание зашито в виджете (Ctrl+Alt+W); null — его уже заняла другая программа
            string hot = _widget.HotkeyText;
            TextBlock t = LimitValue();
            t.Text = hot != null ? hot : Lang.S("занято другой программой", "taken by another program");
            page.Children.Add(Line(Lang.S("Показать / скрыть", "Show / hide"), t));

            BuildAdminSection(page);
            BuildUpdatesSection(page);
            BuildDiagSections(page);
        }

        // ------------------------------------------------ расширенные данные (администратор)

        bool _adminBusy;

        void BuildAdminSection(StackPanel page)
        {
            page.Children.Add(Section(Lang.S("РАСШИРЕННЫЕ ДАННЫЕ", "EXTENDED DATA")));

            TextBlock label;
            Border chip = Toggle(Lang.S("Подключить", "Connect"), out label);
            chip.Margin = new Thickness(0);

            TextBlock status = LimitValue();
            _repaints.Add(delegate
            {
                label.Text = AdminHelper.Connected ? Lang.S("Отключить", "Disconnect") : Lang.S("Подключить", "Connect");
                status.Text = AdminHelper.Connected ? Lang.S("подключено", "connected")
                            : _adminBusy ? Lang.S("жду подтверждения UAC…", "waiting for UAC…")
                            : AdminHelper.LastError.Length > 0 ? AdminHelper.LastError : Lang.S("выключено", "off");
                Paint(chip, label, AdminHelper.Connected);
            });

            chip.MouseLeftButtonUp += delegate
            {
                if (AdminHelper.Connected) { AdminHelper.Stop(); Refresh(); return; }
                if (_adminBusy) return;
                _adminBusy = true;
                Refresh();
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    AdminHelper.Launch();
                    _win.Dispatcher.BeginInvoke((Action)delegate { _adminBusy = false; Refresh(); });
                });
            };

            var right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.HorizontalAlignment = HorizontalAlignment.Right;
            status.Margin = new Thickness(0, 0, 8, 0);
            right.Children.Add(status);
            right.Children.Add(chip);
            page.Children.Add(Line(Lang.S("Температура и износ диска", "Drive temperature and wear"), right));

            var note = new TextBlock();
            note.Text = Lang.S(
                "Windows отдаёт эти данные только администратору. По кнопке этот же файл запускается ещё раз с правами администратора (запрос UAC) и только отвечает на запросы виджета; закрывается вместе с ним. Включайте, если папка с программой недоступна на запись другим программам.",
                "Windows gives this data to administrators only. The button launches this same file once more with administrator rights (UAC prompt); it only answers the widget's requests and closes with it. Enable only if the program folder is not writable by other programs.");
            note.TextWrapping = TextWrapping.Wrap;
            note.FontFamily = new FontFamily(Theme.FontUi);
            note.FontSize = Theme.Fs(10);
            note.LineHeight = Theme.Fs(14);
            note.Foreground = Theme.B(Theme.TextMid);
            note.Margin = new Thickness(0, 4, 0, 0);
            page.Children.Add(note);
        }

        // ----------------------------------------------------------- обновления

        StackPanel _updList;
        TextBlock _updStatus;

        void BuildUpdatesSection(StackPanel page)
        {
            page.Children.Add(Section(Lang.S("ОБНОВЛЕНИЯ", "UPDATES")));

            Border check = ActionChip(Lang.S("Проверить", "Check"), delegate { CheckUpdates(); });
            Border open = ActionChip(Lang.S("Центр обновления", "Windows Update"), delegate { Updates.OpenWindowsUpdate(); });
            var chips = new StackPanel();
            chips.Orientation = Orientation.Horizontal;
            chips.HorizontalAlignment = HorizontalAlignment.Right;
            check.Margin = new Thickness(0);
            chips.Children.Add(check);
            chips.Children.Add(open);
            page.Children.Add(Line(Lang.S("Windows и программы", "Windows and apps"), chips));

            _updStatus = new TextBlock();
            _updStatus.TextWrapping = TextWrapping.Wrap;
            _updStatus.FontFamily = new FontFamily(Theme.FontUi);
            _updStatus.FontSize = Theme.Fs(10);
            _updStatus.Foreground = Theme.B(Theme.TextMid);
            _updStatus.Margin = new Thickness(0, 8, 0, 0);
            page.Children.Add(_updStatus);

            _updList = new StackPanel();
            page.Children.Add(_updList);
            ShowUpdates();
        }

        void CheckUpdates()
        {
            if (Updates.Busy) return;
            Updates.Run(delegate
            {
                _win.Dispatcher.BeginInvoke((Action)delegate { ShowUpdates(); });
            });
            ShowUpdates();      // Busy уже выставлен — покажет «Проверка…»
        }

        static string GoodColor { get { return Theme.Light ? "#059669" : "#34D399"; } }     // всё актуально
        static string NoteColor { get { return Theme.Light ? "#B45309" : "#FBBF24"; } }     // есть обновления
        // ошибка — Theme.WarnColor

        /// <summary>Нарисовать последний результат проверки (или подсказку, если проверки ещё не было).</summary>
        void ShowUpdates()
        {
            _updList.Children.Clear();
            if (Updates.Busy)
            {
                _updStatus.Text = Lang.S("Идёт проверка… Windows отвечает до минуты.",
                                         "Checking… Windows can take up to a minute.");
                return;
            }
            if (Updates.Checked == DateTime.MinValue)
            {
                _updStatus.Text = Lang.S("Проверка запускается кнопкой. Обновления Windows ставит Центр обновления (нужны права администратора).",
                                         "Checks run on demand. Windows updates are installed by Windows Update (needs administrator).");
                return;
            }
            _updStatus.Text = Lang.S("Проверено в ", "Checked at ") + Updates.Checked.ToString("HH:mm");

            // ---- Windows
            if (Updates.WinError.Length > 0)
                _updList.Children.Add(UpdGroup("WINDOWS", "⚠ " + Lang.S("ошибка", "error"), Theme.WarnColor,
                    UpdNote(Updates.WinError, Theme.WarnColor)));
            else if (Updates.WinTitles.Count == 0)
                _updList.Children.Add(UpdGroup("WINDOWS", "✓ " + Lang.S("актуально", "up to date"), GoodColor,
                    UpdNote(Lang.S("Обновлений не найдено — установлены последние версии.",
                                   "No updates found — you are up to date."), GoodColor)));
            else
            {
                var rows = new List<FrameworkElement>();
                foreach (string title in Updates.WinTitles) rows.Add(UpdRow(title, null));
                rows.Add(UpdNote(Lang.S("Устанавливаются в Центре обновления Windows (кнопка «Центр обновления» выше).",
                                        "Install them in Windows Update (the button above)."), null));
                _updList.Children.Add(UpdGroup("WINDOWS", "● " + Updates.WinTitles.Count + " " + Lang.S("доступно", "available"),
                    NoteColor, rows.ToArray()));
            }

            // необязательные драйверы — отдельно: в основной список Центра обновления они не входят
            if (Updates.WinDrivers.Count > 0)
            {
                var rows = new List<FrameworkElement>();
                foreach (string d in Updates.WinDrivers) rows.Add(UpdRow(d, null));
                rows.Add(UpdNote(Lang.S("Центр обновления показывает их в разделе «Дополнительные параметры → Необязательные обновления».",
                                        "Windows Update lists them under Advanced options → Optional updates."), null));
                _updList.Children.Add(UpdGroup(Lang.S("ДРАЙВЕРЫ (НЕОБЯЗАТЕЛЬНО)", "DRIVERS (OPTIONAL)"),
                    Updates.WinDrivers.Count.ToString(), null, rows.ToArray()));
            }

            // ---- программы
            if (Updates.AppError.Length > 0)
                _updList.Children.Add(UpdGroup(Lang.S("ПРОГРАММЫ", "APPS"), "⚠ " + Lang.S("ошибка", "error"), Theme.WarnColor,
                    UpdNote(Updates.AppError, Theme.WarnColor)));
            else if (Updates.Apps.Count == 0)
                _updList.Children.Add(UpdGroup(Lang.S("ПРОГРАММЫ", "APPS"), "✓ " + Lang.S("актуально", "up to date"), GoodColor,
                    UpdNote(Lang.S("Обновлений не найдено — установлены последние версии.",
                                   "No updates found — you are up to date."), GoodColor)));
            else
            {
                var rows = new List<FrameworkElement>();
                foreach (AppUpdate u in Updates.Apps)
                {
                    AppUpdate app = u;
                    Border go = ActionChip(Lang.S("Обновить", "Update"), delegate { Updates.UpgradeApp(app.Id); });
                    go.Margin = new Thickness(8, 0, 0, 0);
                    rows.Add(UpdRow(app.Name, app.Version + " → " + app.Available, go));
                }
                _updList.Children.Add(UpdGroup(Lang.S("ПРОГРАММЫ", "APPS"), "● " + Updates.Apps.Count + " " + Lang.S("доступно", "available"),
                    NoteColor, rows.ToArray()));
            }

            // список вырос или сократился — окну нужно пересчитать высоту
            if (_win != null)
                _win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)delegate { Place(); });
        }

        /// <summary>Группа: заголовок слева, цветной значок состояния справа, под ним строки.</summary>
        FrameworkElement UpdGroup(string title, string badge, string badgeColor, params FrameworkElement[] body)
        {
            var box = new StackPanel();

            var head = new DockPanel();
            head.Margin = new Thickness(0, 14, 0, 2);
            head.LastChildFill = false;

            TextBlock label = Section(title);
            label.Margin = new Thickness(0);
            label.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(label, Dock.Left);
            head.Children.Add(label);

            var pillText = new TextBlock();
            pillText.Text = badge;
            pillText.FontFamily = new FontFamily(Theme.FontUi);
            pillText.FontSize = Theme.Fs(10);
            pillText.Foreground = badgeColor != null ? Theme.B(badgeColor) : Theme.B(Theme.TextHi);

            var pill = new Border();
            pill.CornerRadius = new CornerRadius(Theme.RadiusPill);
            pill.Padding = new Thickness(8, 2, 8, 2);
            pill.Background = Theme.B(badgeColor != null ? Theme.Fade(badgeColor, 0x26) : Theme.StrokeSoft);
            pill.Child = pillText;
            pill.HorizontalAlignment = HorizontalAlignment.Right;
            DockPanel.SetDock(pill, Dock.Right);
            head.Children.Add(pill);

            box.Children.Add(head);
            foreach (FrameworkElement e in body) box.Children.Add(e);
            return box;
        }

        /// <summary>Строка элемента: название слева (с переносом), справа — версия и кнопка.</summary>
        FrameworkElement UpdRow(string name, string detail, params FrameworkElement[] extra)
        {
            // с версией и кнопкой — в две линии: название на всю ширину, ниже версия и кнопка;
            // в одну линию версия «14.51.36247.0 → 14.52.37029.0» сжимала название в узкую полоску
            if (detail != null)
            {
                var card = new StackPanel();
                card.Margin = new Thickness(0, 6, 0, 0);

                var title = new TextBlock();
                title.Text = name;
                title.TextWrapping = TextWrapping.Wrap;
                title.FontFamily = new FontFamily(Theme.FontUi);
                title.FontSize = Theme.Fs(11);
                title.Foreground = Theme.B(Theme.TextHi);
                card.Children.Add(title);

                var line = new DockPanel();
                line.LastChildFill = true;
                line.Margin = new Thickness(0, 2, 0, 0);
                var btns = new StackPanel();
                btns.Orientation = Orientation.Horizontal;
                btns.VerticalAlignment = VerticalAlignment.Center;
                foreach (FrameworkElement e in extra) btns.Children.Add(e);
                DockPanel.SetDock(btns, Dock.Right);
                line.Children.Add(btns);
                TextBlock d = LimitValue();
                d.Text = detail;
                d.HorizontalAlignment = HorizontalAlignment.Left;
                d.TextAlignment = TextAlignment.Left;
                d.MinWidth = 0;
                d.FontSize = Theme.Fs(11);
                d.FontWeight = FontWeights.Normal;
                d.Foreground = Theme.B(Theme.TextMid);
                d.TextWrapping = TextWrapping.Wrap;
                d.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(d);
                card.Children.Add(line);
                return card;
            }

            var row = new DockPanel();
            row.LastChildFill = true;
            row.Margin = new Thickness(0, 6, 0, 0);

            var right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Center;
            if (detail != null)
            {
                TextBlock d = LimitValue();
                d.Text = detail;
                right.Children.Add(d);
            }
            foreach (FrameworkElement e in extra) right.Children.Add(e);
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);

            var t = new TextBlock();
            t.Text = name;
            t.TextWrapping = TextWrapping.Wrap;
            t.FontFamily = new FontFamily(Theme.FontUi);
            t.FontSize = Theme.Fs(11);
            t.Foreground = Theme.B(Theme.TextHi);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(t);
            return row;
        }

        /// <summary>Строка пояснения; с цветом — плашка (зелёная «всё хорошо», красная «ошибка»).</summary>
        FrameworkElement UpdNote(string message, string color)
        {
            var t = new TextBlock();
            t.Text = message;
            t.TextWrapping = TextWrapping.Wrap;
            t.FontFamily = new FontFamily(Theme.FontUi);
            t.FontSize = Theme.Fs(10.5);
            t.LineHeight = Theme.Fs(14);
            t.Foreground = Theme.B(color ?? Theme.TextMid);

            var plate = new Border();
            plate.Margin = new Thickness(0, 6, 0, 0);
            if (color != null)
            {
                plate.Padding = new Thickness(8, 6, 8, 6);
                plate.CornerRadius = new CornerRadius(Theme.GroupRadius);
                plate.Background = Theme.B(Theme.Fade(color, 0x1A));
                plate.BorderBrush = Theme.B(Theme.Fade(color, 0x66));
                plate.BorderThickness = new Thickness(1);
            }
            plate.Child = t;
            return plate;
        }

        /// <summary>Перерисовать всё, что собрано кирпичиками, и плашку перезапуска.</summary>
        void RunRepaints()
        {
            PaintTabs();
            for (int i = 0; i < _repaints.Count; i++)
            {
                try { _repaints[i](); }
                catch (Exception ex) { ErrorLog.Write("settings repaint", ex); }
            }
            if (_restartBar != null)
                _restartBar.Visibility = Prefs.NeedsRestart ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
