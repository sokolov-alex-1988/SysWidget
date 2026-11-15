using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace SysWidget
{
    /// <summary>
    /// Точка входа: единственный экземпляр, значок в трее, меню и таймеры.
    /// </summary>
    public class Program
    {
        // dev.flag рядом с exe — песочница: свои имена мьютекса и события, чтобы копия
        // для разработки жила рядом с рабочей и не принимала её за «второй запуск».
        static readonly string Sfx = File.Exists(Path.Combine(AppPaths.AppDir, "dev.flag")) ? ".Dev" : "";
        static readonly string MutexName = @"Local\SysWidget.Desktop.SingleInstance" + Sfx;
        static readonly string ShowEventName = @"Local\SysWidget.Desktop.Show" + Sfx;
        const string RestartArg = "--restart";

        /// <summary>Описание для ярлыка — по текущему языку.</summary>
        static string Desc
        {
            get { return Lang.S("Виджет загрузки ОЗУ / ЦП / дисков", "RAM / CPU / disk usage widget"); }
        }

        static readonly IFormatProvider Inv = CultureInfo.InvariantCulture;

        Mutex _mutex;
        EventWaitHandle _showEvent;

        Sampler _sampler;
        Widget _widget;
        SettingsWindow _settings;
        PeripheralService _periphSvc;

        WinForms.NotifyIcon _ni;
        WinForms.ContextMenuStrip _menu;
        WinForms.ToolStripMenuItem _miToggle, _miClean, _miAlerts, _miPeriph, _miTop, _miAuto, _miDash, _miSettings, _miExit;

        DispatcherTimer _timer, _zTimer, _menuWatch;

        System.Drawing.Icon _trayIcon;
        string _trayKey = "";
        // последние значения — по ним рисуется каждый кадр дождя
        double _trayCpu, _trayRam, _traySsd, _trayGpu = -1;
        int _trayFrame;
        bool _trayWarn;   // цвет, который сейчас стоит в трее: красный / обычный
        DispatcherTimer _trayAnim;

        string _exePath, _iconPath, _startupLnk, _startLnk;

        [STAThread]
        public static void Main(string[] args)
        {
            // режим помощника с правами администратора (запускается из настроек по кнопке, см. AdminHelper):
            // без окон, трея и единственного экземпляра — только ответы по именованному каналу
            if (args != null && args.Length > 0 && args[0] == "--helper")
            {
                try { ErrorLog.Init(AppPaths.DataDir); } catch { }
                Environment.Exit(AdminHelper.RunServer(args));
                return;
            }

            try
            {
                new Program().Run();
            }
            catch (Exception ex)
            {
                // Сбой до создания окна (например, не открылся PDH): писать
                // некому, поэтому журнал идёт рядом с exe, а пользователь
                // видит хотя бы сообщение, а не молчаливый исчезающий процесс.
                ErrorLog.Init(AppPaths.DataDir);
                ErrorLog.Write("startup", ex);
                try
                {
                    WinForms.MessageBox.Show(
                        Lang.S("Виджет не смог запуститься. Подробности — в журнале error.log:\n",
                               "The widget could not start. Details are in the error.log file:\n")
                        + (ErrorLog.Path ?? AppPaths.DataDir),
                        "SysWidget", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
                }
                catch { }
            }
        }

        void Run()
        {
            WinForms.Application.EnableVisualStyles();

            ErrorLog.Init(AppPaths.DataDir);

            // Исключение в фоновом потоке (замеры железа, очистка памяти)
            // нельзя перехватить: процесс всё равно завершится, но причина
            // останется в журнале.
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                var ex = e.ExceptionObject as Exception;
                ErrorLog.Write("appdomain", ex ?? new Exception(Convert.ToString(e.ExceptionObject)));
            };

            // ---------- единственный экземпляр ----------
            _mutex = new Mutex(false, MutexName);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

            // После «Применить и перезапустить» старый экземпляр ещё закрывается —
            // новый ждёт освобождения мьютекса, а не принимает его за второй запуск.
            bool restarted = Array.IndexOf(Environment.GetCommandLineArgs(), RestartArg) >= 0;
            bool owned;
            try { owned = _mutex.WaitOne(restarted ? 15000 : 0); }
            catch (AbandonedMutexException) { owned = true; }

            if (!owned)
            {
                // виджет уже запущен (возможно, свёрнут в трей) — просим показаться
                try { _showEvent.Set(); } catch { }
                return;
            }

            _exePath = Assembly.GetExecutingAssembly().Location;
            _iconPath = Path.Combine(AppPaths.AppDir, "icon.ico");
            string stateFile = AppPaths.File_("state.json");

            // язык нужен раньше всего: от него зависят имя ярлыка «Пуск» и
            // описание — они создаются ниже
            Lang.Load(stateFile);

            // настройки вида и палитра — до создания окон, меню и значка: они читают Theme.
            // Тему по умолчанию берём из старого поля state.json, пока prefs.json не создан.
            Prefs.Load(AppPaths.File_("prefs.json"), Theme.NeonFromState(stateFile));
            Theme.Apply(Prefs.Neon, Prefs.Light);
            Theme.ApplyPrefs();

            // ---------- ярлыки ----------
            // Windows связывает окно с приложением только если существует ярлык с
            // тем же AppUserModelID. Без этого у окна нет своей кнопки на панели задач.
            try { Native.SetCurrentProcessExplicitAppUserModelID(Shortcuts.AppId); } catch { }

            _startLnk = StartLnkPath();
            Shortcuts.Create(_startLnk, _exePath, _iconPath, Desc);

            // автозапуск: если ярлык уже был (от старой PowerShell-версии) —
            // перенацеливаем его на exe
            _startupLnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup), "SysWidget.lnk");
            if (File.Exists(_startupLnk)) Shortcuts.Create(_startupLnk, _exePath, _iconPath, Desc);

            _sampler = new Sampler();
            _sampler.Start();
            MemoryWatch.Start();

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnLastWindowClose;
            app.DispatcherUnhandledException += OnDispatcherException;

            _widget = new Widget(_sampler, this);
            _widget.MenuRequested += ShowWidgetMenu;
            _widget.VisibilityChanged += delegate { SyncToggleText(); };
            _widget.SettingsRequested += OpenSettings;

            // Периферия: обход устройств и чтение заряда идут в своём потоке,
            // на UI приходит готовый список — карточка рисует его строками,
            // как ОЗУ и диски.
            _periphSvc = new PeripheralService();
            _periphSvc.Updated += delegate(List<Peripheral> list)
            {
                System.Windows.Threading.Dispatcher d = _widget.Win.Dispatcher;
                if (d == null) return;
                d.BeginInvoke(DispatcherPriority.Background, (Action)delegate
                {
                    _widget.SetPeripherals(list);
                });
            };
            _widget.DeviceChanged += delegate { _periphSvc.RequestScan(); };
            _periphSvc.Start();

            BuildTray();

            _widget.Win.Closed += OnClosed;
            _widget.Win.Show();
            SyncToggleText();       // первый Show не поднимает VisibilityChanged

            // ---------- таймеры ----------
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(1000);
            _timer.Tick += delegate
            {
                Guard("tick", delegate
                {
                    // повторный запуск ярлыка: показываем свёрнутый в трей виджет
                    if (_showEvent.WaitOne(0))
                    {
                        if (_widget.IsVisible) _widget.Win.Activate();
                        else _widget.Toggle();
                    }
                    _widget.Update();
                });
            };
            _timer.Start();

            // Отдельный быстрый сторож z-order: панель задач поднимается над
            // карточкой сразу после клика, а раз в секунду возврат «мигал» бы.
            _zTimer = new DispatcherTimer();
            _zTimer.Interval = TimeSpan.FromMilliseconds(100);
            _zTimer.Tick += delegate
            {
                Guard("z-order", delegate
                {
                    _widget.SyncTaskbarOwner();
                    _widget.KeepAboveTaskbar();
                });
            };
            _zTimer.Start();

            // Меню-сторож. WinForms закрывает ContextMenuStrip по клику мимо через
            // свой фильтр сообщений, а он работает только в цикле Application.Run();
            // у нас цикл WPF, поэтому меню «залипало» и не реагировало на клики.
            _menuWatch = new DispatcherTimer();
            _menuWatch.Interval = TimeSpan.FromMilliseconds(100);
            _menuWatch.Tick += delegate
            {
                Guard("menu", delegate
                {
                    if (!_menu.Visible) return;
                    if (WinForms.Control.MouseButtons == WinForms.MouseButtons.None) return;
                    var p = WinForms.Cursor.Position;
                    if (!_menu.Bounds.Contains(p)) _menu.Close();
                });
            };
            _menuWatch.Start();

            app.Run();
        }

        // ------------------------------------------------------------- трей

        void BuildTray()
        {
            _ni = new WinForms.NotifyIcon();
            // подсказка значка — имя виджета, а не проценты: метрики и так
            // видны на карточке, а цифры в трее дёргались каждую секунду
            _ni.Text = "SysWidget";

            // при выключенной анимации сразу рисуем «стоячий» дождь, а не первый кадр
            try
            {
                _trayIcon = TrayIconFactory.Create(0, 0, 0, -1, _widget.TrayAnim ? 0 : -1);
                _ni.Icon = _trayIcon;
            }
            catch { try { _ni.Icon = new System.Drawing.Icon(_iconPath); } catch { } }

            // Дождь в значке идёт кадрами ~9 раз в секунду: 32x32 рисуется за
            // доли миллисекунды, а значок перестаёт выглядеть наклейкой. Если
            // машина слабая, анимацию выключают в настройках — тогда значок
            // статичный и трей не перерисовывается вовсе.
            _trayAnim = new DispatcherTimer();
            _trayAnim.Interval = TimeSpan.FromMilliseconds(110);
            _trayAnim.Tick += delegate
            {
                _trayFrame++;
                DrawTrayIcon();
            };
            if (_widget.TrayAnim) _trayAnim.Start();

            _menu = new WinForms.ContextMenuStrip();
            _menu.ShowImageMargin = false;
            // Колонка под галочки нужна всем пунктам сразу: иначе при включении
            // отметки подпись сдвигается вправо, а при выключении — обратно.
            _menu.ShowCheckMargin = true;
            // меню — часть приложения: те же фон, текст и шрифт, что у карточки
            _menu.BackColor = Theme.C(Theme.Surface);
            _menu.ForeColor = Theme.C(Theme.TextHi);
            _menu.Font = new System.Drawing.Font(Theme.FontMenu, 9f);
            _menu.Renderer = new DarkMenuRenderer();
            _menu.DropShadowEnabled = false;
            _menu.Padding = new WinForms.Padding(3, 4, 3, 4);

            _miToggle = new WinForms.ToolStripMenuItem();
            _miToggle.Click += delegate { _widget.Toggle(); SyncToggleText(); };

            _miClean = new WinForms.ToolStripMenuItem();
            _miClean.Click += delegate { _widget.StartClean(); };

            _miAlerts = new WinForms.ToolStripMenuItem();
            _miAlerts.Click += delegate
            {
                _widget.AlertsEnabled = !_widget.AlertsEnabled;
                SetMenuMark(_miAlerts, _widget.AlertsEnabled);
            };
            SetMenuMark(_miAlerts, _widget.AlertsEnabled);

            // Периферия — такая же строка карточки, как ОЗУ и диски: её можно
            // скрыть, правило «скрыть можно всё» действует без исключений.
            _miPeriph = new WinForms.ToolStripMenuItem();
            _miPeriph.Click += delegate
            {
                bool on = !_widget.CardRowVisible(Widget.CardRow.Periph);
                _widget.SetCardRowVisible(Widget.CardRow.Periph, on);
                SetMenuMark(_miPeriph, _widget.CardRowVisible(Widget.CardRow.Periph));
            };
            SetMenuMark(_miPeriph, _widget.CardRowVisible(Widget.CardRow.Periph));

            _miTop = new WinForms.ToolStripMenuItem();
            _miTop.Click += delegate
            {
                _widget.Win.Topmost = !_widget.Win.Topmost;
                SetMenuMark(_miTop, _widget.Win.Topmost);
                _widget.SaveState();
            };
            SetMenuMark(_miTop, _widget.Win.Topmost);

            _miAuto = new WinForms.ToolStripMenuItem();
            _miAuto.Click += delegate
            {
                SetAutostart(!File.Exists(_startupLnk));
                SetMenuMark(_miAuto, File.Exists(_startupLnk));
            };
            SetMenuMark(_miAuto, File.Exists(_startupLnk));

            _miDash = new WinForms.ToolStripMenuItem();
            _miDash.Click += delegate { _widget.ToggleDashboard(); };

            _miSettings = new WinForms.ToolStripMenuItem();
            _miSettings.Click += delegate { OpenSettings(); };

            _miExit = new WinForms.ToolStripMenuItem();
            _miExit.Click += delegate { _widget.Win.Close(); };

            _menu.Items.Add(_miToggle);
            _menu.Items.Add(_miClean);
            _menu.Items.Add(new WinForms.ToolStripSeparator());
            _menu.Items.Add(_miAlerts);
            _menu.Items.Add(_miPeriph);
            _menu.Items.Add(_miTop);
            _menu.Items.Add(_miAuto);
            _menu.Items.Add(new WinForms.ToolStripSeparator());
            _menu.Items.Add(_miDash);
            _menu.Items.Add(_miSettings);
            _menu.Items.Add(_miExit);

            _ni.MouseUp += delegate(object s, WinForms.MouseEventArgs e)
            {
                if (e.Button != WinForms.MouseButtons.Right) return;
                ShowTrayMenu();
            };
            _ni.MouseDoubleClick += delegate { _widget.Toggle(); };

            _ni.Visible = true;

            RefreshLanguage();
        }

        /// <summary>
        /// Подписи меню и трея — по текущему языку. Базовый текст кладём в Tag:
        /// отметку ставит Checked у пункта, а не текст, поэтому при смене языка
        /// она не задваивается.
        /// </summary>
        static void SetMenuText(WinForms.ToolStripMenuItem item, string ru, string en)
        {
            item.Tag = Lang.S(ru, en);
            item.Text = (string)item.Tag;
        }

        /// <summary>Обновить всё, что зависит от языка, кроме самой карточки.</summary>
        void RefreshLanguage()
        {
            if (_ni != null)
                _ni.Text = "SysWidget";   // имя виджета, а не проценты и не описание

            if (_miToggle != null)
            {
                SetMenuText(_miClean, "Освободить память", "Free up memory");
                SetMenuText(_miAlerts, "Оповещения", "Alerts");
                SetMenuText(_miPeriph, "Периферия", "Peripherals");
                SetMenuText(_miTop, "Поверх всех окон", "Always on top");
                SetMenuText(_miAuto, "Запускать при входе", "Run at sign-in");
                SetMenuText(_miDash, "Окно мониторинга", "Monitor window");
                SetMenuText(_miSettings, "Настройки…", "Settings…");
                SetMenuText(_miExit, "Выход", "Exit");
                SyncToggleText();
                SyncMenuMarks();
            }

            SyncStartShortcut();

            // подсказка значка переписывается на ближайшем тике, даже если
            // проценты не изменились
            _trayKey = "";

            if (_settings != null) _settings.ApplyLanguage();
        }

        /// <summary>
        /// Сменить язык интерфейса: карточка, меню, трей и настройки
        /// перерисовываются сразу, выбор запоминается в state.json.
        /// </summary>
        public void SetLanguage(bool en)
        {
            if (Lang.En == en) return;
            _widget.SetLanguage(en);
            RefreshLanguage();
        }

        /// <summary>Ярлык «Пуск» с именем на текущем языке.</summary>
        static string StartLnkPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Lang.S("Виджет системы.lnk", "System widget.lnk"));
        }

        /// <summary>
        /// Язык сменился — ярлык в «Пуске» переименовывается: создаём новый и
        /// убираем старый, чтобы в меню не висели два виджета.
        /// </summary>
        void SyncStartShortcut()
        {
            try
            {
                string now = StartLnkPath();
                if (now == _startLnk) return;
                Shortcuts.Create(now, _exePath, _iconPath, Desc);
                if (!string.IsNullOrEmpty(_startLnk) && File.Exists(_startLnk))
                    File.Delete(_startLnk);
                _startLnk = now;
            }
            catch { }
        }

        /// <summary>
        /// Отметка включённого пункта. Стандартная галочка WinForms в тёмном меню
        /// почти не видна, поэтому DarkMenuRenderer рисует свою, в акцентном
        /// цвете; ToolStrip держит под неё отдельную колонку у всех пунктов.
        /// </summary>
        void SetMenuMark(WinForms.ToolStripMenuItem item, bool on)
        {
            item.Checked = on;
        }

        void SyncToggleText()
        {
            if (_miToggle == null || _widget == null) return;
            string key = _widget.HotkeyText == null ? "" : "  (" + _widget.HotkeyText + ")";
            SetMenuText(_miToggle,
                (_widget.IsVisible ? "Скрыть виджет" : "Показать виджет") + key,
                (_widget.IsVisible ? "Hide widget" : "Show widget") + key);
        }

        /// <summary>Ярлык автозапуска: создать или удалить. Общий код для меню и настроек.</summary>
        void SetAutostart(bool on)
        {
            try
            {
                if (on) Shortcuts.Create(_startupLnk, _exePath, _iconPath, Desc);
                else if (File.Exists(_startupLnk)) File.Delete(_startupLnk);
            }
            catch { }
            SetMenuMark(_miAuto, File.Exists(_startupLnk));
        }

        void OpenSettings()
        {
            if (_settings == null)
            {
                _settings = new SettingsWindow(_widget,
                    delegate { return File.Exists(_startupLnk); },
                    SetAutostart,
                    SetLanguage);
            }
            _settings.Show();
        }

        /// <summary>
        /// Перед показом меню сверяем отметки с настоящим состоянием: его могли
        /// поменять из окна настроек, пока меню было закрыто.
        /// </summary>
        void SyncMenuMarks()
        {
            SetMenuMark(_miAlerts, _widget.AlertsEnabled);
            SetMenuMark(_miPeriph, _widget.CardRowVisible(Widget.CardRow.Periph));
            SetMenuMark(_miTop, _widget.Win.Topmost);
            SetMenuMark(_miAuto, File.Exists(_startupLnk));
        }

        /// <summary>
        /// Меню по правому клику на карточке: открываем по её центру, а не у курсора —
        /// у правого края экрана оно иначе уезжало сильно влево. Если снизу места
        /// нет, показываем над карточкой.
        /// </summary>
        void ShowWidgetMenu()
        {
            try
            {
                SyncMenuMarks();
                var sz = _menu.GetPreferredSize(new System.Drawing.Size(0, 0));
                var tl = _widget.Win.PointToScreen(new System.Windows.Point(0, 0));
                var br = _widget.Win.PointToScreen(
                    new System.Windows.Point(_widget.Win.ActualWidth, _widget.Win.ActualHeight));

                int x = (int)((tl.X + br.X - sz.Width) / 2);
                int y = (int)(br.Y + 4);
                var wa = WinForms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
                if (y + sz.Height > wa.Bottom) y = (int)(tl.Y - sz.Height - 4);
                x = Math.Min(Math.Max(x, wa.Left + 2), wa.Right - sz.Width - 2);
                y = Math.Min(Math.Max(y, wa.Top + 2), wa.Bottom - sz.Height - 2);

                IntPtr h = _menu.Handle;
                Native.MakeToolWindow(h);
                _menu.Show(new System.Drawing.Point(x, y));
            }
            catch { }
        }

        /// <summary>
        /// Меню из трея показываем сами: встроенный показ ставит нижний край меню по
        /// курсору, а курсор лежит уже на панели задач (60 px при масштабе 125%) —
        /// последний пункт «Выход» уезжал под неё. Show сам прижимает меню к рабочей
        /// области монитора, поэтому считаем только верхнюю левую точку.
        /// </summary>
        void ShowTrayMenu()
        {
            try
            {
                SyncMenuMarks();
                var p = WinForms.Cursor.Position;
                var sz = _menu.GetPreferredSize(new System.Drawing.Size(0, 0));
                var wa = WinForms.Screen.FromPoint(p).WorkingArea;
                int x = Math.Min(Math.Max(p.X - sz.Width + 10, wa.Left), wa.Right - sz.Width - 2);
                int y = Math.Min(Math.Max(p.Y - sz.Height, wa.Top), wa.Bottom - sz.Height - 2);

                // окно меню не должно висеть кнопкой на панели задач
                IntPtr h = _menu.Handle;
                Native.MakeToolWindow(h);
                _menu.Show(new System.Drawing.Point(x, y));
            }
            catch { }
        }

        // ---------------------------------------------------------- обновления

        /// <summary>
        /// Включить или выключить живой значок. Выключенный — один кадр дождя,
        /// и дальше трей не трогаем: на слабой машине даже десять обновлений
        /// в секунду видны в диспетчере задач.
        /// </summary>
        public void SetTrayAnimation(bool on)
        {
            try
            {
                if (_trayAnim == null) return;
                if (on) _trayAnim.Start();
                else
                {
                    _trayAnim.Stop();
                    DrawTrayIcon(true);   // один «упавший» кадр текущего цвета
                }
            }
            catch { }
        }

        /// <summary>
        /// Принять свежие проценты. Сам значок рисует таймер кадров — здесь мы
        /// только запоминаем числа (по ним считается цвет).
        /// </summary>
        public void UpdateTrayIcon(double cpu, double ram, double ssd, double gpu)
        {
            _trayCpu = cpu;
            _trayRam = ram;
            _traySsd = ssd;
            _trayGpu = gpu;

            // по неизменившимся процентам незачем перерисовывать значок
            string key = string.Format(Inv, "{0:0}|{1:0}|{2:0}|{3:0}", cpu, ram, ssd, gpu);
            if (key == _trayKey) return;
            _trayKey = key;

            try
            {
                // анимация выключена — значок всё равно должен краснеть,
                // поэтому перерисовываем его на каждой смене процентов (раз в секунду)
                if (_trayAnim != null && !_trayAnim.IsEnabled) DrawTrayIcon();
            }
            catch { }
        }

        /// <summary>
        /// Нарисовать значок. С анимацией — очередной кадр; без неё — «стоячий»
        /// дождь. Значок у NotifyIcon не меняется на месте, поэтому старый
        /// подменяем новым и сразу освобождаем — иначе за минуту накопились бы
        /// сотни GDI-объектов.
        /// </summary>
        void DrawTrayIcon(bool force = false)
        {
            try
            {
                bool live = _trayAnim != null && _trayAnim.IsEnabled;
                bool warn = TrayIconFactory.Warn(_trayCpu, _trayRam, _traySsd, _trayGpu);

                // Без анимации значок меняется только при входе в красную зону
                // и обратно. Цвет тот же — трей не трогаем: даже одно обновление
                // в секунду заставляет оболочку перерисовывать иконку.
                if (!force && !live && warn == _trayWarn && _trayIcon != null) return;

                var icon = TrayIconFactory.Create(_trayCpu, _trayRam, _traySsd, _trayGpu,
                                                  live ? _trayFrame : -1);
                var old = _trayIcon;
                _ni.Icon = icon;
                _trayIcon = icon;
                _trayWarn = warn;
                if (old != null) old.Dispose();
            }
            catch { }
        }

        public void ShowBalloon(string msg)
        {
            try
            {
                _ni.ShowBalloonTip(3000, Lang.S("Виджет системы", "System widget"), msg,
                                   WinForms.ToolTipIcon.Info);
            }
            catch { }
        }

        // ------------------------------------------------------------- сбои

        /// <summary>
        /// Выполнить кусок работы таймера, не давая исключению убить виджет.
        /// Ошибка уходит в error.log; следующая секунда начнётся с чистого листа.
        /// </summary>
        static void Guard(string context, Action body)
        {
            try { body(); }
            catch (Exception ex) { ErrorLog.Write(context, ex); }
        }

        DateTime _warnAt = DateTime.MinValue;

        /// <summary>
        /// Последний рубеж для UI-потока: обработчики WPF-событий, меню и трея
        /// идут через диспетчер. Без этого любое исключение вне Guard роняло бы
        /// всё приложение. Пишем в журнал, редко предупреждаем и продолжаем.
        /// </summary>
        void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            ErrorLog.Write("dispatcher", e.Exception);
            e.Handled = true;

            // не закидываем пользователя шариками, если ошибка повторяется
            if ((DateTime.Now - _warnAt).TotalMinutes < 5) return;
            _warnAt = DateTime.Now;
            ShowBalloon(Lang.S(
                "Внутренняя ошибка — виджет продолжает работать, подробности в error.log",
                "Internal error — the widget keeps running, details in error.log"));
        }

        // -------------------------------------------------------------- выход

        /// <summary>
        /// Перезапуск: запускаем новый экземпляр (он подождёт освобождения
        /// мьютекса) и закрываем этот. Нужен для настроек, которые читаются при
        /// создании окон: тема, плотность, шрифт, цвета, профили.
        /// </summary>
        public void Restart()
        {
            try
            {
                var psi = new ProcessStartInfo(_exePath, RestartArg);
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("restart", ex);
                return;
            }
            _widget.Win.Close();
        }
        void OnClosed(object sender, EventArgs e)
        {
            try { _timer.Stop(); _zTimer.Stop(); _menuWatch.Stop(); } catch { }
            try { if (_trayAnim != null) _trayAnim.Stop(); } catch { }
            try { if (_settings != null) _settings.Close(); } catch { }
            try { if (_periphSvc != null) _periphSvc.Stop(); } catch { }
            try { _widget.SaveState(); } catch { }
            try { _widget.Shutdown(); } catch { }
            try { AdminHelper.Stop(); } catch { }
            try { _ni.Visible = false; _ni.Dispose(); } catch { }
            try { _sampler.Dispose(); } catch { }
            try { if (_trayIcon != null) _trayIcon.Dispose(); } catch { }
            try { _menu.Dispose(); } catch { }
            try { _mutex.ReleaseMutex(); _mutex.Dispose(); } catch { }
            try { Application.Current.Shutdown(); } catch { }
        }
    }
}
