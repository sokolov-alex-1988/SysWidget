using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SysWidget
{
    /// <summary>
    /// Ярлыки (.lnk). Делаем через WScript.Shell — он есть на любой Windows и
    /// не требует ни COM-интерфейсов, ни сторонних библиотек.
    /// </summary>
    public static class Shortcuts
    {
        public const string AppId = "SysWidget.Desktop";

        /// <summary>Создаёт/перезаписывает ярлык на наш exe.</summary>
        public static void Create(string lnkPath, string exePath, string iconPath, string description)
        {
            try
            {
                Type shType = Type.GetTypeFromProgID("WScript.Shell");
                object sh = Activator.CreateInstance(shType);
                object lnk = shType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null,
                                                 sh, new object[] { lnkPath });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { exePath });
                // Пусто, а не «как было»: старый ярлык PowerShell-версии нёс
                // аргумент с путём к .vbs, и он переезжал бы вместе с целью.
                lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { "" });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk,
                                new object[] { Path.GetDirectoryName(exePath) });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk,
                                new object[] { iconPath + ",0" });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { description });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);

                Marshal.ReleaseComObject(lnk);
                Marshal.ReleaseComObject(sh);

                // Без System.AppUserModel.ID панель задач не связывает окно с ярлыком
                // и не даёт закрепить приложение.
                SetShortcutAppId(lnkPath, AppId);
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8), MarshalAs(UnmanagedType.LPWStr)] public string pwsz;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint c);
            [PreserveSig] int GetAt(uint i, out PROPERTYKEY k);
            [PreserveSig] int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
            [PreserveSig] int SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
            [PreserveSig] int Commit();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr reserved,
            uint flags, ref Guid iid, out IPropertyStore store);

        static bool SetShortcutAppId(string shortcutPath, string aumid)
        {
            var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
            IPropertyStore ps;
            if (SHGetPropertyStoreFromParsingName(shortcutPath, IntPtr.Zero, 2, ref iid, out ps) != 0)
                return false;
            try
            {
                var key = new PROPERTYKEY();
                key.fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
                key.pid = 5;                     // PKEY_AppUserModel_ID
                var pv = new PROPVARIANT();
                pv.vt = 31;                      // VT_LPWSTR
                pv.pwsz = aumid;
                ps.SetValue(ref key, ref pv);    // возвращает S_FALSE, но значение пишется
                ps.Commit();
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(ps);
            }
        }
    }
}
