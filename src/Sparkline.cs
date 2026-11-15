using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SysWidget
{
    /// <summary>
    /// Мини-график: линия с лёгкой заливкой. Окно по времени фиксированное, поэтому пока данных
    /// мало, график растёт справа налево, а не растягивается на всю ширину. Пустые значения (NaN)
    /// рвут линию. Для длинных периодов можно добавить тонкую линию пиков; в окне мониторинга
    /// график ещё показывает значение и время под курсором.
    /// </summary>
    public sealed class Sparkline : FrameworkElement
    {
        public const int Window = 120;      // точек на графике (секунд)

        readonly Brush _fill;
        readonly Pen _line;
        readonly Pen _peak;
        readonly Pen _base;
        readonly Pen _grid;
        readonly Pen _cursor;
        readonly Brush _dot;
        double[] _v = new double[0];
        double[] _p;
        double _max = 100;
        double _hoverX = -1;
        bool _interactive;

        /// <summary>Сколько точек (секунд) помещается по ширине; для 15-минутного графика — 900.</summary>
        public int Points = Window;

        /// <summary>Пунктирная линия на середине высоты (для графиков в процентах).</summary>
        public bool MidGrid;

        /// <summary>Текст у курсора для точки с номером i из n; null — подсказки нет.</summary>
        public Func<int, int, string> Describe;

        public Sparkline(string color)
        {
            Width = 176;
            Height = 26;
            HorizontalAlignment = HorizontalAlignment.Left;
            IsHitTestVisible = false;

            _fill = Theme.B(Theme.Fade(color, 0x33));
            _line = new Pen(Theme.B(color), 1.3);
            _line.LineJoin = PenLineJoin.Round;
            _line.Freeze();
            _peak = new Pen(Theme.B(Theme.Fade(color, 0x66)), 1);
            _peak.LineJoin = PenLineJoin.Round;
            _peak.Freeze();
            _base = new Pen(Theme.B(Theme.StrokeSoft), 1);
            _base.Freeze();
            _grid = new Pen(Theme.B(Theme.StrokeSoft), 1);
            _grid.DashStyle = DashStyles.Dot;
            _grid.Freeze();
            _cursor = new Pen(Theme.B(Theme.TextMid), 1);
            _cursor.Freeze();
            _dot = Theme.B(color);
        }

        /// <summary>Включить подсказку под курсором (нужен Describe).</summary>
        public bool Interactive
        {
            get { return _interactive; }
            set { _interactive = value; IsHitTestVisible = value; }
        }

        public void Set(double[] values, double max)
        {
            _v = values;
            _max = max > 0 ? max : 1;
            InvalidateVisual();
        }

        /// <summary>Пики по тем же точкам (длина как у ряда) или null.</summary>
        public void SetPeaks(double[] peaks)
        {
            _p = peaks;
        }

        protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_interactive) return;
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

        double Y(double v, double top, double bottom)
        {
            double f = Math.Min(1.0, Math.Max(0.0, v / _max));
            return bottom - f * (bottom - top);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (_interactive) dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // чтобы принимать мышь по всей площади
            double top = 2, bottom = h - 1.5;
            if (MidGrid) dc.DrawLine(_grid, new Point(0, (top + bottom) / 2), new Point(w, (top + bottom) / 2));
            dc.DrawLine(_base, new Point(0, h - 0.5), new Point(w, h - 0.5));
            int n = _v.Length;
            if (n < 2) return;

            double step = w / (Math.Max(2, Points) - 1);
            double x0 = w - (n - 1) * step;

            // пики — тонкая линия позади основной
            if (_p != null && _p.Length == n)
            {
                var pk = new StreamGeometry();
                using (StreamGeometryContext c = pk.Open())
                {
                    bool open = false;
                    for (int i = 0; i < n; i++)
                    {
                        if (double.IsNaN(_p[i])) { open = false; continue; }
                        var pt = new Point(x0 + i * step, Y(_p[i], top, bottom));
                        if (!open) { c.BeginFigure(pt, false, false); open = true; }
                        else c.LineTo(pt, true, true);
                    }
                }
                pk.Freeze();
                dc.DrawGeometry(null, _peak, pk);
            }

            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (StreamGeometryContext c = line.Open())
            using (StreamGeometryContext a = area.Open())
            {
                bool open = false;
                double lastX = 0;
                for (int i = 0; i < n; i++)
                {
                    if (double.IsNaN(_v[i]))
                    {
                        if (open) { a.LineTo(new Point(lastX, bottom), false, false); open = false; }
                        continue;
                    }
                    var p = new Point(x0 + i * step, Y(_v[i], top, bottom));
                    if (!open)
                    {
                        c.BeginFigure(p, false, false);
                        a.BeginFigure(new Point(p.X, bottom), true, true);
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
                if (open) a.LineTo(new Point(lastX, bottom), false, false);
            }
            line.Freeze();
            area.Freeze();

            dc.DrawGeometry(_fill, null, area);
            dc.DrawGeometry(null, _line, line);

            if (_interactive && _hoverX >= 0 && Describe != null) DrawHover(dc, w, n, x0, step, top, bottom);
        }

        void DrawHover(DrawingContext dc, double w, int n, double x0, double step, double top, double bottom)
        {
            int i = (int)Math.Round((_hoverX - x0) / step);
            if (i < 0 || i >= n) return;
            double x = x0 + i * step;
            dc.DrawLine(_cursor, new Point(x, 0), new Point(x, bottom + 1));
            if (!double.IsNaN(_v[i])) dc.DrawEllipse(_dot, null, new Point(x, Y(_v[i], top, bottom)), 2.5, 2.5);

            string text = Describe(i, n);
            if (string.IsNullOrEmpty(text)) return;
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(Theme.FontUi), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                Theme.Fs(10), Theme.B(Theme.TextHi), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double bw = ft.Width + 12, bh = ft.Height + 4;
            double bx = x + 10;
            if (bx + bw > w) bx = x - 10 - bw;
            if (bx < 0) bx = 0;
            dc.DrawRoundedRectangle(Theme.B(Theme.Fade(Theme.Bg, 0xF0)), new Pen(Theme.B(Theme.Stroke), 1),
                new Rect(bx, 1, bw, bh), 3, 3);
            dc.DrawText(ft, new Point(bx + 6, 3));
        }
    }
}
