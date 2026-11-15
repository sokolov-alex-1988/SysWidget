using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

namespace SysWidget
{
    /// <summary>
    /// Установленные TCP-соединения по процессам: у каждого соединения известен номер
    /// процесса-владельца (GetExtendedTcpTable, TCP_TABLE_OWNER_PID_ALL). Это работает
    /// без прав администратора; сколько байт прошло по соединению, таким способом узнать
    /// нельзя — только «кто с кем соединён». Локальные адреса (127.x, ::1) пропускаются.
    /// </summary>
    public static class NetConnections
    {
        public sealed class Conn
        {
            public int Pid;
            public string Remote = "";      // «203.0.113.5:443»
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

        const int AF_INET = 2, AF_INET6 = 23;
        const int TCP_TABLE_OWNER_PID_ALL = 5;
        const uint ERROR_INSUFFICIENT_BUFFER = 122;
        const uint StateEstablished = 5;

        /// <summary>Все установленные соединения (IPv4 и IPv6) без петель.</summary>
        public static List<Conn> Read()
        {
            var res = new List<Conn>();
            try { Read4(res); } catch { }
            try { Read6(res); } catch { }
            return res;
        }

        static IntPtr Fetch(int family, out int count)
        {
            count = 0;
            int size = 0;
            uint r = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
            if (r != ERROR_INSUFFICIENT_BUFFER || size <= 0) return IntPtr.Zero;

            // таблица могла вырасти между вызовами: берём с запасом и повторяем
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size + 4096);
                int sz = size + 4096;
                r = GetExtendedTcpTable(buf, ref sz, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
                if (r == 0) { count = Marshal.ReadInt32(buf); return buf; }
                Marshal.FreeHGlobal(buf);
                if (r != ERROR_INSUFFICIENT_BUFFER) return IntPtr.Zero;
                size = sz;
            }
            return IntPtr.Zero;
        }

        static void Read4(List<Conn> res)
        {
            int n;
            IntPtr buf = Fetch(AF_INET, out n);
            if (buf == IntPtr.Zero) return;
            try
            {
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid
                const int RowSize = 24;
                for (int i = 0; i < n; i++)
                {
                    IntPtr row = new IntPtr(buf.ToInt64() + 4 + (long)i * RowSize);
                    uint state = (uint)Marshal.ReadInt32(row, 0);
                    if (state != StateEstablished) continue;
                    uint remote = (uint)Marshal.ReadInt32(row, 12);
                    uint rport = (uint)Marshal.ReadInt32(row, 16);
                    int pid = Marshal.ReadInt32(row, 20);

                    var ip = new IPAddress(remote);                 // порядок байт уже сетевой
                    if (IPAddress.IsLoopback(ip) || remote == 0) continue;
                    res.Add(new Conn { Pid = pid, Remote = ip + ":" + Port(rport) });
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static void Read6(List<Conn> res)
        {
            int n;
            IntPtr buf = Fetch(AF_INET6, out n);
            if (buf == IntPtr.Zero) return;
            try
            {
                // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort,
                //                        remoteAddr[16], remoteScope, remotePort, state, owningPid
                const int RowSize = 56;
                for (int i = 0; i < n; i++)
                {
                    IntPtr row = new IntPtr(buf.ToInt64() + 4 + (long)i * RowSize);
                    uint state = (uint)Marshal.ReadInt32(row, 48);
                    if (state != StateEstablished) continue;
                    var addr = new byte[16];
                    Marshal.Copy(new IntPtr(row.ToInt64() + 24), addr, 0, 16);
                    uint rport = (uint)Marshal.ReadInt32(row, 44);
                    int pid = Marshal.ReadInt32(row, 52);

                    var ip = new IPAddress(addr);
                    if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6Any)) continue;
                    res.Add(new Conn { Pid = pid, Remote = "[" + ip + "]:" + Port(rport) });
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>Порт лежит в младших 16 битах в сетевом порядке байт.</summary>
        static int Port(uint raw)
        {
            return (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
        }
    }
}
