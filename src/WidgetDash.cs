using System;
using System.Collections.Generic;
using System.Globalization;

namespace SysWidget
{
    /// <summary>Вердикт «почему тормозит»: важность (0 — всё в порядке, 1 — внимание, 2 — плохо) и фраза.</summary>
    public sealed class DashVerdict
    {
        public int Level;
        public string Text = "";
        public ProcessInfo Culprit;     // виновник главной строки (null — не определён или закрывать нельзя)
    }
    /// <summary>Содержимое одной плитки окна мониторинга: всё, что нужно показать.</summary>
    public sealed class DashTileData
    {
        public string Title = "";
        public string Color = "#FFFFFF";
        public string Value = "";                       // крупное значение: «64%»
        public string Unit = "";                        // мелкая приписка к значению: «МБ/с»
        public string Detail = "";                      // текст подсказки метрики (несколько строк)
        public string Note = "";                        // «за 2 мин: ср 12% · макс 40%»
        public double[] Series = new double[0];         // ряд для графика (NaN — нет данных)
        public double[] Peaks;                          // пики по минутам (только для периодов в час и сутки)
        public double Max = 100;                        // верх шкалы графика
        public double[] Cores;                          // загрузка по ядрам (только у ЦП)
        public bool Net;                                // ряд в МБ/с, а не в процентах
        public double Pct = -1;                         // значение в процентах для цвета по порогам; -1 — не процент
        public int StepSec = 1;                         // сколько секунд между точками ряда
    }

    /// <summary>
    /// Данные для окна мониторинга. Тексты и цвета берутся из тех же функций, что и
    /// подсказки карточки (RamTip, CpuTip …), чтобы в двух местах не расходились.
    /// </summary>
    public partial class Widget
    {
        Dashboard _dash;

        /// <summary>Окно мониторинга открыто.</summary>
        public bool DashboardOpen { get { return _dash != null && _dash.IsOpen; } }

        /// <summary>Открыть окно мониторинга или закрыть, если оно уже открыто.</summary>
        public void ToggleDashboard()
        {
            try
            {
                if (_dash == null) _dash = new Dashboard(this);
                if (_dash.IsOpen) _dash.Hide();
                else _dash.Show();
            }
            catch (Exception ex) { ErrorLog.Write("dashboard", ex); }
        }

