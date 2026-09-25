using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Pult.Services;

// Дополнительные сведения о системе: только BCL + P/Invoke. Все методы static, при неудаче — пусто.
public static class ExtraInfoService
{
    public record WifiNet(string Ssid, uint SignalPct, string Auth, bool Connected);
    public record TcpRow(string Proto, string Local, string Remote, string State, string Process);

    // ---------- Wi-Fi (wlanapi) ----------

    private enum WLAN_INTERFACE_STATE : int
    {
        NotReady = 0,
        Connected = 1,
        AdHocNetworkFormed = 2,
        Disconnecting = 3,
        Disconnected = 4,
        Associating = 5,
        Discovering = 6,
        Authenticating = 7,
    }

    private enum WLAN_INTF_OPCODE : int
    {
        AutoconfStart = 0,
        AutoconfEnabled = 1,
        BackgroundScanEnabled = 2,
        MediaStreamingMode = 3,
        RadioState = 4,
        BssType = 5,
        InterfaceState = 6,
        CurrentConnection = 7,
        ChannelNumber = 8,
        SupportedInfrastructureAuthCipherPairs = 9,
        SupportedAdhocAuthCipherPairs = 10,
        SupportedCountryOrRegionStringList = 11,
        CurrentOperationMode = 12,
        SupportedSafeMode = 13,
        CertifiedSafeMode = 14,
        HostedNetworkCapable = 15,
        ManagementFrameProtectionCapable = 16,
    }

