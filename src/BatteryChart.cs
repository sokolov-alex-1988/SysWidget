using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SysWidget
{
    /// <summary>
    /// График заряда устройства за 14 дней: линия по замерам, штрихи на 50% и 20%,
    /// точка на последнем значении. Ось времени фиксирована — слева 14 дней назад, справа «сейчас»,
    /// поэтому пока истории мало, линия занимает только правый край.
    /// </summary>
    public sealed class BatteryChart : FrameworkElement
    {
        public const int Days = 14;

        readonly Pen _line;
        readonly Pen _grid;
        readonly Brush _dot;
        readonly Brush _fill;
        List<int[]> _pts = new List<int[]>();
        int _nowMinutes;

        public BatteryChart(string color)
        {
            Width = 240;
            Height = 44;
            HorizontalAlignment = HorizontalAlignment.Left;
            IsHitTestVisible = false;

            _line = new Pen(Theme.B(color), 1.4);
            _line.LineJoin = PenLineJoin.Round;
            _line.Freeze();
            _grid = new Pen(Theme.B(Theme.StrokeSoft), 1);
            _grid.DashStyle = DashStyles.Dot;
            _grid.Freeze();
            _dot = Theme.B(color);
            _fill = Theme.B(Theme.Fade(color, 0x26));
        }

        /// <summary>Замеры [минуты от эпохи, заряд %] и текущее время в тех же минутах.</summary>
        public void Set(List<int[]> points, int nowMinutes)
        {
            _pts = points;
            _nowMinutes = nowMinutes;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            double top = 3, bottom = h - 3;
            // штрихи на 50% и 20%
            foreach (double level in new double[] { 50, 20 })
            {
                double y = bottom - level / 100.0 * (bottom - top);
                dc.DrawLine(_grid, new Point(0, y), new Point(w, y));
            }
            if (_pts.Count == 0) return;

            double span = Days * 24 * 60.0;
            double t0 = _nowMinutes - span;

            var pts = new List<Point>();
            foreach (int[] p in _pts)
            {
                double x = (p[0] - t0) / span * w;
                if (x < 0) x = 0;
                double y = bottom - Math.Min(100, Math.Max(0, p[1])) / 100.0 * (bottom - top);
                pts.Add(new Point(x, y));
            }
            // до «сейчас» держим последнее значение горизонтально
            pts.Add(new Point(w, pts[pts.Count - 1].Y));

            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (StreamGeometryContext c = line.Open())
            using (StreamGeometryContext a = area.Open())
            {
                c.BeginFigure(pts[0], false, false);
                a.BeginFigure(new Point(pts[0].X, bottom), true, true);
                a.LineTo(pts[0], false, false);
                for (int i = 1; i < pts.Count; i++)
                {
                    // ступеньками: заряд держится, пока не пришёл новый замер
                    var step = new Point(pts[i].X, pts[i - 1].Y);
                    c.LineTo(step, true, false); c.LineTo(pts[i], true, false);
                    a.LineTo(step, false, false); a.LineTo(pts[i], false, false);
                }
                a.LineTo(new Point(pts[pts.Count - 1].X, bottom), false, false);
            }
            line.Freeze();
            area.Freeze();
            dc.DrawGeometry(_fill, null, area);
            dc.DrawGeometry(null, _line, line);
            dc.DrawEllipse(_dot, null, pts[pts.Count - 1], 2.2, 2.2);
        }
    }
}
