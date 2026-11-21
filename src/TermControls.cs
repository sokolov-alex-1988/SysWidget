using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SysWidget
{
    /// <summary>Общие помощники рисования для элементов темы «Терминал».</summary>
    static class TermDraw
    {
        /// <summary>Цвет на трёхцветном градиенте: t = 0 — c0, 0,5 — c1, 1 — c2.</summary>
        public static Color Grad(string c0, string c1, string c2, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            Color a = (Color)ColorConverter.ConvertFromString(t < 0.5 ? c0 : c1);
            Color b = (Color)ColorConverter.ConvertFromString(t < 0.5 ? c1 : c2);
            double k = t < 0.5 ? t * 2 : (t - 0.5) * 2;
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
        }

        public static FormattedText Text(Visual owner, string s, double size, Brush brush)
        {
            return new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(Theme.FontUi), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size, brush, VisualTreeHelper.GetDpi(owner).PixelsPerDip);
        }

        /// <summary>Ширина одного знака моноширинного шрифта.</summary>
        public static double CharWidth(Visual owner, double size)
        {
            return Text(owner, "0", size, Brushes.White).Width;
        }

        /// <summary>
        /// Шкала из квадратиков 4 px с просветом 1 px: заполненные красятся по градиенту вдоль шкалы (зелёный → жёлтый →
        /// красный у загрузки), пустые — тусклые, как у консольных мониторов.
        /// </summary>
        public static void SegBar(DrawingContext dc, double x, double y, double w, double h, double frac,
                                  string c0, string c1, string c2)
        {
            const double seg = 4, gap = 1;
            int count = (int)Math.Floor((w + gap) / (seg + gap));
            if (count < 1) return;
            int filled = (int)Math.Round(Math.Max(0, Math.Min(1, frac)) * count);
            Brush dim = Theme.B(Theme.StrokeSoft);
            for (int k = 0; k < count; k++)
            {
                Brush br = k < filled ? Theme.B(ColorHex(Grad(c0, c1, c2, count == 1 ? 0 : (double)k / (count - 1)))) : dim;
                dc.DrawRectangle(br, null, new Rect(Math.Round(x + k * (seg + gap)), Math.Round(y), seg, h));
            }
        }

        public static string ColorHex(Color c)
        {
            return "#FF" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }
    }

    public sealed class MeterRow
    {
        public string Label = "", Value = "";
        public double Frac;                                       // 0..1
        public string C0 = "#FF98BB6C", C1 = "#FFE6C384", C2 = "#FFFF5D62";
    }

    /// <summary>
    /// Строки «подпись — шкала — значение» в несколько колонок: память и диски, сетка ядер ЦП.
    /// Шкала из квадратиков с градиентом цвета (TermDraw.SegBar).
    /// </summary>
    public sealed class MeterRows : FrameworkElement
    {
        public List<MeterRow> Rows = new List<MeterRow>();
        public int Columns = 1;
        public int LabelChars = 4, ValueChars = 6;
        public double RowHeight = 17;

        public void Set(List<MeterRow> rows)
        {
            Rows = rows;
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size available)
        {
            int perCol = (Rows.Count + Columns - 1) / Math.Max(1, Columns);
            double w = double.IsInfinity(available.Width) ? 200 : available.Width;
            return new Size(w, perCol * RowHeight);
        }

        protected override void OnRender(DrawingContext dc)
        {
            int n = Rows.Count;
            if (n == 0) return;
            double size = Theme.Fs(10);
            double cw = TermDraw.CharWidth(this, size);
            int cols = Math.Max(1, Columns);
            int perCol = (n + cols - 1) / cols;
            double gap = 18;
            double cellW = (ActualWidth - gap * (cols - 1)) / cols;
            Brush label = Theme.B(Theme.TextLabel), value = Theme.B(Theme.TextHi);
            // ширина колонок подписей и значений — по самым длинным строкам, чтобы текст не заходил на шкалу
            int lc = LabelChars, vc = ValueChars;
            for (int i = 0; i < n; i++)
            {
                lc = Math.Max(lc, Rows[i].Label.Length + 1);
                vc = Math.Max(vc, Rows[i].Value.Length + 1);
            }
            double lw = lc * cw, vw = vc * cw;
            for (int i = 0; i < n; i++)
            {
                MeterRow r = Rows[i];
                int col = i / perCol, row = i % perCol;
                double x = col * (cellW + gap), y = row * RowHeight;
                FormattedText lt = TermDraw.Text(this, r.Label, size, label);
                dc.DrawText(lt, new Point(x, y + (RowHeight - lt.Height) / 2));
                FormattedText vt = TermDraw.Text(this, r.Value, size, value);
                dc.DrawText(vt, new Point(x + cellW - vt.Width, y + (RowHeight - vt.Height) / 2));
                double bx = x + lw + 4, bw = cellW - lw - vw - 12;
                if (bw > 8) TermDraw.SegBar(dc, bx, y + 3, bw, RowHeight - 7, r.Frac, r.C0, r.C1, r.C2);
            }
        }
    }

    /// <summary>
    /// Точечный график: колонки через три пикселя, в каждой столбик из квадратиков 2x2 через четыре пикселя от нуля до значения.
    /// Цвет по высоте идёт по градиенту (как «облако» брайлевских символов у консольных мониторов) или один цвет на ряд.
    /// </summary>
    public sealed class DotGraph : FrameworkElement
    {
        public List<double[]> Series = new List<double[]>();
        public List<string> Colors = new List<string>();
        public double Max = 100;
        public int Points = 120;
        public bool Gradient;
        public string G0 = "#FF98BB6C", G1 = "#FFE6C384", G2 = "#FFFF5D62";
        public string TopLabel = "";

        public void Set(List<double[]> series, List<string> colors, double max)
        {
            Series = series; Colors = colors; Max = max > 0 ? max : 1;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 12 || h < 12) return;
            const double colStep = 3, rowStep = 4;
            int cols = (int)Math.Floor(w / colStep);
            int rowsN = (int)Math.Floor(h / rowStep);
            if (cols < 2 || rowsN < 2) return;
            double step = (w - 2) / (Math.Max(2, Points) - 1);       // сколько пикселей на одну точку ряда

            for (int s = 0; s < Series.Count; s++)
            {
                double[] v = Series[s];
                int n = v.Length;
                if (n < 2) continue;
                var frac = new double[cols];
                for (int c = 0; c < cols; c++)
                {
                    double x = w - 2 - c * colStep;                          // справа налево
                    int i = n - 1 - (int)Math.Round((w - 2 - x) / step);
                    frac[c] = (i < 0 || i >= n || double.IsNaN(v[i])) ? -1 : Math.Min(1, v[i] / Max);
                }
                for (int r = 0; r < rowsN; r++)
                {
                    double rowFrac = (r + 0.5) / rowsN;                       // r = 0 — нижний ряд
                    var g = new StreamGeometry();
                    bool any = false;
                    using (StreamGeometryContext sc = g.Open())
                    {
                        for (int c = 0; c < cols; c++)
                        {
                            if (frac[c] < rowFrac) continue;
                            double x = w - 2 - (c + 1) * colStep + 1, y = h - (r + 1) * rowStep;
                            sc.BeginFigure(new Point(x, y), true, true);
                            sc.LineTo(new Point(x + 2, y), false, false);
                            sc.LineTo(new Point(x + 2, y + 2), false, false);
                            sc.LineTo(new Point(x, y + 2), false, false);
                            any = true;
                        }
                    }
                    if (!any) continue;
                    g.Freeze();
                    Brush br = Gradient
                        ? Theme.B(TermDraw.ColorHex(TermDraw.Grad(G0, G1, G2, rowFrac)))
                        : Theme.B(Theme.Fade(s < Colors.Count ? Colors[s] : "#FFFFFFFF", (byte)(0x60 + 0x9F * rowFrac)));
                    dc.DrawGeometry(br, null, g);
                }
            }
            if (TopLabel.Length > 0)
                dc.DrawText(TermDraw.Text(this, TopLabel, Theme.Fs(9), Theme.B(Theme.TextMid)), new Point(2, 0));
        }
    }

    public sealed class ProcRow
    {
        public int Pid;
        public string Name = "";
        public int Count;
        public double RamMb, Cpu;
    }

    /// <summary>
    /// Таблица процессов: PID, программа, память, ЦП со шкалой. Строки по загрузке ЦП; сколько влезло по высоте, столько и
    /// показано. Рисуется напрямую моноширинным шрифтом, поэтому колонки ровные.
    /// </summary>
    public sealed class ProcTable : FrameworkElement
    {
        public List<ProcRow> Rows = new List<ProcRow>();
        public string[] Headers = { "Pid:", "Программа:", "Память", "ЦП%" };

        public void Set(List<ProcRow> rows)
        {
            Rows = rows;
            InvalidateVisual();
        }

        static string Mem(double mb)
        {
            return mb >= 1024 ? (mb / 1024).ToString("0.0", CultureInfo.InvariantCulture) + "G"
                              : mb.ToString("0", CultureInfo.InvariantCulture) + "M";
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 80 || h < 20) return;
            double size = Theme.Fs(10), rowH = 17;
            double cw = TermDraw.CharWidth(this, size);
            double pidW = 7 * cw, memW = 6 * cw, cpuW = 5 * cw, barW = 48, gap = 8;
            double nameW = w - pidW - memW - cpuW - barW - gap * 4;
            Brush head = Theme.B(Theme.HotColor), txt = Theme.B(Theme.TextLabel), hi = Theme.B(Theme.TextHi), mid = Theme.B(Theme.TextMid);

            double xp = 0, xn = pidW + gap, xm = xn + nameW + gap, xc = xm + memW + gap, xb = xc + cpuW + gap;
            DrawCell(dc, Headers[0], xp, 0, pidW, size, head, true);
            DrawCell(dc, Headers[1], xn, 0, nameW, size, head, false);
            DrawCell(dc, Headers[2], xm, 0, memW, size, head, true);
            DrawCell(dc, Headers[3], xc, 0, cpuW, size, head, true);

            int maxRows = (int)Math.Floor((h - rowH) / rowH);
            for (int i = 0; i < Rows.Count && i < maxRows; i++)
            {
                ProcRow r = Rows[i];
                double y = (i + 1) * rowH;
                string name = r.Name + (r.Count > 1 ? " ×" + r.Count : "");
                DrawCell(dc, r.Pid > 0 ? r.Pid.ToString(CultureInfo.InvariantCulture) : "-", xp, y, pidW, size, mid, true);
                DrawCell(dc, name, xn, y, nameW, size, r.Cpu >= 50 ? Theme.B(Theme.WarnColor) : txt, false);
                DrawCell(dc, Mem(r.RamMb), xm, y, memW, size, hi, true);
                DrawCell(dc, r.Cpu.ToString("0.0", CultureInfo.InvariantCulture), xc, y, cpuW, size, hi, true);
                TermDraw.SegBar(dc, xb, y + 4, barW, rowH - 8, Math.Min(1, r.Cpu / 100.0 * 2.5),
                    "#FF98BB6C", "#FFE6C384", "#FFFF5D62");
            }
        }

        /// <summary>Текст в ячейке: обрезается по ширине (многоточие), выравнивание по левому или правому краю.</summary>
        void DrawCell(DrawingContext dc, string s, double x, double y, double width, double size, Brush brush, bool right)
        {
            FormattedText t = TermDraw.Text(this, s, size, brush);
            t.MaxTextWidth = Math.Max(1, width);
            t.MaxLineCount = 1;
            t.Trimming = TextTrimming.CharacterEllipsis;
            double ty = y + (17 - t.Height) / 2;
            dc.DrawText(t, new Point(right ? x + width - Math.Min(width, t.Width) : x, ty));
        }
    }
}
