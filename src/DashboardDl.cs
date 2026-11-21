using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SysWidget
{
    /// <summary>
    /// Шапка окна мониторинга: периоды графика по центру, часы и колокольчик оповещений справа
    /// (здесь); под ней — всё сразу, без выбора действий (DashboardBody.cs). Оформление берётся
    /// из Theme, поэтому окно одинаково выглядит в неоне, тёмной и светлой темах.
    /// </summary>
    public sealed partial class Dashboard
    {
        DispatcherTimer _clockTimer;
        TextBlock _clock, _date, _bellText;
        Border _bell;

        // ------------------------------------------------------------ шапка

        FrameworkElement BuildHeaderDl()
        {
            var grid = new Grid();
            grid.Background = Brushes.Transparent;      // чтобы принимать мышь по всей полосе
            grid.Margin = new Thickness(18, 12, 14, 10);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _head = grid;

            _title = new TextBlock();
            _title.Text = Lang.S("Мониторинг", "Monitor");
            _title.FontFamily = new FontFamily(Theme.FontUi);
            _title.FontSize = Theme.Fs(13);
            _title.FontWeight = FontWeights.SemiBold;
            _title.Foreground = Theme.B(cAccent);
            _title.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_title, 0);
            grid.Children.Add(_title);

            // периоды графика — по центру шапки
            var chips = new StackPanel();
            chips.Orientation = Orientation.Horizontal;
            chips.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(chips, 1);
            string[] labels = { Lang.S("2 мин", "2 min"), Lang.S("15 мин", "15 min"), Lang.S("1 ч", "1 h"), Lang.S("24 ч", "24 h") };
            for (int i = 0; i < 4; i++)
            {
                int period = i;
                _chips[i] = Chip(labels[i], delegate { SetPeriod(period); }, out _chipTexts[i]);
                chips.Children.Add(_chips[i]);
            }
            grid.Children.Add(chips);

            // справа: колокольчик, время и дата, закрыть
            var right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(right, 2);

            right.Children.Add(MakeBell());

            var clockBox = new StackPanel();
            clockBox.Margin = new Thickness(12, 0, 6, 0);
            clockBox.VerticalAlignment = VerticalAlignment.Center;
            _clock = Small(15, cHi);
            _clock.FontFamily = new FontFamily(Theme.FontNum);
            _clock.FontWeight = FontWeights.SemiBold;
            _clock.HorizontalAlignment = HorizontalAlignment.Right;
            Typography.SetNumeralAlignment(_clock, FontNumeralAlignment.Tabular);
            _date = Small(9, cMid);
            _date.HorizontalAlignment = HorizontalAlignment.Right;
            clockBox.Children.Add(_clock);
            clockBox.Children.Add(_date);
            right.Children.Add(clockBox);

            TextBlock closeText;
            Border close = Chip("✕", delegate { Hide(); }, out closeText);
            close.Margin = new Thickness(8, 0, 0, 0);
            closeText.FontSize = Theme.Fs(11);
            right.Children.Add(close);
            grid.Children.Add(right);

            StartClock();

            // окно без рамки — тащим за свободное место полосы
            grid.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ClickCount > 1)
                {
                    if (!ReferenceEquals(e.OriginalSource, grid) && !ReferenceEquals(e.OriginalSource, _title)) return;
                    _win.WindowState = _win.WindowState == WindowState.Maximized
                        ? WindowState.Normal : WindowState.Maximized;
                    return;
                }
                if (!ReferenceEquals(e.OriginalSource, grid) && !ReferenceEquals(e.OriginalSource, _title)
                    && !ReferenceEquals(e.OriginalSource, clockBox) && !ReferenceEquals(e.OriginalSource, _clock)
                    && !ReferenceEquals(e.OriginalSource, _date)) return;
                try { _win.DragMove(); } catch { }
            };
            return grid;
        }

        void StartClock()
        {
            StopClock();
            _clockTimer = new DispatcherTimer();
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += delegate { UpdateClock(); };
            UpdateClock();
            _clockTimer.Start();
        }

        void StopClock()
        {
            if (_clockTimer != null) { _clockTimer.Stop(); _clockTimer = null; }
        }

        void UpdateClock()
        {
            if (_clock == null) return;
            DateTime now = DateTime.Now;
            SetText(_clock, now.ToString("HH:mm:ss", CultureInfo.CurrentCulture));
            SetText(_date, now.ToString("d MMMM, dddd", CultureInfo.CurrentCulture));
        }

        /// <summary>Колокольчик: включает и выключает оповещения (тот же переключатель, что в меню трея).</summary>
        Border MakeBell()
        {
            _bellText = new TextBlock();
            _bellText.FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol");
            _bellText.FontSize = Theme.Fs(12.5);
            _bellText.VerticalAlignment = VerticalAlignment.Center;

            _bell = new Border();
            _bell.CornerRadius = new CornerRadius(Theme.RadiusPill);
            _bell.Padding = new Thickness(8, 3, 8, 3);
            _bell.Background = Brushes.Transparent;
            _bell.Cursor = Cursors.Hand;
            _bell.VerticalAlignment = VerticalAlignment.Center;
            _bell.Child = _bellText;
            _bell.MouseEnter += delegate { _bell.Background = Theme.B(Theme.BtnHover); };
            _bell.MouseLeave += delegate { _bell.Background = Brushes.Transparent; };
            _bell.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                _w.AlertsEnabled = !_w.AlertsEnabled;
                UpdateBell();
            };
            UpdateBell();
            return _bell;
        }

        void UpdateBell()
        {
            if (_bellText == null) return;
            bool on = _w.AlertsEnabled;
            string text = on ? "\U0001F514" : "\U0001F515";
            if (_bellText.Text != text)
            {
                _bellText.Text = text;
                _bellText.Foreground = Theme.B(on ? cAccent : cMid);
                _bell.ToolTip = on
                    ? Lang.S("Оповещения включены — нажмите, чтобы выключить", "Alerts are on — click to turn off")
                    : Lang.S("Оповещения выключены — нажмите, чтобы включить", "Alerts are off — click to turn on");
            }
        }

    }
}
