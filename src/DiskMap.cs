using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SysWidget
{
    /// <summary>Папка в карте диска: размер и самые крупные вложенные папки.</summary>
    public sealed class DirNode
    {
        public string Name = "";
        public string Path = "";
        public long Size;
        public int Files;
        public List<DirNode> Kids = new List<DirNode>();
        public long OtherSize;          // остальные вложенные папки, не вошедшие в список
        public int OtherCount;
    }

    public sealed class BigFile
    {
        public string Path = "";
        public long Size;
    }

    /// <summary>
    /// «Крупные папки»: один обход системного диска в фоне (низкий приоритет, без прав администратора).
    /// Строится дерево из трёх уровней папок с размерами и список самых больших файлов. Ссылки и
    /// точки повторного разбора (junction, symlink) не обходятся — иначе одно и то же считалось бы
    /// дважды и можно уйти в цикл; «облачные» файлы OneDrive (ещё не скачаны) не считаются: места на
    /// диске они не занимают. Размер — длина файлов, а не место на диске с учётом кластеров. Папки без
    /// доступа (системные) пропускаются и считаются отдельно; охват показывается рядом с итогом.
    /// </summary>
    public static class DiskMap
    {
        public const int MaxDepth = 3;          // уровней папок с вложенными списками
        const int TopFiles = 10;

        public static volatile bool Busy;
        public static DateTime Checked = DateTime.MinValue;
        public static double Seconds;
        public static DirNode Root;
        public static List<BigFile> Files = new List<BigFile>();
        public static string Drive = "";
        public static long DriveUsed, DriveTotal;
        public static int Denied;

        // ход сканирования (читает окно настроек)
        public static long SeenFiles, SeenBytes;

        static readonly List<BigFile> _top = new List<BigFile>();
        static long _minTop;

        public static void Run(Action done)
        {
            if (Busy) return;
            Busy = true;
            Thread t = new Thread((ThreadStart)delegate
            {
                try { Scan(); }
                catch (Exception ex) { ErrorLog.Write("disk map", ex); }
                finally { Busy = false; }
                if (done != null) { try { done(); } catch { } }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        static void Scan()
        {
            DateTime t0 = DateTime.Now;
            string root = System.IO.Path.GetPathRoot(Environment.SystemDirectory);      // C:\
            Drive = root;
            try
            {
                DriveInfo di = new DriveInfo(root);
                DriveTotal = di.TotalSize;
                DriveUsed = di.TotalSize - di.TotalFreeSpace;
            }
            catch { DriveTotal = 0; DriveUsed = 0; }

            SeenFiles = 0; SeenBytes = 0; Denied = 0;
            _top.Clear(); _minTop = 0;

            var node = new DirNode();
            node.Name = root.TrimEnd('\\');
            node.Path = root;
            node.Size = Walk(root, node, 1);

            Files = new List<BigFile>(_top);
            Root = node;
            Seconds = (DateTime.Now - t0).TotalSeconds;
            Checked = DateTime.Now;
        }

        /// <summary>Размер папки; для папок глубже MaxDepth дочерние узлы не создаются.</summary>
        static long Walk(string dir, DirNode node, int depth)
        {
            long total = 0;
            try
            {
                foreach (FileSystemInfo fi in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    FileAttributes a = fi.Attributes;
                    if ((a & FileAttributes.Directory) != 0)
                    {
                        if ((a & FileAttributes.ReparsePoint) != 0) continue;       // junction / symlink
                        if (depth <= MaxDepth && node != null)
                        {
                            var kid = new DirNode();
                            kid.Name = fi.Name;
                            kid.Path = fi.FullName;
                            kid.Size = Walk(fi.FullName, depth < MaxDepth ? kid : null, depth + 1);
                            node.Kids.Add(kid);
                            total += kid.Size;
                        }
                        else total += Walk(fi.FullName, null, depth + 1);
                    }
                    else
                    {
                        // файл, ещё не скачанный из облака (OneDrive): RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS / OFFLINE
                        if ((a & (FileAttributes)0x00040000) != 0 || (a & (FileAttributes)0x00400000) != 0 ||
                            (a & FileAttributes.Offline) != 0) continue;
                        long len;
                        try { len = ((FileInfo)fi).Length; } catch { continue; }
                        total += len;
                        SeenFiles++;
                        SeenBytes += len;
                        if (node != null) node.Files++;
                        AddTop(fi.FullName, len);
                    }
                }
            }
            catch (UnauthorizedAccessException) { Denied++; }
            catch (IOException) { }
            catch (Exception ex) { ErrorLog.Write("disk map walk", ex); }

            if (node != null)
            {
                node.Kids.Sort(delegate(DirNode x, DirNode y) { return y.Size.CompareTo(x.Size); });
                int keep = depth == 1 ? 12 : 8;
                if (node.Kids.Count > keep)
                {
                    for (int i = keep; i < node.Kids.Count; i++) node.OtherSize += node.Kids[i].Size;
                    node.OtherCount = node.Kids.Count - keep;
                    node.Kids.RemoveRange(keep, node.Kids.Count - keep);
                }
            }
            return total;
        }

        static void AddTop(string path, long len)
        {
            if (_top.Count >= TopFiles && len <= _minTop) return;
            var f = new BigFile();
            f.Path = path;
            f.Size = len;
            int i = 0;
            while (i < _top.Count && _top[i].Size >= len) i++;
            _top.Insert(i, f);
            if (_top.Count > TopFiles) _top.RemoveAt(_top.Count - 1);
            _minTop = _top.Count >= TopFiles ? _top[_top.Count - 1].Size : 0;
        }

        /// <summary>«1.2 ГБ», «845 МБ», «12 КБ».</summary>
        public static string Fmt(long bytes)
        {
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            double v = bytes;
            if (v >= 1024.0 * 1024 * 1024) return (v / (1024.0 * 1024 * 1024)).ToString(v >= 100.0 * 1024 * 1024 * 1024 ? "0" : "0.0", inv) + " " + Lang.S("ГБ", "GB");
            if (v >= 1024.0 * 1024) return (v / (1024.0 * 1024)).ToString("0", inv) + " " + Lang.S("МБ", "MB");
            if (v >= 1024.0) return (v / 1024.0).ToString("0", inv) + " " + Lang.S("КБ", "KB");
            return bytes.ToString(inv) + " " + Lang.S("Б", "B");
        }
    }
}
