using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SysWidget
{
    /// <summary>
    /// Общий график окна мониторинга: несколько метрик в процентах на одной шкале 0–100 %
    /// (ОЗУ, ЦП, диски, ГП), подписи шкалы слева и времени снизу. Под курсором — вертикальная
    /// линия и плашка со временем и значениями всех рядов. Окно по времени фиксированное:
    /// пока данных мало, ряды растут справа налево, как у мини-графиков.
    /// </summary>
    public sealed class MultiChart : FrameworkElement
    {
        const double PadL = 38, PadR = 8, PadT = 8, PadB = 20;

        readonly List<double[]> _v = new List<double[]>();
        readonly List<string> _names = new List<string>();
        readonly List<string> _colors = new List<string>();
        readonly List<Pen> _pens = new List<Pen>();
        readonly List<Brush> _fills = new List<Brush>();
        readonly List<Brush> _dots = new List<Brush>();
        Pen _gridPen;
        Pen _cursorPen;
        double _hoverX = -1;

        /// <summary>Сколько точек (по числу секунд или минут периода) помещается по ширине.</summary>
        public int Points = 120;

        /// <summary>Подписи оси времени слева направо (равномерно по ширине).</summary>
        public string[] XLabels = new string[0];

        /// <summary>Подпись момента для точки «back» шагов назад от последней; null — без времени.</summary>
        public Func<int, string> When;
        /// <summary>Тема «Терминал»: ряды рисуются точками (облако из квадратиков, как брайлевская графика), а не линиями.</summary>
        public bool Dots;
        readonly List<Brush[]> _dotBrushes = new List<Brush[]>();

        public MultiChart()
        {
            Height = 190;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            _gridPen = new Pen(Theme.B(Theme.StrokeSoft), 1);
            _gridPen.Freeze();
            _cursorPen = new Pen(Theme.B(Theme.TextMid), 1);
            _cursorPen.Freeze();
        }

        /// <summary>Новые данные: ряды, их названия и цвета (в одном порядке).</summary>
        public void Set(List<double[]> values, List<string> names, List<string> colors)
        {
            bool same = colors.Count == _colors.Count;
            for (int i = 0; same && i < colors.Count; i++) if (colors[i] != _colors[i]) same = false;
            if (!same)
            {
                _colors.Clear(); _colors.AddRange(colors);
                _pens.Clear(); _fills.Clear(); _dots.Clear(); _dotBrushes.Clear();
                for (int i = 0; i < colors.Count; i++)
                {
                    var pen = new Pen(Theme.B(colors[i]), 1.6);
                    pen.LineJoin = PenLineJoin.Round;
                    pen.Freeze();
                    _pens.Add(pen);
                    var g = new LinearGradientBrush(
                        (Color)ColorConverter.ConvertFromString(Theme.Fade(colors[i], 0x38)),
                        (Color)ColorConverter.ConvertFromString(Theme.Fade(colors[i], 0x00)), 90);
                    g.Freeze();
                    _fills.Add(g);
                    _dots.Add(Theme.B(colors[i]));
                    var db = new Brush[7];
                    for (int k = 0; k < db.Length; k++) db[k] = Theme.B(Theme.Fade(colors[i], (byte)(0xFF - k * 0x2C)));
                    _dotBrushes.Add(db);
                }
            }
            _names.Clear(); _names.AddRange(names);
            _v.Clear(); _v.AddRange(values);
            InvalidateVisual();
        }

        protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _hoverX = e.GetPosition(this).X;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverX < 0) return;
            _hoverX = -1;
            InvalidateVisual();
        }

        FormattedText Text(string s, double size, string color)
        {
            return new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(Theme.FontUi), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                Theme.Fs(size), Theme.B(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        double Y(double v, double top, double h)
        {
            double f = Math.Min(1.0, Math.Max(0.0, v / 100.0));
            return top + h - f * h;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));      // чтобы принимать мышь по всей площади
            double px = PadL, pw = Math.Max(10, w - PadL - PadR), py = PadT, ph = Math.Max(10, h - PadT - PadB);

            // сетка и подписи шкалы 0–100 %
            for (int k = 0; k <= 4; k++)
            {
                double y = Math.Round(py + ph * k / 4.0) + 0.5;
                dc.DrawLine(_gridPen, new Point(px, y), new Point(px + pw, y));
                FormattedText t = Text((100 - 25 * k) + "%", 9, Theme.TextMid);
                dc.DrawText(t, new Point(px - 6 - t.Width, y - t.Height / 2));
            }

            // подписи времени
            int nl = XLabels.Length;
            for (int i = 0; i < nl; i++)
            {
                FormattedText t = Text(XLabels[i], 9, Theme.TextMid);
                double x = nl == 1 ? px : px + (pw - 0) * i / (nl - 1) - t.Width * i / (nl - 1);
                dc.DrawText(t, new Point(x, py + ph + 5));
            }

            double step = pw / (Math.Max(2, Points) - 1);
            double right = px + pw;

            for (int s = 0; s < _v.Count; s++)
            {
                double[] v = _v[s];
                int n = v.Length;
                if (n < 2) continue;
                double x0 = right - (n - 1) * step;
                if (Dots) { DrawDots(dc, s, v, px, right, py, ph, step); continue; }

                var line = new StreamGeometry();
                var area = new StreamGeometry();
                using (StreamGeometryContext c = line.Open())
                using (StreamGeometryContext a = area.Open())
                {
                    bool open = false;
                    double lastX = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (double.IsNaN(v[i]))
                        {
                            if (open) { a.LineTo(new Point(lastX, py + ph), false, false); open = false; }
                            continue;
                        }
                        var p = new Point(x0 + i * step, Y(v[i], py, ph));
                        if (!open)
                        {
                            c.BeginFigure(p, false, false);
                            a.BeginFigure(new Point(p.X, py + ph), true, true);
                            a.LineTo(p, false, false);
                            open = true;
                        }
                        else
                        {
                            c.LineTo(p, true, true);
                            a.LineTo(p, false, false);
                        }
                        lastX = p.X;
                    }
                    if (open) a.LineTo(new Point(lastX, py + ph), false, false);
                }
                line.Freeze();
                area.Freeze();
                dc.DrawGeometry(_fills[s], null, area);
                dc.DrawGeometry(null, _pens[s], line);
            }

            if (_hoverX >= px && _hoverX <= right) DrawHover(dc, w, px, right, py, ph, step);
        }

        /// <summary>
        /// Ряд точками: в каждой третьей колонке пикселей столбик из пяти квадратиков 2x2 вниз от значения, цвет тускнеет
        /// к низу. Получается «облако» вдоль кривой, как у брайлевской графики консольных мониторов.
        /// </summary>
        void DrawDots(DrawingContext dc, int s, double[] v, double px, double right, double py, double ph, double step)
        {
            int n = v.Length;
            Brush[] br = _dotBrushes[s];
            for (double x = right - 2; x >= px; x -= 3)
            {
                int i = n - 1 - (int)Math.Round((right - x) / step);
                if (i < 0) break;
                if (i >= n || double.IsNaN(v[i])) continue;
                double y = Y(v[i], py, ph);
                for (int k = 0; k < br.Length; k++)
                {
                    double yy = y + k * 4;
                    if (yy > py + ph - 1) break;
                    dc.DrawRectangle(br[k], null, new Rect(Math.Round(x), Math.Round(yy), 2, 2));
                }
            }
        }

        void DrawHover(DrawingContext dc, double w, double px, double right, double py, double ph, double step)
        {
            int back = (int)Math.Round((right - _hoverX) / step);
            if (back < 0) return;
            double x = right - back * step;
            dc.DrawLine(_cursorPen, new Point(x, py), new Point(x, py + ph));

            var lines = new List<FormattedText>();
            var marks = new List<Brush>();
            string when = When != null ? When(back) : null;
            if (!string.IsNullOrEmpty(when)) { lines.Add(Text(when, 10, Theme.TextHi)); marks.Add(null); }
            for (int s = 0; s < _v.Count; s++)
            {
                double[] v = _v[s];
                int i = v.Length - 1 - back;
                if (i < 0 || i >= v.Length || double.IsNaN(v[i])) continue;
                dc.DrawEllipse(_dots[s], null, new Point(x, Y(v[i], py, ph)), 3, 3);
                lines.Add(Text(_names[s] + "  " + v[i].ToString("0", CultureInfo.InvariantCulture) + "%", 10, Theme.TextHi));
                marks.Add(_dots[s]);
            }
            if (lines.Count == 0) return;

            double bw = 0, lh = 0;
            for (int i = 0; i < lines.Count; i++) { bw = Math.Max(bw, lines[i].Width); lh = Math.Max(lh, lines[i].Height); }
            bw += 26; double bh = lines.Count * (lh + 2) + 8;
            double bx = x + 12;
            if (bx + bw > w) bx = x - 12 - bw;
            if (bx < 0) bx = 0;
            dc.DrawRoundedRectangle(Theme.B(Theme.Fade(Theme.Surface, 0xF2)), new Pen(Theme.B(Theme.Stroke), 1), new Rect(bx, py + 2, bw, bh), Theme.RadiusPill, Theme.RadiusPill);
            for (int i = 0; i < lines.Count; i++)
            {
                double ty = py + 6 + i * (lh + 2);
                if (marks[i] != null) dc.DrawEllipse(marks[i], null, new Point(bx + 11, ty + lh / 2), 3, 3);
                dc.DrawText(lines[i], new Point(bx + (marks[i] != null ? 20 : 10), ty));
            }
        }
    }
}
