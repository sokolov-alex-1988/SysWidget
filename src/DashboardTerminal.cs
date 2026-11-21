using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SysWidget
{
    /// <summary>
    /// Раскладка окна мониторинга для темы «Терминал» в духе консольных мониторов: вверху блок ЦП (график точками и сетка
    /// ядер), под ним слева память, диски и сеть, справа таблица процессов, внизу полоса «Состояние ПК» с кнопками. Данные
    /// те же, что у остальных тем (SysStats, History, замер процессов окна); меняется только подача.
    /// </summary>
    public sealed partial class Dashboard
    {
        Grid _tHost;
        DotGraph _tCpuGraph, _tNetGraph;
        MeterRows _tCpuTotal, _tCores, _tMem, _tDisks;
        ProcTable _tProc;
        TextBlock _tCpuTitle, _tCpuRight, _tMemRight, _tDiskRight, _tNetRight, _tProcRight, _tUptime, _tNetText;

        // градиенты цвета шкал (зелёный → жёлтый → красный у загрузки, свои у памяти)
        static readonly string[] GLoad = { "#FF98BB6C", "#FFE6C384", "#FFFF5D62" };
        static readonly string[] GUsed = { "#FF43242B", "#FFC34043", "#FFFF5D62" };
        static readonly string[] GAvail = { "#FF49443C", "#FFDCA561", "#FFE6C384" };
        static readonly string[] GCache = { "#FF223249", "#FF7E9CD8", "#FF7FB4CA" };
        static readonly string[] GFree = { "#FF2B3328", "#FF76946A", "#FF98BB6C" };

        /// <summary>Блок-рамка с вшитым заголовком; right — TextBlock правой части заголовка (его текст обновляют по ходу дела).</summary>
        Border TBox(string color, string title, out TextBlock right, Thickness pad)
        {
            Border b = CardBox(pad);
            TextBlock t = Small(10.5, cHi);
            t.Text = title;
            TextBlock r = Small(10, cMid);
            Frame(b, color, t, r);
            right = r;
            return b;
        }

        static MeterRow MRow(string label, string value, double frac, string[] g)
        {
            var m = new MeterRow();
            m.Label = label; m.Value = value; m.Frac = frac;
            m.C0 = g[0]; m.C1 = g[1]; m.C2 = g[2];
            return m;
        }

        void BuildBodyTerminal()
        {
            _kpis.Clear();
            _cards.Clear();
            _boxes.Clear();
            _hrows.Clear();
            if (_win != null) _win.MinWidth = Math.Min(900, SystemParameters.WorkArea.Width - 20);

            var host = new Grid();
            host.Margin = new Thickness(14, 10, 14, 10);
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 200 });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5, GridUnitType.Star), MinHeight = 300 });
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _tHost = host;
            _host = host;
            _scroll.Content = host;

            // ---------------- ЦП: график точками слева, сетка ядер справа
            Border cpu = TBox(BoxCpu, Lang.S("ЦП", "cpu"), out _tCpuRight, new Thickness(8, 4, 8, 6));
            _tCpuTitle = (cpu as TuiBox).TitleSource;
            var cg = new Grid();
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14, GridUnitType.Star) });
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12, GridUnitType.Star), MinWidth = 280 });
            cpu.Child = cg;

            var gl = new Grid();
            _tCpuGraph = new DotGraph();
            _tCpuGraph.Gradient = true;
            _tCpuGraph.TopLabel = "100%";
            _tCpuGraph.Margin = new Thickness(0, 0, 10, 0);
            gl.Children.Add(_tCpuGraph);
            _tUptime = Small(10, cMid);
            _tUptime.VerticalAlignment = VerticalAlignment.Bottom;
            _tUptime.HorizontalAlignment = HorizontalAlignment.Left;
            _tUptime.Margin = new Thickness(2, 0, 0, 0);
            gl.Children.Add(_tUptime);
            Grid.SetColumn(gl, 0);
            cg.Children.Add(gl);

            var gr = new StackPanel();
            _tCpuTotal = new MeterRows();
            _tCpuTotal.LabelChars = 4;
            _tCpuTotal.ValueChars = 5;
            _tCpuTotal.Margin = new Thickness(0, 0, 0, 4);
            _tCores = new MeterRows();
            _tCores.LabelChars = 3;
            _tCores.ValueChars = 4;
            gr.Children.Add(_tCpuTotal);
            gr.Children.Add(_tCores);
            Grid.SetColumn(gr, 1);
            cg.Children.Add(gr);
            Grid.SetRow(cpu, 0);
            host.Children.Add(cpu);

            // ---------------- середина: слева память, диски, сеть; справа процессы
            var mid = new Grid();
            mid.Margin = new Thickness(0, 8, 0, 0);
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MinWidth = 300 });
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star) });
            Grid.SetRow(mid, 1);
            host.Children.Add(mid);

            var left = new Grid();
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 90 });
            left.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(left, 0);
            mid.Children.Add(left);

            Border mem = TBox(BoxMem, Lang.S("Память", "mem"), out _tMemRight, new Thickness(8, 4, 8, 6));
            _tMem = new MeterRows();
            _tMem.LabelChars = 6;
            _tMem.ValueChars = 9;
            mem.Child = _tMem;
            Grid.SetRow(mem, 0);
            left.Children.Add(mem);

            Border disks = TBox(BoxMem, Lang.S("Диски", "disks"), out _tDiskRight, new Thickness(8, 4, 8, 6));
            disks.Margin = new Thickness(0, 6, 0, 0);
            _tDisks = new MeterRows();
            _tDisks.LabelChars = 4;
            _tDisks.ValueChars = 11;
            disks.Child = _tDisks;
            Grid.SetRow(disks, 1);
            left.Children.Add(disks);

            Border net = TBox(BoxNet, Lang.S("Сеть", "net"), out _tNetRight, new Thickness(8, 4, 8, 6));
            net.Margin = new Thickness(0, 6, 0, 0);
            var ng = new Grid();
            ng.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            ng.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _tNetGraph = new DotGraph();
            Grid.SetRow(_tNetGraph, 0);
            ng.Children.Add(_tNetGraph);
            _tNetText = Small(10, cLabel);
            _tNetText.TextWrapping = TextWrapping.Wrap;
            _tNetText.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetRow(_tNetText, 1);
            ng.Children.Add(_tNetText);
            net.Child = ng;
            Grid.SetRow(net, 2);
            left.Children.Add(net);

            Border proc = TBox(BoxProc, Lang.S("Процессы", "proc"), out _tProcRight, new Thickness(8, 4, 8, 6));
            _tProc = new ProcTable();
            _tProc.Headers = new[] { "Pid:", Lang.S("Программа:", "Program:"), Lang.S("Память", "Mem"), Lang.S("ЦП%", "Cpu%") };
            proc.Child = _tProc;
            Grid.SetColumn(proc, 1);
            mid.Children.Add(proc);

            // ---------------- внизу: состояние ПК (проверки и кнопки)
            _ring = new Ring();                       // кольцо в этой раскладке не показывается, но UpdateHealth его заполняет
            _hStatus = Small(11, cHi);
            _hSub = Small(10, cMid);
            TextBlock dummy;
            Border foot = TBox(BoxProc, Lang.S("Состояние ПК", "health"), out dummy, new Thickness(8, 4, 8, 4));
            (foot as TuiBox).RightSource = _hStatus;
            foot.Margin = new Thickness(0, 8, 0, 0);
            var fp = new StackPanel();
            var items = new WrapPanel();
            string[] names =
            {
                Lang.S("Обновления", "Updates"), Lang.S("Свободное место", "Free space"), Lang.S("Состояние дисков", "Disk health"),
                Lang.S("Автозагрузка", "Startup items"), Lang.S("Ошибки системы", "System errors"),
                Lang.S("Время работы", "Uptime"), Lang.S("Утечки памяти", "Memory leaks")
            };
            for (int i = 0; i < names.Length; i++)
            {
                var r = new HRow();
                var sp = new StackPanel();
                sp.Orientation = Orientation.Horizontal;
                sp.Margin = new Thickness(0, 2, 18, 2);
                r.Dot = new Ellipse();
                r.Dot.Width = 7;
                r.Dot.Height = 7;
                r.Dot.VerticalAlignment = VerticalAlignment.Center;
                r.Dot.Margin = new Thickness(0, 0, 6, 0);
                TextBlock nm = Small(10, cLabel);
                nm.Text = names[i] + ": ";
                r.Value = Small(10, cHi);
                sp.Children.Add(r.Dot);
                sp.Children.Add(nm);
                sp.Children.Add(r.Value);
                items.Children.Add(sp);
                _hrows.Add(r);
            }
            fp.Children.Add(items);
            WrapPanel btns = MakeHealthButtons();
            btns.Margin = new Thickness(0, 2, 0, 0);
            fp.Children.Add(btns);
            foot.Child = fp;
            Grid.SetRow(foot, 2);
            host.Children.Add(foot);

            _healthAt = DateTime.MinValue;
            ApplyDensity();
            StartHealthScans();
            ScheduleFit();
        }

        static string Gb(double v)
        {
            return v.ToString("0.0", Inv) + Lang.S(" ГБ", " GB");
        }

        static string Mbs(double v)
        {
            return v.ToString("0.0", Inv) + Lang.S(" МБ/с", " MB/s");
        }

        void RefreshTerminal(SysStats s)
        {
            if (_tHost == null || !ReferenceEquals(_host, _tHost)) BuildBodyTerminal();
            int n = _w.DashSlots;
            int pts = Points;

            // ---- ЦП
            DashTileData cpu = _w.DashTile(1, s, _period);
            _tCpuGraph.Points = pts;
            _tCpuGraph.Set(new List<double[]> { cpu.Series }, new List<string> { cpu.Color }, 100);
            string first = cpu.Detail.Split('\n')[0];
            SetText(_tCpuRight, first);
            var total = new List<MeterRow>();
            total.Add(MRow(Lang.S("ЦП", "CPU"), s.Cpu.ToString("0", Inv) + "%", s.Cpu / 100.0, GLoad));
            _tCpuTotal.Set(total);
            var cores = new List<MeterRow>();
            for (int i = 0; i < s.CpuCores.Length; i++)
                cores.Add(MRow("C" + i, s.CpuCores[i].ToString("0", Inv) + "%", s.CpuCores[i] / 100.0, GLoad));
            _tCores.Columns = s.CpuCores.Length > 12 ? 3 : (s.CpuCores.Length > 4 ? 2 : 1);
            _tCores.Set(cores);
            TimeSpan up = TimeSpan.FromMilliseconds((double)GetTickCount64());
            SetText(_tUptime, "up " + (up.Days > 0 ? up.Days + Lang.S("д ", "d ") : "") + up.Hours.ToString("00") + ":" + up.Minutes.ToString("00"));

            // ---- память
            double totalGb = s.RamTotalGB, used = s.RamUsedGB;
            double avail = Math.Max(0, totalGb - used);
            double cache = Math.Min(s.CacheGB, avail);
            double free = Math.Max(0, avail - cache);
            double den = totalGb > 0 ? totalGb : 1;
            var mem = new List<MeterRow>();
            mem.Add(MRow(Lang.S("Занято", "Used"), Gb(used), used / den, GUsed));
            mem.Add(MRow(Lang.S("Доступно", "Avail"), Gb(avail), avail / den, GAvail));
            mem.Add(MRow(Lang.S("Кэш", "Cache"), Gb(cache), cache / den, GCache));
            mem.Add(MRow(Lang.S("Свободно", "Free"), Gb(free), free / den, GFree));
            _tMem.Set(mem);
            SetText(_tMemRight, Lang.S("всего ", "total ") + Gb(totalGb));

            // ---- диски: тома (занято/всего) и загрузка диска
            var dr = new List<MeterRow>();
            foreach (DiskStat d in s.Disks)
            {
                foreach (string letter in d.Letters.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var di = new System.IO.DriveInfo(letter);
                        if (!di.IsReady || di.TotalSize <= 0) continue;
                        double t = di.TotalSize / 1073741824.0, f = di.AvailableFreeSpace / 1073741824.0;
                        dr.Add(MRow(letter, (t - f).ToString("0", Inv) + "/" + t.ToString("0", Inv) + Lang.S(" ГБ", " GB"), (t - f) / t, GUsed));
                    }
                    catch { }
                }
                dr.Add(MRow("IO", d.Pct.ToString("0", Inv) + "%  " + Mbs(d.ReadMBs + d.WriteMBs), d.Pct / 100.0, GLoad));
            }
            _tDisks.Set(dr);
            SetText(_tDiskRight, s.Disks.Count > 0 ? Lang.S("загрузка ", "busy ") + s.DiskPct.ToString("0", Inv) + "%" : "");

            // ---- сеть
            DashTileData net = _w.DashTile(n - 1, s, _period);
            _tNetGraph.Points = pts;
            _tNetGraph.Set(new List<double[]> { net.Series }, new List<string> { net.Color }, net.Max);
            _tNetGraph.TopLabel = Mbs(net.Max);
            SetText(_tNetRight, "↓ " + Mbs(s.NetRxMBs) + "  ↑ " + Mbs(s.NetTxMBs));
            SetText(_tNetText, net.Detail.Contains("\n") ? net.Detail.Substring(net.Detail.IndexOf('\n') + 1) : net.Detail);

            // ---- процессы: по загрузке ЦП (замер идёт раз в 5 секунд, см. UpdateVerdict)
            ProcessInfo[] procs = _vProcs;
            var rows = new List<ProcRow>();
            if (procs != null)
            {
                var sorted = new List<ProcessInfo>(procs);
                sorted.Sort(delegate(ProcessInfo a, ProcessInfo b) { return b.Cpu.CompareTo(a.Cpu); });
                for (int i = 0; i < sorted.Count && i < 60; i++)
                {
                    ProcessInfo p = sorted[i];
                    var pr = new ProcRow();
                    pr.Pid = p.Pids.Count > 0 ? p.Pids[0] : 0;
                    pr.Name = p.Name; pr.Count = p.Count; pr.RamMb = p.RamMb; pr.Cpu = p.Cpu;
                    rows.Add(pr);
                }
            }
            _tProc.Set(rows);
            SetText(_tProcRight, rows.Count > 0 ? Lang.S("по ЦП · ", "by cpu · ") + rows.Count : Lang.S("замер…", "sampling…"));

            foreach (TuiBox bx in _boxes) bx.InvalidateVisual();
            UpdateHealth();
            UpdateBell();
            UpdateVerdict(s);
            UpdateMinHeight();
        }
    }
}
