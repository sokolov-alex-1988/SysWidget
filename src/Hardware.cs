using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using Microsoft.Win32;

namespace SysWidget
{
    /// <summary>Один физический накопитель: номер, модель, тип и размер.</summary>
    public sealed class DiskInfo
    {
        public int Index;          // номер PhysicalDisk: по нему сходятся WMI и PDH
        public string Model = "";
        public string Kind = "";   // «SSD», «HDD» или пусто — тип не определился
        public long Size;
        public bool System;        // на нём стоит Windows — всегда виден на карточке
    }

    /// <summary>
    /// Постоянные сведения об оборудовании: модели, объёмы, производители.
    /// Читаются один раз в фоновом потоке через WMI и реестр — обычному
    /// пользователю это разрешено, ни драйверов, ни прав администратора не нужно.
    /// </summary>
    public sealed class Hardware
    {
        const string GpuClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        const string PciEnumKey = @"SYSTEM\CurrentControlSet\Enum\PCI";
        const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        const double GB = 1073741824.0;

        static readonly object Sync = new object();
        static Hardware _cur;
        static bool _started;

        public string RamMaker = "", RamPart = "", RamType = "";
        public int RamSpeedMHz;

        public string CpuName = "";
        public int CpuCores, CpuThreads, CpuMaxMHz;

        public string DiskModel = "";
        public string SystemDrive = "";
        public long VolumeSize, VolumeFree;
        public List<DiskInfo> Disks = new List<DiskInfo>();

        public string GpuName = "", GpuMaker = "";
        public double GpuVramGB;        // общий объём видеопамяти, 0 — неизвестен

        public static Hardware Current
        {
            get { lock (Sync) return _cur; }
        }

        /// <summary>Запустить чтение один раз; к первому наведению обычно готово.</summary>
        public static void WarmUp()
        {
            lock (Sync)
            {
                if (_started) return;
                _started = true;
            }
            var t = new System.Threading.Thread(Load);
            t.IsBackground = true;
            t.Name = "SysWidgetHardware";
            t.Start();
        }

        static void Load()
        {
            var h = new Hardware();
            try { h.ReadRam(); } catch { }
            try { h.ReadCpu(); } catch { }
            try { h.ReadDisk(); } catch { }
            try { h.ReadDisks(); } catch { }
            try { h.ReadGpu(); } catch { }
            lock (Sync) _cur = h;
        }

        // ------------------------------------------------------------- память

        void ReadRam()
        {
            int type = 0;
            using (var q = new ManagementObjectSearcher(
                "SELECT Manufacturer,PartNumber,Speed,SMBIOSMemoryType FROM Win32_PhysicalMemory"))
            using (var found = q.Get())
            {
                foreach (ManagementBaseObject o in found)
                {
                    if (RamMaker.Length == 0) RamMaker = Text(o, "Manufacturer");
                    if (RamPart.Length == 0) RamPart = Text(o, "PartNumber");
                    RamSpeedMHz = Math.Max(RamSpeedMHz, Num(o, "Speed"));
                    type = Math.Max(type, Num(o, "SMBIOSMemoryType"));
                }
            }
            RamType = RamTypeName(type);
        }

        static string RamTypeName(int t)
        {
            if (t == 24) return "DDR3";
            if (t == 26) return "DDR4";
            if (t == 34) return "DDR5";
            if (t == 35) return "LPDDR5";
            return "";
        }

        // ---------------------------------------------------------- процессор

        void ReadCpu()
        {
            using (var q = new ManagementObjectSearcher(
                "SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor"))
            using (var found = q.Get())
            {
                foreach (ManagementBaseObject o in found)
                {
                    if (CpuName.Length == 0) CpuName = Clean(Text(o, "Name"));
                    CpuCores += Num(o, "NumberOfCores");
                    CpuThreads += Num(o, "NumberOfLogicalProcessors");
                    CpuMaxMHz = Math.Max(CpuMaxMHz, Num(o, "MaxClockSpeed"));
                }
            }
        }

        // ------------------------------------------------------------ накопитель

        void ReadDisk()
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory);
            SystemDrive = (root ?? "").TrimEnd('\\');
            try
            {
                var di = new DriveInfo(root);
                VolumeSize = di.TotalSize;
                VolumeFree = di.AvailableFreeSpace;
            }
            catch { }

