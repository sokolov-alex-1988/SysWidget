using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace SysWidget
{
    /// <summary>Одна запись автозагрузки.</summary>
    public sealed class StartupItem
    {
        public string Name = "";
        public string Command = "";
        public string Exe = "";
        public string Company = "";
        public string Source = "";          // человекочитаемый источник
        public int Kind;                    // 0 — реестр HKCU, 1 — реестр HKLM, 2 — папка пользователя, 3 — общая папка
        public string RegName = "";         // имя значения или файла для StartupApproved
        public bool Enabled = true;
        public bool CanToggle;
        public string Lock = "";            // почему нельзя переключить
        public bool IsSelf;                 // это сам виджет
        public bool Running;
        public double CpuPct;               // средняя загрузка ЦП за время жизни процессов, % одного ядра
        public double MemMB;
        public int Impact;                  // 0 — не запущено, 1 — низкое, 2 — среднее, 3 — высокое
    }

    /// <summary>
    /// Что запускается при входе в Windows. Читает автозагрузку без прав администратора:
    /// реестр Run (HKCU и HKLM) и папки «Автозагрузка». Отключение своей записи — как в диспетчере
    /// задач: запись остаётся на месте, в StartupApproved (HKCU) ставится флаг «выключено»;
    /// включение возвращает флаг обратно. Записи «для всех пользователей» только показываются.
    /// «Влияние» — оценка по тому, сколько сейчас потребляют запущенные процессы этой записи
    /// (память и средняя загрузка ЦП за время жизни), а не замер времени загрузки Windows.
    /// </summary>
    public static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        public static List<StartupItem> Items = new List<StartupItem>();
        public static DateTime Checked = DateTime.MinValue;
        public static volatile bool Busy;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        /// <summary>Просканировать в фоне; по готовности вызвать done (из фонового потока).</summary>
        public static void Run(Action done)
        {
            if (Busy) return;
            Busy = true;
            Thread t = new Thread((ThreadStart)delegate
            {
                try
                {
                    List<StartupItem> list = Scan();
                    Items = list;
                    Checked = DateTime.Now;
                }
                catch (Exception ex) { ErrorLog.Write("autostart scan", ex); }
                finally { Busy = false; }
                if (done != null) { try { done(); } catch { } }
            });
            t.IsBackground = true;
            t.Start();
        }

        static List<StartupItem> Scan()
        {
            var list = new List<StartupItem>();
            var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            // реестр: HKCU
            ReadRun(list, seen, Registry.CurrentUser, RegistryView.Default, 0, "Run", Lang.S("Реестр, пользователь", "Registry, current user"));

            // реестр: HKLM (64- и 32-битный разделы)
            foreach (RegistryView view in new RegistryView[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        ReadRun(list, seen, lm, view, 1, view == RegistryView.Registry32 ? "Run32" : "Run",
                            Lang.S("Реестр, все пользователи", "Registry, all users"));
                }
                catch { }
            }

            // папки «Автозагрузка»
            ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), 2,
                Lang.S("Папка «Автозагрузка»", "Startup folder"));
            ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), 3,
                Lang.S("Папка «Автозагрузка», все пользователи", "Startup folder, all users"));

            string self = "";
            try { self = System.Reflection.Assembly.GetExecutingAssembly().Location; } catch { }

            // процессы: путь → сумма
            var usage = ProcessUsage();

            foreach (StartupItem it in list)
            {
                it.IsSelf = self.Length > 0 && string.Equals(it.Exe, self, StringComparison.OrdinalIgnoreCase);
                it.CanToggle = (it.Kind == 0 || it.Kind == 2) && !it.IsSelf;
                if (it.IsSelf) it.Lock = Lang.S("это виджет — «Запускать при входе» в меню", "this widget — use “Run at sign-in” in the menu");
                else if (it.Kind == 1 || it.Kind == 3) it.Lock = Lang.S("для всех пользователей — меняет администратор", "all users — an administrator changes it");

                it.Company = CompanyOf(it.Exe);

                double[] u = FindUsage(usage, it.Exe);
                if (u != null)
                {
                    it.Running = true;
                    it.MemMB = u[1] / (1024.0 * 1024.0);
                    double life = Math.Max(30, u[2]);
                    it.CpuPct = u[0] / life * 100.0;
                    it.Impact = it.MemMB >= 300 || it.CpuPct >= 3 ? 3
                              : it.MemMB >= 100 || it.CpuPct >= 0.5 ? 2 : 1;
                }
            }

            list.Sort(delegate(StartupItem a, StartupItem b)
            {
                int ea = a.Enabled ? 1 : 0, eb = b.Enabled ? 1 : 0;
                if (ea != eb) return eb.CompareTo(ea);
                if (a.Impact != b.Impact) return b.Impact.CompareTo(a.Impact);
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return list;
        }

        static void ReadRun(List<StartupItem> list, Dictionary<string, bool> seen, RegistryKey root,
                            RegistryView view, int kind, string approvalSub, string source)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(RunKey))
                {
                    if (k == null) return;
                    foreach (string name in k.GetValueNames())
                    {
                        string cmd = Convert.ToString(k.GetValue(name));
                        if (string.IsNullOrEmpty(name) || cmd == null || cmd.Trim().Length == 0) continue;
                        string key = name + "|" + cmd;
                        if (seen.ContainsKey(key)) continue;
                        seen[key] = true;

                        var it = new StartupItem();
                        it.Name = name;
                        it.Command = cmd;
                        it.Exe = ExeOf(cmd);
                        it.Source = source;
                        it.Kind = kind;
                        it.RegName = name;
                        it.Enabled = IsEnabled(approvalSub, name);
                        list.Add(it);
                    }
                }
            }
            catch (Exception ex) { ErrorLog.Write("autostart registry", ex); }
        }

        static void ReadFolder(List<StartupItem> list, string dir, int kind, string source)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir))
                {
                    string file = Path.GetFileName(f);
                    if (string.Equals(file, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    var it = new StartupItem();
                    it.Name = Path.GetFileNameWithoutExtension(f);
                    it.RegName = file;
                    it.Kind = kind;
                    it.Source = source;
                    string target = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? LnkTarget(f) : f;
                    it.Command = target.Length > 0 ? target : f;
                    it.Exe = target;
                    it.Enabled = IsEnabled("StartupFolder", file);
                    list.Add(it);
                }
            }
            catch (Exception ex) { ErrorLog.Write("autostart folder", ex); }
        }

        /// <summary>Флаг из StartupApproved: первый байт, младший бит — «выключено». Приоритет у пользователя.</summary>
        static bool IsEnabled(string sub, string name)
        {
            bool? v = Approved(Registry.CurrentUser, sub, name);
            if (v == null) v = Approved(Registry.LocalMachine, sub, name);
            return v == null || v.Value;
        }

        static bool? Approved(RegistryKey root, string sub, string name)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(AppKey + sub))
                {
                    if (k == null) return null;
                    byte[] b = k.GetValue(name) as byte[];
                    if (b == null || b.Length == 0) return null;
                    return (b[0] & 1) == 0;
                }
            }
            catch { return null; }
        }

        /// <summary>Включить или выключить свою запись (без прав администратора). Пустая строка — успех.</summary>
        public static string SetEnabled(StartupItem it, bool enable)
        {
            if (!it.CanToggle) return it.Lock;
            try
            {
                string sub = it.Kind == 0 ? "Run" : "StartupFolder";
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(AppKey + sub))
                {
                    byte[] b = new byte[12];
                    if (enable) b[0] = 2;
                    else
                    {
                        b[0] = 3;
                        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(b, 4);
                    }
                    k.SetValue(it.RegName, b, RegistryValueKind.Binary);
                }
                it.Enabled = enable;
                return "";
            }
            catch (Exception ex)
            {
                ErrorLog.Write("autostart toggle", ex);
                return ex.Message;
            }
        }

        // ------------------------------------------------------------ вспомогательное

        static string ExeOf(string cmd)
        {
            if (cmd == null) return "";
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (cmd.Length == 0) return "";
            if (cmd[0] == '"')
            {
                int e = cmd.IndexOf('"', 1);
                return e > 1 ? cmd.Substring(1, e - 1) : cmd.Substring(1);
            }
            int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i > 0) return cmd.Substring(0, i + 4);
            int sp = cmd.IndexOf(' ');
            return sp > 0 ? cmd.Substring(0, sp) : cmd;
        }

        static string LnkTarget(string path)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic sh = Activator.CreateInstance(t);
                dynamic lnk = sh.CreateShortcut(path);
                return Environment.ExpandEnvironmentVariables((string)lnk.TargetPath ?? "");
            }
            catch { return ""; }
        }

        static string CompanyOf(string exe)
        {
            try
            {
                if (exe.Length == 0 || !Path.IsPathRooted(exe) || !File.Exists(exe)) return "";
                string c = FileVersionInfo.GetVersionInfo(exe).CompanyName;
                return c == null ? "" : c.Trim();
            }
            catch { return ""; }
        }

        /// <summary>Путь exe (в нижнем регистре) → [секунд ЦП, байт памяти, возраст самого старого процесса, с].</summary>
        static Dictionary<string, double[]> ProcessUsage()
        {
            var map = new Dictionary<string, double[]>();
            DateTime now = DateTime.Now;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string path = ImagePath(p.Id);
                    if (path.Length == 0) continue;
                    string key = path.ToLowerInvariant();
                    double cpu = 0, mem = 0, age = 0;
                    try { cpu = p.TotalProcessorTime.TotalSeconds; } catch { }
                    try { mem = p.WorkingSet64; } catch { }
                    try { age = (now - p.StartTime).TotalSeconds; } catch { }
                    double[] a;
                    if (!map.TryGetValue(key, out a)) { a = new double[3]; map[key] = a; }
                    a[0] += cpu; a[1] += mem; if (age > a[2]) a[2] = age;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return map;
        }

        static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);       // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally { CloseHandle(h); }
        }

        static double[] FindUsage(Dictionary<string, double[]> map, string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            double[] a;
            if (Path.IsPathRooted(exe))
                return map.TryGetValue(exe.ToLowerInvariant(), out a) ? a : null;

            // команда без пути (rundll32, cmd) — по имени файла
            string name = Path.GetFileName(exe).ToLowerInvariant();
            double[] sum = null;
            foreach (KeyValuePair<string, double[]> kv in map)
            {
                if (!kv.Key.EndsWith("\\" + name)) continue;
                if (sum == null) sum = new double[3];
                sum[0] += kv.Value[0]; sum[1] += kv.Value[1]; if (kv.Value[2] > sum[2]) sum[2] = kv.Value[2];
            }
            return sum;
        }
    }
}
