using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SysWidget
{
    /// <summary>Что за устройство: мышь, клавиатура, наушники, колонка.</summary>
    public enum PeriphKind { Mouse, Keyboard, Headphones, Speaker, Audio }

    /// <summary>Как устройство подключено к компьютеру.</summary>
    public enum PeriphLink { Usb, Bluetooth, BluetoothLe }

    /// <summary>
    /// Одно подключённое периферийное устройство. Собирается по «контейнеру»
    /// PnP — Windows складывает в один контейнер все узлы одного физического
    /// устройства (сам адаптер, HID-мышь, службу батареи, аудиопоток).
    /// </summary>
    public sealed class Peripheral
    {
        public PeriphKind Kind;
        /// <summary>
        /// В том же контейнере есть и клавиатура: обычно это общий USB-приёмник
        /// 2,4 ГГц, за которым сидят мышь и клавиатура. Строка одна, но со
        /// значками обоих устройств.
        /// </summary>
        public bool HasKeyboard;
        public string Name = "";
        public string Manufacturer = "";
        public PeriphLink Link;
        /// <summary>Заряд в процентах; -1 — неизвестен или устройство проводное.</summary>
        public int Battery = -1;
        /// <summary>Адрес Bluetooth LE — по нему читается служба батареи; 0 — не BLE.</summary>
        public ulong Address;
        /// <summary>Идентификатор контейнера: по нему строки карточки не пересобираются зря.</summary>
        public string Key = "";

        public string KindName
        {
            get
            {
                if (Kind == PeriphKind.Mouse && HasKeyboard)
                    return Lang.S("Мышь + клавиатура", "Mouse + keyboard");
                if (Kind == PeriphKind.Mouse) return Lang.S("Мышь", "Mouse");
                if (Kind == PeriphKind.Keyboard) return Lang.S("Клавиатура", "Keyboard");
                if (Kind == PeriphKind.Speaker) return Lang.S("Колонка", "Speaker");
                if (Kind == PeriphKind.Headphones) return Lang.S("Наушники", "Headphones");
                return Lang.S("Аудио", "Audio");
            }
        }

        /// <summary>Короткая метка способа подключения для строки карточки.</summary>
        public string LinkShort
        {
            get
            {
                if (Link == PeriphLink.Usb) return "USB";
                return "BT";
            }
        }

        /// <summary>Полное название способа подключения для подсказки.</summary>
        public string LinkFull
        {
            get
            {
                if (Link == PeriphLink.Usb)
                    return Battery >= 0
                        ? Lang.S("Радиоканал 2,4 ГГц", "2.4 GHz wireless")
                        : Lang.S("Кабель USB", "USB cable");
                if (Link == PeriphLink.BluetoothLe)
                    return Lang.S("Bluetooth LE", "Bluetooth LE");
                return Lang.S("Bluetooth", "Bluetooth");
            }
        }
    }

    /// <summary>
    /// Фоновый обход периферии. Устройства ищутся через SetupAPI (то же, чем
    /// пользуется «Диспетчер устройств»), заряд беспроводных — через службу
    /// батареи Bluetooth LE. Всё это доступно обычному пользователю: драйверы
    /// и права администратора не нужны.
    ///
    /// Поток отдельный: обход SetupAPI и чтение GATT занимают десятки и сотни
    /// миллисекунд, и на UI-потоке это было бы видно как подёргивание карточки.
    /// </summary>
    public sealed class PeripheralService
    {
        /// <summary>Как часто пересматривать список подключённых устройств.</summary>
        const int ScanPeriodMs = 10000;

        /// <summary>Как часто перечитывать заряд Bluetooth LE-устройств.</summary>
        const int BatteryPeriodSec = 60;

        /// <summary>Сколько помнить последний удачный заряд, если устройство уснуло.</summary>
        const int BatteryStaleSec = 300;

        readonly ManualResetEvent _wake = new ManualResetEvent(false);
        volatile bool _stop;
        Thread _thread;

        // кэш зарядов: только поток службы, поэтому без блокировок
        readonly Dictionary<ulong, int> _level = new Dictionary<ulong, int>();
        readonly Dictionary<ulong, DateTime> _levelAt = new Dictionary<ulong, DateTime>();
        readonly Dictionary<ulong, DateTime> _okAt = new Dictionary<ulong, DateTime>();

        List<Peripheral> _snapshot = new List<Peripheral>();

        /// <summary>Свежий список устройств. Приходит из фонового потока — на UI его надо перекинуть.</summary>
        public event Action<List<Peripheral>> Updated;

        public List<Peripheral> Snapshot { get { return _snapshot; } }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "SysWidget.Peripherals";
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            try { _wake.Set(); } catch { }
        }

        /// <summary>Разбудить обход — вызывается по WM_DEVICECHANGE, когда что-то подключили.</summary>
        public void RequestScan()
        {
            try { _wake.Set(); } catch { }
        }

        void Loop()
        {
            while (!_stop)
            {
                List<Peripheral> list = null;
                try
                {
                    list = PeripheralScanner.Scan();
                    FillBattery(list);
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("periph", ex);
                }

                if (list != null && !_stop)
                {
                    _snapshot = list;
                    Action<List<Peripheral>> h = Updated;
                    if (h != null)
                    {
                        try { h(list); } catch { }
                    }
                }

                if (_stop) break;
                _wake.WaitOne(ScanPeriodMs);
                _wake.Reset();
            }
        }

        /// <summary>
        /// Дописать заряд Bluetooth LE-устройствам. Читаем не чаще раза в минуту:
        /// каждое чтение — реальный обмен по радио, на 10-секундном обходе это
        /// был бы лишний расход батареи у самой мыши.
        /// </summary>
        void FillBattery(List<Peripheral> list)
        {
            DateTime now = DateTime.UtcNow;
            foreach (Peripheral p in list)
            {
                if (p.Address == 0) continue;

                DateTime at;
                if (_levelAt.TryGetValue(p.Address, out at)
                    && (now - at).TotalSeconds < BatteryPeriodSec)
                {
                    p.Battery = KnownLevel(p.Address, now);
                    continue;
                }

                int v = PeripheralScanner.ReadBleBattery(p.Address);
                _levelAt[p.Address] = now;
                if (v >= 0)
                {
                    _level[p.Address] = v;
                    _okAt[p.Address] = now;
                    p.Battery = v;
                }
                else
                {
                    // устройство уснуло или не отдаёт заряд — держим последнее
                    // удачное значение несколько минут, а не мигаем прочерком
                    p.Battery = KnownLevel(p.Address, now);
                }
            }
        }

        int KnownLevel(ulong addr, DateTime now)
        {
            int v;
            DateTime ok;
            if (!_level.TryGetValue(addr, out v)) return -1;
            if (!_okAt.TryGetValue(addr, out ok)) return -1;
            if ((now - ok).TotalSeconds > BatteryStaleSec) return -1;
            return v;
        }
    }

    /// <summary>
    /// Поиск периферии средствами Windows: SetupAPI + чтение службы батареи
    /// Bluetooth LE. Чужой виджет на панели задач делает то же самое — его
    /// история зарядов хранит ключи вида «bt:d2bceae823db», то есть адреса
    /// Bluetooth-устройств.
    /// </summary>
    static class PeripheralScanner
    {
        // ------------------------------------------------------------ SetupAPI

        const uint DIGCF_PRESENT = 0x2;
        const uint DIGCF_ALLCLASSES = 0x4;

        const uint SPDRP_DEVICEDESC = 0x00;
        const uint SPDRP_CLASS = 0x07;
        const uint SPDRP_MFG = 0x0B;
        const uint SPDRP_FRIENDLYNAME = 0x0C;

        const uint DEVPROP_TYPE_UINT32 = 7;
        const uint DEVPROP_TYPE_GUID = 13;
        const uint DEVPROP_TYPE_STRING = 18;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator,
            IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data,
            StringBuilder id, uint idSize, out uint required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data,
            uint property, out uint regType, byte[] buffer, uint bufferSize, out uint required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data,
            ref DEVPROPKEY key, out uint type, byte[] buffer, uint bufferSize,
            out uint required, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        // --------------------------------------------------- Bluetooth: связь
        // Сопряжённое, но выключенное устройство Windows всё равно заводит в
        // дереве PnP (у JBL Tune 520BT узлы видны, даже когда наушники лежат
        // в сумке). Поэтому «присутствует» ещё не значит «используется»:
        // у классического Bluetooth спрашиваем fConnected, у BLE — статус
        // соединения через WinRT. Драйверы для этого не нужны.

        [StructLayout(LayoutKind.Sequential)]
        struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public uint dwSize;
            public bool fReturnAuthenticated;
            public bool fReturnRemembered;
            public bool fReturnUnknown;
            public bool fReturnConnected;
            public bool fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct BLUETOOTH_DEVICE_INFO
        {
            public int dwSize;
            public ulong Address;
            public uint ClassOfDevice;
            public bool fConnected;
            public bool fRemembered;
            public bool fAuthenticated;
            public SYSTEMTIME LastSeen;
            public SYSTEMTIME LastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
            public string Name;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEMTIME
        {
            public ushort Year, Month, DayOfWeek, Day;
            public ushort Hour, Minute, Second, Milliseconds;
        }

        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern IntPtr BluetoothFindFirstDevice(
            ref BLUETOOTH_DEVICE_SEARCH_PARAMS search, ref BLUETOOTH_DEVICE_INFO info);

        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern bool BluetoothFindNextDevice(
            IntPtr find, ref BLUETOOTH_DEVICE_INFO info);

        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern bool BluetoothFindDeviceClose(IntPtr find);

        // DEVPKEY_Device_ContainerId: GUID контейнера физического устройства.
        static readonly DEVPROPKEY KeyContainerId = MakeKey("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);
        // Сведения Bluetooth: 4/5 — производитель и модель из GATT 0x180A,
        // а у классического Bluetooth в поле 4 лежит Class of Device (UInt32).
        static readonly DEVPROPKEY KeyBtProp = MakeKey("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A", 4);
        static readonly DEVPROPKEY KeyBtProp5 = MakeKey("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A", 5);

        static DEVPROPKEY MakeKey(string guid, uint pid)
        {
            var k = new DEVPROPKEY();
            k.fmtid = new Guid(guid);
            k.pid = pid;
            return k;
        }

        // ------------------------------------------------------------ модель

        sealed class Node
        {
            public string Id = "";
            public string Class = "";
            public string Friendly = "";
            public string Desc = "";
            public string Mfg = "";
            public string Container = "";
            // Свойства Bluetooth: у BLE в 4/5 лежат производитель и модель из
            // GATT 0x180A, у классического Bluetooth в 4 — Class of Device.
            public uint BtCod;
            public string BtManufacturer = "";
            public string BtModel = "";
        }

        sealed class Group
        {
            public string Key = "";
            public readonly List<Node> Nodes = new List<Node>();
            public string RootKind = "";    // "BLE", "BT", "USB"
            public string RootId = "";
            public ulong Address;
        }

        /// <summary>
        /// Один проход по всем присутствующим устройствам. Возвращает по
        /// устройству на контейнер, только внешнюю периферию (USB / Bluetooth).
        /// Встроенные тачпад, клавиатура и динамики отсекаются тем, что их
        /// верхний узел — ACPI или HDAUDIO, а не USB/Bluetooth.
        /// </summary>
        public static List<Peripheral> Scan()
        {
            var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
            {
                return new List<Peripheral>();
            }

            try
            {
                var data = new SP_DEVINFO_DATA();
                data.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    Node n = ReadNode(set, ref data);
                    if (n == null || n.Container.Length == 0) continue;

                    Group g;
                    if (!groups.TryGetValue(n.Container, out g))
                    {
                        g = new Group();
                        g.Key = n.Container;
                        groups[n.Container] = g;
                    }
                    g.Nodes.Add(n);
                }
            }
            finally
            {
                try { SetupDiDestroyDeviceInfoList(set); } catch { }
            }

            // Список известных классических Bluetooth-устройств нужен только
            // если среди групп есть BTHENUM-узел: у чистого BLE его не читаем.
            bool anyClassic = false;
            foreach (Group g in groups.Values)
            {
                foreach (Node n in g.Nodes)
                {
                    if (IsRoot(n.Id, "BTHENUM\\DEV_")) { anyClassic = true; break; }
                }
                if (anyClassic) break;
            }
            Dictionary<ulong, bool> classic = anyClassic ? ClassicConnections() : null;

            var result = new List<Peripheral>();
            foreach (Group g in groups.Values)
            {
                Peripheral p = Build(g, classic);
                if (p != null) result.Add(p);
            }

            result.Sort(Compare);
            return result;
        }

        // «Нулевой» контейнер Windows выдаёт узлам, у которых физического
        // устройства нет вовсе (служебные SWD-узлы, некоторые контроллеры).
        // Все такие узлы попадают в одну общую группу — её надо отбросить.
        static readonly Guid NullContainer = new Guid("00000000-0000-0000-FFFF-FFFFFFFFFFFF");
        static readonly Guid EmptyContainer = Guid.Empty;

        static Node ReadNode(IntPtr set, ref SP_DEVINFO_DATA data)
        {
            var sb = new StringBuilder(512);
            uint need;
            if (!SetupDiGetDeviceInstanceIdW(set, ref data, sb, (uint)sb.Capacity, out need))
                return null;

            var n = new Node();
            n.Id = sb.ToString();
            n.Class = GetRegString(set, ref data, SPDRP_CLASS);

            Guid cid = GetGuidProp(set, ref data, KeyContainerId);
            if (cid == EmptyContainer || cid == NullContainer) return n;
            n.Container = cid.ToString();

            // Имя и производитель нужны только внешним шинам и тем классам,
            // по которым мы вообще показываем устройство. Остальные узлы несут
            // в контейнер лишь класс — чтение семи свойств у сотни служебных
            // узлов замедляло обход в разы.
            bool root = n.Id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
                     || IsRoot(n.Id, "BTHLE\\DEV_") || IsRoot(n.Id, "BTHENUM\\DEV_");
            bool interesting = n.Class == "Mouse" || n.Class == "Keyboard"
                            || n.Class == "MEDIA" || n.Class == "Bluetooth";

            if (root || interesting)
            {
                n.Friendly = GetRegString(set, ref data, SPDRP_FRIENDLYNAME);
                n.Desc = GetRegString(set, ref data, SPDRP_DEVICEDESC);
                n.Mfg = GetRegString(set, ref data, SPDRP_MFG);
            }

            if (n.Class == "Bluetooth")
            {
                n.BtCod = GetUInt32Prop(set, ref data, KeyBtProp);
                n.BtManufacturer = GetStringProp(set, ref data, KeyBtProp);
                n.BtModel = GetStringProp(set, ref data, KeyBtProp5);
            }
            return n;
        }

        static string GetRegString(IntPtr set, ref SP_DEVINFO_DATA data, uint prop)
        {
            var buf = new byte[2048];
            uint type, need;
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, prop, out type, buf,
                    (uint)buf.Length, out need))
                return "";
            if (need < 2 || need > buf.Length) return "";
            return Encoding.Unicode.GetString(buf, 0, (int)need).TrimEnd('\0');
        }

        static Guid GetGuidProp(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
        {
            var buf = new byte[64];
            uint type, need;
            if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buf,
                    (uint)buf.Length, out need, 0))
                return Guid.Empty;
            if (type != DEVPROP_TYPE_GUID || need < 16) return Guid.Empty;
            var b = new byte[16];
            Array.Copy(buf, b, 16);
            return new Guid(b);
        }

        static string GetStringProp(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
        {
            var buf = new byte[1024];
            uint type, need;
            if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buf,
                    (uint)buf.Length, out need, 0))
                return "";
            if (type != DEVPROP_TYPE_STRING || need < 2) return "";
            return Encoding.Unicode.GetString(buf, 0, (int)Math.Min(need, (uint)buf.Length)).TrimEnd('\0');
        }

        static uint GetUInt32Prop(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
        {
            var buf = new byte[16];
            uint type, need;
            if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buf,
                    (uint)buf.Length, out need, 0))
                return 0;
            if (type != DEVPROP_TYPE_UINT32 || need < 4) return 0;
            return BitConverter.ToUInt32(buf, 0);
        }

        // ------------------------------------------------------ сборка устройства

        /// <summary>
        /// Состояние классических Bluetooth-устройств: адрес → подключено ли.
        /// Один проход по всем радиомодулям (hRadio = NULL), без опроса эфира.
        /// Если API недоступен, возвращаем пустую карту — тогда ничего не скрываем.
        /// </summary>
        static Dictionary<ulong, bool> ClassicConnections()
        {
            var map = new Dictionary<ulong, bool>();
            try
            {
                var sp = new BLUETOOTH_DEVICE_SEARCH_PARAMS();
                sp.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS));
                sp.fReturnAuthenticated = true;
                sp.fReturnRemembered = true;
                sp.fReturnUnknown = true;
                sp.fReturnConnected = true;
                sp.fIssueInquiry = false;          // без поиска: только известные
                sp.cTimeoutMultiplier = 0;
                sp.hRadio = IntPtr.Zero;           // все радиомодули

                var info = new BLUETOOTH_DEVICE_INFO();
                info.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));
                info.Name = "";

                IntPtr find = BluetoothFindFirstDevice(ref sp, ref info);
                if (find == IntPtr.Zero) return map;
                try
                {
                    while (true)
                    {
                        map[info.Address] = info.fConnected;
                        info = new BLUETOOTH_DEVICE_INFO();
                        info.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));
                        info.Name = "";
                        if (!BluetoothFindNextDevice(find, ref info)) break;
                    }
                }
                finally
                {
                    try { BluetoothFindDeviceClose(find); } catch { }
                }
            }
            catch (Exception ex) { ErrorLog.Write("periph.classic", ex.Message); }
            return map;
        }

        /// <summary>
        /// BLE-устройство реально на связи? FromBluetoothAddressAsync отдаёт
        /// объект только для доступного устройства; у выключенного — null.
        /// При сбое не скрываем устройство: лучше показать лишнее, чем убрать
        /// работающую мышь.
        /// </summary>
        static bool BleOnline(ulong address)
        {
            if (address == 0) return true;
#if WINRT
            Windows.Devices.Bluetooth.BluetoothLEDevice dev = null;
            try
            {
                dev = Wait(Windows.Devices.Bluetooth.BluetoothLEDevice
                    .FromBluetoothAddressAsync(address));
                return dev != null && dev.ConnectionStatus ==
                    Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("periph.online", ex.Message);
                return true;
            }
            finally
            {
                if (dev != null) { try { dev.Dispose(); } catch { } }
            }
#else
            return true;
#endif
        }

        static Peripheral Build(Group g, Dictionary<ulong, bool> classic)
        {
            bool mouse = false, keyboard = false, media = false, bt = false, ble = false, usb = false;

            foreach (Node n in g.Nodes)
            {
                if (n.Class == "Mouse") mouse = true;
                else if (n.Class == "Keyboard") keyboard = true;
                else if (n.Class == "MEDIA") media = true;

                if (IsRoot(n.Id, "BTHLE\\DEV_")) { ble = true; g.RootKind = "BLE"; g.RootId = n.Id; }
                else if (IsRoot(n.Id, "BTHENUM\\DEV_")) { bt = true; g.RootKind = "BT"; g.RootId = n.Id; }
                else if (n.Id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) && g.RootKind.Length == 0)
                {
                    usb = true; g.RootKind = "USB"; g.RootId = n.Id;
                }
            }

            // внутренние узлы: тачпад, встроенная клавиатура, динамики Realtek —
            // у них нет внешней шины USB/Bluetooth, поэтому они сюда не попадают
            if (!ble && !bt && !usb) return null;
            if (!mouse && !keyboard && !media) return null;

            var p = new Peripheral();
            p.Key = g.Key;

            if (ble) { p.Link = PeriphLink.BluetoothLe; p.Address = AddressOf(g.RootId); g.Address = p.Address; }
            else if (bt) { p.Link = PeriphLink.Bluetooth; g.Address = AddressOf(g.RootId); }
            else p.Link = PeriphLink.Usb;

            // Сопряжено, но не подключено — устройство не используется, и в
            // карточке ему делать нечего. Классический Bluetooth спрашиваем у
            // стека (fConnected), BLE — через статус соединения.
            if (bt && classic != null && g.Address != 0)
            {
                bool on;
                // если адреса нет в списке стека — не скрываем: это сбой
                // перечисления, а не доказанное «выключено»
                if (classic.TryGetValue(g.Address, out on) && !on) return null;
            }
            if (ble && !BleOnline(g.Address)) return null;

            if (mouse) p.Kind = PeriphKind.Mouse;
            else if (keyboard) p.Kind = PeriphKind.Keyboard;
            else p.Kind = AudioKind(g);
            // Общий приёмник 2,4 ГГц: мышь и клавиатура в одном USB-контейнере.
            // По Bluetooth у каждого устройства свой контейнер, и «комбо» там
            // только дублировало бы отдельную строку клавиатуры.
            p.HasKeyboard = keyboard && p.Link == PeriphLink.Usb;

            p.Name = NameOf(g, p.Kind);
            // у комбинированного приёмника имя-заглушка «Мышь» ничего не
            // добавляет к типу «Мышь + клавиатура» — не дублируем его
            if (p.HasKeyboard
                && string.Equals(p.Name, Lang.S("Мышь", "Mouse"), StringComparison.OrdinalIgnoreCase))
                p.Name = p.KindName;
            p.Manufacturer = ManufacturerOf(g, p.Name);
            p.Battery = -1;
            return p;
        }

        static bool IsRoot(string id, string prefix)
        {
            return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Адрес из «BTHLE\DEV_D2BCEAE823DB\…» — те самые 12 hex-символов.</summary>
        static ulong AddressOf(string rootId)
        {
            int i = rootId.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return 0;
            i += 4;
            int end = rootId.IndexOf('\\', i);
            string hex = end < 0 ? rootId.Substring(i) : rootId.Substring(i, end - i);
            if (hex.Length > 12) hex = hex.Substring(0, 12);
            ulong v;
            if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                    null, out v)) return v;
            return 0;
        }

        /// <summary>
        /// Наушники или колонка — по Class of Device классического Bluetooth:
        /// младшие шесть бит «minor device class» говорят прямо, что это.
        /// </summary>
        static PeriphKind AudioKind(Group g)
        {
            uint cod = 0;
            foreach (Node n in g.Nodes)
            {
                if (!string.Equals(n.Id, g.RootId, StringComparison.OrdinalIgnoreCase)) continue;
                cod = n.BtCod;
                break;
            }

            // Class of Device: major 0x04 — аудио/видео, minor 2..7 — тип
            if (cod != 0)
            {
                uint major = (cod >> 8) & 0x1F;
                uint minor = (cod >> 2) & 0x3F;
                if (major == 0x04)
                {
                    if (minor == 0x05 || minor == 0x0A) return PeriphKind.Speaker;
                    if (minor == 0x06 || minor == 0x01 || minor == 0x02 || minor == 0x07)
                        return PeriphKind.Headphones;
                }
            }
            return PeriphKind.Headphones;
        }

        static readonly string[] GenericNames =
        {
            "hid-совместим", "hid-compliant", "hid-совместимое", "hid device",
            "usb input device", "usb-устройство ввода", "составное устройство usb",
            "usb composite device", "клавиатура hid", "hid keyboard device",
            "мышь hid", "usb audio device", "аудиоустройство usb",
            "устройство hid", "hid-клавиатура", "hid-мышь"
        };

        static bool IsGeneric(string name)
        {
            string s = name.ToLowerInvariant();
            foreach (string g in GenericNames)
                if (s.Contains(g)) return true;
            return false;
        }

        /// <summary>
        /// Имя устройства: у Bluetooth это имя самого устройства (JBL Tune 520BT,
        /// FBK27CAS), у USB — имя узла, если оно не служебное («HID-совместимая
        /// мышь» ничего не говорит о модели).
        /// </summary>
        static string NameOf(Group g, PeriphKind kind)
        {
            foreach (Node n in g.Nodes)
            {
                if (!string.Equals(n.Id, g.RootId, StringComparison.OrdinalIgnoreCase)) continue;
                if (n.Friendly.Length > 0 && !IsGeneric(n.Friendly)) return n.Friendly;
                if (n.Desc.Length > 0 && !IsGeneric(n.Desc)) return n.Desc;
            }

            foreach (Node n in g.Nodes)
            {
                string s = n.Friendly.Length > 0 ? n.Friendly : n.Desc;
                if (s.Length > 0 && !IsGeneric(s)) return s;
            }

            if (kind == PeriphKind.Mouse) return Lang.S("Мышь", "Mouse");
            if (kind == PeriphKind.Keyboard) return Lang.S("Клавиатура", "Keyboard");
            if (kind == PeriphKind.Speaker) return Lang.S("Колонка", "Speaker");
            if (kind == PeriphKind.Headphones) return Lang.S("Наушники", "Headphones");
            return Lang.S("Аудио", "Audio");
        }

        /// <summary>
        /// Производитель: у Bluetooth LE он приходит из службы сведений об
        /// устройстве (GATT 0x180A) и Windows хранит его в свойстве устройства;
        /// у USB — из INF-файла. «Microsoft» в этом поле — служебная заглушка,
        /// её не показываем.
        /// </summary>
        static string ManufacturerOf(Group g, string name)
        {
            // GATT-производитель лежит на дочерних узлах (служба батареи и т. п.)
            foreach (Node n in g.Nodes)
            {
                if (n.BtManufacturer.Length > 0 && !IsGenericMfg(n.BtManufacturer))
                    return n.BtManufacturer;
            }

            foreach (Node n in g.Nodes)
            {
                if (n.Mfg.Length > 0 && !IsGenericMfg(n.Mfg)) return n.Mfg;
            }

            // у классического Bluetooth производителя в свойствах нет — берём
            // марку из имени («JBL Tune 520BT» → JBL)
            if (g.RootKind == "BT")
            {
                int sp = name.IndexOf(' ');
                if (sp >= 3 && sp <= 14)
                {
                    string brand = name.Substring(0, sp);
                    if (IsBrandLike(brand)) return brand;
                }
            }
            return "";
        }

        static bool IsGenericMfg(string m)
        {
            string s = m.ToLowerInvariant();
            return s == "microsoft" || s == "майкрософт" || s.Contains("standard system")
                || s == "(стандартные системные устройства)";
        }

        static bool IsBrandLike(string s)
        {
            bool letter = false;
            foreach (char c in s)
            {
                if (char.IsLetter(c)) letter = true;
                else if (c != '-' && c != '.' && c != '&') return false;
            }
            return letter;
        }

        static int Compare(Peripheral a, Peripheral b)
        {
            int ka = KindOrder(a.Kind), kb = KindOrder(b.Kind);
            if (ka != kb) return ka - kb;
            return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        }

        static int KindOrder(PeriphKind k)
        {
            if (k == PeriphKind.Mouse) return 0;
            if (k == PeriphKind.Keyboard) return 1;
            if (k == PeriphKind.Headphones) return 2;
            if (k == PeriphKind.Speaker) return 3;
            return 4;
        }

        // ------------------------------------------------------------ батарея