            long best = 0;
            using (var q = new ManagementObjectSearcher("SELECT Model,Size FROM Win32_DiskDrive"))
            using (var found = q.Get())
            {
                foreach (ManagementBaseObject o in found)
                {
                    long size = 0;
                    try { size = Convert.ToInt64(o["Size"], CultureInfo.InvariantCulture); }
                    catch { }
                    if (size <= best) continue;
                    best = size;
                    DiskModel = Clean(Text(o, "Model"));
                }
            }
        }

        // ------------------------------------------------------------ все диски

        /// <summary>
        /// Список физических накопителей. Тип берётся у хранилища Windows
        /// (MSFT_PhysicalDisk: 3 — HDD, 4 — SSD, 5 — SCM), модель — оттуда же
        /// или из Win32_DiskDrive. Если класс хранилища недоступен, остаются
        /// эвристики по модели и PNPID (NVMe). Съёмные флешки пропускаем.
        /// </summary>
        void ReadDisks()
        {
            var map = new Dictionary<int, DiskInfo>();

            try
            {
                using (var q = new ManagementObjectSearcher(
                    "SELECT Index,Model,Size,MediaType,InterfaceType,PNPDeviceID FROM Win32_DiskDrive"))
                using (var found = q.Get())
                {
                    foreach (ManagementBaseObject o in found)
                    {
                        long size = 0;
                        try { size = Convert.ToInt64(o["Size"], CultureInfo.InvariantCulture); }
                        catch { }
                        if (size < 1073741824L) continue;

                        string media = Text(o, "MediaType");
                        if (media.IndexOf("Removable", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;

                        var d = new DiskInfo();
                        d.Index = Num(o, "Index");
                        d.Model = Clean(Text(o, "Model"));
                        d.Size = size;
                        d.Kind = GuessDiskKind(d.Model + " " + Text(o, "InterfaceType") + " " +
                                               Text(o, "PNPDeviceID"));
                        map[d.Index] = d;
                    }
                }
            }
            catch { }

            // класс хранилища знает точный тип: SSD это или HDD
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();
                using (var q = new ManagementObjectSearcher(scope, new ObjectQuery(
                    "SELECT DeviceId,FriendlyName,MediaType FROM MSFT_PhysicalDisk")))
                using (var found = q.Get())
                {
                    foreach (ManagementBaseObject o in found)
                    {
                        int idx;
                        if (!int.TryParse(Text(o, "DeviceId"), NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out idx))
                            continue;

                        DiskInfo d;
                        if (!map.TryGetValue(idx, out d)) continue;

                        string friendly = Clean(Text(o, "FriendlyName"));
                        if (friendly.Length > 0) d.Model = friendly;

                        int mt = Num(o, "MediaType");
                        if (mt == 3) d.Kind = "HDD";
                        else if (mt == 4 || mt == 5) d.Kind = "SSD";
                    }
                }
            }
            catch { }

            // системный диск: том с Windows («C:») → партиция → номер PhysicalDisk.
            // Ассоциация LogicalDiskToPartition содержит номер диска прямо в
            // DeviceID партиции («Disk #0, Partition #1»).
            try
            {
                string sys = "";
                try { sys = Path.GetPathRoot(Environment.SystemDirectory); } catch { }
                sys = (sys ?? "").TrimEnd('\\');
                if (sys.Length > 0)
                {
                    using (var q = new ManagementObjectSearcher(
                        "SELECT Antecedent,Dependent FROM Win32_LogicalDiskToPartition"))
                    using (var found = q.Get())
                    {
                        foreach (ManagementBaseObject o in found)
                        {
                            string dep = Text(o, "Dependent");
                            if (dep.IndexOf("DeviceID=\"" + sys + "\"",
                                    StringComparison.OrdinalIgnoreCase) < 0)
                                continue;

                            string ant = Text(o, "Antecedent");
                            int pos = ant.IndexOf("Disk #", StringComparison.OrdinalIgnoreCase);
                            if (pos < 0) break;
                            int start = pos + 6, end = start;
                            while (end < ant.Length && char.IsDigit(ant[end])) end++;
                            int idx;
                            if (!int.TryParse(ant.Substring(start, end - start),
                                              NumberStyles.Integer,
                                              CultureInfo.InvariantCulture, out idx))
                                break;

                            DiskInfo si;
                            if (map.TryGetValue(idx, out si)) si.System = true;
                            break;
                        }
                    }
                }
            }
            catch { }

            var list = new List<DiskInfo>(map.Values);
            // на карточке и в настройках показываем только диск с Windows;
            // остальные физические накопители не нужны
            DiskInfo sysDisk = null;
            for (int i = 0; i < list.Count; i++)
                if (list[i].System) { sysDisk = list[i]; break; }

            list.Sort(delegate(DiskInfo a, DiskInfo b) { return a.Index.CompareTo(b.Index); });
            if (sysDisk != null)
            {
                list.Clear();
                list.Add(sysDisk);
            }
            else if (list.Count > 1)
            {
                // ассоциация не сработала — берём диск 0: на нём почти всегда Windows
                DiskInfo first = list[0];
                list.Clear();
                list.Add(first);
            }
            Disks = list;
        }

        static string GuessDiskKind(string text)
        {
            string t = (text ?? "").ToLowerInvariant();
            if (t.Contains("nvme") || t.Contains("ssd") || t.Contains("solid state"))
                return "SSD";
            if (t.Contains("hdd") || t.Contains("hard disk") || t.Contains("harddrive"))
                return "HDD";
            return "";
        }

        // ------------------------------------------------------------- видюха

        /// <summary>
        /// Модель адаптера и объём видеопамяти. Имя ищется в трёх независимых
        /// источниках: ветка драйвера в реестре, Win32_VideoController и
        /// PnP-устройства класса «Дисплей». На старых драйверах (например,
        /// GT 710) DriverDesc в реестре бывает просто «NVIDIA», а полное
        /// «NVIDIA GeForce GT 710» лежит в WMI — поэтому кандидаты
        /// сравниваются по «похожести на модель», а не берутся по порядку.
        /// Ни драйверов, ни прав администратора не нужно.
        /// </summary>
        void ReadGpu()
        {
            var list = new List<GpuInfo>();

            // ---- реестр: имя драйвера, иногда более полное AdapterString, объём
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(GpuClassKey))
                {
                    if (cls != null)
                    {
                        foreach (string sub in cls.GetSubKeyNames())
                        {
                            try
                            {
                                using (var sk = cls.OpenSubKey(sub))
                                {
                                    if (sk == null) continue;
                                    long bytes = RegLong(sk.GetValue("HardwareInformation.qwMemorySize"));
                                    if (bytes <= 0)
                                        bytes = RegLong(sk.GetValue("HardwareInformation.MemorySize"));
                                    double gb = bytes / GB;

                                    AddGpu(list, Clean(ValueText(sk.GetValue("DriverDesc"))), gb, 20);
                                    AddGpu(list, Clean(RegText(sk.GetValue("HardwareInformation.AdapterString"))), gb, 15);
                                    AddGpu(list, Clean(ValueText(sk.GetValue("Device Description"))), gb, 10);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            // ---- WMI: имя и AdapterRAM (только как резерв: 32-битный, врёт на 4 ГБ+)
            try
            {
                using (var q = new ManagementObjectSearcher(
                    "SELECT Name,AdapterRAM FROM Win32_VideoController"))
                using (var found = q.Get())
                {
                    foreach (ManagementBaseObject o in found)
                    {
                        long ram = 0;
                        try { ram = Convert.ToInt64(o["AdapterRAM"], CultureInfo.InvariantCulture); }
                        catch { }
                        AddGpu(list, Clean(Text(o, "Name")), ram / GB, 30);
                    }
                }
            }
            catch { }

            // ---- PnP: ещё одно имя, не зависящее от драйвера дисплея
            try
            {
                using (var q = new ManagementObjectSearcher(
                    "SELECT Name,Description FROM Win32_PnPEntity WHERE PNPClass = 'Display'"))
                using (var found = q.Get())
                {
                    foreach (ManagementBaseObject o in found)
                    {
                        AddGpu(list, Clean(Text(o, "Name")), 0, 25);
                        AddGpu(list, Clean(Text(o, "Description")), 0, 18);
                    }
                }
            }
            catch { }

            // ---- дерево Enum\PCI: FriendlyName из INF производителя,
            // обычно самое точное («NVIDIA GeForce GT 710»)
            try
            {
                using (var pci = Registry.LocalMachine.OpenSubKey(PciEnumKey))
                {
                    if (pci != null)
                    {
                        foreach (string ven in pci.GetSubKeyNames())
                        {
                            try
                            {
                                using (var vk = pci.OpenSubKey(ven))
                                {
                                    if (vk == null) continue;
                                    foreach (string dev in vk.GetSubKeyNames())
                                    {
                                        try
                                        {
                                            using (var dk = vk.OpenSubKey(dev))
                                            {
                                                if (dk == null) continue;
                                                if (!IsDisplayClass(dk.GetValue("ClassGUID"))) continue;
                                                AddGpu(list, Clean(ValueText(dk.GetValue("FriendlyName"))), 0, 30);

                                                // DeviceDesc иногда хранится как
                                                // «@nv...,%name%;NVIDIA GeForce GT 710»
                                                string desc = Clean(ValueText(dk.GetValue("DeviceDesc")));
                                                int semi = desc.LastIndexOf(';');
                                                if (semi >= 0) desc = desc.Substring(semi + 1);
                                                AddGpu(list, desc, 0, 12);
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            // Сначала выбираем адаптер: дискретная карта важнее встроенной
            // (на ПК с iGPU + GT 710 виджет должен показать GT 710). Внутри
            // выбранного производителя — самое «модельное» имя из всех
            // источников: так «NVIDIA» из реестра уступает полному имени из WMI.
            string vendor = PickVendor(list);
            GpuName = BestName(list, vendor);
            GpuMaker = MakerShort(GpuName, "");

            // объём берём только у выбранного производителя: на ноутбуке
            // с двумя картами память iGPU не должна приписываться дискретной.
            // Сначала реестр (точный qwMemorySize), и лишь если его нет —
            // WMI: там AdapterRAM 32-битный и врёт на картах от 4 ГБ.
            double vram = 0;
            foreach (GpuInfo g in list)
            {
                if (g.Rank > 20) continue;
                if (vendor.Length > 0 && Vendor(g.Name) != vendor) continue;
                if (g.Vram > vram) vram = g.Vram;
            }
            if (vram <= 0)
            {
                foreach (GpuInfo g in list)
                {
                    if (vendor.Length > 0 && Vendor(g.Name) != vendor) continue;
                    if (g.Vram > vram) vram = g.Vram;
                }
            }
            if (vram <= 0)
                foreach (GpuInfo g in list)
                    if (g.Vram > vram) vram = g.Vram;
            GpuVramGB = vram;
        }

        struct GpuInfo
        {
            public string Name;
            public double Vram;
            public int Rank;      // насколько источнику можно верить
        }

        static void AddGpu(List<GpuInfo> list, string name, double vram, int rank)
        {
            if (string.IsNullOrEmpty(name)) return;
            var g = new GpuInfo();
            g.Name = name;
            g.Vram = vram > 0 ? vram : 0;
            g.Rank = rank;
            list.Add(g);
        }

        static bool IsDisplayClass(object guid)
        {
            string s = ValueText(guid).Trim();
            return string.Equals(s, DisplayClassGuid, StringComparison.OrdinalIgnoreCase);
        }

        static bool IsDiscrete(string vendor)
        {
            return vendor == "nvidia" || vendor == "amd";
        }

        /// <summary>Производитель главного адаптера: дискретная карта в приоритете.</summary>
        static string PickVendor(List<GpuInfo> list)
        {
            string best = "";
            double bestVram = -1;

            foreach (GpuInfo g in list)
            {
                string v = Vendor(g.Name);
                if (!IsDiscrete(v) || g.Vram <= 0) continue;
                if (g.Vram > bestVram) { bestVram = g.Vram; best = v; }
            }
            if (best.Length > 0) return best;

            int bestDisc = -1;
            foreach (GpuInfo g in list)
            {
                string v = Vendor(g.Name);
                if (v.Length == 0) continue;
                int disc = IsDiscrete(v) ? 1 : 0;
                if (g.Vram > bestVram || (g.Vram == bestVram && disc > bestDisc))
                {
                    bestVram = g.Vram;
                    bestDisc = disc;
                    best = v;
                }
            }
            return best;
        }

        /// <summary>Самое подробное имя среди адаптеров выбранного производителя.</summary>
        static string BestName(List<GpuInfo> list, string vendor)
        {
            string best = "";
            int bestScore = int.MinValue;
            foreach (GpuInfo g in list)
            {
                if (vendor.Length > 0 && Vendor(g.Name) != vendor) continue;
                int sc = NameScore(g.Name) + g.Rank;
                if (sc > bestScore) { bestScore = sc; best = g.Name; }
            }
            return best ?? "";
        }

        /// <summary>
        /// Насколько имя похоже на настоящую модель. «NVIDIA GeForce GT 710» —
        /// да, просто «NVIDIA» — почти нет, «Microsoft Basic Display Adapter» —
        /// это вообще не карта.
        /// </summary>
        static int NameScore(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1000;
            string n = " " + s.ToLowerInvariant() + " ";

            string[] junk = { "basic display", "standard vga", "virtual", "remote",
                              "parsec", "citrix", "indirect", "spacedesk", " meta " };
            foreach (string j in junk)
                if (n.Contains(j)) return -500;

            int score = Math.Min(24, s.Length);

            string[] families = { "geforce", "radeon", "quadro", "rtx", "gtx",
                                  "arc ", "iris", "uhd", "vega", "firepro" };
            foreach (string f in families)
                if (n.Contains(f)) { score += 100; break; }

            if (n.Contains(" graphics") || n.Contains(" gpu")) score += 40;

            string t = n.Trim();
            if (t == "nvidia" || t == "amd" || t == "intel" ||
                t == "nvidia corporation" || t == "intel corporation" ||
                t == "advanced micro devices")
                score -= 80;
            return score;
        }

        static string Vendor(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.Contains("nvidia")) return "nvidia";
            if (n.Contains("advanced micro") || n.Contains("amd") || n.Contains("radeon")) return "amd";
            if (n.Contains("intel")) return "intel";
            if (n.Contains("microsoft")) return "microsoft";
            return "";
        }

        static string MakerShort(string name, string compat)
        {
            string n = (name ?? "").ToLowerInvariant();
            string c = (compat ?? "").ToLowerInvariant();
            string all = n + " " + c;
            if (all.Contains("nvidia")) return "NVIDIA";
            if (all.Contains("advanced micro") || all.Contains("amd") || all.Contains("radeon")) return "AMD";
            if (all.Contains("intel")) return "Intel";
            if (all.Contains("microsoft")) return "Microsoft";
            return (compat ?? "").Replace("Corporation", "").Trim();
        }

        // ---------------------------------------------------------- утилиты

        static string Text(ManagementBaseObject o, string name)
        {
            object v = o[name];
            return v == null ? "" : v.ToString().Trim();
        }

        static int Num(ManagementBaseObject o, string name)
        {
            object v = o[name];
            if (v == null) return 0;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        static string ValueText(object v)
        {
            return v == null ? "" : v.ToString();
        }

        /// <summary>
        /// Текст из значения реестра. AdapterString у части драйверов лежит
        /// не строкой, а массивом байт UTF-16LE — раскодируем его.
        /// </summary>
        static string RegText(object v)
        {
            var bytes = v as byte[];
            if (bytes == null) return ValueText(v);
            if (bytes.Length < 2) return "";

            string s = Encoding.Unicode.GetString(bytes, 0, bytes.Length - (bytes.Length % 2));
            int z = s.IndexOf('\0');
            if (z >= 0) s = s.Substring(0, z);
            foreach (char c in s)
                if (c < 32) return "";        // мусор, а не строка
            return s.Trim();
        }

        /// <summary>qwMemorySize в разных драйверах бывает и QWORD, и REG_BINARY.</summary>
        static long RegLong(object v)
        {
            if (v == null) return 0;
            var bytes = v as byte[];
            if (bytes != null)
            {
                if (bytes.Length >= 8) return BitConverter.ToInt64(bytes, 0);
                if (bytes.Length >= 4) return BitConverter.ToUInt32(bytes, 0);
                return 0;
            }
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        /// <summary>Убираем «(R)», «(TM)» и двойные пробелы из имён оборудования.</summary>
        static string Clean(string s)
        {
            s = (s ?? "").Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Trim();
        }
    }
}