        /// <summary>Показать или скрыть кнопку окна мониторинга на карточке (Prefs.ShowDashBtn).</summary>
        public void ApplyDashButton()
        {
            if (_btnDash == null) return;
            _btnDash.Visibility = Prefs.ShowDashBtn
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        void RefreshDashboard(SysStats s)
        {
            try { if (_dash != null && _dash.IsOpen) _dash.Refresh(s); }
            catch (Exception ex) { ErrorLog.Write("dashboard refresh", ex); }
        }

        /// <summary>Сколько плиток нужно сейчас: ОЗУ, ЦП, диски (по одной на накопитель), ГП, сеть.</summary>
        public int DashSlots { get { return 4 + _diskGroups.Count; } }

        /// <summary>Последний снимок замера (для первого показа окна).</summary>
        public SysStats LastStats { get { return _lastStats; } }

        /// <summary>Периоды графика в окне мониторинга: 2 мин, 15 мин, 1 ч, 24 ч.</summary>
        public static readonly int[] DashPeriodSec = { 120, 900, 3600, 86400 };

        /// <summary>Плитка слота; slot — тот же номер, что у подсказок метрик. period — номер периода (DashPeriodSec).</summary>
        public DashTileData DashTile(int slot, SysStats s, int period)
        {
            var t = new DashTileData();

            int key = SparkKey(slot);
            bool net = key == History.Net;
            t.Net = net;

            double[] ser;
            int spanSec = DashPeriodSec[Math.Max(0, Math.Min(DashPeriodSec.Length - 1, period))];
            if (period >= 2)
            {
                // длинные периоды — по минутам: среднее и пик каждой минуты
                double[] peaks;
                ser = _hist.LongSeries(key, spanSec / 60, out peaks);
                t.Peaks = peaks;
                t.StepSec = 60;
            }
            else
            {
                ser = _hist.Series(key, spanSec);
            }
            t.Series = ser;
            double top = History.Max(t.Peaks != null ? t.Peaks : ser);
            t.Max = net ? Math.Max(0.1, top * 1.15) : 100.0;

            int valid = History.Valid(ser);
            if (valid >= 10 || (period >= 2 && valid >= 2))
            {
                double avg = History.Avg(ser), peak = top;
                int sec = valid * t.StepSec;
                // период, за который реально есть данные: «за 37 мин» вместо «за 24 ч», пока история короткая
                string span = sec >= 5400
                    ? string.Format(Inv, Lang.S("{0:0.#} ч", "{0:0.#} h"), sec / 3600.0)
                    : sec >= 90
                        ? string.Format(Inv, Lang.S("{0} мин", "{0} min"), (int)Math.Ceiling(sec / 60.0))
                        : string.Format(Inv, Lang.S("{0} с", "{0} s"), sec);
                t.Note = net
                    ? string.Format(Inv, Lang.S("за {0}: ср {1:0.0} · макс {2:0.0} МБ/с",
                                                "last {0}: avg {1:0.0} · max {2:0.0} MB/s"), span, avg, peak)
                    : string.Format(Inv, Lang.S("за {0}: ср {1:0}% · макс {2:0}%",
                                                "last {0}: avg {1:0}% · max {2:0}%"), span, avg, peak);
            }

            if (slot == 0)
            {
                t.Title = Lang.Ram;
                t.Color = Theme.RamColor;
                t.Value = string.Format(Inv, "{0:0}%", s.RamPct);
                t.Pct = s.RamPct;
                t.Detail = RamTip(s);
            }
            else if (slot == 1)
            {
                t.Title = Lang.Cpu;
                t.Color = Theme.CpuColor;
                t.Value = string.Format(Inv, "{0:0}%", s.Cpu);
                t.Pct = s.Cpu;
                t.Detail = CpuTip(s);
                t.Cores = s.CpuCores;
            }
            else if (slot == GpuSlot)
            {
                t.Title = Lang.Gpu;
                t.Color = Theme.GpuColor;
                t.Value = s.HasGpu ? string.Format(Inv, "{0:0}%", s.Gpu) : "—";
                t.Pct = s.HasGpu ? s.Gpu : -1;
                t.Detail = GpuTip(s);
            }
            else if (slot == NetSlot)
            {
                t.Title = Lang.Net;
                t.Color = Theme.NetColor;
                t.Value = string.Format(Inv, "↓{0:0.0}  ↑{1:0.0}", s.NetRxMBs, s.NetTxMBs);
                t.Unit = Lang.S("МБ/с", "MB/s");
                t.Detail = NetTip(s);
            }
            else
            {
                int d = slot - 2;
                DiskInfo di = d >= 0 && d < _disks.Count ? _disks[d] : null;
                DiskStat st = di == null ? null : FindDisk(s, di.Index);
                double pct = di != null && di.Index < 0 ? s.DiskPct : (st == null ? 0 : st.Pct);
                t.Title = DiskKindLabel(di);
                t.Color = DiskAccent(di);
                t.Value = string.Format(Inv, "{0:0}%", pct);
                t.Pct = pct;
                t.Detail = di == null ? "" : DiskTip(s, d);
            }
            return t;
        }

        // ------------------------------------------------------------ вердикт «почему тормозит»

        static bool SkipProc(string name)
        {
            return name == "Idle" || name == "System" || name == "Memory Compression" || name == "Registry";
        }

        /// <summary>Строка вердикта по одной метрике: важность (1 — внимание, 2 — плохо) и порядок при равной важности.</summary>
        sealed class Finding
        {
            public int Level, Order;
            public double Score;      // насколько значение выше порога (0…1): при равной важности главной становится сильнее выбившаяся метрика
            public string Text;       // полная фраза для главной строки
            public string Short;      // коротко — для «также: …»
            public ProcessInfo Culprit;
        }

        /// <summary>
        /// Что сейчас нагружает компьютер: одна фраза с виновником. ЦП, ГП и диск берутся
        /// как среднее за последние 15 секунд (чтобы одиночный всплеск не мигал), память — текущая.
        /// Пороги — «Жёлтый от» и «Красный от» из настроек. procs — замер процессов (null, пока
        /// он не готов): без него вердикт называет метрику, но не виновника.
        /// </summary>
        public DashVerdict Diagnose(SysStats s, ProcessInfo[] procs)
        {
            int warn = Prefs.WarnAt, crit = Prefs.CritAt;
            var found = new List<Finding>();

            // --- память
            double commitPct = s.CommitLimitGB > 0 ? s.CommitUsedGB / s.CommitLimitGB * 100.0 : 0;
            ProcessInfo topRam = TopBy(procs, delegate(ProcessInfo p) { return p.RamMb; }, 1.0);
            if (s.RamPct >= warn || commitPct >= crit)
            {
                int lvl = (s.RamPct >= crit || commitPct >= crit) ? 2 : 1;
                string txt = commitPct >= crit && s.RamPct < crit
                    ? string.Format(Inv, Lang.S("Память с файлом подкачки почти исчерпана ({0:0.0} из {1:0.0} ГБ)",
                                                "Memory plus page file is almost used up ({0:0.0} of {1:0.0} GB)"), s.CommitUsedGB, s.CommitLimitGB)
                    : string.Format(Inv, Lang.S(lvl == 2 ? "Оперативная память почти заполнена ({0:0}%)" : "Оперативная память заполняется ({0:0}%)",
                                                lvl == 2 ? "RAM is almost full ({0:0}%)" : "RAM is filling up ({0:0}%)"), s.RamPct);
                if (topRam != null)
                    txt += string.Format(Inv, Lang.S(": больше всего держит {0} — {1:0.0} ГБ", ": biggest is {0} — {1:0.0} GB"),
                                         topRam.Name, topRam.RamMb / 1024.0)
                         + (topRam.Count > 1 ? string.Format(Inv, Lang.S(" ({0} проц.)", " ({0} proc.)"), topRam.Count) : "");
                List<MemoryGrowth> grow = MemoryWatch.Suspects();
                if (grow != null && grow.Count > 0)
                    txt += string.Format(Inv, Lang.S(" · растёт: {0} +{1:0} МБ за {2:0} мин", " · growing: {0} +{1:0} MB in {2:0} min"),
                                         grow[0].Name, grow[0].GrowthMb, grow[0].Minutes);
                found.Add(new Finding { Level = lvl, Order = 0, Score = Over(Math.Max(s.RamPct, commitPct), warn), Text = txt, Culprit = topRam,
                    Short = string.Format(Inv, Lang.S("ОЗУ {0:0}%", "RAM {0:0}%"), s.RamPct) });
            }

            // --- диск: самый занятый
            DiskStat worst = null;
            double worstAvg = 0;
            for (int i = 0; i < s.Disks.Count; i++)
            {
                double a = History.Avg(_hist.Series(History.DiskKey(s.Disks[i].Index), 15));
                if (a > worstAvg) { worstAvg = a; worst = s.Disks[i]; }
            }
            if (worst != null && worstAvg >= warn)
            {
                int lvl = worstAvg >= crit ? 2 : 1;
                string name = worst.Letters.Length > 0 ? worst.Letters : Lang.S("диск ", "disk ") + worst.Index;
                string txt = string.Format(Inv, Lang.S("Диск {0} занят на {1:0}%", "Disk {0} is {1:0}% busy"), name, worstAvg);
                ProcessInfo io = TopBy(procs, delegate(ProcessInfo p) { return p.ReadMBs + p.WriteMBs; }, 1.0);
                if (io != null)
                    txt += string.Format(Inv, Lang.S(": больше всего {0} — {1} {2:0.0} МБ/с", ": mostly {0} — {1} {2:0.0} MB/s"),
                                         io.Name, io.WriteMBs > io.ReadMBs ? Lang.S("пишет", "writes") : Lang.S("читает", "reads"),
                                         Math.Max(io.ReadMBs, io.WriteMBs));
                else if (procs != null)
                    txt += Lang.S(" — по процессам причину определить не удалось", " — no single process stands out");
                found.Add(new Finding { Level = lvl, Order = 1, Score = Over(worstAvg, warn), Text = txt, Culprit = io,
                    Short = string.Format(Inv, Lang.S("диск {0} {1:0}%", "disk {0} {1:0}%"), name, worstAvg) });
            }

            // --- процессор
            double cpuAvg = History.Avg(_hist.Series(History.Cpu, 15));
            if (cpuAvg >= warn)
            {
                int lvl = cpuAvg >= crit ? 2 : 1;
                string txt = string.Format(Inv, Lang.S("Процессор загружен ({0:0}%)", "CPU is busy ({0:0}%)"), cpuAvg);
                ProcessInfo[] top = TopList(procs, delegate(ProcessInfo p) { return p.Cpu; }, 5.0, 2);
                if (top.Length > 0)
                {
                    var parts = new List<string>();
                    for (int i = 0; i < top.Length; i++) parts.Add(string.Format(Inv, "{0} — {1:0}%", top[i].Name, top[i].Cpu));
                    txt += ": " + string.Join(", ", parts.ToArray());
                }
                else if (procs != null)
                    txt += Lang.S(" — нагрузка распределена между многими процессами", " — spread across many processes");
                found.Add(new Finding { Level = lvl, Order = 2, Score = Over(cpuAvg, warn), Text = txt, Culprit = top.Length > 0 ? top[0] : null,
                    Short = string.Format(Inv, Lang.S("ЦП {0:0}%", "CPU {0:0}%"), cpuAvg) });
            }

            // --- видеокарта
            if (s.HasGpu)
            {
                double gpuAvg = History.Avg(_hist.Series(History.Gpu, 15));
                if (gpuAvg >= warn)
                {
                    int lvl = gpuAvg >= crit ? 2 : 1;
                    string txt = string.Format(Inv, Lang.S("Видеокарта загружена ({0:0}%)", "GPU is busy ({0:0}%)"), gpuAvg);
                    GpuProc best = null;
                    for (int i = 0; i < s.GpuProcs.Count; i++)
                        if (best == null || s.GpuProcs[i].Util > best.Util) best = s.GpuProcs[i];
                    string bn = null;
                    ProcessInfo gp = null;
                    if (best != null && best.Util >= 5 && procs != null)
                        for (int i = 0; i < procs.Length && bn == null; i++)
                            if (procs[i].Pids.Contains(best.Pid)) { bn = procs[i].Name; gp = procs[i]; }
                    if (bn != null) txt += string.Format(Inv, ": {0} — {1:0}%", bn, best.Util);
                    found.Add(new Finding { Level = lvl, Order = 3, Score = Over(gpuAvg, warn), Text = txt, Culprit = gp,
                        Short = string.Format(Inv, Lang.S("ГП {0:0}%", "GPU {0:0}%"), gpuAvg) });
                }
            }

            // --- сеть: порог в МБ/с, виновника без прав администратора не определить
            double net = s.NetRxMBs + s.NetTxMBs;
            if (net >= 30)
            {
                found.Add(new Finding { Level = 1, Order = 4, Score = 0,
                    Text = string.Format(Inv, Lang.S("Сеть сильно загружена (↓{0:0.0} ↑{1:0.0} МБ/с)", "Network is very busy (↓{0:0.0} ↑{1:0.0} MB/s)"), s.NetRxMBs, s.NetTxMBs),
                    Short = string.Format(Inv, Lang.S("сеть {0:0} МБ/с", "network {0:0} MB/s"), net) });
            }

            var v = new DashVerdict();
            if (found.Count == 0)
            {
                v.Level = 0;
                v.Text = Lang.S("Всё в порядке: серьёзной нагрузки нет", "All good: no heavy load");
                return v;
            }
            found.Sort(delegate(Finding a, Finding b)
            {
                int c = b.Level.CompareTo(a.Level);
                if (c == 0) c = b.Score.CompareTo(a.Score);
                return c != 0 ? c : a.Order.CompareTo(b.Order);
            });
            v.Level = found[0].Level;
            v.Text = found[0].Text;
            if (found[0].Culprit != null && ProcessPanel.CanClose(found[0].Culprit.Name)) v.Culprit = found[0].Culprit;
            if (found.Count > 1)
            {
                var more = new List<string>();
                for (int i = 1; i < found.Count; i++) more.Add(found[i].Short);
                v.Text += "  ·  " + Lang.S("также: ", "also: ") + string.Join(", ", more.ToArray());
            }
            return v;
        }

        static double Over(double value, int warn)
        {
            return Math.Max(0, (value - warn) / Math.Max(1.0, 100.0 - warn));
        }

        /// <summary>Процесс с наибольшим значением не меньше порога, без служебных.</summary>
        static ProcessInfo TopBy(ProcessInfo[] procs, Func<ProcessInfo, double> key, double min)
        {
            ProcessInfo[] t = TopList(procs, key, min, 1);
            return t.Length > 0 ? t[0] : null;
        }

        static ProcessInfo[] TopList(ProcessInfo[] procs, Func<ProcessInfo, double> key, double min, int n)
        {
            var res = new List<ProcessInfo>();
            if (procs == null) return res.ToArray();
            for (int i = 0; i < procs.Length; i++)
                if (!SkipProc(procs[i].Name) && key(procs[i]) >= min) res.Add(procs[i]);
            res.Sort(delegate(ProcessInfo a, ProcessInfo b) { return key(b).CompareTo(key(a)); });
            if (res.Count > n) res.RemoveRange(n, res.Count - n);
            return res.ToArray();
        }
    }
}
