using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SysWidget
{
    /// <summary>
    /// Все Win32-вызовы в одном месте: геометрия окна (в физических пикселях!),
    /// поиск панели задач, владелец окна, флаги всплывающих окон.
    /// </summary>
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>
        /// Windows 11: скругляет углы окна без заголовка и рисует по скруглению
        /// собственную рамку нужного цвета. Своя прямоугольная рамка в углах
        /// обрезается скруглением и рвётся, поэтому рамку на этих системах
        /// рисует сам Windows. colorRef — 0x00BBGGRR. Возвращает false, если системе
        /// это неизвестно (Windows 10): тогда рамку надо рисовать самим.
        /// </summary>
        public static bool RoundCorners(IntPtr hwnd, int colorRef)
        {
            try
            {
                int round = 2;                                          // DWMWCP_ROUND
                if (DwmSetWindowAttribute(hwnd, 33, ref round, 4) != 0) return false;
                return DwmSetWindowAttribute(hwnd, 34, ref colorRef, 4) == 0;   // DWMWA_BORDER_COLOR
            }
            catch { return false; }
        }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        public const int VK_LBUTTON = 0x01;

        /// <summary>
        /// Кнопка мыши нажата прямо сейчас. При перетаскивании окна это надёжнее
        /// события MouseUp: окно уезжает из-под курсора, и WPF может потерять
        /// хватку мыши вместе с отпусканием кнопки.
        /// </summary>
        public static bool LeftButtonDown()
        {
            try { return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0; }
            catch { return false; }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        /// <summary>DPI окна (то есть монитора, на котором оно сейчас).</summary>
        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        /// <summary>
        /// Рабочая область монитора, на котором стоит окно (физические пиксели —
        /// та же система координат, что у GetWindowRect и SetWindowPos).
        /// </summary>
        public static bool GetWorkArea(IntPtr hWnd, out RECT r)
        {
            r = new RECT();
            try
            {
                return WorkAreaOf(MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST), out r);
            }
            catch { return false; }
        }

        /// <summary>Рабочая область монитора, ближайшего к точке.</summary>
        public static bool GetWorkAreaAt(int x, int y, out RECT r)
        {
            r = new RECT();
            try
            {
                POINT pt = new POINT();
                pt.X = x;
                pt.Y = y;
                return WorkAreaOf(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST), out r);
            }
            catch { return false; }
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        // --- глобальная горячая клавиша
        public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // --- «не беспокоить»: полноэкранная игра, показ презентации, режим фокусировки
        [DllImport("shell32.dll")]
        static extern int SHQueryUserNotificationState(out int state);

        /// <summary>
        /// Человеку сейчас лучше не мешать: полноэкранное приложение (2),
        /// полноэкранный D3D (3), презентация (4) или «Помощник по фокусировке» (6).
        /// При любой ошибке считаем, что мешать можно.
        /// </summary>
        public static bool UserBusy()
        {
            try
            {
                int st;
                if (SHQueryUserNotificationState(out st) != 0) return false;
                return st == 2 || st == 3 || st == 4 || st == 6;
            }
            catch { return false; }
        }

        /// <summary>
        /// Монитор, который сейчас целиком занят окном на переднем плане
        /// (игра, видео во весь экран), или IntPtr.Zero. Оболочку — рабочий
        /// стол, панель задач, собственное окно — не считаем.
        /// </summary>
        public static IntPtr FullscreenForegroundMonitor(IntPtr own)
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == own) return IntPtr.Zero;
                if (!IsWindowVisible(fg)) return IntPtr.Zero;

                var sb = new StringBuilder(64);
                GetClassName(fg, sb, 64);
                string cls = sb.ToString();
                if (cls == "Progman" || cls == "WorkerW" ||
                    cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                    return IntPtr.Zero;

                RECT r;
                if (!GetWindowRect(fg, out r)) return IntPtr.Zero;

                IntPtr mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero) return IntPtr.Zero;

                var mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetMonitorInfo(mon, ref mi)) return IntPtr.Zero;

                RECT m = mi.rcMonitor;
                bool full = r.Left <= m.Left && r.Top <= m.Top &&
                            r.Right >= m.Right && r.Bottom >= m.Bottom;
                return full ? mon : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        static bool WorkAreaOf(IntPtr mon, out RECT r)
        {
            r = new RECT();
            if (mon == IntPtr.Zero) return false;
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(mon, ref mi)) return false;
            r = mi.rcWork;
            return true;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;

        private const uint GW_HWNDPREV = 3;
        private const uint GW_OWNER = 4;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int GWLP_HWNDPARENT = -8;

        /// <summary>Панель задач (основная или вторичного монитора), пересекающаяся с окном.</summary>
        public static IntPtr TaskbarUnder(IntPtr hWnd)
        {
            RECT self;
            if (!GetWindowRect(hWnd, out self)) return IntPtr.Zero;

            IntPtr found = IntPtr.Zero;
            var buf = new StringBuilder(64);
            EnumWindows(delegate(IntPtr w, IntPtr p)
            {
                buf.Length = 0;
                GetClassName(w, buf, 64);
                string cls = buf.ToString();
                if (cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd") return true;
                RECT t;
                if (GetWindowRect(w, out t) &&
                    t.Left < self.Right && t.Right > self.Left &&
                    t.Top < self.Bottom && t.Bottom > self.Top)
                {
                    found = w;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static IntPtr OwnerOf(IntPtr hWnd)
        {
            return GetWindow(hWnd, GW_OWNER);
        }

        /// <summary>
        /// Назначает окну владельца. Owned-окно всегда рисуется выше владельца —
        /// поэтому панель задач (тоже topmost) больше не перекрывает карточку,
        /// и не нужно постоянно перетягивать z-order (это вызывало мерцание).
        /// </summary>
        public static void SetOwner(IntPtr hWnd, IntPtr owner)
        {
            SetWindowLongPtr(hWnd, GWLP_HWNDPARENT, owner);
        }

        /// <summary>Перенос окна без изменения размера, z-order и фокуса.</summary>
        public static void MoveTo(IntPtr hWnd, int x, int y)
        {
            SetWindowPos(hWnd, IntPtr.Zero, x, y, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>Панель задач стоит в z-order выше окна и пересекает его.</summary>
        public static bool CoveredByTaskbar(IntPtr hWnd)
        {
            RECT self;
            if (!GetWindowRect(hWnd, out self)) return false;

            IntPtr p = GetWindow(hWnd, GW_HWNDPREV);
            var buf = new StringBuilder(256);
            int guard = 0;
            while (p != IntPtr.Zero && guard++ < 400)
            {
                buf.Length = 0;
                GetClassName(p, buf, 256);
                string cls = buf.ToString();
                if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                {
                    RECT t;
                    if (GetWindowRect(p, out t) &&
                        t.Left < self.Right && t.Right > self.Left &&
                        t.Top < self.Bottom && t.Bottom > self.Top)
                    {
                        return true;
                    }
                }
                p = GetWindow(p, GW_HWNDPREV);
            }
            return false;
        }

        /// <summary>Поднять окно в верх topmost-слоя, не трогая позицию и фокус.</summary>
        public static void RaiseTopmost(IntPtr hWnd)
        {
            SetWindowPos(hWnd, new IntPtr(-1), 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }

        // ------------------------------------------------------------------
        // Глобальный хук мыши. Панель истории закрывается по любому клику мимо
        // неё; опрос Mouse.LeftButton из таймера для этого не годится — быстрое
        // нажатие (короче периода опроса) проскакивает между тиками.

        public const int WH_MOUSE_LL = 14;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_MBUTTONDOWN = 0x0207;

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn,
            IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        /// <summary>Всплывающие окна (меню, подсказки) не должны попадать на панель задач.</summary>
        public static void MakeToolWindow(IntPtr hWnd)
        {
            int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
            SetWindowLong(hWnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }
}
