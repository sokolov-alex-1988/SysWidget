using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace SysWidget
{
    /// <summary>
    /// Тело окна мониторинга: всё видно сразу, без выбора действий. Оформление берётся из Theme.
    /// Сверху слева чек-лист здоровья, справа полоса значений и общий график (все проценты
    /// на одной шкале); ниже ряд карточек с подробностями метрик; внизу события Windows,
    /// журнал тревог и автозагрузка (DashboardHealth.cs).
    /// </summary>
    public sealed partial class Dashboard
    {
        sealed class Kpi
        {
            public Border Root, Line;
            public TextBlock Title, Value;
        }

        sealed class DCard
        {
            public Border Root, Bar;
            public TextBlock Title, Note, Detail;
            public StackPanel CoreHost;
            public CoreBars Cores;
            public string Color = "";
        }

        readonly List<Kpi> _kpis = new List<Kpi>();
        readonly List<DCard> _cards = new List<DCard>();
        MultiChart _chart;
        TextBlock _chartSub;
        FrameworkElement _bottomRow;
        int _density;              // 0 — всё, 1–4 — всё меньше (ApplyDensity)
        readonly double[] _totAt = new double[6];      // полная высота окна, которая была нужна на каждом уровне плотности
        readonly double[] _totW = new double[6];       // ширина окна в момент этого замера
        double _escAvail;          // сколько места было, когда последний раз убрали лишнее
        double _vH;                // высота строки вердикта с полями (запоминаем, пока она видна)

        // цвета рамок блоков в теме «Терминал» (ЦП, память, сеть, процессы)
        const string BoxCpu = "#FF6A9589", BoxMem = "#FF938056", BoxNet = "#FF957FB8", BoxProc = "#FFC34043";
        readonly List<TuiBox> _boxes = new List<TuiBox>();

        /// <summary>
        /// Тема «Терминал»: заголовок блока вшивается в рамку (TuiBox), а обычные TextBlock заголовка прячутся — их тексты
        /// рамка читает сама. В остальных темах ничего не меняется.
        /// </summary>
        void Frame(Border box, string color, TextBlock title, TextBlock right)
        {
            var t = box as TuiBox;
            if (t == null) return;
            t.BoxColor = color;
            t.TitleSource = title;
            t.RightSource = right;
            if (title != null) title.Visibility = Visibility.Collapsed;
            if (right != null) right.Visibility = Visibility.Collapsed;
            _boxes.Add(t);
        }

        /// <summary>Цвет рамки карточки метрики по месту в ряду: ОЗУ и диски — память, ЦП — процессор, ГП — процессы, сеть — сеть.</summary>
        static string BoxColorFor(int index, int count)
        {
            if (index == 1) return BoxCpu;
            if (index == count - 1) return BoxNet;
            if (index == count - 2) return BoxProc;
            return BoxMem;
        }

        /// <summary>Блок окна: заливка, рамка и скругление как у плашек и карточки виджета (Theme), без теней.</summary>
        static Border CardBox(Thickness pad)
        {
            if (Theme.Terminal)
            {
                var tb = new TuiBox();       // рамка с вшитым заголовком; содержимое отодвигает TuiBox сам
                tb.Background = Theme.B(cCard);
                tb.Padding = pad;
                return tb;
            }
            var b = new Border();
            b.Background = Theme.B(cCard);
            b.BorderBrush = Theme.B(cEdge);
            b.BorderThickness = new Thickness(1);
            b.CornerRadius = new CornerRadius(Theme.RadiusCard);
            b.Padding = pad;
            return b;
        }

        void BuildBodyDl()
        {
            if (Theme.Terminal) { BuildBodyTerminal(); return; }      // тема «Терминал» — своя раскладка (DashboardTerminal.cs)
            BuildBodyStandard();
        }

        void BuildBodyStandard()
        {
            _kpis.Clear();
            _cards.Clear();
            _boxes.Clear();
            int n = _w.DashSlots;
            if (_win != null) _win.MinWidth = Math.Min(960, SystemParameters.WorkArea.Width - 20);

            // сетка: верхний ряд забирает лишнюю высоту (при разворачивании на весь экран растёт график),
            // остальные ряды занимают ровно своё
            var host = new Grid();
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.Margin = new Thickness(14, 10, 14, 0);
            _host = host;
            _scroll.Content = _host;

            // --- верхний ряд: слева чек-лист здоровья, справа значения и общий график
            var top = new Grid();
            top.Margin = new Thickness(0, 0, 0, 10);
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MinWidth = 330 });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7, GridUnitType.Star) });

            Border health = BuildHealthCard();
            Grid.SetColumn(health, 0);
            top.Children.Add(health);

            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(right, 2);
            top.Children.Add(right);

            // полоса значений: по ячейке на метрику (она же легенда общего графика)
            var strip = new Grid();
            strip.Margin = new Thickness(0, 0, 0, 12);
            for (int i = 0; i < n; i++)
            {
                // последняя ячейка — сеть: «↓0.0 ↑0.0» шире остальных значений
                strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == n - 1 ? 1.7 : 1, GridUnitType.Star) });
                Kpi k = MakeKpi(i);
                Grid.SetColumn(k.Root, i);
                _kpis.Add(k);
                strip.Children.Add(k.Root);
            }
            Grid.SetRow(strip, 0);
            right.Children.Add(strip);

            // общий график: занимает всю оставшуюся высоту ряда
            Border chartCard = CardBox(new Thickness(14, 10, 14, 8));
            var cg = new Grid();
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            chartCard.Child = cg;

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock ttl = Small(13.5, cAccent);
            ttl.FontWeight = FontWeights.SemiBold;
            ttl.Text = Lang.S("Нагрузка", "Load");
            Grid.SetColumn(ttl, 0);
            head.Children.Add(ttl);
            _chartSub = Small(10, cMid);
            _chartSub.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_chartSub, 1);
            head.Children.Add(_chartSub);
            Grid.SetRow(head, 0);
            if (Theme.Terminal) { Frame(chartCard, BoxCpu, ttl, _chartSub); head.Visibility = Visibility.Collapsed; }
            cg.Children.Add(head);

            _chart = new MultiChart();
            _chart.Height = double.NaN;
            _chart.MinHeight = 140;
            _chart.Margin = new Thickness(0, 8, 0, 0);
            _chart.When = delegate(int back) { return When(back); };
            _chart.Dots = Theme.Terminal;      // «Терминал»: ряды рисуются точками
            Grid.SetRow(_chart, 1);
            cg.Children.Add(_chart);

            Grid.SetRow(chartCard, 1);
            right.Children.Add(chartCard);
            Grid.SetRow(top, 0);
            host.Children.Add(top);

            // --- второй ряд: подробности каждой метрики
            var row = new UniformGrid();
            row.Rows = 1;
            row.Columns = n;
            row.Margin = new Thickness(0, 0, 0, 10);
            for (int i = 0; i < n; i++)
            {
                DCard c = MakeCard(i);
                _cards.Add(c);
                row.Children.Add(c.Root);
            }
            Grid.SetRow(row, 1);
            host.Children.Add(row);

            // --- третий ряд: события, журнал тревог, автозагрузка
            FrameworkElement bottom = BuildBottomRow();
            Grid.SetRow(bottom, 2);
            host.Children.Add(bottom);
            _bottomRow = bottom;

            _evKey = null; _alKey = null; _auKey = null;
            _healthAt = DateTime.MinValue;
            ApplyDensity();
            StartHealthScans();
            ScheduleFit();
        }

        /// <summary>
        /// Плотность окна под размер экрана. Карточки ОЗУ, ЦП, ГП и так далее (ряд подробностей) и
        /// быстрые кнопки в «Состоянии ПК» не скрываются никогда; нижний ряд (события, тревоги,
        /// автозагрузка) уходит последним. При нехватке места убирается по порядку:
        /// 1 — строка вердикта; 2 — шапка чек-листа (кольцо) и график ниже; 3 — подробный текст
        /// в карточках; 4 — нижний ряд. Блоки верхнего ряда и так сжаты по высоте: верхний ряд
        /// задаёт высоту всему окну, поэтому каждый лишний пиксель в «Состоянии ПК» отнимает место
        /// у нижнего ряда.
        /// Уровень следит за местом постоянно: содержимое не помещается (маленький экран, окно
        /// уменьшили) — шаг вверх, места стало больше (развернули, перенесли на большой монитор) —
        /// шаг вниз. Если и на последнем уровне тесно, остаётся прокрутка.
        /// </summary>
        void ApplyDensity()
        {
            if (_vBox != null) _vBox.Visibility = _density >= 1 ? Visibility.Collapsed : Visibility.Visible;
            if (_healthHdr != null) _healthHdr.Visibility = _density >= 2 ? Visibility.Collapsed : Visibility.Visible;
            if (_chart != null) _chart.MinHeight = _density >= 2 ? 100 : 140;
            foreach (DCard c in _cards) c.Detail.Visibility = _density >= 3 ? Visibility.Collapsed : Visibility.Visible;
            if (_bottomRow != null) _bottomRow.Visibility = _density >= 4 ? Visibility.Collapsed : Visibility.Visible;
        }

        Kpi MakeKpi(int index)
        {
            var k = new Kpi();
            k.Root = new Border();
            k.Root.Margin = new Thickness(index == 0 ? 0 : 12, 0, 0, 0);
            k.Root.Padding = new Thickness(2, 2, 2, 6);

            var sp = new StackPanel();
            k.Title = Small(10.5, cMid);
            k.Title.TextTrimming = TextTrimming.CharacterEllipsis;
            k.Value = Small(26, cHi);
            k.Value.FontFamily = new FontFamily(Theme.FontNum);
            k.Value.FontWeight = FontWeights.SemiBold;
            Typography.SetNumeralAlignment(k.Value, FontNumeralAlignment.Tabular);
            k.Line = new Border();
            k.Line.Height = 2;
            k.Line.CornerRadius = new CornerRadius(Theme.BarRadius);
            k.Line.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(k.Title);
            sp.Children.Add(k.Value);
            sp.Children.Add(k.Line);
            k.Root.Child = sp;
            return k;
        }

        DCard MakeCard(int index)
        {
            var c = new DCard();
            c.Root = CardBox(new Thickness(12, 10, 12, 8));
            c.Root.Margin = new Thickness(index == 0 ? 0 : 12, 0, 0, 0);

            var shell = new Grid();
            c.Root.Child = shell;
            var col = new StackPanel();
            shell.Children.Add(col);

            // «язычок» на верхней кромке: светящаяся полоска цвета метрики
            c.Bar = new Border();
            c.Bar.Width = 36;
            c.Bar.Height = 4;
            c.Bar.CornerRadius = new CornerRadius(Theme.BarRadius);
            c.Bar.HorizontalAlignment = HorizontalAlignment.Center;
            c.Bar.VerticalAlignment = VerticalAlignment.Top;
            c.Bar.Margin = new Thickness(0, -14, 0, 0);
            shell.Children.Add(c.Bar);

            c.Title = Small(12.5, cAccent);
            c.Title.FontWeight = FontWeights.SemiBold;
            c.Note = Small(9.5, cMid);
            c.Note.TextWrapping = TextWrapping.Wrap;
            c.Note.Margin = new Thickness(0, 2, 0, 0);
            if (Theme.Terminal)
            {
                Frame(c.Root, BoxColorFor(index, _w.DashSlots), c.Title, null);      // название — в рамке, язычок не нужен
                c.Bar.Visibility = Visibility.Collapsed;
            }
            c.CoreHost = new StackPanel();
            c.CoreHost.Margin = new Thickness(0, 6, 0, 0);
            c.Detail = Small(10.5, cLabel);
            c.Detail.TextWrapping = TextWrapping.Wrap;
            c.Detail.LineHeight = Theme.Fs(10.5) * 1.35;
            c.Detail.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            c.Detail.Margin = new Thickness(0, 6, 0, 0);
            col.Children.Add(c.Title);
            col.Children.Add(c.Note);
            col.Children.Add(c.CoreHost);
            col.Children.Add(c.Detail);
            return c;
        }

        /// <summary>Подписи оси времени общего графика: пять равных отрезков периода.</summary>
        string[] AxisLabelsDl()
        {
            switch (_period)
            {
                case 0: return new[] { Lang.S("−2 мин", "−2 min"), Lang.S("−90 с", "−90 s"), Lang.S("−1 мин", "−1 min"), Lang.S("−30 с", "−30 s"), Lang.S("сейчас", "now") };
                case 1: return new[] { Lang.S("−15 мин", "−15 min"), Lang.S("−11 мин", "−11 min"), Lang.S("−8 мин", "−8 min"), Lang.S("−4 мин", "−4 min"), Lang.S("сейчас", "now") };
                case 2: return new[] { Lang.S("−1 ч", "−1 h"), Lang.S("−45 мин", "−45 min"), Lang.S("−30 мин", "−30 min"), Lang.S("−15 мин", "−15 min"), Lang.S("сейчас", "now") };
                default: return new[] { Lang.S("−24 ч", "−24 h"), Lang.S("−18 ч", "−18 h"), Lang.S("−12 ч", "−12 h"), Lang.S("−6 ч", "−6 h"), Lang.S("сейчас", "now") };
            }
        }

        void RefreshDl(SysStats s)
        {
            if (Theme.Terminal) { RefreshTerminal(s); return; }
            RefreshStandard(s);
        }

        void RefreshStandard(SysStats s)
        {
            int n = _w.DashSlots;
            if (_kpis.Count != n || _chart == null) BuildBodyDl();      // накопитель подключили или отключили

            var vals = new List<double[]>();
            var names = new List<string>();
            var colors = new List<string>();
            for (int i = 0; i < _kpis.Count; i++)
            {
                DashTileData d = _w.DashTile(i, s, _period);

                Kpi k = _kpis[i];
                SetText(k.Title, d.Title);
                SetText(k.Value, d.Value);
                string col = SeverityColor(d.Pct, d.Color);
                if ((k.Value.Tag as string) != col) { k.Value.Tag = col; k.Value.Foreground = Theme.B(col); }
                if ((k.Line.Tag as string) != d.Color) { k.Line.Tag = d.Color; k.Line.Background = Theme.B(Theme.Fade(d.Color, 0xB0)); }

                DCard c = _cards[i];
                SetText(c.Title, d.Title);
                SetText(c.Note, d.Note);
                c.Note.Visibility = d.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                SetDetail(c.Detail, d.Detail);
                if (c.Color != d.Color)
                {
                    c.Color = d.Color;
                    c.Bar.Background = Theme.B(d.Color);
                }
                if (d.Cores != null && d.Cores.Length > 0)
                {
                    if (c.Cores == null)
                    {
                        c.Cores = new CoreBars(d.Color);
                        c.Cores.Height = 14;
                        c.CoreHost.Children.Add(c.Cores);
                    }
                    c.Cores.Set(d.Cores);
                }

                // на общем графике — только проценты; сеть в МБ/с остаётся в полосе и в своей карточке
                if (!d.Net) { vals.Add(d.Series); names.Add(d.Title); colors.Add(d.Color); }
            }

            _chart.Points = Points;
            _chart.XLabels = AxisLabelsDl();
            _chart.Set(vals, names, colors);
            SetText(_chartSub, _chipTexts[_period].Text + "  ·  " + string.Join(", ", names.ToArray()));

            foreach (TuiBox bx in _boxes) bx.InvalidateVisual();      // «Терминал»: заголовки в рамках могли измениться
            UpdateHealth();
            UpdateEvents();
            UpdateAlertLog();
            UpdateAutostartList();
            UpdateBell();
            UpdateVerdict(s);
            UpdateMinHeight();
        }
    }
}
