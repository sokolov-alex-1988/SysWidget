using System;
using System.IO;

namespace SysWidget
{
    /// <summary>
    /// Где лежат файлы данных (state.json, alerts.json, window.json, error.log).
    ///
    /// Порядок выбора:
    ///   1. рядом с exe лежит файл portable.flag — переносной режим, данные
    ///      остаются в папке программы (флешка, сетевая папка);
    ///   2. иначе %LOCALAPPDATA%\SysWidget — работает и из Program Files,
    ///      и из папки без прав записи;
    ///   3. если и туда писать нельзя — папка программы, как раньше.
    ///
    /// При первом запуске в обычном режиме настройки, лежавшие рядом с exe
    /// (старые версии), копируются в папку данных. Копируются, а не
    /// переносятся: так старая копия и переносной архив продолжают работать.
    /// </summary>
    public static class AppPaths
    {
        public const string PortableFlag = "portable.flag";

        static readonly string[] Migrated = { "state.json", "alerts.json", "window.json" };

        static string _dataDir;

        /// <summary>Папка программы (где лежат exe, icon.ico, SysWidget.exe.config).</summary>
        public static string AppDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        /// <summary>Переносной режим: данные хранятся рядом с exe.</summary>
        public static bool IsPortable
        {
            get
            {
                try { return File.Exists(Path.Combine(AppDir, PortableFlag)); }
                catch { return false; }
            }
        }

        /// <summary>Папка данных; выбирается один раз при первом обращении.</summary>
        public static string DataDir
        {
            get
            {
                if (_dataDir == null) _dataDir = Resolve();
                return _dataDir;
            }
        }

        /// <summary>Полный путь к файлу данных.</summary>
        public static string File_(string name)
        {
            return Path.Combine(DataDir, name);
        }

        static string Resolve()
        {
            string app = AppDir;
            if (IsPortable) return app;

            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SysWidget");
                Directory.CreateDirectory(dir);
                if (!CanWrite(dir)) return app;

                Migrate(app, dir);
                return dir;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("paths", ex);
                return app;
            }
        }

        static void Migrate(string from, string to)
        {
            foreach (string name in Migrated)
            {
                try
                {
                    string src = Path.Combine(from, name);
                    string dst = Path.Combine(to, name);
                    if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst, false);
                }
                catch (Exception ex) { ErrorLog.Write("paths.migrate " + name, ex); }
            }
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".syswidget-write-test");
                using (new FileStream(probe, FileMode.Create, FileAccess.Write,
                                      FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                }
                return true;
            }
            catch { return false; }
        }
    }
}
