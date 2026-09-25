using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Pult.Services;

// Железо без NuGet-пакетов: kernel32 + DriveInfo. Порт system_tools.py.
public sealed record DiskInfo(string Name, long FreeBytes, long TotalBytes, string Label = "", string Format = "")
{
    public bool IsReady => TotalBytes > 0;
    public double UsedPct => TotalBytes <= 0 ? 0 : (TotalBytes - FreeBytes) * 100.0 / TotalBytes;
    public static string Gb(long b) => $"{b / 1024.0 / 1024 / 1024:F1} ГБ";
}

public sealed class SystemMonitor
{
    // Без этого GetEncoding(866) кидает — и ВЕСЬ вывод внешних
    // процессов (powercfg, pnputil, sc) молча превращается в "".
    static SystemMonitor()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(
                System.Text.CodePagesEncodingProvider.Instance);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; }

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

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX ms);

    private static ulong ToU64(FILETIME t) => ((ulong)t.High << 32) | t.Low;

    private ulong _prevIdle, _prevTotal;
    private bool _primed;

    public SystemMonitor() => PrimeCpu();

    private void PrimeCpu()
    {
        if (!GetSystemTimes(out var i, out var k, out var u)) return;
        _prevIdle = ToU64(i);
        _prevTotal = ToU64(k) + ToU64(u);
        _primed = true;
    }

    public double CpuPercent()
    {
        if (!_primed) PrimeCpu();
        if (!GetSystemTimes(out var i, out var k, out var u)) return 0;
        ulong idle = ToU64(i), total = ToU64(k) + ToU64(u);
        ulong dIdle = idle - _prevIdle, dTotal = total - _prevTotal;
        _prevIdle = idle;
        _prevTotal = total;
        if (dTotal == 0) return 0;
        return Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100);
    }

    public (double UsedPct, long UsedBytes, long TotalBytes) Memory()
    {
        var ms = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref ms)) return (0, 0, 0);
        long used = (long)(ms.TotalPhys - ms.AvailPhys);
        double pct = ms.TotalPhys == 0 ? 0 : used * 100.0 / ms.TotalPhys;
        return (pct, used, (long)ms.TotalPhys);
    }

    // ---- Процессы: общий чёрный список для UI и чата ----

    public static readonly HashSet<string> ProtectedProcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "csrss", "wininit", "services", "lsass",
        "winlogon", "smss", "svchost", "dwm", "explorer", "registry",
        "memory compression", "pult", "msmpeng", "nissrv", "spoolsv",
        "audiodg",
    };

    public static bool IsProtectedProc(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        string n = name.Trim().ToLowerInvariant();
        if (n.EndsWith(".exe")) n = n[..^4];
        return ProtectedProcs.Contains(n);
    }

    // Запуск без повышения прав: из-под админа explorer.exe сбрасывает токен.
    public static string OpenDeElevated(string pathOrUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{pathOrUrl}\"")
            {
                UseShellExecute = false,
            });
            return $"Открыл: {pathOrUrl}";
        }
        catch (Exception ex) { return $"Ошибка открытия: {ex.Message}"; }
    }

    public static List<DiskInfo> Disks()
    {
        var list = new List<DiskInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                // Показываем ВСЕ диски кроме CD-ROM, включая USB и сетевые.
                // Неготовые (пустой картридер) — серой строкой, а не пропуском.
                if (d.DriveType == DriveType.CDRom) continue;
                string name = d.Name.TrimEnd('\\');
                if (!d.IsReady)
                {
                    list.Add(new DiskInfo(name, 0, 0, "", ""));
                    continue;
                }
                string label = "";
                string fmt = "";
                try { label = d.VolumeLabel ?? ""; } catch { }
                try { fmt = d.DriveFormat ?? ""; } catch { }
                list.Add(new DiskInfo(name, d.AvailableFreeSpace, d.TotalSize, label, fmt));
            }
            catch { /* чужой диск — пропускаем */ }
        }
        return list;
    }

    public static string DiskReport()
    {
        var disks = Disks().Where(d => d.IsReady).ToList();
        if (disks.Count == 0) return "Готовых дисков не найдено.";
        var lines = new List<string>();
        foreach (var d in disks)
            lines.Add($"Диск {d.Name}: свободно {DiskInfo.Gb(d.FreeBytes)} из {DiskInfo.Gb(d.TotalBytes)} ({d.UsedPct:F0}% занято)");
        return string.Join("\n", lines);
    }

    // ---- Сеть: живой трафик по основному интерфейсу ----

    private static long _prevRx, _prevTx;
    private static DateTime _prevNetTs = DateTime.MinValue;
    private static string _prevIface = "";

    public static (string Iface, double DownKbps, double UpKbps) NetworkSpeed()
    {
        try
        {
            NetworkInterface? best = null;
            IPv4InterfaceStatistics? bestStats = null;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var st = ni.GetIPv4Statistics();
                    if (st.BytesReceived <= 0 && st.BytesSent <= 0) continue;
                    if (best == null || st.BytesReceived + st.BytesSent
                        > bestStats!.BytesReceived + bestStats.BytesSent)
                    {
                        best = ni;
                        bestStats = st;
                    }
                }
                catch { }
            }
            if (best == null || bestStats == null) return ("", 0, 0);
            DateTime now = DateTime.UtcNow;
            double down = 0, up = 0;
            if (_prevNetTs != DateTime.MinValue && best.Name == _prevIface)
            {
                double sec = Math.Max(0.5, (now - _prevNetTs).TotalSeconds);
                down = Math.Max(0, bestStats.BytesReceived - _prevRx) / sec;
                up = Math.Max(0, bestStats.BytesSent - _prevTx) / sec;
            }
            _prevRx = bestStats.BytesReceived;
            _prevTx = bestStats.BytesSent;
            _prevNetTs = now;
            _prevIface = best.Name;
            return (best.Name, down / 1024.0, up / 1024.0);
        }
        catch { return ("", 0, 0); }
    }

    // ---- Драйверы: GPU-версия из реестра + проблемные устройства через pnputil ----

    public static (string GpuVersion, string GpuDate) GpuDriver()
    {
        try
        {
            using var video = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Video");
            if (video == null) return ("", "");
            foreach (string guid in video.GetSubKeyNames())
            {
                if (!guid.StartsWith("{")) continue;
                try
                {
                    using var k = video.OpenSubKey(guid + @"\0000");
                    if (k == null) continue;
                    string ver = (k.GetValue("DriverVersion") as string ?? "").Trim();
                    if (ver.Length == 0) continue;
                    string date = "";
                    try
                    {
                        object? dd = k.GetValue("DriverDate");
                        if (dd is string s && s.Length >= 8)
                            date = $"{s.Substring(6, 2)}.{s.Substring(4, 2)}.{s.Substring(0, 4)}";
                    }
                    catch { }
                    return (ver, date);
                }
                catch { }
            }
        }
        catch { }
        return ("", "");
    }

    public static List<string> ProblemDevices(int max = 10)
    {
        var res = new List<string>();
        try
        {
            var pr = Proc.Run("pnputil.exe", "/enum-devices /problem", 15000);
            string output = pr.TimedOut ? "" : pr.Stdout;
            if (output.Contains("не найден") || output.Contains("No devices")
                || output.Trim().Length == 0) return res;
            var blocks = output.Split(new[] { "\r\n\r\n", "\n\n" },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string b in blocks)
            {
                if (res.Count >= max) break;
                var lines = b.Split(new[] { '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("Microsoft PnP"))
                    .Take(2).ToList();
                if (lines.Count > 0) res.Add(string.Join(" — ", lines));
            }
        }
        catch { }
        return res;
    }

    // ---- Программы: топ по размеру из Uninstall-веток ----

    public sealed record ProgramInfo(string Name, string Version, long SizeKb, string Publisher, string Uninstall = "");

    public static System.Text.Encoding Oem()
    {
        try { return System.Text.Encoding.GetEncoding(866); }
        catch { return System.Text.Encoding.UTF8; }
    }

    private static System.Text.Encoding GetOemEncoding() => Oem();

    public static string RunCmd(string exe, string args, int timeoutMs = 30000)
    {
        var r = Proc.Run(exe, args, timeoutMs);
        if (r.TimedOut) return "";
        return r.Stdout;
    }

    public static string ServiceControl(string name, string action)
    {
        // action: start | stop | config-auto | config-demand | config-disabled
        try
        {
            string args = action switch
            {
                "start" => $"start \"{name}\"",
                "stop" => $"stop \"{name}\"",
                "config-auto" => $"config \"{name}\" start= auto",
                "config-demand" => $"config \"{name}\" start= demand",
                "config-disabled" => $"config \"{name}\" start= disabled",
                _ => "",
            };
            if (args.Length == 0) return "Неизвестное действие.";
            string output = RunCmd("sc.exe", args, 30000);
            if (output.Contains("[SC]") && (output.Contains("STATE") || output.Contains("SUCCESS")))
                return "Готово.";
            string err = output.Trim();
            if (err.Length > 300) err = err[..300];
            return err.Length == 0 ? "Готово." : err;
        }
        catch (Exception ex) { return $"Ошибка: {ex.Message}"; }
    }

    public static (int Total, List<ProgramInfo> Top) InstalledPrograms(int top = 15)
    {
        var all = new Dictionary<string, ProgramInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (string hive in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        })
        {
            foreach (var root in new[]
            {
                Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser,
            })
            {
                try
                {
                    using var k = root.OpenSubKey(hive);
                    if (k == null) continue;
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        try
                        {
                            using var app = k.OpenSubKey(sub);
                            if (app == null) continue;
                            string name = (app.GetValue("DisplayName") as string ?? "").Trim();
                            if (name.Length == 0) continue;
                            object? sys = app.GetValue("SystemComponent");
                            if (sys is int si && si == 1) continue;
                            string ver = (app.GetValue("DisplayVersion") as string ?? "").Trim();
                            long size = 0;
                            try
                            {
                                object? es = app.GetValue("EstimatedSize");
                                if (es is int i) size = i;
                            }
                            catch { }
                            string pub = (app.GetValue("Publisher") as string ?? "").Trim();
                            string uninst = (app.GetValue("UninstallString") as string ?? "").Trim();
                            if (!all.ContainsKey(name))
                                all[name] = new ProgramInfo(name, ver, Math.Max(0, size), pub, uninst);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        var sorted = all.Values.OrderByDescending(p => p.SizeKb).ToList();
        return (all.Count, sorted.Take(Math.Max(1, top)).ToList());
    }

    // ---- Службы: прямое SCM API (sc.exe виснет на пайпе) ----

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumServicesStatusEx(IntPtr dbHandle, int infoLevel,
        uint serviceType, uint serviceState, byte[]? buffer, uint bufSize,
        out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle,
        string? groupName);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUM_SERVICE_STATUS_PROCESS
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string ServiceName;
        [MarshalAs(UnmanagedType.LPWStr)] public string DisplayName;
        public SERVICE_STATUS_PROCESS Status;
    }

    public static string UninstallProgram(ProgramInfo p)
    {
        try
        {
            string cmd = (p.Uninstall ?? "").Trim();
            if (cmd.Length == 0) return "Нет команды удаления.";
            string exe, args;
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end < 0) return "Кривая команда удаления.";
                exe = cmd[1..end];
                args = cmd[(end + 1)..].Trim();
            }
            else
            {
                int sp = cmd.IndexOf(' ');
                if (sp < 0) { exe = cmd; args = ""; }
                else { exe = cmd[..sp]; args = cmd[(sp + 1)..].Trim(); }
            }
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            return $"Запущено удаление: {p.Name}.";
        }
        catch (Exception ex) { return $"Не вышло запустить удаление: {ex.Message}"; }
    }

    public static (int Running, int Total, List<(string Name, string Display)> AutoStopped) ServicesInfo(int max = 10)
    {
        int running = 0, total = 0;
        var bad = new List<(string, string)>();
        IntPtr scm = IntPtr.Zero;
        try
        {
            scm = OpenSCManager(null, null, 0x0004);
            if (scm == IntPtr.Zero) return (0, 0, bad);
            uint resume = 0, needed = 0, returned = 0;
            EnumServicesStatusEx(scm, 0, 0x00000030, 0x00000003,
                null, 0, out needed, out returned, ref resume, null);
            if (needed == 0) return (0, 0, bad);
            var buf = new byte[needed];
            resume = 0;
            if (!EnumServicesStatusEx(scm, 0, 0x00000030, 0x00000003,
                buf, (uint)buf.Length, out needed, out returned, ref resume, null))
                return (0, 0, bad);
            int structSize = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
            unsafe
            {
                // Цикл ВНУТРИ fixed: указатель живёт только пока массив закреплён.
                fixed (byte* p = buf)
                {
                    IntPtr basePtr = (IntPtr)p;
                    for (int i = 0; i < returned; i++)
                    {
                        IntPtr cur = basePtr + i * structSize;
                        var essp = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(cur);
                        total++;
                        bool isRunning = essp.Status.CurrentState == 4;
                        if (isRunning) running++;
                        else if (bad.Count < max && IsAutoStart(essp.ServiceName))
                            bad.Add((essp.ServiceName,
                                essp.DisplayName.Length > 0 ? essp.DisplayName : essp.ServiceName));
                    }
                }
            }
        }
        catch { }
        finally
        {
            if (scm != IntPtr.Zero)
                try { CloseServiceHandle(scm); } catch { }
        }
        return (running, total, bad);
    }

    private static bool IsAutoStart(string serviceName)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + serviceName);
            object? v = k?.GetValue("Start");
            // 2 = auto, 0 = boot, 1 = system. Интересует именно auto.
            return v is int i && i == 2;
        }
        catch { return false; }
    }

    // ---- Батарея подробно + подкачка + установка Windows ----

    public static string BatteryDetail()
    {
        try
        {
            SYSTEM_POWER_STATUS st;
            if (!GetSystemPowerStatus(out st)) return "";
            if (st.BatteryFlag == 128) return "Нет батареи (ПК).";
            string pct = st.BatteryLifePercent > 100 ? "—" : $"{st.BatteryLifePercent}%";
            string state = st.ACLineStatus == 1 ? "заряжается"
                : st.ACLineStatus == 0 ? "от батареи" : "";
            string left = st.BatteryLifeTime > 0 && st.BatteryLifeTime < uint.MaxValue
                ? $", осталось ~{st.BatteryLifeTime / 3600}ч {(st.BatteryLifeTime % 3600) / 60}м"
                : "";
            string crit = ((st.BatteryFlag & 4) != 0 || (st.BatteryFlag & 8) != 0)
                ? " [низкий заряд]" : "";
            return $"{pct} ({state}{left}){crit}".Trim();
        }
        catch { return ""; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static (long UsedBytes, long TotalBytes) PageFile()
    {
        try
        {
            var ms = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref ms)) return (0, 0);
            long total = (long)ms.TotalPageFile;
            long avail = (long)ms.AvailPageFile;
            if (total <= 0) return (0, 0);
            return (total - avail, total);
        }
        catch { return (0, 0); }
    }

    public static (string InstallDate, string SecureBoot, string BootTime) WindowsInfo()
    {
        string install = "", sb = "", boot = "";
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            object? v = k?.GetValue("InstallDate");
            if (v is int unix)
                install = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("d MMMM yyyy");
        }
        catch { }
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            object? v = k?.GetValue("UEFISecureBootEnabled");
            if (v is int i) sb = i == 1 ? "включён" : i == 0 ? "выключен" : "";
        }
        catch { }
        try
        {
            uint ticks = (uint)Environment.TickCount64;
            DateTime bootTime = DateTime.Now.AddMilliseconds(-ticks);
            TimeSpan up = DateTime.Now - bootTime;
            boot = up.TotalDays >= 1 ? $"{(int)up.TotalDays}д назад" : $"{(int)up.TotalHours}ч назад";
        }
        catch { }
        return (install, sb, boot);
    }

    // ---- Сетевые адаптеры (имя, тип, скорость, MAC, IP) ----

    public sealed record AdapterInfo(string Name, string Kind, long SpeedMbps, string Mac, string Ip);

    public static List<AdapterInfo> Adapters()
    {
        var res = new List<AdapterInfo>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    string nm = ni.Name ?? "";
                    // Виртуальные фильтры (WFP/QoS/LightWeight) засоряют список — их десятки с одним MAC.
                    if (nm.Contains("WFP", StringComparison.OrdinalIgnoreCase)
                        || nm.Contains("QoS", StringComparison.OrdinalIgnoreCase)
                        || nm.Contains("LightWeight", StringComparison.OrdinalIgnoreCase)
                        || nm.Contains("Filter-000", StringComparison.OrdinalIgnoreCase)
                        || nm.Contains("Miniport", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string kind = ni.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Ethernet => "Ethernet",
                        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                        _ => ni.NetworkInterfaceType.ToString(),
                    };
                    long mbps = ni.Speed > 0 ? ni.Speed / 1_000_000 : 0;
                    string mac = "";
                    try
                    {
                        byte[] bytes = ni.GetPhysicalAddress()?.GetAddressBytes() ?? Array.Empty<byte>();
                        if (bytes.Length > 0)
                            mac = string.Join("-", bytes.Select(b => b.ToString("X2")));
                    }
                    catch { }
                    string ip = "";
                    try
                    {
                        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily
                                == System.Net.Sockets.AddressFamily.InterNetwork
                                && !System.Net.IPAddress.IsLoopback(ua.Address)
                                && !ua.Address.ToString().StartsWith("169.254."))
                            {
                                ip = ua.Address.ToString();
                                break;
                            }
                        }
                    }
                    catch { }
                    // Виртуальные «Подключения по локальной сети*» без IP — мусор фильтров.
                    if (ip.Length == 0 && (nm.Contains("Подключение по локальной сети", StringComparison.OrdinalIgnoreCase)
                        || nm.Contains("Local Area Connection", StringComparison.OrdinalIgnoreCase)
                        || nm.Contains('*')))
                        continue;
                    res.Add(new AdapterInfo(nm, kind, mbps, mac, ip));
                }
                catch { }
            }
        }
        catch { }
        return res;
    }

    // ---- Папки пользователя с размерами (куда делось место) ----

    public static List<(string Name, string Path, long Size, bool Complete)> HomeFolders(int seconds = 10)
    {
        var res = new List<(string, string, long, bool)>();
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var targets = new (string Name, string Path)[]
            {
                ("Рабочий стол", Path.Combine(home, "Desktop")),
                ("Загрузки", Path.Combine(home, "Downloads")),
                ("Документы", Path.Combine(home, "Documents")),
                ("Картинки", Path.Combine(home, "Pictures")),
                ("Видео", Path.Combine(home, "Videos")),
                ("Музыка", Path.Combine(home, "Music")),
            };
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(3, seconds));
            foreach (var (name, path) in targets)
            {
                long size = 0;
                bool complete = true;
                try
                {
                    if (!Directory.Exists(path))
                    {
                        res.Add((name, path, 0, true));
                        continue;
                    }
                    var stack = new Stack<string>();
                    stack.Push(path);
                    while (stack.Count > 0)
                    {
                        if (DateTime.UtcNow >= deadline) { complete = false; break; }
                        string dir = stack.Pop();
                        string[] subs = Array.Empty<string>();
                        try { subs = Directory.GetDirectories(dir); }
                        catch { continue; }
                        string[] files = Array.Empty<string>();
                        try { files = Directory.GetFiles(dir); }
                        catch { }
                        foreach (string f in files)
                        {
                            try { size += new FileInfo(f).Length; }
                            catch { }
                        }
                        foreach (string s in subs) stack.Push(s);
                    }
                }
                catch { complete = false; }
                res.Add((name, path, size, complete));
            }
        }
        catch { }
        return res;
    }

    // ---- Мониторы (разрешение + главный) ----

    public sealed record MonitorInfo(int W, int H, bool Primary);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip,
        MonitorEnumProc callback, IntPtr data);

    private delegate bool MonitorEnumProc(IntPtr hmon, IntPtr hdc,
        ref RECT rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public uint Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    public static List<MonitorInfo> Monitors()
    {
        var res = new List<MonitorInfo>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hmon, IntPtr hdc, ref RECT r, IntPtr d) =>
                {
                    try
                    {
                        var mi = new MONITORINFO { Size = (uint)Marshal.SizeOf<MONITORINFO>() };
                        if (GetMonitorInfo(hmon, ref mi))
                            res.Add(new MonitorInfo(
                                mi.Monitor.Right - mi.Monitor.Left,
                                mi.Monitor.Bottom - mi.Monitor.Top,
                                (mi.Flags & 1) != 0));
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
        }
        catch { }
        return res;
    }

    // ---- Автозагрузка: чтение + вкл/выкл с бэкапом ----

    public sealed record StartupEntry(string Name, string Location, string Command,
        string Kind, string KeyPath);

    private static string DisabledFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Pult", "disabled_startup.json");

    private static Dictionary<string, StartupEntry> LoadDisabled()
    {
        try
        {
            string f = DisabledFile;
            if (!File.Exists(f)) return new Dictionary<string, StartupEntry>();
            var d = JsonDeserialize<Dictionary<string, StartupEntry>>(File.ReadAllText(f));
            return d ?? new Dictionary<string, StartupEntry>();
        }
        catch { return new Dictionary<string, StartupEntry>(); }
    }

    private static T? JsonDeserialize<T>(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json);
        }
        catch { return default; }
    }

    private static void SaveDisabled(Dictionary<string, StartupEntry> d)
    {
        try
        {
            string f = DisabledFile;
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, System.Text.Json.JsonSerializer.Serialize(d,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static List<StartupEntry> StartupEntries()
    {
        var res = new List<StartupEntry>();
        void FromRegistry(string hive, string sub, string loc)
        {
            try
            {
                using var root = hive == "HKCU"
                    ? Microsoft.Win32.Registry.CurrentUser
                    : Microsoft.Win32.Registry.LocalMachine;
                using var k = root.OpenSubKey(sub);
                if (k == null) return;
                foreach (string name in k.GetValueNames())
                {
                    string cmd = (k.GetValue(name) as string ?? "").Trim();
                    res.Add(new StartupEntry(name, loc, cmd, "reg-" + hive, sub + "|" + name));
                }
            }
            catch { }
        }
        FromRegistry("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "реестр HKCU");
        FromRegistry("HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run", "реестр HKLM");
        foreach (var special in new[]
        {
            Environment.SpecialFolder.Startup,
            Environment.SpecialFolder.CommonStartup,
        })
        {
            try
            {
                string dir = Environment.GetFolderPath(special);
                if (!Directory.Exists(dir)) continue;
                foreach (string f in Directory.GetFiles(dir, "*.lnk"))
                    res.Add(new StartupEntry(Path.GetFileNameWithoutExtension(f),
                        "папка автозагрузки", f, "file", f));
            }
            catch { }
        }
        return res;
    }

    // Выключить: реестр — удалить значение (команда в бэкапе), lnk — в подпапку.
    public static string DisableStartup(StartupEntry e)
    {
        try
        {
            var dis = LoadDisabled();
            if (e.Kind == "file")
            {
                string dir = Path.GetDirectoryName(e.KeyPath) ?? "";
                string off = Path.Combine(dir, "PultDisabled");
                Directory.CreateDirectory(off);
                string dest = Path.Combine(off, Path.GetFileName(e.KeyPath));
                File.Move(e.KeyPath, dest, true);
                dis[e.Name] = e with { KeyPath = dest };
            }
            else
            {
                string[] parts = e.KeyPath.Split('|');
                if (parts.Length != 2) return "Кривая запись.";
                using var root = e.Kind == "reg-HKCU"
                    ? Microsoft.Win32.Registry.CurrentUser
                    : Microsoft.Win32.Registry.LocalMachine;
                using var k = root.OpenSubKey(parts[0], true);
                if (k == null) return "Не нашёл в реестре (нужны права?).";
                k.DeleteValue(parts[1], false);
                dis[e.Name] = e;
            }
            SaveDisabled(dis);
            return $"Выключено: {e.Name}.";
        }
        catch (Exception ex) { return $"Не вышло выключить: {ex.Message}"; }
    }

    public static string EnableStartup(string name)
    {
        try
        {
            var dis = LoadDisabled();
            if (!dis.TryGetValue(name, out var e)) return "Нет в бэкапе.";
            if (e.Kind == "file")
            {
                string dir = Path.GetDirectoryName(
                    Path.GetDirectoryName(e.KeyPath) ?? "") ?? "";
                if (dir.Length == 0) return "Кривой бэкап.";
                string dest = Path.Combine(dir, Path.GetFileName(e.KeyPath));
                File.Move(e.KeyPath, dest, true);
            }
            else
            {
                string[] parts = e.KeyPath.Split('|');
                if (parts.Length != 2) return "Кривой бэкап.";
                using var root = e.Kind == "reg-HKCU"
                    ? Microsoft.Win32.Registry.CurrentUser
                    : Microsoft.Win32.Registry.LocalMachine;
                using var k = root.OpenSubKey(parts[0], true);
                if (k == null) return "Не нашёл ветку (нужны права?).";
                k.SetValue(parts[1], e.Command);
            }
            dis.Remove(name);
            SaveDisabled(dis);
            return $"Включено: {name}.";
        }
        catch (Exception ex) { return $"Не вышло включить: {ex.Message}"; }
    }
}
