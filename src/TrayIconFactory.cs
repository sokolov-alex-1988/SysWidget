using System;
using Drawing = System.Drawing;

namespace SysWidget
{
    /// <summary>
    /// Значок в трее — «цифровой дождь»: четыре потока пиксельных капель на
    /// тёмной скруглённой плитке. Не четыре столбика: шкалы уже есть на самой
    /// карточке, и дублировать их в трее незачем.
    ///
    /// Плитка решает две задачи: даёт силуэт (иначе в трее просто полоски
    /// непонятных границ) и держит значок читаемым на светлой теме.
    ///
    /// Кадр задаётся снаружи (frame): значок живой, потоки падают с разной
    /// скоростью. Цвет несёт смысл: обычно дождь мятно-бирюзовый (цвета метрик
    /// с карточки), при загрузке любой метрики от 90 % — красный.
    ///
    /// Кадр -1 — «остановленный дождь» (анимация выключена): капли не висят
    /// где попало, а все дошли до низа — головы встали на нижнюю границу
    /// плитки, хвосты заполняют столбцы снизу вверх.
    /// </summary>
    public static class TrayIconFactory
    {
        const double WarnAt = 90;

        /// <summary>
        /// Пора краснеть: хотя бы одна метрика дошла до 90 %. Считаем в одном
        /// месте, чтобы значок и его перерисовка не разошлись в трактовке.
        /// </summary>
        public static bool Warn(double cpu, double ram, double ssd, double gpu)
        {
            // тревога общая на весь значок: дождь один, и краснеть ему целиком
            return Hit(cpu) || Hit(ram) || Hit(ssd) || Hit(gpu);
        }

        public static Drawing.Icon Create(double cpu, double ram, double ssd, double gpu, int frame)
        {
            bool warn = Warn(cpu, ram, ssd, gpu);
            bool settled = frame < 0;   // дождь остановлен: всё лежит на дне

            using (var bmp = new Drawing.Bitmap(32, 32, Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(Drawing.Color.Transparent);
                    g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    // подложка: скруглённый квадрат 32x32, заливка как у карточки
                    using (var path = new Drawing.Drawing2D.GraphicsPath())
                    {
                        const int r = 6;
                        path.AddArc(0, 0, 2 * r, 2 * r, 180, 90);
                        path.AddArc(31 - 2 * r, 0, 2 * r, 2 * r, 270, 90);
                        path.AddArc(31 - 2 * r, 31 - 2 * r, 2 * r, 2 * r, 0, 90);
                        path.AddArc(0, 31 - 2 * r, 2 * r, 2 * r, 90, 90);
                        path.CloseFigure();
                        using (var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(235, Theme.C(Theme.TrayBg))))
                        {
                            g.FillPath(bg, path);
                        }
                        using (var pen = new Drawing.Pen(Drawing.Color.FromArgb(190, Theme.C(Theme.TrayStroke)), 2f))
                        {
                            g.DrawPath(pen, path);
                        }
                    }

                    // капли — прямоугольники по целым пикселям, поэтому сглаживание
                    // выключаем: иначе на 16 px края размазываются в кашу
                    g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.None;

                    Drawing.Color head, body;
                    if (warn)
                    {
                        head = Drawing.Color.FromArgb(0xFF, 0xD9, 0xD9);
                        body = Drawing.Color.FromArgb(0xFF, 0x6B, 0x6B);
                    }
                    else
                    {
                        head = Theme.C(Theme.TrayHead);
                        body = Theme.C(Theme.TrayBody);
                    }

                    // Немая подложка: редкие приглушённые капли на постоянных
                    // местах. Без них между проходами потоков значок пустел и
                    // мигал «ничего нет» — дождь должен идти постоянно.
                    using (var br = new Drawing.SolidBrush(Drawing.Color.FromArgb(45, body)))
                    {
                        g.FillRectangle(br, 6, 8, 2, 2);
                        g.FillRectangle(br, 13, 21, 2, 2);
                        g.FillRectangle(br, 21, 6, 2, 2);
                        g.FillRectangle(br, 27, 18, 2, 2);
                        g.FillRectangle(br, 9, 24, 2, 2);
                        g.FillRectangle(br, 24, 11, 2, 2);
                    }

                    // столбцы дождя: x, скорость в пикселях за кадр, длина хвоста
                    // и стартовый сдвиг — чтобы потоки шли не в ногу
                    int[] xs = { 4, 11, 18, 25 };
                    double[] spd = { 2.0, 2.8, 1.5, 2.4 };
                    int[] len = { 5, 6, 4, 5 };
                    double[] off = { 0, 10, 20, 30 };

                    const double travel = 32;   // круг: от -8 до 24 — так поток почти всегда в кадре
                    const int block = 3;        // высота капли
                    const int pitch = 4;        // шаг: 3 px капля + 1 px просвет
                    const int bottomY = 27;     // нижняя строка капель: 27..29 — вплотную к обводке
                    const int settledLen = 7;   // остановленный дождь: голова снизу, хвост до верха плитки

                    for (int c = 0; c < xs.Length; c++)
                    {
                        // стоящий дождь одинаков у всех столбцов: поток «долил» до низа
                        int lenC = settled ? settledLen : len[c];
                        double headY = settled
                            ? bottomY
                            : ((frame * spd[c] + off[c]) % travel) - 8;

                        for (int k = 0; k < lenC; k++)
                        {
                            int y = (int)Math.Round(headY - k * pitch);
                            if (y < 3 || y > 27) continue;   // за плиткой не рисуем

                            Drawing.Color col;
                            if (k == 0) col = head;
                            else
                            {
                                // у стоящего дождя хвост гаснет плавнее — он должен
                                // заполнить столбец, а не раствориться на середине
                                int a = Math.Max(settled ? 70 : 55,
                                                 235 - k * (settled ? 30 : 40));
                                col = Drawing.Color.FromArgb(a, body);
                            }
                            using (var br = new Drawing.SolidBrush(col))
                            {
                                g.FillRectangle(br, xs[c], y, block, block);
                            }
                        }
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    // Clone — независимая копия: исходный HICON после этого можно
                    // уничтожить (иначе GDI-объекты утекали бы)
                    using (var tmp = Drawing.Icon.FromHandle(hIcon))
                    {
                        return (Drawing.Icon)tmp.Clone();
                    }
                }
                finally
                {
                    Native.DestroyIcon(hIcon);
                }
            }
        }

        static bool Hit(double p)
        {
            return !double.IsNaN(p) && p >= WarnAt;
        }
    }
}
