using System;
using System.IO;
using System.Text;

namespace SysWidget
{
    /// <summary>
    /// Запись небольших файлов состояния «всё или ничего». File.WriteAllText
    /// перезаписывает файл на месте: если в этот момент пропадёт питание или
    /// система упадёт, останется обрезанный JSON — и виджет потеряет позицию,
    /// тему и набор строк. Здесь текст сначала пишется во временный файл рядом,
    /// а потом подменяет старый одной операцией.
    /// </summary>
    public static class AtomicFile
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static void WriteAllText(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, Utf8);

            // File.Replace атомарен и сохраняет атрибуты старого файла, но
            // требует, чтобы тот существовал и лежал на том же томе.
            try { File.Replace(tmp, path, null); return; }
            catch { }

            try
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
        }
    }
}
