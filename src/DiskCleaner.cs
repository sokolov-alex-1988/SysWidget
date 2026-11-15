using System;
using System.Collections.Generic;
using System.IO;

namespace SysWidget
{
    /// <summary>
    /// Очистка мусора и безопасных кэшей без прав администратора и драйверов.
    ///
    /// Что считается безопасным: только то, что Windows и программы пересоздают сами —
    /// временные файлы, отчёты об ошибках, кэш значков и эскизов, шейдерный кэш,
    /// кэш браузеров. Не трогаем: корзину, «Загрузки», куки, пароли, историю,
    /// папки Windows, требующие администратора (SoftwareDistribution, Prefetch).
    ///
    /// Защита: ссылки и точки соединения (junction) не раскрываются; файлы, занятые
    /// программами, пропускаются; временные файлы моложе суток не удаляются —
    /// их может использовать работающий установщик.
    /// </summary>
    public static class DiskCleaner
    {
        /// <summary>Одно место для очистки: папка, маска файлов, вглубь ли, минимальный возраст.</summary>
        internal sealed class Spot
        {
            public string Dir;
            public string Pattern;
            public bool Deep;
            public int MinAgeHours;
        }

        public sealed class Category
        {
            public string NameRu, NameEn;
            public string Name { get { return Lang.S(NameRu, NameEn); } }
            internal Func<List<Spot>> Spots;

            /// <summary>Найдено при последней проверке, байт.</summary>
            public long Bytes;
            /// <summary>Файлов при последней проверке.</summary>
            public int Files;
            /// <summary>Входит в очистку.</summary>
            public bool On = true;
        }

        public sealed class Result
        {
            public long Freed;
            public int Deleted;
            public int Skipped;     // занятые или защищённые файлы
        }

        public static readonly Category[] Categories = new Category[]
        {
            new Category { NameRu = "Временные файлы пользователя", NameEn = "User temp files", Spots = UserTemp },
            new Category { NameRu = "Временные файлы Windows", NameEn = "Windows temp files", Spots = WinTemp },
            new Category { NameRu = "Отчёты об ошибках и дампы", NameEn = "Error reports and dumps", Spots = Reports },
            new Category { NameRu = "Кэш значков, эскизов и шейдеров", NameEn = "Icon, thumbnail and shader cache", Spots = ShellCache },
            new Category { NameRu = "Кэш браузеров", NameEn = "Browser cache", Spots = Browsers }
        };

        // ------------------------------------------------------------ места

        static string Env(Environment.SpecialFolder f)
        {
            try { return Environment.GetFolderPath(f); } catch { return ""; }
        }

        static void Add(List<Spot> l, string dir, string pattern, bool deep, int hours)
        {
            if (string.IsNullOrEmpty(dir)) return;
            l.Add(new Spot { Dir = dir, Pattern = pattern, Deep = deep, MinAgeHours = hours });
        }

        static List<Spot> UserTemp()
        {
            var l = new List<Spot>();
            Add(l, Path.GetTempPath(), "*", true, 24);
            return l;
        }

        static List<Spot> WinTemp()
        {
            var l = new List<Spot>();
            string win = Environment.GetEnvironmentVariable("WINDIR");
            if (!string.IsNullOrEmpty(win)) Add(l, Path.Combine(win, "Temp"), "*", true, 24);
            return l;
        }