#if WINRT
        /// <summary>
        /// Заряд Bluetooth LE: служба батареи 0x180F, характеристика 0x2A19.
        /// WinRT-типы берём из WinMetadata, которая есть в составе Windows, —
        /// SDK и Windows Kits не нужны. Асинхронные операции ждём опросом
        /// статуса: встроенный await требует SDK-сборок.
        /// </summary>
        public static int ReadBleBattery(ulong address)
        {
            if (address == 0) return -1;
            Windows.Devices.Bluetooth.BluetoothLEDevice dev = null;
            try
            {
                dev = Wait(Windows.Devices.Bluetooth.BluetoothLEDevice
                    .FromBluetoothAddressAsync(address));
                if (dev == null) return -1;

                var res = Wait(dev.GetGattServicesForUuidAsync(
                    Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceUuids.Battery));
                if (res == null || res.Status !=
                    Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
                    return -1;
                var services = res.Services;
                if (services == null || services.Count == 0) return -1;

                var chars = Wait(services[0].GetCharacteristicsForUuidAsync(
                    Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicUuids.BatteryLevel));
                if (chars == null || chars.Status !=
                    Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
                    return -1;
                if (chars.Characteristics == null || chars.Characteristics.Count == 0) return -1;

                var read = Wait(chars.Characteristics[0].ReadValueAsync());
                if (read == null || read.Status !=
                    Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
                    return -1;
                if (read.Value == null) return -1;

                var reader = Windows.Storage.Streams.DataReader.FromBuffer(read.Value);
                if (reader.UnconsumedBufferLength < 1) return -1;
                return reader.ReadByte();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("periph.battery", ex.Message);
                return -1;
            }
            finally
            {
                if (dev != null)
                {
                    try { dev.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// Дождаться WinRT-операции без await. Отдаём управление каждые 10 мс,
        /// чтобы не жечь ядро; через 6 секунд операцию отменяем — уснувшее
        /// устройство иначе задерживало бы весь обход.
        /// </summary>
        static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            if (op == null) return default(T);
            int waited = 0;
            while (op.Status == Windows.Foundation.AsyncStatus.Started)
            {
                if (waited >= 6000)
                {
                    try { op.Cancel(); } catch { }
                    return default(T);
                }
                Thread.Sleep(10);
                waited += 10;
            }
            if (op.Status != Windows.Foundation.AsyncStatus.Completed) return default(T);
            return op.GetResults();
        }
#else
        /// <summary>
        /// На старых Windows без WinMetadata заряд Bluetooth LE не читаем:
        /// устройство всё равно будет в списке, просто без процентов.
        /// </summary>
        public static int ReadBleBattery(ulong address)
        {
            return -1;
        }
#endif
    }
}
