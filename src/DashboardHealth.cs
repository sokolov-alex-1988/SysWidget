using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SysWidget
{
    /// <summary>
    /// Блоки окна мониторинга, которые отвечают на вопрос «с ПК всё в порядке?», а не «какая
    /// нагрузка»: чек-лист здоровья с кольцом и быстрыми кнопками, лента событий Windows,
    /// журнал тревог и автозагрузка. Данные берутся из тех же модулей, что и окно настроек.
    /// </summary>
    public sealed partial class Dashboard
    {
        [DllImport("kernel32.dll")]
        static extern ulong GetTickCount64();

        sealed class HRow { public Ellipse Dot; public TextBlock Value; }
        sealed class HVal { public string V = ""; public int L = 3; }      // L: 0 в порядке, 1 замечание, 2 проблема, 3 неизвестно

        readonly List<HRow> _hrows = new List<HRow>();
        Ring _ring;
        Grid _healthHdr;
        TextBlock _hStatus, _hSub;
        DateTime _healthAt = DateTime.MinValue, _scanAt = DateTime.MinValue;
        TextBlock _cleanText;
        int _cleanState;                        // 0 — готово, 1 — проверяю, 2 — можно очистить, 3 — очищаю

        TextBlock _evSub, _alSub, _auSub;
        StackPanel _evRows, _alRows, _auRows;
        string _evKey, _alKey, _auKey;

        static string LevelColor(int level)
        {
            return level == 0 ? cOk : level == 1 ? Theme.BattMidColor : level == 2 ? Theme.WarnColor : cMid;
        }

        static string Stamp(DateTime t)
        {
            return t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("d MMM HH:mm");
        }

        // ------------------------------------------------------------ чек-лист здоровья

        Border BuildHealthCard()
        {
            Border card = CardBox(new Thickness(14, 10, 14, 8));
            var dock = new DockPanel();
            dock.LastChildFill = true;
            card.Child = dock;

            TextBlock title = Small(13.5, cAccent);
            title.FontWeight = FontWeights.SemiBold;
            title.Text = Lang.S("Состояние ПК", "PC health");
            DockPanel.SetDock(title, Dock.Top);
            if (Theme.Terminal) Frame(card, BoxCpu, title, null);      // «Терминал»: название в рамке
            dock.Children.Add(title);

            // кольцо и общий вывод
            var hdr = new Grid();
            _healthHdr = hdr;
            hdr.Margin = new Thickness(0, 6, 0, 4);
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _ring = new Ring();
            _ring.Width = 56;
            _ring.Height = 56;
            Grid.SetColumn(_ring, 0);
            hdr.Children.Add(_ring);
            var st = new StackPanel();
            st.Margin = new Thickness(14, 0, 0, 0);
            st.VerticalAlignment = VerticalAlignment.Center;
            _hStatus = Small(15, cHi);
            _hStatus.FontWeight = FontWeights.SemiBold;
            _hSub = Small(10, cMid);
            st.Children.Add(_hStatus);
            st.Children.Add(_hSub);
            Grid.SetColumn(st, 1);
            hdr.Children.Add(st);
            DockPanel.SetDock(hdr, Dock.Top);
            dock.Children.Add(hdr);

            // быстрые кнопки — внизу карточки
            WrapPanel btns = MakeHealthButtons();
            btns.Margin = new Thickness(0, 6, 0, 0);
            DockPanel.SetDock(btns, Dock.Bottom);
            dock.Children.Add(btns);

            // строки проверок
            string[] names =
            {
                Lang.S("Обновления", "Updates"), Lang.S("Свободное место", "Free space"), Lang.S("Состояние дисков", "Disk health"),
                Lang.S("Автозагрузка", "Startup items"), Lang.S("Ошибки системы", "System errors"),
                Lang.S("Время работы", "Uptime"), Lang.S("Утечки памяти", "Memory leaks")
            };
            var rows = new StackPanel();
            _hrows.Clear();
            for (int i = 0; i < names.Length; i++)
            {
                var r = new HRow();
                var line = new Border();
                line.BorderThickness = new Thickness(0, 0, 0, 1);
                line.BorderBrush = Theme.B(Theme.StrokeSoft);
                line.Padding = new Thickness(0, 3, 0, 3);

                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                r.Dot = new Ellipse();
                r.Dot.Width = 8;
                r.Dot.Height = 8;
                r.Dot.HorizontalAlignment = HorizontalAlignment.Left;
                r.Dot.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(r.Dot, 0);
                g.Children.Add(r.Dot);

                TextBlock nm = Small(11, cLabel);
                nm.Text = names[i];
                nm.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(nm, 1);
                g.Children.Add(nm);

                r.Value = Small(11, cHi);
                r.Value.VerticalAlignment = VerticalAlignment.Center;
                r.Value.TextAlignment = TextAlignment.Right;
                r.Value.TextTrimming = TextTrimming.CharacterEllipsis;
                r.Value.MaxWidth = 220;
                Grid.SetColumn(r.Value, 2);
                g.Children.Add(r.Value);

                line.Child = g;
                rows.Children.Add(line);
                _hrows.Add(r);
            }
            dock.Children.Add(rows);
            return card;
        }

        /// <summary>Быстрые кнопки «Состояния ПК»: очистка кэшей, память, диспетчер задач, центр обновления, параметры.</summary>
        WrapPanel MakeHealthButtons()
        {
            var btns = new WrapPanel();
            TextBlock t;
            btns.Children.Add(PillBtn(Lang.S("Очистить кэши", "Clean caches"), OnCleanClick, out _cleanText));
            btns.Children.Add(PillBtn(Lang.S("Освободить память", "Free memory"), delegate { _w.StartClean(); }, out t));
            btns.Children.Add(PillBtn(Lang.S("Диспетчер задач", "Task Manager"), delegate { Launch("taskmgr.exe"); }, out t));
            btns.Children.Add(PillBtn(Lang.S("Центр обновления", "Windows Update"), delegate { Updates.OpenWindowsUpdate(); }, out t));
            btns.Children.Add(PillBtn(Lang.S("Параметры", "Settings"), delegate { _w.OpenSettings(); }, out t));
            return btns;
        }

        Border PillBtn(string text, Action click, out TextBlock label)
        {
            TextBlock t = Small(10.5, cLabel);
            t.Text = text;
            t.VerticalAlignment = VerticalAlignment.Center;
            label = t;

            var b = new Border();
            b.CornerRadius = new CornerRadius(Theme.RadiusPill);
            b.Padding = new Thickness(9, 3, 9, 3);
            b.Margin = new Thickness(0, 0, 6, 4);
            b.BorderThickness = new Thickness(1);
            b.BorderBrush = Theme.B(cEdge);
            b.Background = Theme.B(Theme.StrokeSoft);
            b.Cursor = Cursors.Hand;
            b.Child = t;
            b.MouseEnter += delegate { b.Background = Theme.B(Theme.BtnHover); t.Foreground = Theme.B(cHi); };
            b.MouseLeave += delegate { b.Background = Theme.B(Theme.StrokeSoft); t.Foreground = Theme.B(cLabel); };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; click(); };
            return b;
        }

        static void Launch(string file)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true }); }
            catch (Exception ex) { ErrorLog.Write("dashboard launch " + file, ex); }
        }

        /// <summary>«Очистить кэши»: первый клик считает, сколько можно освободить, второй — очищает.</summary>
        void OnCleanClick()
        {
            if (_win == null || _cleanState == 1 || _cleanState == 3) return;
            Dispatcher disp = _win.Dispatcher;
            if (_cleanState == 0)
            {
                _cleanState = 1;
                _cleanText.Text = Lang.S("Проверяю…", "Scanning…");
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    long bytes = 0;
                    try { bytes = DiskCleaner.Scan(); }
                    catch (Exception ex) { ErrorLog.Write("dashboard clean scan", ex); }
                    disp.BeginInvoke((Action)delegate
                    {
                        if (bytes <= 0)
                        {
                            _cleanText.Text = Lang.S("Нечего очищать", "Nothing to clean");
                            ResetCleanLater(4);
                        }
                        else
                        {
                            _cleanText.Text = Lang.S("Очистить ", "Clean ") + DiskCleaner.Size(bytes);
                            _cleanState = 2;
                        }
                    });
                });
            }
            else
            {
                _cleanState = 3;
                _cleanText.Text = Lang.S("Очищаю…", "Cleaning…");
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    long freed = 0;
                    try { freed = DiskCleaner.Clean().Freed; }
                    catch (Exception ex) { ErrorLog.Write("dashboard clean", ex); }
                    disp.BeginInvoke((Action)delegate
                    {
                        _cleanText.Text = Lang.S("Освобождено ", "Freed ") + DiskCleaner.Size(freed);
                        ResetCleanLater(6);
                    });
                });
            }
        }

        void ResetCleanLater(int seconds)
        {
            var timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(seconds);
            timer.Tick += delegate
            {
                timer.Stop();
                _cleanState = 0;
                if (_cleanText != null) _cleanText.Text = Lang.S("Очистить кэши", "Clean caches");
            };
            timer.Start();
        }

        /// <summary>Фоновые проверки для чек-листа: журнал событий, автозагрузка, обновления, состояние дисков.</summary>
        void StartHealthScans()
        {
            try
            {
                _scanAt = DateTime.Now;
                if ((DateTime.Now - WinEvents.Checked).TotalMinutes > 5) WinEvents.Run(null);
                if ((DateTime.Now - Autostart.Checked).TotalMinutes > 5) Autostart.Run(null);
                if (Updates.Checked == DateTime.MinValue) Updates.Run(null);       // долго и по сети: один раз за запуск
                DiskHealth.Ensure(null);
            }
            catch (Exception ex) { ErrorLog.Write("dashboard scans", ex); }
        }

        static HVal H(string v, int l)
        {
            var h = new HVal();
            h.V = v;
            h.L = l;
            return h;
        }

        HVal[] ComputeHealth()
        {
            var r = new HVal[7];

            // 0. обновления
            if (Updates.Checked == DateTime.MinValue)
                r[0] = H(Updates.Busy ? Lang.S("проверяются…", "checking…") : Lang.S("не проверялись", "not checked"), 3);
            else
            {
                int w = Updates.WinTitles.Count + Updates.WinDrivers.Count, a = Updates.Apps.Count;
                if (w == 0 && a == 0)
                    r[0] = Updates.WinError.Length > 0 ? H(Lang.S("не удалось проверить", "check failed"), 3) : H(Lang.S("всё актуально", "up to date"), 0);
                else
                {
                    string t = w > 0 ? "Windows: " + w : "";
                    if (a > 0) t += (t.Length > 0 ? ", " : "") + Lang.S("программы: ", "apps: ") + a;
                    r[0] = H(t, w > 0 ? 1 : 0);
                }
            }

            // 1. свободное место: самый заполненный из постоянных дисков
            double worst = 101;
            string wtxt = "";
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue;
                        double pct = d.AvailableFreeSpace * 100.0 / d.TotalSize;
                        if (pct >= worst) continue;
                        worst = pct;
                        wtxt = d.Name.TrimEnd('\\') + "  " + (d.AvailableFreeSpace / 1073741824.0).ToString("0") + Lang.S(" ГБ", " GB")
                            + " (" + pct.ToString("0") + "%)";
                    }
                    catch { }
                }
            }
            catch { }
            r[1] = worst > 100 ? H(Lang.S("нет данных", "no data"), 3) : H(wtxt, worst < 10 ? 2 : worst < 20 ? 1 : 0);

            // 2. состояние дисков по данным хранилища Windows
            bool any = false, bad = false;
            string badTxt = "";
            for (int i = 0; i < 16; i++)
            {
                string g = DiskHealth.Get(i);
                if (g.Length == 0) continue;
                any = true;
                if (DiskHealth.NeedsAttention(i)) { bad = true; badTxt = Lang.S("диск ", "disk ") + i + ": " + g; }
            }
            r[2] = !any ? H(Lang.S("нет данных", "no data"), 3) : bad ? H(badTxt, 2) : H(Lang.S("исправны", "healthy"), 0);

            // 3. автозагрузка
            if (Autostart.Checked == DateTime.MinValue)
                r[3] = H(Autostart.Busy ? Lang.S("проверяется…", "checking…") : Lang.S("не проверялась", "not checked"), 3);
            else
            {
                int on = 0, heavy = 0;
                foreach (StartupItem it in Autostart.Items)
                {
                    if (!it.Enabled || it.IsSelf) continue;
                    on++;
                    if (it.Impact >= 3) heavy++;
                }
                r[3] = H(on + (heavy > 0 ? "  ·  " + Lang.S("тяжёлых: ", "heavy: ") + heavy : ""), heavy >= 2 ? 1 : 0);
            }

            // 4. ошибки системы за сутки
            if (WinEvents.Checked == DateTime.MinValue)
                r[4] = H(WinEvents.Busy ? Lang.S("проверяются…", "checking…") : Lang.S("не проверялись", "not checked"), 3);
            else
            {
                DateTime since = DateTime.Now.AddHours(-24);
                int n = 0;
                bool crit = false;
                foreach (WinEvent e in WinEvents.Items)
                {
                    if (e.Time < since) continue;
                    n++;
                    if (e.Cat == WinEvents.CatReboot || e.Cat == WinEvents.CatHardware) crit = true;
                }
                r[4] = n == 0 ? H(Lang.S("нет за сутки", "none in 24 h"), 0) : H(Lang.S("за сутки: ", "in 24 h: ") + n, crit ? 2 : 1);
            }

            // 5. время работы без перезагрузки
            TimeSpan up = TimeSpan.FromMilliseconds((double)GetTickCount64());
            string ut = up.Days > 0
                ? up.Days + Lang.S(" д ", " d ") + up.Hours + Lang.S(" ч", " h")
                : up.Hours + Lang.S(" ч ", " h ") + up.Minutes + Lang.S(" мин", " min");
            r[5] = H(ut, up.TotalDays > 14 ? 1 : 0);

            // 6. подозрения на утечку памяти
            List<MemoryGrowth> sus = MemoryWatch.Suspects();
            r[6] = sus.Count == 0
                ? H(Lang.S("не замечено", "none"), 0)
                : H(sus[0].Name + " +" + sus[0].GrowthMb.ToString("0") + Lang.S(" МБ", " MB") + (sus.Count > 1 ? "  (+" + (sus.Count - 1) + ")" : ""), 1);
            return r;
        }

        void UpdateHealth()
        {
            if ((DateTime.Now - _scanAt).TotalMinutes > 5) StartHealthScans();
            if ((DateTime.Now - _healthAt).TotalSeconds < 5) return;
            _healthAt = DateTime.Now;

            HVal[] v = ComputeHealth();
            int ok = 0, warn = 0, bad = 0, known = 0;
            for (int i = 0; i < v.Length && i < _hrows.Count; i++)
            {
                HRow row = _hrows[i];
                SetText(row.Value, v[i].V);
                string col = LevelColor(v[i].L);
                if ((row.Dot.Tag as string) != col)
                {
                    row.Dot.Tag = col;
                    row.Dot.Fill = Theme.B(col);
                }
                if (v[i].L == 3) continue;
                known++;
                if (v[i].L == 0) ok++; else if (v[i].L == 1) warn++; else bad++;
            }

            string status, scol;
            if (bad > 0) { status = Lang.S("Есть проблемы", "Problems found"); scol = Theme.WarnColor; }
            else if (warn > 0) { status = Lang.S("Есть замечания", "Needs attention"); scol = Theme.BattMidColor; }
            else { status = Lang.S("Всё в порядке", "All good"); scol = cOk; }
            _ring.Set(known == 0 ? 0 : (double)ok / known, scol, ok + "/" + known);
            SetText(_hStatus, status);
            if ((_hStatus.Tag as string) != scol) { _hStatus.Tag = scol; _hStatus.Foreground = Theme.B(scol); }
            SetText(_hSub, bad + warn > 0
                ? Lang.S("проверок с замечаниями: ", "checks with issues: ") + (bad + warn)
                : Lang.S("все проверки пройдены", "all checks passed"));
        }

        // ------------------------------------------------------------ списки внизу

        Border ListCard(string title, out TextBlock sub, out StackPanel rows)
        {
            Border card = CardBox(new Thickness(14, 10, 14, 8));
            var col = new StackPanel();
            card.Child = col;

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock t = Small(13, cAccent);
            t.FontWeight = FontWeights.SemiBold;
            t.Text = title;
            Grid.SetColumn(t, 0);
            head.Children.Add(t);
            TextBlock s = Small(10, cMid);
            s.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(s, 1);
            head.Children.Add(s);
            col.Children.Add(head);
            if (Theme.Terminal) { Frame(card, BoxProc, t, s); head.Visibility = Visibility.Collapsed; }      // «Терминал»: название и пояснение в рамке

            var r = new StackPanel();
            r.Margin = new Thickness(0, 6, 0, 0);
            col.Children.Add(r);
            sub = s;
            rows = r;
            return card;
        }

        Grid ListRow(string time, string dotColor, string text, string right, string rightColor)
        {
            var g = new Grid();
            g.Margin = new Thickness(0, 2, 0, 2);
            TextBlock tm = Small(10, cMid);
            // колонка времени — по самой широкой метке («00 ноя 00:00»), чтобы дата не заезжала под точку
            tm.Text = "00 ноя 00:00";
            tm.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double timeW = time.Length > 0 ? Math.Max(80, Math.Ceiling(tm.DesiredSize.Width) + 6) : 0;
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(timeW) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            tm.Text = time;
            tm.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(tm, 0);
            g.Children.Add(tm);

            var dot = new Ellipse();
            dot.Width = 7;
            dot.Height = 7;
            dot.HorizontalAlignment = HorizontalAlignment.Left;
            dot.VerticalAlignment = VerticalAlignment.Center;
            dot.Fill = Theme.B(dotColor);
            Grid.SetColumn(dot, 1);
            g.Children.Add(dot);

            TextBlock tx = Small(10.5, cLabel);
            tx.Text = text;
            tx.TextTrimming = TextTrimming.CharacterEllipsis;
            tx.VerticalAlignment = VerticalAlignment.Center;
            tx.ToolTip = text;
            Grid.SetColumn(tx, 2);
            g.Children.Add(tx);

            TextBlock rt = Small(10, rightColor);
            rt.Text = right;
            rt.Margin = new Thickness(10, 0, 0, 0);
            rt.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(rt, 3);
            g.Children.Add(rt);
            return g;
        }

        void Note(Panel rows, string text, string color)
        {
            TextBlock t = Small(10.5, color);
            t.Text = text;
            t.Margin = new Thickness(0, 6, 0, 6);
            t.TextWrapping = TextWrapping.Wrap;
            rows.Children.Add(t);
        }

        FrameworkElement BuildBottomRow()
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
            row.Margin = new Thickness(0, 0, 0, 8);

            Border ev = ListCard(Lang.S("События Windows", "Windows events"), out _evSub, out _evRows);
            Grid.SetColumn(ev, 0);
            row.Children.Add(ev);
            Border al = ListCard(Lang.S("Журнал тревог", "Alert log"), out _alSub, out _alRows);
            Grid.SetColumn(al, 2);
            row.Children.Add(al);
            Border au = ListCard(Lang.S("Автозагрузка", "Startup items"), out _auSub, out _auRows);
            Grid.SetColumn(au, 4);
            row.Children.Add(au);
            return row;
        }

        void UpdateEvents()
        {
            string key = WinEvents.Checked.Ticks + "|" + WinEvents.Items.Count + "|" + WinEvents.Busy + "|" + DateTime.Today.Ticks;
            if (key == _evKey) return;
            _evKey = key;
            _evRows.Children.Clear();
            if (WinEvents.Checked == DateTime.MinValue)
            {
                SetText(_evSub, "");
                Note(_evRows, Lang.S("Читаю журнал Windows…", "Reading the Windows log…"), cMid);
                return;
            }

            // одинаковые события сворачиваем в «×N», показываем самые свежие
            var order = new List<WinEvent>();
            var count = new Dictionary<string, int>();
            foreach (WinEvent e in WinEvents.Items)
            {
                if (!count.ContainsKey(e.Title)) { count[e.Title] = 0; order.Add(e); }
                count[e.Title]++;
            }
            SetText(_evSub, string.Format(Lang.S("за {0} суток · {1}", "last {0} days · {1}"), WinEvents.Days, WinEvents.Items.Count));
            if (order.Count == 0)
            {
                Note(_evRows, Lang.S("Сбоев и ошибок не было — всё спокойно", "No crashes or errors — all quiet"), cOk);
                return;
            }
            for (int i = 0; i < order.Count && i < 6; i++)
            {
                WinEvent e = order[i];
                int n = count[e.Title];
                string col = (e.Cat == WinEvents.CatReboot || e.Cat == WinEvents.CatDisk || e.Cat == WinEvents.CatHardware)
                    ? Theme.WarnColor : Theme.BattMidColor;
                _evRows.Children.Add(ListRow(Stamp(e.Time), col, e.Title, n > 1 ? "×" + n : "", cMid));
            }
        }

        void UpdateAlertLog()
        {
            string key = AlertLog.Version + "|" + _w.AlertsEnabled + "|" + DateTime.Today.Ticks;
            if (key == _alKey) return;
            _alKey = key;
            _alRows.Children.Clear();
            SetText(_alSub, _w.AlertsEnabled ? Lang.S("оповещения включены", "alerts on") : Lang.S("оповещения выключены", "alerts off"));
            List<AlertEntry> list = AlertLog.Recent(6);
            if (list.Count == 0)
            {
                Note(_alRows, Lang.S("Тревог не было", "No alerts yet"), cOk);
                return;
            }
            foreach (AlertEntry a in list)
                _alRows.Children.Add(ListRow(Stamp(a.Time), a.Level >= 2 ? Theme.WarnColor : Theme.BattMidColor, a.Text, "", cMid));
        }

        void UpdateAutostartList()
        {
            string key = Autostart.Checked.Ticks + "|" + Autostart.Items.Count + "|" + Autostart.Busy;
            if (key == _auKey) return;
            _auKey = key;
            _auRows.Children.Clear();
            if (Autostart.Checked == DateTime.MinValue)
            {
                SetText(_auSub, "");
                Note(_auRows, Lang.S("Проверяю автозагрузку…", "Checking startup items…"), cMid);
                return;
            }

            var list = new List<StartupItem>();
            foreach (StartupItem it in Autostart.Items) if (it.Enabled && !it.IsSelf) list.Add(it);
            list.Sort(delegate(StartupItem a, StartupItem b)
            {
                int c = b.Impact.CompareTo(a.Impact);
                return c != 0 ? c : b.MemMB.CompareTo(a.MemMB);
            });
            SetText(_auSub, string.Format(Lang.S("включено: {0}", "enabled: {0}"), list.Count));
            if (list.Count == 0)
            {
                Note(_auRows, Lang.S("Программ в автозагрузке нет", "No startup programs"), cOk);
                return;
            }
            for (int i = 0; i < list.Count && i < 6; i++)
            {
                StartupItem it = list[i];
                string label, col;
                if (it.Impact >= 3) { label = Lang.S("высокая", "high"); col = Theme.WarnColor; }
                else if (it.Impact == 2) { label = Lang.S("средняя", "medium"); col = Theme.BattMidColor; }
                else if (it.Impact == 1) { label = Lang.S("низкая", "low"); col = cOk; }
                else { label = Lang.S("не запущена", "idle"); col = cMid; }
                _auRows.Children.Add(ListRow("", col, it.Name, label, col));
            }
        }
    }
}