        static List<Spot> Reports()
        {
            var l = new List<Spot>();
            string local = Env(Environment.SpecialFolder.LocalApplicationData);
            Add(l, Path.Combine(local, "CrashDumps"), "*", true, 24);
            Add(l, Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"), "*", true, 24);
            Add(l, Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue"), "*", true, 24);
            return l;
        }

        static List<Spot> ShellCache()
        {
            var l = new List<Spot>();
            string local = Env(Environment.SpecialFolder.LocalApplicationData);
            string explorer = Path.Combine(local, @"Microsoft\Windows\Explorer");
            Add(l, explorer, "thumbcache_*.db", false, 0);
            Add(l, explorer, "iconcache_*.db", false, 0);
            Add(l, Path.Combine(local, "D3DSCache"), "*", true, 0);
            Add(l, Path.Combine(local, @"Microsoft\Windows\INetCache"), "*", true, 0);
            return l;
        }

        static List<Spot> Browsers()
        {
            var l = new List<Spot>();
            string local = Env(Environment.SpecialFolder.LocalApplicationData);

            // Chromium: Chrome, Edge, Brave, Vivaldi — в каждом профиле только каш-папки
            string[] chromium =
            {
                @"Google\Chrome\User Data", @"Microsoft\Edge\User Data",
                @"BraveSoftware\Brave-Browser\User Data", @"Vivaldi\User Data"
            };
            string[] caches = { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage" };
            foreach (string rel in chromium)
            {
                string root = Path.Combine(local, rel);
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (string profile in Directory.GetDirectories(root))
                    {
                        string name = Path.GetFileName(profile);
                        if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                        foreach (string c in caches) Add(l, Path.Combine(profile, c), "*", true, 0);
                    }
                    Add(l, Path.Combine(root, "ShaderCache"), "*", true, 0);
                    Add(l, Path.Combine(root, "GrShaderCache"), "*", true, 0);
                }
                catch { }
            }

            // Firefox: cache2 лежит в Local, профили — в Roaming (куки и пароли там, их не трогаем)
            string ff = Path.Combine(local, @"Mozilla\Firefox\Profiles");
            if (Directory.Exists(ff))
            {
                try
                {
                    foreach (string p in Directory.GetDirectories(ff))
                        Add(l, Path.Combine(p, "cache2"), "*", true, 0);
                }
                catch { }
            }
            return l;
        }

        // ---------------------------------------------------------- обход

        static bool IsLink(FileSystemInfo fi)
        {
            return (fi.Attributes & FileAttributes.ReparsePoint) != 0;
        }

        static bool Eligible(FileInfo f, int minAgeHours)
        {
            if (IsLink(f)) return false;
            if (minAgeHours <= 0) return true;
            return f.LastWriteTime < DateTime.Now.AddHours(-minAgeHours)
                && f.LastAccessTime < DateTime.Now.AddHours(-minAgeHours);
        }

        static void Walk(Spot s, Action<FileInfo> onFile, Action<DirectoryInfo> onDirDone)
        {
            DirectoryInfo root;
            try
            {
                root = new DirectoryInfo(s.Dir);
                if (!root.Exists || IsLink(root)) return;
            }
            catch { return; }
            WalkDir(root, s, onFile, onDirDone, true);
        }

        static void WalkDir(DirectoryInfo d, Spot s, Action<FileInfo> onFile,
                            Action<DirectoryInfo> onDirDone, bool isRoot)
        {
            try
            {
                foreach (FileInfo f in d.GetFiles(s.Pattern))
                    onFile(f);

                if (s.Deep)
                {
                    foreach (DirectoryInfo sub in d.GetDirectories())
                    {
                        if (IsLink(sub)) continue;
                        WalkDir(sub, s, onFile, onDirDone, false);
                    }
                    if (!isRoot && onDirDone != null) onDirDone(d);
                }
            }
            catch { }
        }

        // ---------------------------------------------------- проверка и очистка

        /// <summary>Посчитать, сколько можно освободить. Ничего не удаляет.</summary>
        public static long Scan()
        {
            long total = 0;
            foreach (Category c in Categories)
            {
                long bytes = 0; int files = 0;
                List<Spot> spots;
                try { spots = c.Spots(); } catch { spots = new List<Spot>(); }
                foreach (Spot s in spots)
                {
                    Spot sp = s;
                    Walk(sp, delegate(FileInfo f)
                    {
                        try
                        {
                            if (!Eligible(f, sp.MinAgeHours)) return;
                            bytes += f.Length; files++;
                        }
                        catch { }
                    }, null);
                }
                c.Bytes = bytes; c.Files = files;
                if (c.On) total += bytes;
            }
            return total;
        }

        /// <summary>Удалить выбранные категории. Занятые файлы пропускаются.</summary>
        public static Result Clean()
        {
            var r = new Result();
            foreach (Category c in Categories)
            {
                if (!c.On) continue;
                List<Spot> spots;
                try { spots = c.Spots(); } catch { spots = new List<Spot>(); }
                foreach (Spot s in spots)
                {
                    Spot sp = s;
                    Walk(sp, delegate(FileInfo f)
                    {
                        try
                        {
                            if (!Eligible(f, sp.MinAgeHours)) return;
                            long len = f.Length;
                            if ((f.Attributes & FileAttributes.ReadOnly) != 0)
                                f.Attributes = FileAttributes.Normal;
                            f.Delete();
                            r.Freed += len; r.Deleted++;
                        }
                        catch { r.Skipped++; }
                    }, delegate(DirectoryInfo d)
                    {
                        // пустую подпапку убираем; непустая или занятая — остаётся
                        try
                        {
                            if (d.GetFileSystemInfos().Length == 0) d.Delete();
                        }
                        catch { }
                    });
                }
                c.Bytes = 0; c.Files = 0;
            }
            return r;
        }

        public static string Size(long bytes)
        {
            double mb = bytes / 1048576.0;
            if (mb >= 1024) return (mb / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + Lang.S(" ГБ", " GB");
            return mb.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + Lang.S(" МБ", " MB");
        }
    }
}
