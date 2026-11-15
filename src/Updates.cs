using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SysWidget
{
    /// <summary>Доступное обновление программы (из winget).</summary>
    public sealed class AppUpdate
    {
        public string Name, Id, Version, Available;
    }

    /// <summary>
    /// Проверка обновлений без прав администратора: Windows — через встроенный Windows
    /// Update Agent (COM, поиск доступен обычному пользователю), программы — через winget.
    /// Установку не делаем: Windows ставит обновления только с правами администратора,
    /// а программы обновляет сам пользователь в видимом окне winget.
    /// Результат запоминается, чтобы окно настроек не гоняло проверку при каждом открытии.
    /// </summary>
    public static class Updates
    {
        /// <summary>Программные обновления Windows (без драйверов): то, что Центр обновления показывает в основном списке.</summary>
        public static List<string> WinTitles = new List<string>();
        /// <summary>Необязательные драйверы: Центр обновления прячет их в «Дополнительных параметрах».</summary>
        public static List<string> WinDrivers = new List<string>();
        public static List<AppUpdate> Apps = new List<AppUpdate>();
        public static string WinError = "", AppError = "";
        public static DateTime Checked = DateTime.MinValue;
        public static bool Busy;

        // ---------------------------------------------------------------- Windows

        /// <summary>
        /// Неустановленные обновления: программные и драйверы раздельно. Драйверы Центр
        /// обновления в основной список не включает, поэтому в «есть обновления» их не считаем.
        /// Медленно (десятки секунд) — только из фона.
        /// </summary>
        static void CheckWindows(out List<string> software, out List<string> drivers, out string error)
        {
            error = "";
            software = new List<string>();
            drivers = new List<string>();
            try
            {
                Type t = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (t == null) { error = Lang.S("Windows Update Agent недоступен", "Windows Update Agent unavailable"); return; }
                dynamic session = Activator.CreateInstance(t);
                dynamic searcher = session.CreateUpdateSearcher();
                dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
                int n = result.Updates.Count;
                for (int i = 0; i < n; i++)
                {
                    dynamic u = result.Updates.Item(i);
                    int type = (int)u.Type;                    // 1 — программное, 2 — драйвер
                    if (type == 2) drivers.Add((string)u.Title); else software.Add((string)u.Title);
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Write("check windows updates", ex);
                error = Lang.S("не удалось проверить (", "check failed (") + ex.Message.Trim() + ")";
            }
        }

        // ----------------------------------------------------------------- winget

        const string WingetArgs = "upgrade --include-unknown --disable-interactivity --accept-source-agreements";

        /// <summary>
        /// Одна попытка: запуск winget, ожидание с настоящим таймаутом (чтение вывода идёт
        /// в фоне, иначе зависший winget завесил бы и ReadToEnd).
        /// </summary>
        static bool RunWinget(out string text, out int exitCode)
        {
            text = ""; exitCode = -1;
            var psi = new ProcessStartInfo("winget.exe", WingetArgs);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.StandardOutputEncoding = Encoding.UTF8;

            using (Process p = Process.Start(psi))
            {
                string captured = "";
                var reader = new System.Threading.Thread((System.Threading.ThreadStart)delegate
                {
                    try { captured = p.StandardOutput.ReadToEnd(); } catch { }
                });
                reader.IsBackground = true;
                reader.Start();

                if (!p.WaitForExit(90000))
                {
                    try { p.Kill(); } catch { }
                    return false;
                }
                reader.Join(5000);
                text = captured;
                exitCode = p.ExitCode;
                return true;
            }
        }

        /// <summary>
        /// Надёжность: таблица есть и разобрана — список; таблицы нет, winget завершился
        /// кодом 0 — обновлений нет; любой другой исход (код ≠ 0, таймаут, таблица не
        /// распознана) — ошибка, а не «всё актуально». Один повтор при сбое.
        /// </summary>
        static List<AppUpdate> CheckApps(out string error)
        {
            error = "";
            try
            {
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    string text; int code;
                    if (!RunWinget(out text, out code))
                    {
                        error = Lang.S("winget не ответил за 90 с", "winget did not answer in 90 s");
                        continue;
                    }

                    List<AppUpdate> rows = ParseWinget(text);
                    if (rows != null && rows.Count > 0) { error = ""; return rows; }   // нашли
                    if (rows == null)
                    {
                        error = Lang.S("не удалось разобрать ответ winget", "could not parse winget output");
                        continue;
                    }
                    if (code == 0) { error = ""; return rows; }                         // таблицы нет, код 0 — обновлений нет
                    error = Lang.S("winget завершился с кодом 0x", "winget exited with code 0x") + code.ToString("X");
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                error = Lang.S("winget не установлен", "winget is not installed");
            }
            catch (Exception ex)
            {
                ErrorLog.Write("check app updates", ex);
                error = Lang.S("не удалось проверить (", "check failed (") + ex.Message.Trim() + ")";
            }
            return new List<AppUpdate>();
        }

        /// <summary>
        /// Таблица winget: заголовок, строка из дефисов, затем строки. Колонки режем по
        /// позициям слов заголовка — так не зависит от языка и пробелов в названиях программ.
        /// </summary>
        public static List<AppUpdate> ParseWinget(string text)
        {
            var res = new List<AppUpdate>();
            var lines = new List<string>();
            foreach (string raw in text.Split('\n'))
            {
                // индикатор «- \ | /» winget рисует через \r: берём последнюю часть
                string[] parts = raw.Split('\r');
                string line = "";
                for (int i = parts.Length - 1; i >= 0; i--)
                    if (parts[i].Trim().Length > 0) { line = parts[i]; break; }
                lines.Add(line.TrimEnd());
            }

            int dash = -1;
            for (int i = 1; i < lines.Count; i++)
                if (Regex.IsMatch(lines[i], "^-{10,}$") && lines[i - 1].Trim().Length > 0) { dash = i; break; }
            if (dash < 0) return res;

            var cols = new List<int>();
            foreach (Match m in Regex.Matches(lines[dash - 1], @"\S+")) cols.Add(m.Index);
            if (cols.Count < 4) return null;                // таблица есть, но формат незнакомый

            for (int i = dash + 1; i < lines.Count; i++)
            {
                string l = lines[i];
                if (l.Length <= cols[2]) break;          // пустая строка или итоговая надпись
                string name = Cut(l, cols[0], cols[1]);
                string id = Cut(l, cols[1], cols[2]);
                string ver = Cut(l, cols[2], cols[3]);
                string avail = Cut(l, cols[3], cols.Count > 4 ? cols[4] : l.Length);
                if (id.Length == 0 || id.IndexOf(' ') >= 0) break;
                res.Add(new AppUpdate { Name = name, Id = id, Version = ver, Available = avail });
            }
            return res.Count > 0 ? res : null;              // заголовок есть, а строк разобрать не удалось
        }

        static string Cut(string s, int from, int to)
        {
            if (from >= s.Length) return "";
            if (to > s.Length) to = s.Length;
            return s.Substring(from, to - from).Trim();
        }

        /// <summary>Обновить одну программу: видимое окно winget, чтобы пользователь видел запрос UAC.</summary>
        public static void UpgradeApp(string id)
        {
            if (!Regex.IsMatch(id ?? "", @"^[A-Za-z0-9._+\-]+$")) return;   // id идёт в командную строку
            try
            {
                var psi = new ProcessStartInfo("cmd.exe",
                    "/c \"winget upgrade --id " + id + " --accept-source-agreements --accept-package-agreements & pause\"");
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { ErrorLog.Write("upgrade app", ex); }
        }

        public static void OpenWindowsUpdate()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true }); }
            catch (Exception ex) { ErrorLog.Write("open windows update", ex); }
        }

        // ----------------------------------------------------------------- запуск

        /// <summary>Проверить всё в фоне и позвать done в этом же потоке по окончании.</summary>
        public static void Run(Action done)
        {
            if (Busy) return;
            Busy = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> sw = new List<string>(), dr = new List<string>();
                string we = "", ae = "";
                List<AppUpdate> a = new List<AppUpdate>();
                try
                {
                    // Windows ищется долго, winget — быстро: идут параллельно
                    var tw = new System.Threading.Thread((System.Threading.ThreadStart)delegate
                    {
                        List<string> s2, d2; string e2;
                        CheckWindows(out s2, out d2, out e2);
                        sw = s2; dr = d2; we = e2;
                    });
                    tw.IsBackground = true;
                    tw.Start();
                    a = CheckApps(out ae);
                    tw.Join();
                }
                catch (Exception ex) { ErrorLog.Write("check updates", ex); }
                finally
                {
                    WinTitles = sw; WinDrivers = dr; WinError = we;
                    Apps = a; AppError = ae;
                    Checked = DateTime.Now;
                    Busy = false;
                }
                if (done != null) done();
            });
        }
    }
}