    private enum WLAN_OPCODE_VALUE_TYPE
    {
        QueryOnly = 0,
        SetByGroupPolicy = 1,
        SetByUser = 2,
        Invalid = 3,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;
        public WLAN_INTERFACE_STATE isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID dot11Ssid;
        public uint dot11BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;
        public uint dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate;
        public uint ulTxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_SECURITY_ATTRIBUTES
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool bSecurityEnabled;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bOneXEnabled;
        public uint dot11AuthAlgorithm;
        public uint dot11CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_ATTRIBUTES
    {
        public WLAN_INTERFACE_STATE isState;
        public uint wlanConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
        public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(
        uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(
        IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(
        IntPtr hClientHandle, ref Guid pInterfaceGuid, WLAN_INTF_OPCODE OpCode,
        IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData,
        out WLAN_OPCODE_VALUE_TYPE pWlanOpcodeValueType);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanGetProfile(
        IntPtr hClientHandle, ref Guid pInterfaceGuid,
        [MarshalAs(UnmanagedType.LPWStr)] string strProfileName,
        IntPtr pReserved, out IntPtr pstrProfileXml,
        out uint pdwFlags, out uint pdwGrantedAccess);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    // Список Wi-Fi подключений/интерфейсов. Если Wi-Fi нет — пустой список.
    public static List<WifiNet> WifiNetworks()
    {
        var result = new List<WifiNet>();
        IntPtr client = IntPtr.Zero;
        IntPtr listPtr = IntPtr.Zero;
        try
        {
            uint negotiated;
            if (WlanOpenHandle(2, IntPtr.Zero, out negotiated, out client) != 0 || client == IntPtr.Zero)
                return result;
            if (WlanEnumInterfaces(client, IntPtr.Zero, out listPtr) != 0 || listPtr == IntPtr.Zero)
                return result;

            uint count = (uint)Marshal.ReadInt32(listPtr, 0);
            int itemSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
            for (int i = 0; i < count; i++)
            {
                IntPtr itemPtr = IntPtr.Add(listPtr, 8 + i * itemSize);
                WLAN_INTERFACE_INFO iface;
                try { iface = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(itemPtr); }
                catch { continue; }

                bool connected = iface.isState == WLAN_INTERFACE_STATE.Connected;
                string ssid = "";
                uint quality = 0;
                string auth = "";
                Guid guid = iface.InterfaceGuid;
                IntPtr dataPtr = IntPtr.Zero;
                try
                {
                    uint dataSize;
                    WLAN_OPCODE_VALUE_TYPE valueType;
                    if (WlanQueryInterface(client, ref guid, WLAN_INTF_OPCODE.CurrentConnection,
                            IntPtr.Zero, out dataSize, out dataPtr, out valueType) == 0
                        && dataPtr != IntPtr.Zero)
                    {
                        var attrs = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(dataPtr);
                        connected = attrs.isState == WLAN_INTERFACE_STATE.Connected;
                        try
                        {
                            var raw = attrs.wlanAssociationAttributes.dot11Ssid;
                            int len = (int)Math.Min(raw.uSSIDLength, raw.ucSSID == null ? 0 : raw.ucSSID.Length);
                            if (len > 0 && raw.ucSSID != null)
                                ssid = Encoding.UTF8.GetString(raw.ucSSID, 0, len);
                        }
                        catch { ssid = ""; }
                        quality = attrs.wlanAssociationAttributes.wlanSignalQuality;
                        // Профиль → authentication из XML.
                        try
                        {
                            string profile = attrs.strProfileName ?? "";
                            if (profile != "")
                            {
                                IntPtr xmlPtr = IntPtr.Zero;
                                try
                                {
                                    uint flags, access;
                                    if (WlanGetProfile(client, ref guid, profile, IntPtr.Zero,
                                            out xmlPtr, out flags, out access) == 0 && xmlPtr != IntPtr.Zero)
                                    {
                                        string xml = Marshal.PtrToStringUni(xmlPtr) ?? "";
                                        var m = Regex.Match(xml, "<authentication>(.*?)</",
                                            RegexOptions.IgnoreCase | RegexOptions.Singleline);
                                        if (m.Success)
                                            auth = m.Groups[1].Value.Trim();
                                    }
                                }
                                catch { }
                                finally { if (xmlPtr != IntPtr.Zero) WlanFreeMemory(xmlPtr); }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                finally { if (dataPtr != IntPtr.Zero) WlanFreeMemory(dataPtr); }

                if (ssid == "")
                    ssid = connected ? "(без имени)" : "(нет подключения)";
                result.Add(new WifiNet(ssid, quality, auth, connected));
            }
            return result;
        }
        catch { return new List<WifiNet>(); }
        finally { if (listPtr != IntPtr.Zero) WlanFreeMemory(listPtr); if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero); }
    }

    // ---------- Аудиоустройства ----------

    // Имена устройств воспроизведения (только включённые, DeviceState == 1).
    public static List<string> AudioDevices()
    {
        var result = new List<string>();
        try
        {
            using var render = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render");
            if (render == null)
                return result;
            string[] guids;
            try { guids = render.GetSubKeyNames(); }
            catch { return result; }
            foreach (string guid in guids)
            {
                try
                {
                    using var dev = render.OpenSubKey(guid);
                    if (dev == null)
                        continue;
                    object? st = dev.GetValue("DeviceState");
                    int state = st is int si ? si : (st != null && int.TryParse(st.ToString(), out int sv) ? sv : 0);
                    if (state != 1)
                        continue;
                    object? raw = Registry.GetValue(
                        $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{guid}\Properties",
                        "{a45c254e-df1c-4efd-8020-67d146a850e0},2", "");
                    string name = (raw?.ToString() ?? "").Trim();
                    if (name != "")
                        result.Add(name);
                }
                catch { }
            }
            return result;
        }
        catch { return new List<string>(); }
    }

    // ---------- Центр обновления ----------

    private static string Cut60(string s)
    {
        try
        {
            s = (s ?? "").Trim();
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }
        catch { return ""; }
    }

    // Время последнего поиска/установки обновлений и признак нужной перезагрузки.
    public static (string LastSearch, string LastInstall, bool RebootNeeded) WindowsUpdate()
    {
        try
        {
            string search = "";
            string install = "";
            try
            {
                object? rs = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Search",
                    "LastSuccessTime", "");
                search = Cut60(rs?.ToString() ?? "");
            }
            catch { }
            try
            {
                object? ri = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install",
                    "LastSuccessTime", "");
                install = Cut60(ri?.ToString() ?? "");
            }
            catch { }
            bool reboot = false;
            try
            {
                using var k1 = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
                using var k2 = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
                reboot = k1 != null || k2 != null;
            }
            catch { }
            return (search, install, reboot);
        }
        catch { return ("", "", false); }
    }

    // Проверка обновлений: открываем страницу Центра обновления
    // (надёжнее UsoClient, который на Win11 часто молчит).
    public static string StartUpdateScan()
    {
        return SystemMonitor.OpenDeElevated("ms-settings:windowsupdate-action");
    }

    // ---------- TCP/UDP (iphlpapi) ----------

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen, bool sort,
        int ipVersion, int tblClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int dwOutBufLen, bool sort,
        int ipVersion, int tblClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr;
        public uint localPort;
        public uint owningPid;
    }

    private static readonly string[] TcpStateNames =
    {
        "CLOSED", "CLOSING", "LISTEN", "SYN_SENT", "ESTABLISHED", "SYN_RCVD",
        "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_DELETE",
    };

    private static string TcpStateName(uint state)
    {
        try
        {
            if (state >= 1 && state <= TcpStateNames.Length)
                return TcpStateNames[state - 1];
            return "STATE_" + state;
        }
        catch { return ""; }
    }

    private static int NetPort(uint p)
    {
        // Порт в network order: младшие 2 байта DWORD.
        return (int)(((p >> 8) & 0xFF) | ((p & 0xFF) << 8));
    }

    private static string IpToString(uint addr)
    {
        try { return new System.Net.IPAddress(BitConverter.GetBytes(addr)).ToString(); }
        catch { return ""; }
    }

    private static string ProcName(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch { return pid.ToString(); }
    }

    // Активные TCP/UDP соединения IPv4. ESTABLISHED первыми. Лимит max.
    public static List<TcpRow> TcpConnections(int max = 30)
    {
        var rows = new List<TcpRow>();
        try
        {
            if (max <= 0)
                max = 30;
            // TCP
            IntPtr buf = IntPtr.Zero;
            try
            {
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (size > 0)
                {
                    buf = Marshal.AllocHGlobal(size);
                    if (GetExtendedTcpTable(buf, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) == 0)
                    {
                        int n = Marshal.ReadInt32(buf, 0);
                        int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                        for (int i = 0; i < n; i++)
                        {
                            try
                            {
                                IntPtr rp = IntPtr.Add(buf, 4 + i * rowSize);
                                var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rp);
                                string local = IpToString(r.localAddr) + ":" + NetPort(r.localPort);
                                string remote = IpToString(r.remoteAddr) + ":" + NetPort(r.remotePort);
                                rows.Add(new TcpRow("TCP", local, remote, TcpStateName(r.state), ProcName(r.owningPid)));
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }

            // UDP
            IntPtr ubuf = IntPtr.Zero;
            try
            {
                int size = 0;
                GetExtendedUdpTable(IntPtr.Zero, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0);
                if (size > 0)
                {
                    ubuf = Marshal.AllocHGlobal(size);
                    if (GetExtendedUdpTable(ubuf, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0) == 0)
                    {
                        int n = Marshal.ReadInt32(ubuf, 0);
                        int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                        for (int i = 0; i < n; i++)
                        {
                            try
                            {
                                IntPtr rp = IntPtr.Add(ubuf, 4 + i * rowSize);
                                var r = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rp);
                                string local = IpToString(r.localAddr) + ":" + NetPort(r.localPort);
                                rows.Add(new TcpRow("UDP", local, "—", "UDP", ProcName(r.owningPid)));
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            finally { if (ubuf != IntPtr.Zero) Marshal.FreeHGlobal(ubuf); }

            return rows
                .OrderByDescending(r => r.State == "ESTABLISHED")
                .Take(max)
                .ToList();
        }
        catch { return new List<TcpRow>(); }
    }

    // ---------- Большие системные папки ----------

    // Размеры системных папок с дедлайном. Complete=false, если упёрлись в дедлайн.
    public static List<(string Name, string Path, long Size, bool Complete)> BigSystemFolders(int seconds = 12)
    {
        var result = new List<(string Name, string Path, long Size, bool Complete)>();
        try
        {
            var folders = new (string Name, string Path)[]
            {
                ("Windows", @"C:\Windows"),
                ("Program Files", @"C:\Program Files"),
                ("Program Files (x86)", @"C:\Program Files (x86)"),
                ("ProgramData", @"C:\ProgramData"),
                ("Users", @"C:\Users"),
            };
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds <= 0 ? 12 : seconds);
            foreach (var (name, path) in folders)
            {
                long total = 0;
                bool complete = true;
                try
                {
                    if (!System.IO.Directory.Exists(path))
                    {
                        result.Add((name, path, 0, true));
                        continue;
                    }
                    var stack = new Stack<string>();
                    stack.Push(path);
                    while (stack.Count > 0)
                    {
                        if (DateTime.UtcNow > deadline) { complete = false; break; }
                        string dir = stack.Pop();
                        string[] files = Array.Empty<string>();
                        string[] dirs = Array.Empty<string>();
                        try { files = System.IO.Directory.GetFiles(dir); }
                        catch { continue; }
                        try { dirs = System.IO.Directory.GetDirectories(dir); }
                        catch { dirs = Array.Empty<string>(); }
                        foreach (string f in files)
                        {
                            if (DateTime.UtcNow > deadline) { complete = false; break; }
                            try { total += new System.IO.FileInfo(f).Length; }
                            catch { }
                        }
                        if (!complete && DateTime.UtcNow > deadline) break;
                        foreach (string d in dirs)
                        {
                            try { stack.Push(d); }
                            catch { }
                        }
                    }
                }
                catch { complete = false; }
                result.Add((name, path, total, complete));
                if (DateTime.UtcNow > deadline)
                {
                    // Остальные папки помечаем незавершёнными без обхода.
                    foreach (var rest in folders.Skip(result.Count))
                        result.Add((rest.Name, rest.Path, 0, false));
                    break;
                }
            }
            return result;
        }
        catch { return new List<(string Name, string Path, long Size, bool Complete)>(); }
    }

    // ---------- Ошибки журналов ----------

    // Последние ошибки из Application + System, суммарно не больше max.
    public static List<(DateTime Time, string Source, string Message)> EventErrors(int max = 15)
    {
        var all = new List<(DateTime Time, string Source, string Message)>();
        try
        {
            if (max <= 0)
                max = 15;
            foreach (string logName in new[] { "Application", "System" })
            {
                try
                {
                    using var log = new EventLog(logName);
                    EventLogEntryCollection entries = log.Entries;
                    int count = 0;
                    try { count = entries.Count; }
                    catch { continue; }
                    int taken = 0;
                    for (int i = count - 1; i >= 0 && taken < max; i--)
                    {
                        EventLogEntry? e = null;
                        try { e = entries[i]; }
                        catch { continue; }
                        try
                        {
                            if (e == null || e.EntryType != EventLogEntryType.Error)
                                continue;
                            string msg = (e.Message ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
                            if (msg.Length > 160)
                                msg = msg.Substring(0, 160);
                            all.Add((e.TimeGenerated, e.Source ?? "", msg));
                            taken++;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return all.OrderByDescending(x => x.Time).Take(max).ToList();
        }
        catch { return new List<(DateTime Time, string Source, string Message)>(); }
    }
}
