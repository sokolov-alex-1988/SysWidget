using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SysWidget
{
    /// <summary>
    /// Тёмное меню в стиле карточки. Стандартный ContextMenuStrip светлый и с
    /// серыми градиентами — рядом с виджетом выглядел чужеродно. Красим всё в
    /// цвета Theme: фон, рамку, разделители и подсветку наведения.
    /// </summary>
    public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        /// <summary>
        /// Галочка в акцентном цвете. Системная на почти чёрном фоне почти не
        /// видна, а «включено» должно читаться так же ясно, как чипы карточки.
        /// </summary>
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            if (r.Width < 6 || r.Height < 6)
            {
                base.OnRenderItemCheck(e);
                return;
            }

            Graphics g = e.Graphics;
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                using (var pen = new Pen(Theme.C(Theme.NetColor), 1.8f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    float x = r.Left, y = r.Top, w = r.Width, h = r.Height;
                    g.DrawLines(pen, new[]
                    {
                        new PointF(x + w * 0.18f, y + h * 0.52f),
                        new PointF(x + w * 0.42f, y + h * 0.76f),
                        new PointF(x + w * 0.84f, y + h * 0.24f)
                    });
                }
            }
            finally
            {
                g.SmoothingMode = old;
            }
        }

        /// <summary>
        /// Подпись чуть правее. Колонка под галочку слева оказывается на
        /// несколько пикселей уже правого поля, и у самого длинного пункта
        /// отступы слева и справа получаются разными; сдвиг на 2 px их
        /// уравнивает.
        /// </summary>
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Rectangle r = e.TextRectangle;
            // левый край подписи нужен разделителям: у них такой же отступ
            _textLeft = e.Item.Bounds.Left + r.Left;
            r.X += 2;
            e.TextRectangle = r;
            base.OnRenderItemText(e);
        }

        int _textLeft = -1;

        /// <summary>
        /// Разделитель с одинаковыми отступами с обеих сторон. Базовая линия
        /// начинается после колонки галочек, но доходила до самого правого
        /// края — справа отступ пропадал. Рисуем от подписи и зеркально
        /// обрываем справа.
        /// </summary>
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            if (e.Vertical || _textLeft < 0)
            {
                base.OnRenderSeparator(e);
                return;
            }

            int left = _textLeft;
            Rectangle b = e.Item.Bounds;
            // координаты — от угла пункта, в него же обрезана отрисовка
            int x1 = left - b.Left;
            int x2 = e.ToolStrip.ClientSize.Width - 1 - left - b.Left;
            int y = b.Height / 2;

            using (var pen = new Pen(Theme.C(Theme.Stroke), 1f))
                e.Graphics.DrawLine(pen, x1, y, x2, y);
        }

        sealed class DarkColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.C(Theme.Surface); } }
            public override Color ImageMarginGradientBegin { get { return Theme.C(Theme.Surface); } }
            public override Color ImageMarginGradientMiddle { get { return Theme.C(Theme.Surface); } }
            public override Color ImageMarginGradientEnd { get { return Theme.C(Theme.Surface); } }
            public override Color MenuBorder { get { return Theme.C(Theme.Stroke); } }
            public override Color MenuItemBorder { get { return Theme.C(Theme.SurfaceHover); } }
            public override Color MenuItemSelected { get { return Theme.C(Theme.SurfaceHover); } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.C(Theme.SurfaceHover); } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.C(Theme.SurfaceHover); } }
            public override Color SeparatorDark { get { return Theme.C(Theme.Stroke); } }
            public override Color SeparatorLight { get { return Theme.C(Theme.Surface); } }
        }
    }
}
