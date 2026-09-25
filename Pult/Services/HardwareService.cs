using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Pult.Services;

// Сведения о железе: только BCL + P/Invoke, без NuGet. Кэш на 120 секунд.
public static class HardwareService
{
    private static readonly object _lock = new();
    private static Dictionary<string, string>? _cached;
    private static DateTime _cachedAt = DateTime.MinValue;

    // P/Invoke: видеокарта (первый дисплей) и RAM.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX ms);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static Dictionary<string, string> GetInfo()
    {
        lock (_lock)
        {
            if (_cached != null && (DateTime.UtcNow - _cachedAt).TotalSeconds < 120)
                return new Dictionary<string, string>(_cached);
        }

        var d = new Dictionary<string, string>
        {
            ["os"] = ReadOs(),
            ["cpu"] = ReadCpu(),
            ["cpu_detail"] = ReadCpuDetail(),
            ["gpu"] = ReadGpu(),
            ["ram_total"] = ReadRamTotal(),
            ["board"] = ReadBoard(),
            ["bios"] = ReadBios(),
            ["uptime"] = ReadUptime(),
            ["user"] = ReadUser(),
            ["network"] = ReadNetwork(),
            ["battery"] = ReadBattery(),
        };

        lock (_lock)
        {
            _cached = new Dictionary<string, string>(d);
            _cachedAt = DateTime.UtcNow;
        }
        return d;
    }

    private static string RegString(RegistryHive hive, string path, string value)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(path);
            return key?.GetValue(value)?.ToString()?.Trim() ?? "";
        }
        catch { return ""; }
    }

    private static string ReadOs()
    {
        try
        {
            string name = RegString(RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");
            string ver = RegString(RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
            string os = (name + (ver == "" ? "" : " " + ver)).Trim();
            return os == "" ? Environment.OSVersion.ToString() : os;
        }
        catch { return Environment.OSVersion.ToString(); }
    }

    private static string ReadCpu()
    {
        return RegString(RegistryHive.LocalMachine,
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
    }

    private static string ReadCpuDetail()
    {
        try
        {
            int threads = Environment.ProcessorCount;
            string mhzRaw = RegString(RegistryHive.LocalMachine,
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz");
            string mhz = int.TryParse(mhzRaw, out int m) ? $"{m} МГц" : "—";
            return $"{threads} потоков / {mhz}";
        }
        catch { return ""; }
    }

    private static string ReadGpu()
    {
        try
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(null, 0, ref dd, 0))
                return (dd.DeviceString ?? "").Trim();
            return "";
        }
        catch { return ""; }
    }

    private static string ReadRamTotal()
    {
        try
        {
            var ms = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref ms) || ms.TotalPhys == 0) return "";
            return $"{ms.TotalPhys / 1024.0 / 1024 / 1024:F1} ГБ";
        }
        catch { return ""; }
    }

    private static bool IsJunk(string s)
    {
        string[] junk =
        {
            "default string", "to be filled", "not specified", "not available",
            "system product name", "product", "default", "none", "unknown",
            "o.e.m.", "oem", "type1family",
        };
        string t = s.Trim().ToLowerInvariant();
        if (t == "") return true;
        return junk.Any(j => t == j || t.Contains(j));
    }

    private static string ReadBoard()
    {
        try
        {
            string maker = RegString(RegistryHive.LocalMachine,
                @"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardManufacturer");
            string product = RegString(RegistryHive.LocalMachine,
                @"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct");
            var parts = new[] { maker, product }
                .Where(p => p != "" && !IsJunk(p)).ToArray();
            return string.Join(" ", parts);
        }
        catch { return ""; }
    }

    private static string ReadBios()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default)
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (key == null) return "";
            object? raw = key.GetValue("BIOSVersion");
            string[] values = raw switch
            {
                string[] arr => arr,
                string s => new[] { s },
                _ => Array.Empty<string>(),
            };
            foreach (string v in values)
            {
                string t = (v ?? "").Trim();
                if (t == "" || IsJunk(t)) continue;
                // Отсекаем шапки вида "BIOS Date: ..." — оставляем только версию.
                if (t.StartsWith("BIOS ", StringComparison.OrdinalIgnoreCase)
                    && t.Contains(':')) continue;
                return t;
            }
            return "";
        }
        catch { return ""; }
    }

    private static string ReadUptime()
    {
        try
        {
            var ts = TimeSpan.FromMilliseconds(Environment.TickCount64);
            if (ts.TotalMinutes < 1) return "меньше минуты";
            if (ts.Days > 0) return $"{ts.Days}д {ts.Hours}ч {ts.Minutes}м";
            if (ts.Hours > 0) return $"{ts.Hours}ч {ts.Minutes}м";
            return $"{ts.Minutes}м";
        }
        catch { return ""; }
    }

    private static string ReadUser()
    {
        try { return Environment.UserName; }
        catch { return ""; }
    }

    private static string ReadNetwork()
    {
        try
        {
            var entry = Dns.GetHostEntry(Dns.GetHostName());
            var ips = entry.AddressList
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Take(3)
                .Select(a => a.ToString())
                .ToArray();
            return string.Join(", ", ips);
        }
        catch { return ""; }
    }

    private static string ReadBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var st)) return "";
            if (st.BatteryLifePercent == 255) return "";
            string state = st.ACLineStatus == 1 ? "заряжается" : "от батареи";
            return $"{st.BatteryLifePercent}% ({state})";
        }
        catch { return ""; }
    }
}
