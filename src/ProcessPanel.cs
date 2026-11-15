using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace SysWidget
{
    /// <summary>
    /// Плашка «топ процессов»: открывается кликом по строке ЦП или ОЗУ.
    /// Показывает самые тяжёлые процессы (одноимённые сложены в одну строку,
    /// как «chrome ×23») и даёт завершить процесс в два клика: первый по
    /// крестику спрашивает «Завершить?», второй — завершает. Системные
    /// процессы и сам виджет без крестика.
    /// </summary>
    public sealed class ProcessPanel
    {
        public enum Kind { Cpu, Ram, Disk, Gpu, Net, Periph }

        const int RowCount = 6;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Без крестика: завершить их нельзя без прав администратора, а explorer
        // и виджет — тем более не стоит.
        static readonly HashSet<string> Guarded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services",
            "lsass", "svchost", "dwm", "explorer", "fontdrvhost", "SysWidget",
            "MemCompression", "Memory Compression", "audiodg", "sihost", "taskhostw"
        };

        /// <summary>Можно ли предлагать завершить процесс с таким именем (служебные и сам виджет — нельзя).</summary>
        public static bool CanClose(string name)
        {
            return !Guarded.Contains(name);
        }

        public readonly Border Root;
        readonly TextBlock _title;
        readonly StackPanel _list;
        readonly StackPanel _head;
        /// <summary>Физический номер диска, которому принадлежит плашка (Kind.Disk); −1 — не задан.</summary>
        public int DiskIndex = -1;

        /// <summary>Шапка плашки: готовый блок «текст + график» метрики; null — убрать.</summary>
        public void SetHeader(FrameworkElement head)
        {
            _head.Children.Clear();
            if (head != null)
            {
                // блок мог остаться в другой плашке/родителе — отцепляем
                var parent = System.Windows.Media.VisualTreeHelper.GetParent(head) as Panel;
                if (parent != null) parent.Children.Remove(head);
                _head.Children.Add(head);
            }
            _head.Visibility = head != null ? Visibility.Visible : Visibility.Collapsed;
        }
        readonly Brush _hover = Theme.B(Theme.SurfaceHover);

        Kind _kind;
        /// <summary>Устройство для плашки периферии (null — отключено) и его ключ для журнала.</summary>
        public Peripheral Device;
        public string DeviceKey = "";
        public void Redraw() { Render(); }
        /// <summary>Сетевой снимок: график трафика по дням и лимит под списком.</summary>
        public NetSnapshot Net;
        bool _gpuShared;     // встроенный ГП: память процессов общая, в столбце её не показываем
        /// <summary>Последний снимок системы: итог по памяти под списком.</summary>
        public SysStats Stats;
        ProcessInfo[] _data;
        int _samples;
        string _confirm;
        DateTime _confirmAt;
        string _note;
        DateTime _noteAt;

        public ProcessPanel()
        {
            _title = new TextBlock();
            _title.FontFamily = new FontFamily(Theme.FontUi);
            _title.FontSize = Theme.SizeText;
            _title.FontWeight = FontWeights.SemiBold;
            _title.Margin = new Thickness(0, 0, 0, 4);

            _list = new StackPanel();
            Grid.SetIsSharedSizeScope(_list, true);     // столбцы чисел одной ширины у всех строк

            // шапка: сводка по метрике и график (бывшая подсказка при наведении)
            _head = new StackPanel();
            _head.Margin = new Thickness(0, 0, 0, 8);
            _head.Visibility = Visibility.Collapsed;

            var sp = new StackPanel();
            sp.Children.Add(_title);
            sp.Children.Add(_head);
            sp.Children.Add(_list);

            Root = new Border();
            Root.Background = Theme.B(Theme.Surface);
            Root.BorderBrush = Theme.B(Theme.Stroke);
            Root.BorderThickness = new Thickness(1);
            Root.CornerRadius = new CornerRadius(Theme.RadiusPill);
            Root.Padding = new Thickness(9, 6, 9, 6);
            Root.MinWidth = 250;
            Root.Child = sp;
        }

        public void SetKind(Kind kind)
        {
            _kind = kind;
            _data = null;
            _samples = 0;
            _confirm = null;
            _note = null;
            Render();
        }

        public void Update(ProcessInfo[] data, int samples)
        {
            _data = data;
            _samples = samples;
            DateTime now = DateTime.Now;
            if (_confirm != null && (now - _confirmAt).TotalSeconds > 4) _confirm = null;
            if (_note != null && (now - _noteAt).TotalSeconds > 3) _note = null;
            Render();
        }

        void Render()
        {
            bool cpu = _kind == Kind.Cpu;
            bool disk = _kind == Kind.Disk;
            bool gpu = _kind == Kind.Gpu;
            bool net = _kind == Kind.Net;
            _title.Text = cpu ? Lang.S("ЦП · топ процессов", "CPU · top processes")
                        : disk ? Lang.S("Диск · топ процессов по вводу-выводу", "Disk · top processes by I/O")
                        : gpu ? Lang.S("ГП · топ процессов", "GPU · top processes")
                        : net ? Lang.S("Сеть · с кем соединены процессы", "Network · who is connected")
                        : Lang.S("ОЗУ · топ процессов", "RAM · top processes");
            _title.Foreground = Theme.B(cpu ? Theme.CpuColor : disk ? Theme.SsdColor : gpu ? Theme.GpuColor
                                        : net ? Theme.NetColor : Theme.RamColor);

            _list.Children.Clear();

            // периферия: плашка устройства не зависит от замера процессов
            if (_kind == Kind.Periph)
            {
                string devName = Device != null
                    ? (string.Equals(Device.Name, Device.KindName, StringComparison.OrdinalIgnoreCase)
                        ? Device.KindName : Device.KindName + " · " + Device.Name)
                    : Lang.S("Устройство отключено", "Device disconnected");
                _title.Text = devName;
                _title.Foreground = Theme.B(Theme.TextHi);
                _list.Children.Add(PeriphContent());
                return;
            }

            if (gpu && Stats != null && !Stats.HasGpu)
            {
                _list.Children.Add(Text(Lang.S("Счётчики ГП Windows недоступны", "Windows GPU counters unavailable"),
                                        Theme.TextMid, false));
                return;
            }

            // загрузка ЦП и скорость ввода-вывода — прирост между двумя замерами; с первого их нет
            if (_data == null || ((cpu || disk) && _samples < 2) || (gpu && Stats == null))
            {
                _list.Children.Add(Text(Lang.S("Измеряю…", "Measuring…"), Theme.TextMid, false));
                return;
            }

            var rows = new List<ProcessInfo>(_data);
            if (cpu) rows.Sort(delegate(ProcessInfo a, ProcessInfo b) { return b.Cpu.CompareTo(a.Cpu); });
            else if (net)
            {
                // соединения приходят по номерам pid — складываем по группам одноимённых процессов
                var byPid = new Dictionary<int, List<string>>();
                foreach (NetConnections.Conn c in NetConnections.Read())
                {
                    List<string> l;
                    if (!byPid.TryGetValue(c.Pid, out l)) { l = new List<string>(); byPid[c.Pid] = l; }
                    l.Add(c.Remote);
                }
                foreach (ProcessInfo p in rows)
                {
                    p.Conns = 0; p.Remotes = new List<string>();
                    foreach (int pid in p.Pids)
                    {
                        List<string> l;
                        if (!byPid.TryGetValue(pid, out l)) continue;
                        p.Conns += l.Count;
                        foreach (string r in l) if (!p.Remotes.Contains(r)) p.Remotes.Add(r);
                    }
                }
                rows.RemoveAll(delegate(ProcessInfo p) { return p.Conns == 0; });
                rows.Sort(delegate(ProcessInfo a, ProcessInfo b) { return b.Conns.CompareTo(a.Conns); });
            }
            else if (gpu)
            {
                // ГП по процессам приходит в SysStats по номеру pid — привязываем к группам по имени
                var byPid = new Dictionary<int, GpuProc>();
                foreach (GpuProc gp in Stats.GpuProcs) byPid[gp.Pid] = gp;
                bool shared = Stats.VramUsedGB < 0.05;      // у встроенной графики вся память — общая
                _gpuShared = shared;
                foreach (ProcessInfo p in rows)
                {
                    p.GpuPct = 0; p.GpuMb = 0;
                    foreach (int pid in p.Pids)
                    {
                        GpuProc gp;
                        if (!byPid.TryGetValue(pid, out gp)) continue;
                        p.GpuPct += gp.Util;
                        p.GpuMb += shared ? gp.SharedMb : gp.DedMb;
                    }
                    if (p.GpuPct > 100) p.GpuPct = 100;
                }
                rows.RemoveAll(delegate(ProcessInfo p) { return p.GpuPct < 0.5 && (shared || p.GpuMb < 50); });
                rows.Sort(delegate(ProcessInfo a, ProcessInfo b)
                {
                    int c = b.GpuPct.CompareTo(a.GpuPct);
                    return c != 0 ? c : b.GpuMb.CompareTo(a.GpuMb);
                });
            }
            else if (disk)
            {
                rows.RemoveAll(delegate(ProcessInfo p) { return p.ReadMBs + p.WriteMBs < 0.005; });
                rows.Sort(delegate(ProcessInfo a, ProcessInfo b)
                    { return (b.ReadMBs + b.WriteMBs).CompareTo(a.ReadMBs + a.WriteMBs); });
            }
            else rows.Sort(delegate(ProcessInfo a, ProcessInfo b) { return b.RamMb.CompareTo(a.RamMb); });

            int n = Math.Min(RowCount, rows.Count);
            for (int i = 0; i < n; i++) _list.Children.Add(MakeRow(rows[i], cpu));
            if (disk && n == 0)
                _list.Children.Add(Text(Lang.S("Сейчас процессы почти не обращаются к диску", "No process is using the disk right now"),
                                        Theme.TextMid, false));
            if (gpu && n == 0)
                _list.Children.Add(Text(Lang.S("Сейчас ГП почти не используется процессами", "No process is using the GPU right now"),
                                        Theme.TextMid, false));
            if (gpu) _list.Children.Add(GpuBlock());
            if (net && n == 0)
                _list.Children.Add(Text(Lang.S("Сейчас нет установленных внешних соединений", "No external connections right now"),
                                        Theme.TextMid, false));
            if (net) _list.Children.Add(NetBlock());

            if (disk && Stats != null) _list.Children.Add(DiskBlock());

            // итог по ядрам под списком ЦП; сводка по памяти ОЗУ — в шапке плашки
            if (cpu && Stats != null) _list.Children.Add(CoreBlock());

            if (_note != null)
            {
                TextBlock t = Text(_note, Theme.WarnColor, false);
                t.Margin = new Thickness(0, 4, 0, 0);
                _list.Children.Add(t);
            }
        }

        Border MakeRow(ProcessInfo g, bool cpu)
        {
            // Столбцы числа и единицы — общие для всех строк (SharedSizeGroup в _list):
            // иначе у каждой строки своя ширина и цифры «плывут». Число прижато вправо,
            // единица — влево, поэтому разряды и знак % стоят друг под другом.
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "trend" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "num" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "unit" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });

            bool asking = !Guarded.Contains(g.Name)
                && string.Equals(_confirm, g.Name, StringComparison.OrdinalIgnoreCase);

            string name = g.Name.Length > 24 ? g.Name.Substring(0, 23) + "…" : g.Name;
            if (g.Count > 1) name += " ×" + g.Count.ToString(Inv);
            // вопрос «Завершить?» стоит на месте имени, чтобы ширина столбцов не менялась
            TextBlock nameT = Text(asking ? Lang.S("Завершить? ", "End it? ") + name : name,
                                   asking ? Theme.WarnColor : Theme.TextHi, false);
            nameT.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(nameT, 0);
            grid.Children.Add(nameT);

            // ↗ у процесса, чья память растёт без остановки (MemoryWatch) — только в списке ОЗУ
            MemoryGrowth gr = _kind != Kind.Ram ? null : MemoryWatch.Find(g.Name);
            if (gr != null)
            {
                TextBlock trend = Text(string.Format(Inv, "↗ +{0:0} {1}", gr.GrowthMb, Lang.S("МБ", "MB")),
                                       Theme.BattMidColor, true);
                trend.Margin = new Thickness(0, 0, 10, 0);
                trend.ToolTip = string.Format(Inv,
                    Lang.S("Память растёт без остановки уже ~{0:0} мин. Возможна утечка.",
                           "Memory has been growing for ~{0:0} min. Possible leak."), gr.Minutes);
                Grid.SetColumn(trend, 1);
                grid.Children.Add(trend);
            }

            string num, unit;
            if (_kind == Kind.Net)
            {
                // соединений — в столбце «num», единица общая; с кем — в подсказке строки
                num = g.Conns.ToString(Inv);
                unit = Lang.S("подкл.", "conn.");
                var sb = new System.Text.StringBuilder();
                sb.Append(Lang.S("Соединены с:", "Connected to:"));
                for (int i = 0; i < g.Remotes.Count && i < 8; i++) sb.Append('\n').Append(g.Remotes[i]);
                if (g.Remotes.Count > 8) sb.Append('\n').Append("…");
                grid.ToolTip = sb.ToString();
            }
            else if (_kind == Kind.Gpu)
            {
                // загрузка — в столбце «trend», видеопамять — в «num», единица общая
                TextBlock pct = Text(g.GpuPct.ToString("0", Inv) + "%", Theme.TextHi, true);
                pct.TextAlignment = TextAlignment.Right;
                pct.HorizontalAlignment = HorizontalAlignment.Right;
                pct.Margin = new Thickness(0, 0, 12, 0);
                Grid.SetColumn(pct, 1);
                grid.Children.Add(pct);
                if (_gpuShared)
                {
                    // у встроенной графики счётчик общей памяти по процессам завышен (dwm может
                    // показать гигабайты при занятых в целом 1–2 ГБ), поэтому столбец памяти
                    // пустой, а число — в подсказке строки
                    num = "";
                    unit = "";
                    grid.ToolTip = string.Format(Inv, Lang.S("Общая видеопамять процесса по счётчику Windows: {0:0} МБ (может быть завышена)",
                                                             "Shared GPU memory per Windows counter: {0:0} MB (may be overstated)"), g.GpuMb);
                }
                else
                {
                    num = FormatMb(g.GpuMb);
                    unit = Lang.S("МБ", "MB");
                }
            }
            else if (_kind == Kind.Disk)
            {
                // чтение — в столбце «trend», запись — в «num», единица общая
                TextBlock rd = Text("↓ " + FormatMBs(g.ReadMBs), Theme.TextHi, true);
                rd.TextAlignment = TextAlignment.Right;
                rd.HorizontalAlignment = HorizontalAlignment.Right;
                rd.Margin = new Thickness(0, 0, 10, 0);
                Grid.SetColumn(rd, 1);
                grid.Children.Add(rd);
                num = "↑ " + FormatMBs(g.WriteMBs);
                unit = Lang.S("МБ/с", "MB/s");
            }
            else if (cpu) { num = g.Cpu.ToString("0.0", Inv); unit = "%"; }
            else { num = FormatMb(g.RamMb); unit = Lang.S("МБ", "MB"); }

            TextBlock numT = Text(num, Theme.TextHi, true);
            numT.TextAlignment = TextAlignment.Right;
            numT.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(numT, 2);
            grid.Children.Add(numT);

            TextBlock unitT = Text(unit, Theme.TextMid, false);
            unitT.Margin = new Thickness(4, 0, 0, 0);
            unitT.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(unitT, 3);
            grid.Children.Add(unitT);

            if (!Guarded.Contains(g.Name))
            {
                TextBlock kill = Text("✕", asking ? Theme.WarnColor : Theme.TextMid, false);
                kill.Margin = new Thickness(8, 0, 0, 0);
                kill.Cursor = Cursors.Hand;
                kill.ToolTip = null;
                ProcessInfo target = g;
                kill.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    OnKill(target);
                };
                Grid.SetColumn(kill, 4);
                grid.Children.Add(kill);
            }

            var row = new Border();
            row.Background = Brushes.Transparent;
            row.CornerRadius = new CornerRadius(4);
            row.Padding = new Thickness(3, 2, 3, 2);
            row.Child = grid;
            row.MouseEnter += delegate { row.Background = _hover; };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            return row;
        }

        /// <summary>
        /// Под списком ЦП: столбики загрузки каждого логического ядра, текущая частота
        /// и предупреждение, если при высокой нагрузке частота заметно ниже номинала
        /// (перегрев, предел мощности или режим энергосбережения).
        /// </summary>
        FrameworkElement CoreBlock()
        {
            var box = new StackPanel();
            box.Margin = new Thickness(3, 8, 3, 0);

            double[] cores = Stats.CpuCores ?? new double[0];
            if (cores.Length > 0)
            {
                const double H = 26, W = 9;
                var bars = new WrapPanel();
                bars.MaxWidth = 260;
                // с MaxWidth и растяжением WPF центрирует элемент в ширине плашки — столбики
                // уезжали от левого края; прижимаем к левому краю, как у текста строк
                bars.HorizontalAlignment = HorizontalAlignment.Left;
                for (int i = 0; i < cores.Length; i++)
                {
                    double c = cores[i];
                    var fill = new Border();
                    fill.VerticalAlignment = VerticalAlignment.Bottom;
                    fill.Height = c < 0.5 ? 1 : Math.Max(1, Math.Round(c / 100.0 * H));
                    fill.CornerRadius = new CornerRadius(Theme.BarRadius);
                    fill.Background = Theme.B(c >= Prefs.CritAt ? Theme.WarnColor : Theme.CpuColor);

                    var track = new Border();
                    track.Width = W; track.Height = H;
                    track.Margin = new Thickness(0, 0, 3, 3);
                    track.CornerRadius = new CornerRadius(Theme.BarRadius);
                    track.Background = Theme.B(Theme.Fade(Theme.CpuColor, 0x40));
                    track.Child = fill;
                    track.ToolTip = string.Format(Inv, Lang.S("Ядро {0}: {1:0}%", "Core {0}: {1:0}%"), i, c)
                        + (Stats.CoreMHz != null && i < Stats.CoreMHz.Length && Stats.CoreMHz[i] > 0
                            ? string.Format(Inv, " · {0:0.00} {1}", Stats.CoreMHz[i] / 1000.0, Lang.S("ГГц", "GHz")) : "");
                    bars.Children.Add(track);
                }
                box.Children.Add(bars);
            }

            // частота и предупреждение о троттлинге — в шапке плашки (текст подсказки ЦП)
            TextBlock t = Text(string.Format(Inv, Lang.S("Логических ядер: {0}", "Logical cores: {0}"), cores.Length),
                               Theme.TextMid, false);
            t.FontSize = Theme.SizeText - 1;
            t.Margin = new Thickness(0, 2, 0, 0);
            box.Children.Add(t);
            return box;
        }

        void OnKill(ProcessInfo g)
        {
            if (!string.Equals(_confirm, g.Name, StringComparison.OrdinalIgnoreCase))
            {
                _confirm = g.Name;
                _confirmAt = DateTime.Now;
                Render();
                return;
            }

            _confirm = null;
            int failed = Kill(g);
            if (failed > 0)
            {
                _note = string.Format(Inv,
                    Lang.S("{0}: не удалось завершить {1} из {2}", "{0}: could not end {1} of {2}"),
                    g.Name, failed, g.Pids.Count);
                _noteAt = DateTime.Now;
            }
            else
            {
                // убираем строку сразу, не ожидая следующего замера
                if (_data != null)
                {
                    var rest = new List<ProcessInfo>(_data);
                    rest.Remove(g);
                    _data = rest.ToArray();
                }
            }
            Render();
        }

        /// <summary>Завершить все процессы группы; вернуть, сколько не удалось.</summary>
        public static int Kill(ProcessInfo g)
        {
            int failed = 0;
            for (int i = 0; i < g.Pids.Count; i++)
            {
                try
                {
                    using (Process p = Process.GetProcessById(g.Pids[i]))
                    {
                        // номер мог достаться другому процессу — сверяем имя
                        if (!string.Equals(p.ProcessName, g.Name, StringComparison.OrdinalIgnoreCase))
                            continue;
                        p.Kill();
                    }
                }
                catch (ArgumentException) { }          // уже завершился
                catch (Exception ex)
                {
                    failed++;
                    ErrorLog.Write("kill " + g.Name, ex);
                }
            }
            return failed;
        }

        /// <summary>
        /// Плашка устройства: способ подключения, заряд, прогноз «хватит ещё на…», график заряда
        /// за 14 дней и журнал подключений. Данные копятся с момента первого запуска этой версии.
        /// </summary>
        FrameworkElement PeriphContent()
        {
            var box = new StackPanel();
            box.Margin = new Thickness(3, 2, 3, 0);
            Peripheral p = Device;

            if (p != null)
            {
                if (p.Manufacturer.Length > 0 && !string.Equals(p.Manufacturer, p.Name, StringComparison.OrdinalIgnoreCase))
                    box.Children.Add(SmallLine(p.Manufacturer, Theme.TextMid));
                box.Children.Add(SmallLine(p.LinkFull, Theme.TextMid));

                if (p.Battery >= 0)
                {
                    string col = p.Battery >= 50 ? Theme.RamColor : p.Battery >= 20 ? Theme.BattMidColor : Theme.WarnColor;
                    TextBlock b = SmallLine(Lang.S("Заряд ", "Battery ") + p.Battery.ToString(Inv) + "%", col);
                    b.FontSize = Theme.SizeText;
                    b.FontWeight = FontWeights.SemiBold;
                    b.Margin = new Thickness(0, 4, 0, 0);
                    box.Children.Add(b);

                    double basis;
                    double left = PeriphLog.HoursLeft(DeviceKey, out basis);
                    string fc;
                    if (left < 0)
                        fc = Lang.S("Прогноз появится, когда накопится история разряда (нужно несколько часов).",
                                    "Forecast appears once enough discharge history is collected (a few hours).");
                    else if (left >= 48)
                        fc = string.Format(Inv, Lang.S("Хватит ещё примерно на {0:0} дн.", "About {0:0} days left"), left / 24.0);
                    else
                        fc = string.Format(Inv, Lang.S("Хватит ещё примерно на {0:0} ч", "About {0:0} h left"), left);
                    if (left >= 0)
                        fc += string.Format(Inv, Lang.S(" (по расходу за {0:0} ч)", " (based on the last {0:0} h)"), basis);
                    box.Children.Add(SmallLine(fc, Theme.TextMid));
                }
                else
                {
                    box.Children.Add(SmallLine(p.Link == PeriphLink.Usb
                        ? Lang.S("Заряд недоступен: проводное устройство или приёмник 2,4 ГГц.", "Battery unavailable: wired device or 2.4 GHz receiver.")
                        : Lang.S("Это устройство не сообщает заряд.", "This device does not report battery."), Theme.TextMid));
                }
            }
            else
            {
                box.Children.Add(SmallLine(Lang.S("Сейчас не подключено. Журнал ниже.", "Not connected now. See the log below."), Theme.TextMid));
            }

            // график заряда за 14 дней
            List<int[]> samples = PeriphLog.SamplesFor(DeviceKey);
            if (samples.Count > 0)
            {
                int now = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMinutes;
                var chart = new BatteryChart(Theme.RamColor);
                chart.Set(samples, now);
                chart.Margin = new Thickness(0, 8, 0, 0);
                box.Children.Add(chart);

                var axis = new DockPanel();
                axis.Width = 240;
                axis.HorizontalAlignment = HorizontalAlignment.Left;
                TextBlock right = SmallLine(Lang.S("сейчас", "now"), Theme.TextMid);
                DockPanel.SetDock(right, Dock.Right);
                axis.Children.Add(right);
                axis.Children.Add(SmallLine(Lang.S("14 дней назад", "14 days ago"), Theme.TextMid));
                box.Children.Add(axis);
            }

            // журнал подключений
            List<PeriphEvent> events = PeriphLog.EventsFor(DeviceKey, 6);
            if (events.Count > 0)
            {
                TextBlock h = SmallLine(Lang.S("ЖУРНАЛ", "LOG"), Theme.TextMid);
                h.Margin = new Thickness(0, 8, 0, 0);
                box.Children.Add(h);
                DateTime today = DateTime.Now.Date;
                foreach (PeriphEvent e in events)
                {
                    string when = e.Time.Date == today
                        ? Lang.S("сегодня ", "today ") + e.Time.ToString("HH:mm", Inv)
                        : e.Time.ToString("dd.MM HH:mm", Inv);
                    box.Children.Add(SmallLine(when + " · " + (e.Connected ? Lang.S("подключено", "connected") : Lang.S("отключено", "disconnected")),
                                               e.Connected ? Theme.TextHi : Theme.TextMid));
                }
            }
            else
            {
                TextBlock n = SmallLine(Lang.S("Журнал подключений пуст: пишется с первого запуска этой версии.",
                                               "Connection log is empty: recorded from the first run of this version."), Theme.TextMid);
                n.Margin = new Thickness(0, 8, 0, 0);
                box.Children.Add(n);
            }
            return box;
        }

        static TextBlock SmallLine(string text, string color)
        {
            TextBlock t = Text(text, color, false);
            t.FontSize = Theme.SizeText - 1;
            t.TextWrapping = TextWrapping.Wrap;
            t.MaxWidth = 280;
            return t;
        }

        /// <summary>
        /// Под списком сети: график трафика за 30 дней (столбик — день, высота — приём + передача),
        /// итог за 30 дней и пик, а при заданном лимите — полоса расхода за месяц.
        /// История начинается с первого запуска этой версии: раньше дни не записывались.
        /// </summary>
        FrameworkElement NetBlock()
        {
            var box = new StackPanel();
            box.Margin = new Thickness(3, 8, 3, 0);
            if (Net == null) return box;

            // 30 ячеек: от (сегодня − 29) до сегодня; дни без записи — пустые столбики
            var byDay = new Dictionary<string, DayTraffic>();
            foreach (DayTraffic d in Net.Days) byDay[d.Day] = d;
            DateTime today = DateTime.Now.Date;

            var days = new DayTraffic[30];
            double max = 0, sumRx = 0, sumTx = 0;
            DayTraffic peak = null;
            for (int i = 0; i < 30; i++)
            {
                DateTime dt = today.AddDays(i - 29);
                DayTraffic d;
                if (!byDay.TryGetValue(dt.ToString("yyyy-MM-dd", Inv), out d)) d = new DayTraffic { Day = dt.ToString("yyyy-MM-dd", Inv) };
                days[i] = d;
                double tot = d.Rx + d.Tx;
                sumRx += d.Rx; sumTx += d.Tx;
                if (tot > max) { max = tot; peak = d; }
            }

            const double H = 34, W = 6;
            var bars = new StackPanel();
            bars.Orientation = Orientation.Horizontal;
            for (int i = 0; i < days.Length; i++)
            {
                DayTraffic d = days[i];
                double tot = d.Rx + d.Tx;
                bool isToday = i == days.Length - 1;

                var fill = new Border();
                fill.VerticalAlignment = VerticalAlignment.Bottom;
                fill.Height = tot <= 0 || max <= 0 ? 0 : Math.Max(1, Math.Round(tot / max * H));
                fill.CornerRadius = new CornerRadius(Theme.BarRadius);
                fill.Background = Theme.B(isToday ? Theme.NetColor : Theme.Fade(Theme.NetColor, 0xA0));

                var track = new Border();
                track.Width = W; track.Height = H;
                track.Margin = new Thickness(0, 0, 2, 0);
                track.CornerRadius = new CornerRadius(Theme.BarRadius);
                track.Background = Theme.B(Theme.Fade(Theme.NetColor, 0x26));
                track.Child = fill;
                DateTime dt = DateTime.ParseExact(d.Day, "yyyy-MM-dd", Inv);
                track.ToolTip = tot > 0
                    ? string.Format(Inv, "{0:dd.MM}: ↓ {1} ↑ {2}", dt, FormatBytesShort(d.Rx), FormatBytesShort(d.Tx))
                    : string.Format(Inv, Lang.S("{0:dd.MM}: нет данных", "{0:dd.MM}: no data"), dt);
                bars.Children.Add(track);
            }
            box.Children.Add(bars);

            TextBlock sum = Text(string.Format(Inv,
                Lang.S("30 дней: ↓ {0} ↑ {1}", "30 days: ↓ {0} ↑ {1}"), FormatBytesShort(sumRx), FormatBytesShort(sumTx)),
                Theme.TextMid, false);
            sum.FontSize = Theme.SizeText - 1;
            sum.Margin = new Thickness(0, 4, 0, 0);
            box.Children.Add(sum);

            if (peak != null && max > 0)
            {
                TextBlock pk = Text(string.Format(Inv, Lang.S("Больше всего: {0:dd.MM} — {1}", "Busiest: {0:dd.MM} — {1}"),
                    DateTime.ParseExact(peak.Day, "yyyy-MM-dd", Inv), FormatBytesShort(max)), Theme.TextMid, false);
                pk.FontSize = Theme.SizeText - 1;
                box.Children.Add(pk);
            }

            // лимит за месяц: полоса и проценты; цвет — по близости к пределу
            if (Prefs.TrafficLimitGB > 0)
            {
                double used = Net.MonRx + Net.MonTx;
                double limit = Prefs.TrafficLimitGB * 1073741824.0;
                double frac = Math.Min(1.0, used / limit);
                string col = frac >= 1.0 ? Theme.WarnColor : frac >= 0.8 ? Theme.BattMidColor : Theme.NetColor;

                var barFill = new Border();
                barFill.HorizontalAlignment = HorizontalAlignment.Left;
                barFill.Width = Math.Max(1, Math.Round(frac * 200));
                barFill.Height = 5;
                barFill.CornerRadius = new CornerRadius(Theme.BarRadius);
                barFill.Background = Theme.B(col);

                var barTrack = new Border();
                barTrack.Width = 200; barTrack.Height = 5;
                barTrack.HorizontalAlignment = HorizontalAlignment.Left;
                barTrack.Margin = new Thickness(0, 8, 0, 0);
                barTrack.CornerRadius = new CornerRadius(Theme.BarRadius);
                barTrack.Background = Theme.B(Theme.Fade(Theme.NetColor, 0x26));
                barTrack.Child = barFill;
                box.Children.Add(barTrack);

                TextBlock lt = Text(string.Format(Inv, Lang.S("Лимит за месяц: {0} из {1} ГБ ({2:0}%)", "Monthly limit: {0} of {1} GB ({2:0}%)"),
                    FormatBytesShort(used), Prefs.TrafficLimitGB, used / limit * 100.0), col, false);
                lt.FontSize = Theme.SizeText - 1;
                lt.Margin = new Thickness(0, 3, 0, 0);
                box.Children.Add(lt);
            }

            TextBlock note = Text(Lang.S("Трафик считается с момента первого запуска этой версии; по процессам байты без прав администратора недоступны — показаны соединения.",
                                         "Traffic is counted from the first run of this version; per-process bytes need administrator rights — connections are shown instead."),
                                  Theme.TextMid, false);
            note.FontSize = Theme.SizeText - 1;
            note.TextWrapping = TextWrapping.Wrap;
            note.MaxWidth = 280;
            note.Margin = new Thickness(0, 6, 0, 0);
            box.Children.Add(note);
            return box;
        }

        static string FormatBytesShort(double b)
        {
            double gb = b / 1073741824.0;
            if (gb >= 100) return gb.ToString("0", Inv) + Lang.S(" ГБ", " GB");
            if (gb >= 1) return gb.ToString("0.0", Inv) + Lang.S(" ГБ", " GB");
            return (b / 1048576.0).ToString("0", Inv) + Lang.S(" МБ", " MB");
        }

        /// <summary>
        /// Под списком ГП: адаптер, общая загрузка, видеопамять и нагрузка по типам движков.
        /// Загрузка процесса — его самый занятый движок (3D, видео, копирование…).
        /// </summary>
        FrameworkElement GpuBlock()
        {
            var box = new StackPanel();
            box.Margin = new Thickness(3, 8, 3, 0);

            Hardware h = Hardware.Current;
            // название адаптера, загрузка и видеопамять — в шапке плашки; здесь — движки
            // движки: показываем только заметно занятые, самые нагруженные первыми
            var eng = new List<KeyValuePair<string, double>>();
            foreach (KeyValuePair<string, double> kv in Stats.GpuEngines)
                if (kv.Value >= 1) eng.Add(kv);
            eng.Sort(delegate(KeyValuePair<string, double> a, KeyValuePair<string, double> b) { return b.Value.CompareTo(a.Value); });
            if (eng.Count > 0)
            {
                var parts = new List<string>();
                for (int i = 0; i < eng.Count && i < 4; i++)
                    parts.Add(EngineName(eng[i].Key) + " " + eng[i].Value.ToString("0", Inv) + "%");
                TextBlock e = Text(Lang.S("Движки: ", "Engines: ") + string.Join(" · ", parts.ToArray()), Theme.TextMid, false);
                e.FontSize = Theme.SizeText - 1;
                e.TextWrapping = TextWrapping.Wrap;
                e.MaxWidth = 280;
                box.Children.Add(e);
            }

            TextBlock note = Text(Lang.S("Загрузка процесса — его самый занятый движок ГП.",
                                         "Process load is its busiest GPU engine."), Theme.TextMid, false);
            note.FontSize = Theme.SizeText - 1;
            note.TextWrapping = TextWrapping.Wrap;
            note.MaxWidth = 280;
            note.Margin = new Thickness(0, 6, 0, 0);
            box.Children.Add(note);
            return box;
        }

        static string EngineName(string type)
        {
            string u = type.ToUpperInvariant();
            if (u == "3D") return "3D";
            if (u.IndexOf("VIDEODECODE") >= 0) return Lang.S("Декод видео", "Video decode");
            if (u.IndexOf("VIDEOENCODE") >= 0) return Lang.S("Кодек видео", "Video encode");
            if (u.IndexOf("VIDEOPROCESSING") >= 0) return Lang.S("Обработка видео", "Video proc.");
            if (u.IndexOf("COPY") >= 0) return Lang.S("Копирование", "Copy");
            if (u.IndexOf("COMPUTE") >= 0) return Lang.S("Вычисления", "Compute");
            return type;
        }

        static string FormatMBs(double v)
        {
            return v.ToString("0.0", Inv);
        }

        /// <summary>
        /// Под списком диска: состояние выбранного диска по данным хранилища Windows. Модель,
        /// тома, свободное место и загрузка — в шапке плашки. Температура и износ без прав
        /// администратора не читаются — их здесь нет.
        /// </summary>
        FrameworkElement DiskBlock()
        {
            var box = new StackPanel();
            box.Margin = new Thickness(3, 8, 3, 0);

            foreach (DiskStat d in Stats.Disks)
            {
                if (DiskIndex >= 0 && d.Index != DiskIndex) continue;
                string health = DiskHealth.Get(d.Index);
                if (health.Length > 0)
                {
                    bool bad = DiskHealth.NeedsAttention(d.Index);
                    TextBlock head = Text(Lang.S("Состояние диска: ", "Drive health: ") + health,
                                          bad ? Theme.WarnColor : Theme.TextHi, false);
                    head.FontSize = Theme.SizeText - 1;
                    box.Children.Add(head);
                }

                // температура и износ — только в режиме «расширенные данные» (права администратора)
                DiskReliability r = AdminHelper.GetDisk(d.Index);
                if (r != null)
                {
                    var parts = new List<string>();
                    if (r.TempC >= 0) parts.Add(r.TempC.ToString(Inv) + " °C");
                    if (r.WearPct >= 0) parts.Add(Lang.S("износ ", "wear ") + r.WearPct.ToString(Inv) + "%");
                    if (r.Hours >= 0) parts.Add(Lang.S("наработка ", "power-on ") + r.Hours.ToString("#,##0", Inv).Replace(',', ' ') + Lang.S(" ч", " h"));
                    if (r.ReadErrors >= 0 || r.WriteErrors >= 0)
                        parts.Add(Lang.S("ошибки чт/зп ", "errors r/w ") + Math.Max(0, r.ReadErrors).ToString(Inv) + "/" + Math.Max(0, r.WriteErrors).ToString(Inv));
                    bool hot = r.TempC >= 70 || (r.WearPct >= 90);
                    TextBlock sm = Text(parts.Count > 0 ? string.Join(" · ", parts.ToArray())
                                                       : Lang.S("диск не отдаёт данные SMART", "the drive reports no SMART data"),
                                        hot ? Theme.WarnColor : Theme.TextHi, false);
                    sm.FontSize = Theme.SizeText - 1;
                    sm.TextWrapping = TextWrapping.Wrap;
                    sm.MaxWidth = 280;
                    box.Children.Add(sm);
                }
                else if (!AdminHelper.Connected)
                {
                    TextBlock hint = Text(Lang.S("Температура и износ: Настройки → Система → «Расширенные данные»",
                                                 "Temperature and wear: Settings → System → “Extended data”"),
                                          Theme.TextMid, false);
                    hint.FontSize = Theme.SizeText - 1;
                    hint.TextWrapping = TextWrapping.Wrap;
                    hint.MaxWidth = 280;
                    box.Children.Add(hint);
                }
            }

            TextBlock note = Text(Lang.S("Учитывается весь ввод-вывод процесса: файлы, сеть и каналы.",
                                         "Counts all process I/O: files, network and pipes."),
                                  Theme.TextMid, false);
            note.FontSize = Theme.SizeText - 1;
            note.TextWrapping = TextWrapping.Wrap;
            note.MaxWidth = 280;
            note.Margin = new Thickness(0, 4, 0, 0);
            box.Children.Add(note);
            return box;
        }

        /// <summary>Память целым числом МБ с пробелом между тысячами («1 234»): одна единица
        /// у всех строк, поэтому разряды выстраиваются в столбик.</summary>
        static string FormatMb(double mb)
        {
            string s = Math.Round(mb).ToString("#,##0", Inv);
            return s.Replace(',', '\u00A0');      // неразрывный пробел: число не переносится
        }

        static TextBlock Text(string s, string color, bool numeric)
        {
            var t = new TextBlock();
            t.Text = s;
            t.Foreground = Theme.B(color);
            t.FontFamily = new FontFamily(numeric ? Theme.FontNum : Theme.FontUi);
            t.FontSize = Theme.SizeText;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.HorizontalAlignment = HorizontalAlignment.Left;   // с MaxWidth иначе центрируется в плашке
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            if (numeric) Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            return t;
        }
    }
}
