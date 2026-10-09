/*
 * ==================================================================
 * 端口探测 (Services/PortInfo.cs)
 * ==================================================================
 * 用 GetExtendedTcpTable 一次读出"某进程正在监听的 TCP 端口"。
 *
 * 【为什么不用 netstat 子进程】
 *   每个端口起一个 netstat 子进程, 检测 20 多个端口就要几秒, 界面会卡;
 *   而且非英文系统上 netstat 的输出列名/格式不同, 解析会失败。
 *   直接读系统 TCP 监听表是一次调用、毫秒级, 也不受系统语言影响。
 *
 * 用途: 首页显示服务端 / 网页 GM 实际在听的端口 (S1A5: 游戏端口默认 10011,
 * 网页 GM 端口默认 5052)。
 * ==================================================================
 */
using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

namespace ServerUI.Services;

public static class PortInfo
{
    // TCP_TABLE_OWNER_PID_LISTENER = 3
    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
        bool sort, int ipVersion, int tblClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;      // 网络字节序, 低 16 位是端口
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    /// <summary>某进程正在监听的 TCP 端口列表 (升序); 查不到返回空表</summary>
    public static List<int> ListeningPorts(int pid)
    {
        var res = new List<int>();
        if (pid <= 0) return res;

        IntPtr buf = IntPtr.Zero;
        try
        {
            int len = 0;
            // 先问需要多大缓冲 (必然返回 ERROR_INSUFFICIENT_BUFFER = 122)
            GetExtendedTcpTable(IntPtr.Zero, ref len, false, 2, 3, 0);
            if (len <= 0) return res;

            buf = Marshal.AllocHGlobal(len);
            var rc = GetExtendedTcpTable(buf, ref len, false, 2, 3, 0);
            if (rc != 0) return res;

            int count = Marshal.ReadInt32(buf);
            var rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
            var ptr = (IntPtr)((long)buf + 4);
            for (int i = 0; i < count; i++)
            {
                var row = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(ptr, typeof(MIB_TCPROW_OWNER_PID));
                if (row.owningPid == (uint)pid)
                {
                    // 端口在网络字节序里存在低 16 位 → 按大端还原
                    int port = (int)(((row.localPort & 0xFF) << 8) | ((row.localPort >> 8) & 0xFF));
                    if (port > 0 && !res.Contains(port)) res.Add(port);
                }
                ptr = (IntPtr)((long)ptr + rowSize);
            }
            res.Sort();
        }
        catch { }
        finally { if (buf != IntPtr.Zero) try { Marshal.FreeHGlobal(buf); } catch { } }
        return res;
    }

    /// <summary>把 IPv4 的 uint 地址转成可读字符串</summary>
    public static string AddrText(uint addr)
    {
        try { return new IPAddress(addr).ToString(); } catch { return "--"; }
    }
}
