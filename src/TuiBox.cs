using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SysWidget
{
    /// <summary>
    /// Блок окна мониторинга в теме «Терминал»: тонкая цветная рамка с закруглёнными углами, а заголовок вшит в верхнюю
    /// линию («╭─ cpu ──── 3.6 GHz ─╮»): слева название, справа поясняющий текст. У названия первая буква красная
    /// (как «горячая клавиша» у консольных программ), остальные светлые. Тексты берутся из обычных TextBlock, которые лежат вне
    /// дерева: так заголовок меняется вместе с данными, а код блоков не знает о теме. Перерисовку при смене текста
    /// запрашивает окно раз в секунду.
    /// </summary>
    public sealed class TuiBox : Border
    {
        const double TitleRow = 13;     // высота строки заголовка над содержимым
        const double Radius = 4;

        public TextBlock TitleSource, RightSource;
        public string BoxColor = "#FF556D59";

        public TuiBox()
        {
            // рамку рисуем сами; толщина лишь отодвигает содержимое от края и от строки заголовка
            BorderThickness = new Thickness(2, TitleRow, 2, 2);
        }

        FormattedText Text(string s, double size)
        {
            return new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(Theme.FontUi), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                Theme.Fs(size), Theme.B(Theme.TextHi), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);       // заливка фона
            double w = ActualWidth, h = ActualHeight;
            if (w < 16 || h < 16) return;

            var pen = new Pen(Theme.B(BoxColor), 1);
            double x0 = 0.5, x1 = Math.Round(w) - 0.5, y0 = Math.Round(TitleRow / 2) + 0.5, y1 = Math.Round(h) - 0.5;

            string left = TitleSource != null ? (TitleSource.Text ?? "") : "";
            string right = RightSource != null ? (RightSource.Text ?? "") : "";
            FormattedText lt = null, rt = null;
            double lx = 0, rx = 0;
            if (left.Length > 0)
            {
                lt = Text(left, 10.5);
                lt.SetForegroundBrush(Theme.B(Theme.HotColor), 0, 1);       // первая буква — красная
                lx = x0 + Radius + 8;
            }
            if (right.Length > 0)
            {
                rt = Text(right, 10);
                rt.SetForegroundBrush(Theme.B(Theme.TextMid));
                rx = x1 - Radius - 8 - rt.Width;
                if (lt != null && rx < lx + lt.Width + 16) rt = null;       // справа не помещается рядом с названием
            }

            // верхняя линия: разрывы под заголовками
            double a = x0 + Radius, b = x1 - Radius;
            if (lt != null) { DrawH(dc, pen, y0, a, lx - 4); a = lx + lt.Width + 4; }
            if (rt != null) { DrawH(dc, pen, y0, a, rx - 4); a = rx + rt.Width + 4; }
            DrawH(dc, pen, y0, a, b);

            dc.DrawLine(pen, new Point(x0, y0 + Radius), new Point(x0, y1 - Radius));         // левая
            dc.DrawLine(pen, new Point(x1, y0 + Radius), new Point(x1, y1 - Radius));         // правая
            dc.DrawLine(pen, new Point(x0 + Radius, y1), new Point(x1 - Radius, y1));         // нижняя

            Corner(dc, pen, new Point(x0, y0 + Radius), new Point(x0 + Radius, y0));          // углы дугами
            Corner(dc, pen, new Point(x1 - Radius, y0), new Point(x1, y0 + Radius));
            Corner(dc, pen, new Point(x1, y1 - Radius), new Point(x1 - Radius, y1));
            Corner(dc, pen, new Point(x0 + Radius, y1), new Point(x0, y1 - Radius));

            if (lt != null) dc.DrawText(lt, new Point(lx, y0 - lt.Height / 2));
            if (rt != null) dc.DrawText(rt, new Point(rx, y0 - rt.Height / 2));
        }

        static void DrawH(DrawingContext dc, Pen pen, double y, double xa, double xb)
        {
            if (xb > xa) dc.DrawLine(pen, new Point(xa, y), new Point(xb, y));
        }

        static void Corner(DrawingContext dc, Pen pen, Point from, Point to)
        {
            var g = new StreamGeometry();
            using (StreamGeometryContext c = g.Open())
            {
                c.BeginFigure(from, false, false);
                c.ArcTo(to, new Size(Radius, Radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
    }
}
