using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SysWidget
{
    /// <summary>
    /// Кольцевой индикатор: цветная дуга по доле (0–1) на тусклом кольце, в центре подпись.
    /// Для чек-листа здоровья в окне мониторинга.
    /// </summary>
    public sealed class Ring : FrameworkElement
    {
        double _f;
        string _color = "#FFFFFFFF";
        string _label = "";
        Pen _track;

        public Ring()
        {
            Width = 68;
            Height = 68;
            _track = new Pen(Theme.B(Theme.StrokeStrong), 6);
            _track.Freeze();
        }

        public void Set(double fraction, string color, string label)
        {
            _f = Math.Max(0, Math.Min(1, fraction));
            _color = color;
            _label = label ?? "";
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            var c = new Point(w / 2, h / 2);
            double r = Math.Min(w, h) / 2 - 4;
            dc.DrawEllipse(null, _track, c, r, r);

            var arc = new Pen(Theme.B(_color), 6);
            arc.StartLineCap = PenLineCap.Round;
            arc.EndLineCap = PenLineCap.Round;
            if (_f >= 0.999)
                dc.DrawEllipse(null, arc, c, r, r);
            else if (_f > 0.001)
            {
                double a = -Math.PI / 2 + 2 * Math.PI * _f;
                var end = new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
                var g = new StreamGeometry();
                using (StreamGeometryContext s = g.Open())
                {
                    s.BeginFigure(new Point(c.X, c.Y - r), false, false);
                    s.ArcTo(end, new Size(r, r), 0, _f > 0.5, SweepDirection.Clockwise, true, false);
                }
                g.Freeze();
                dc.DrawGeometry(null, arc, g);
            }

            var ft = new FormattedText(_label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(Theme.FontNum), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                15, Theme.B(Theme.TextHi), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
        }
    }
}
