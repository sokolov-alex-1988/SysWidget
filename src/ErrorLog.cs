using System;
using System.IO;
using System.Text;

namespace SysWidget
{
    /// <summary>
    /// Журнал сбоев. Ошибки не показываются диалогом и не теряются в пустом
    /// catch: они пишутся в error.log рядом с exe, чтобы полевую проблему можно
    /// было разобрать без отладчика.
    ///
    /// Файл ограничен по размеру: при переполнении предыдущая версия уходит в
    /// error.old.log, поэтому журнал не растёт бесконечно. Если папка программы
    /// только для чтения (Program Files), пишем в %LOCALAPPDATA%\SysWidget.
    /// </summary>
    public static class ErrorLog
    {
        const long MaxBytes = 128 * 1024;
        const string FileName = "error.log";

        static string _path;

        /// <summary>Куда пишется журнал (null — писать некуда). Для диагностики.</summary>
        public static string Path { get { return _path; } }

        /// <summary>
        /// Выбрать файл журнала. Пробуем папку exe, при отказе — LocalAppData.
        /// Проверка записи создаёт временный файл с DeleteOnClose, так что при
        /// штатном запуске пустой error.log не появляется.
        /// </summary>
        public static void Init(string exeDir)
        {
            try
            {
                string p = System.IO.Path.Combine(exeDir, FileName);
                if (CanWrite(exeDir)) { _path = p; return; }
            }
            catch { }

            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SysWidget");
                Directory.CreateDirectory(dir);
                if (CanWrite(dir)) _path = System.IO.Path.Combine(dir, FileName);
            }
            catch { }
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string probe = System.IO.Path.Combine(dir, ".syswidget-write-test");
                using (new FileStream(probe, FileMode.Create, FileAccess.Write,
                                      FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Дописать одну запись: время, где случилось, текст исключения.</summary>
        public static void Write(string context, Exception ex)
        {
            if (ex == null) return;
            Write(context, ex.ToString());
        }

        public static void Write(string context, string detail)
        {
            try
            {
                if (_path == null) return;

                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    string old = _path + ".old";
                    try { if (File.Exists(old)) File.Delete(old); } catch { }
                    try { File.Move(_path, old); } catch { }
                }

                File.AppendAllText(_path, string.Format(
                    "{0:yyyy-MM-dd HH:mm:ss} [{1}]{2}{3}{2}--------------------{2}",
                    DateTime.Now, context, Environment.NewLine, detail),
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
