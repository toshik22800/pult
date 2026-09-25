using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Pult.Services;

// Порт питон-сканера игр: Steam + ярлыки + папки. Только BCL + реестр + COM late-binding.
public sealed record Game(string Name, string Source, string Launch, string Path, long SizeBytes);

public static class GameService
{
    private static readonly string[] ShortcutSubBlacklist =
    {
        "uninstall", "readme", "manual", "help", "documentation",
        "editor", "cmd", "powershell", "python",
    };

    // Точное совпадение по Normalize. Roblox намеренно отсутствует — это игра.
    private static readonly HashSet<string> ExactBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "internetexplorer", "iexplore", "ollama", "onedrive",
        "visualstudiocode", "vscode", "winrar", "winzip", "7zip",
        "steam", "epicgameslauncher", "goggalaxy", "discord", "telegram",
        "skype", "zoom", "chrome", "firefox", "edge", "opera",
        "word", "excel", "powerpoint", "notepad", "pycharm", "obs",
        "ccleaner", "defender",
        "aida64", "aida64extreme", "androidstudio", "studio64",
        "gitbash", "gitgui", "gitcmd", "nodejs", "node",
        "nvidiaapp", "nvidiageforceexperience", "geforceexperience",
        "windowsmediaplayer", "wmplayer", "mediaplayer",
        "opencode", "code", "visualstudiocodeinsiders",
        "pult",
    };

    // Папки установленных Steam-игр (ground truth для ярлыков).
    private static readonly HashSet<string> SteamGameDirs = new(StringComparer.OrdinalIgnoreCase);

    // Игровые маркеры рядом с exe: движки, стим-апи, пак-файлы.
    private static bool HasGameMarkers(string dir, string exeStem = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "steam_appid.txt"))) return true;
            if (Directory.Exists(Path.Combine(dir, "Content", "Paks"))) return true;
            if (Directory.Exists(Path.Combine(dir, "Binaries", "Win64"))) return true;
            if (Directory.Exists(Path.Combine(dir, "Bin"))) return true;
            if (!string.IsNullOrEmpty(exeStem)
                && Directory.Exists(Path.Combine(dir, exeStem + "_Data"))) return true;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*.pak", SearchOption.TopDirectoryOnly))
                    return true;
                foreach (string f in Directory.EnumerateFiles(dir, "*.pck", SearchOption.TopDirectoryOnly))
                    return true;
                foreach (string f in Directory.EnumerateFiles(dir, "steam_api*.dll", SearchOption.TopDirectoryOnly))
                    return true;
            }
            catch { }
        }
        catch { }
        return false;
    }

    private static readonly string[] SystemPrefixes =
    {
        @"c:\windows",
        @"c:\program files\windows",
        @"c:\program files\microsoft",
        @"c:\program files (x86)\microsoft",
        @"c:\program files\common files",
    };

    private static readonly string[] ExeSkipWords =
    {
        "unins", "uninstall", "setup", "install", "vcredist", "directx",
        "dxsetup", "dotnet", "redist", "crash", "report", "helper",
        "config", "settings", "dedicated",
    };

    private static readonly string[] SteamBlacklist =
    {
        "steamworks", "steam linux runtime", "steamvr", "proton",
        "redistributable", "dedicated server", "wallpaper engine", "soundpad",
    };

    private static readonly string[] SkipDirNames =
    {
        "redist", "directx", "dotnet", "vcredist", "temp", "cache", "windows", "appdata",
    };

    public static string Normalize(string s)
    {
        s ??= "";
        string lower = s.ToLowerInvariant();
        var buf = new char[lower.Length];
        int n = 0;
        foreach (char c in lower)
        {
            bool keep = (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || (c >= 'а' && c <= 'я')
                || c == 'ё';
            if (keep) buf[n++] = c;
        }
        return new string(buf, 0, n);
    }

    public static string FormatGb(long bytes)
    {
        if (bytes <= 0) return "—";
        double gb = bytes / 1024.0 / 1024 / 1024;
        if (gb >= 1) return $"{gb:F1} ГБ";
        return $"{bytes / 1024.0 / 1024:F0} МБ";
    }

    // ---- Steam ----

    internal static string SteamDir()
    {
        string? dir = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        dir ??= Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        dir ??= Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string;
        if (string.IsNullOrWhiteSpace(dir)) dir = @"C:\Program Files (x86)\Steam";
        return dir.Replace('/', '\\');
    }

    private static List<string> SteamAppsDirs()
    {
        var res = new List<string>();
        try
        {
            string dir = SteamDir();
            if (!Directory.Exists(dir)) return res;
            var libs = new List<string> { dir };
            string vdf = Path.Combine(dir, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                try
                {
                    string text = File.ReadAllText(vdf);
                    foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
                    {
                        string p = m.Groups[1].Value.Replace(@"\\", @"\").Replace('/', '\\');
                        if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)
                            && !libs.Contains(p, StringComparer.OrdinalIgnoreCase))
                            libs.Add(p);
                    }
                }
                catch { }
            }
            foreach (string lib in libs)
            {
                string sa = lib.EndsWith("steamapps", StringComparison.OrdinalIgnoreCase)
                    ? lib : Path.Combine(lib, "steamapps");
                if (Directory.Exists(sa) && !res.Contains(sa, StringComparer.OrdinalIgnoreCase))
                    res.Add(sa);
            }
        }
        catch { }
        return res;
    }

    public static List<Game> ScanSteam()
    {
        var res = new List<Game>();
        SteamGameDirs.Clear();
        try
        {
            var seenApp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string sa in SteamAppsDirs())
            {
                string[] manifests;
                try { manifests = Directory.GetFiles(sa, "appmanifest_*.acf"); }
                catch { continue; }
                foreach (string acf in manifests)
                {
                    try
                    {
                        string text = File.ReadAllText(acf);
                        string name = Regex.Match(text, "\"name\"\\s*\"([^\"]+)\"").Groups[1].Value.Trim();
                        string appid = Regex.Match(text, "\"appid\"\\s*\"([^\"]+)\"").Groups[1].Value.Trim();
                        string installdir = Regex.Match(text, "\"installdir\"\\s*\"([^\"]+)\"").Groups[1].Value.Trim();
                        if (name.Length == 0 || appid.Length == 0) continue;
                        string nl = name.ToLowerInvariant();
                        if (SteamBlacklist.Any(b => nl.Contains(b))) continue;
                        if (!seenApp.Add(appid)) continue;
                        string path = installdir.Length > 0 ? Path.Combine(sa, "common", installdir) : sa;
                        try { SteamGameDirs.Add(Path.GetFullPath(path).TrimEnd('\\')); }
                        catch { }
                        res.Add(new Game(name, "Steam", $"steam://rungameid/{appid}", path, 0));
                    }
                    catch { }
                }
            }
        }
        catch { }
        return res;
    }

    // ---- Ярлыки (COM late-binding, без NuGet) ----

    public static List<Game> ScanShortcuts()
    {
        var res = new List<Game>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var folders = new List<string>();
            void Add(string p)
            {
                if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) folders.Add(p);
            }
            Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"));
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"));

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return res;
            object? shellObj;
            try { shellObj = Activator.CreateInstance(shellType); }
            catch { return res; }
            if (shellObj == null) return res;
            dynamic shell = shellObj;

            foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string[] links;
                try { links = Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (string lnk in links)
                {
                    try
                    {
                        string shortcutName = Path.GetFileNameWithoutExtension(lnk);
                        string nl = shortcutName.ToLowerInvariant();
                        if (ShortcutSubBlacklist.Any(b => nl.Contains(b))) continue;
                        if (ExactBlacklist.Contains(Normalize(shortcutName))) continue;

                        dynamic sc = shell.CreateShortcut(lnk);
                        string target = ((string)sc.TargetPath) ?? "";
                        if (string.IsNullOrWhiteSpace(target)) continue;
                        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        string tl = target.ToLowerInvariant();
                        if (SystemPrefixes.Any(p => tl.StartsWith(p))) continue;
                        string exeName = Path.GetFileName(target).ToLowerInvariant();
                        if (ExeSkipWords.Any(w => exeName.Contains(w))) continue;
                        if (ExactBlacklist.Contains(Normalize(Path.GetFileNameWithoutExtension(target)))) continue;
                        if (!File.Exists(target)) continue;
                        if (!seen.Add(tl)) continue;

                        // Ярлык в Program Files без игровых маркеров рядом с exe —
                        // почти наверняка не игра (AIDA64, Git, Node, NVIDIA App…).
                        // Исключение: exe внутри известной папки Steam-игры.
                        try
                        {
                            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).ToLowerInvariant();
                            string pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).ToLowerInvariant();
                            bool inPf = (pf.Length > 3 && tl.StartsWith(pf + "\\"))
                                || (pfx.Length > 3 && tl.StartsWith(pfx + "\\"));
                            bool inSteamGame = SteamGameDirs.Any(d =>
                                tl.StartsWith(d.ToLowerInvariant() + "\\"));
                            if (inPf && !inSteamGame)
                            {
                                string exeDir = Path.GetDirectoryName(target) ?? "";
                                string stem = Path.GetFileNameWithoutExtension(target);
                                bool markers = HasGameMarkers(exeDir, stem);
                                if (!markers)
                                {
                                    try
                                    {
                                        string? parent = Path.GetDirectoryName(exeDir.TrimEnd('\\'));
                                        markers = parent != null && HasGameMarkers(parent, stem);
                                    }
                                    catch { }
                                }
                                if (!markers) continue;
                            }
                        }
                        catch { }

                        string dir = Path.GetDirectoryName(target) ?? target;
                        res.Add(new Game(shortcutName, "Ярлык", target, dir, 0));
                    }
                    catch { }
                }
            }
        }
        catch { }
        return res;
    }

    // ---- Папки ----

    private static string? FindMainExe(string folder, DateTime deadline)
    {
        try
        {
            bool hasMarkers = false;
            try
            {
                if (File.Exists(Path.Combine(folder, "steam_appid.txt"))) hasMarkers = true;
                else if (Directory.Exists(Path.Combine(folder, "Content", "Paks"))) hasMarkers = true;
                else if (Directory.Exists(Path.Combine(folder, "Binaries", "Win64"))) hasMarkers = true;
                else if (Directory.Exists(Path.Combine(folder, "Bin"))) hasMarkers = true;
                else
                {
                    try { hasMarkers = Directory.EnumerateFiles(folder, "*.pak", SearchOption.TopDirectoryOnly).Any(); }
                    catch { }
                    // Unity-игры: рядом с exe лежит папка «<Игра>_Data».
                    if (!hasMarkers)
                    {
                        try { hasMarkers = Directory.EnumerateDirectories(folder, "*_Data", SearchOption.TopDirectoryOnly).Any(); }
                        catch { }
                    }
                }
            }
            catch { }

            string folderNorm = Normalize(new DirectoryInfo(folder.TrimEnd('\\')).Name);
            var candidates = new List<(string Full, long Size)>();
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((folder, 0));
            while (queue.Count > 0)
            {
                if (DateTime.UtcNow >= deadline) break;
                var (dir, depth) = queue.Dequeue();
                string[] subdirs = Array.Empty<string>();
                string[] files = Array.Empty<string>();
                try
                {
                    if (depth < 4) subdirs = Directory.GetDirectories(dir);
                    files = Directory.GetFiles(dir, "*.exe");
                }
                catch { continue; }
                foreach (string f in files)
                {
                    try
                    {
                        string fn = Path.GetFileName(f).ToLowerInvariant();
                        if (ExeSkipWords.Any(w => fn.Contains(w))) continue;
                        long len = new FileInfo(f).Length;
                        if (len < 200L * 1024) continue;
                        if (!hasMarkers)
                        {
                            bool bigEnough = len >= 5L * 1024 * 1024;
                            bool nameMatch = folderNorm.Length > 0
                                && Normalize(Path.GetFileNameWithoutExtension(f)) == folderNorm;
                            if (!bigEnough && !nameMatch) continue;
                        }
                        candidates.Add((f, len));
                    }
                    catch { }
                }
                if (depth + 1 > 4) continue;
                foreach (string sub in subdirs)
                {
                    string dn = Path.GetFileName(sub).ToLowerInvariant();
                    if (SkipDirNames.Any(s => dn.Contains(s))) continue;
                    queue.Enqueue((sub, depth + 1));
                }
            }
            if (candidates.Count == 0) return null;
            if (folderNorm.Length > 0)
            {
                var exact = candidates.FirstOrDefault(c =>
                    Normalize(Path.GetFileNameWithoutExtension(c.Full)) == folderNorm);
                if (exact.Full != null) return exact.Full;
            }
            return candidates.OrderByDescending(c => c.Size).First().Full;
        }
        catch { return null; }
    }

    // Папки-контейнеры: сама папка игрой не является, смотрим только внутрь.
    private static readonly HashSet<string> ContainerDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "common", "games", "repack", "r.g. mechanics", "gog games", "игры",
        "epic games", "steam", "steamapps", "gog galaxy", "origin games",
    };

    // exe внутри уже найденной Steam-игры — не дублировать записью «Папка».
    private static bool IsUnderSteamGame(string exe)
    {
        try
        {
            foreach (string d in SteamGameDirs)
            {
                if (d.Length == 0) continue;
                if (exe.StartsWith(d, StringComparison.OrdinalIgnoreCase)
                    && (exe.Length == d.Length || exe[d.Length] is '\\' or '/'))
                    return true;
            }
        }
        catch { }
        return false;
    }

    public static List<Game> ScanFolders(IEnumerable<string>? extraDirs, int timeLimitSec = 25)
    {
        var res = new List<Game>();
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeLimitSec);
        try
        {
            var bases = new List<string>();
            // Папки, явно добавленные пользователем: только для них self-match.
            var userSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    string root = drive.RootDirectory.FullName;
                    foreach (string rel in new[]
                    {
                        "Games", "Игры", "Repack", "R.G. Mechanics", "GOG Games",
                        Path.Combine("Games", "Repack"),
                        Path.Combine("Games", "R.G. Mechanics"),
                        Path.Combine("Games", "GOG Games"),
                        Path.Combine("Игры", "Repack"),
                    })
                    {
                        string p = Path.Combine(root, rel);
                        if (Directory.Exists(p)) bases.Add(p);
                    }
                }
                catch { }
            }
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (string p in new[]
            {
                Path.Combine(pf, "Steam", "steamapps", "common"),
                Path.Combine(pf86, "Steam", "steamapps", "common"),
                @"C:\Program Files\Steam\steamapps\common",
                @"C:\Program Files (x86)\Steam\steamapps\common",
                Path.Combine(pf, "Epic Games"),
                Path.Combine(pf86, "Epic Games"),
                @"C:\Epic Games",
                Path.Combine(pf, "GOG Galaxy", "Games"),
                Path.Combine(pf86, "GOG Galaxy", "Games"),
                @"C:\GOG Games",
            })
            {
                try { if (Directory.Exists(p)) bases.Add(p); } catch { }
            }
            if (extraDirs != null)
            {
                foreach (string d in extraDirs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(d) || !Directory.Exists(d)) continue;
                        bases.Add(d);
                        userSet.Add(Path.GetFullPath(d).TrimEnd('\\'));
                    }
                    catch { }
                }
            }

            foreach (string b in bases.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (DateTime.UtcNow >= deadline) break;
                // Self-match — только для папок, явно добавленных пользователем.
                // Встроенные контейнеры (steamapps\common и т.п.) смотрим только внутрь,
                // иначе largest exe даст фантома с именем папки.
                bool probeSelf = false;
                try
                {
                    string baseFull = Path.GetFullPath(b).TrimEnd('\\');
                    string baseName = new DirectoryInfo(baseFull).Name;
                    probeSelf = userSet.Contains(baseFull) && !ContainerDirNames.Contains(baseName);
                }
                catch { }
                if (probeSelf)
                {
                    try
                    {
                        string? self = FindMainExe(b, deadline);
                        if (self != null && !IsUnderSteamGame(self))
                            res.Add(new Game(new DirectoryInfo(b.TrimEnd('\\')).Name, "Папка", self, b, 0));
                    }
                    catch { }
                }
                string[] subs;
                try { subs = Directory.GetDirectories(b); }
                catch { continue; }
                foreach (string sub in subs)
                {
                    if (DateTime.UtcNow >= deadline) break;
                    try
                    {
                        string? exe = FindMainExe(sub, deadline);
                        if (exe == null) continue;
                        res.Add(new Game(new DirectoryInfo(sub).Name, "Папка", exe, sub, 0));
                    }
                    catch { }
                }
            }
        }
        catch { }
        return res;
    }

    public static List<Game> ScanSingleFolder(string folder, int timeLimitSec = 20)
    {
        var res = new List<Game>();
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return res;
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeLimitSec);
            try
            {
                string? self = FindMainExe(folder, deadline);
                if (self != null)
                    res.Add(new Game(new DirectoryInfo(folder.TrimEnd('\\')).Name, "Папка", self, folder, 0));
            }
            catch { }
            string[] subs;
            try { subs = Directory.GetDirectories(folder); }
            catch { return res; }
            foreach (string sub in subs)
            {
                if (DateTime.UtcNow >= deadline) break;
                try
                {
                    string? exe = FindMainExe(sub, deadline);
                    if (exe == null) continue;
                    res.Add(new Game(new DirectoryInfo(sub).Name, "Папка", exe, sub, 0));
                }
                catch { }
            }
        }
        catch { }
        return res;
    }

    // ---- Всё вместе + кэш ----

    private sealed class GamesCache
    {
        public List<Game> Games { get; set; } = new();
    }

    private static string CacheFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pult", "games.json");

    public static void SaveCache(List<Game> games)
    {
        try
        {
            string file = CacheFile;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(new GamesCache { Games = games },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static List<Game> LoadCache()
    {
        try
        {
            string file = CacheFile;
            if (!File.Exists(file)) return new List<Game>();
            var c = JsonSerializer.Deserialize<GamesCache>(File.ReadAllText(file));
            return c?.Games ?? new List<Game>();
        }
        catch { return new List<Game>(); }
    }

    // Ключ дедупликации: нормализованный путь к exe.
    // Пусто — если Launch не путь к файлу (steam:// и т.п.).
    private static string ExeKey(string launch)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(launch)) return "";
            string t = launch.Trim();
            if (t.StartsWith("steam://", StringComparison.OrdinalIgnoreCase)) return "";
            string full = Path.GetFullPath(t);
            if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "";
            return full.ToLowerInvariant();
        }
        catch { return ""; }
    }

    public static List<Game> ScanAll(IEnumerable<string>? extraDirs)
    {
        var all = new List<Game>();
        try
        {
            all.AddRange(ScanSteam());
            all.AddRange(ScanShortcuts());
            all.AddRange(ScanFolders(extraDirs));
            var seen = new HashSet<string>();
            var seenExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uniq = new List<Game>();
            foreach (var g in all)
            {
                // Один и тот же exe под разными именами — одна запись.
                // (Steam-ссылки — не пути, для них работает ключ по имени ниже.)
                string exeKey = ExeKey(g.Launch);
                if (exeKey.Length > 0 && !seenExe.Add(exeKey)) continue;
                string key = Normalize(g.Name);
                if (key.Length == 0) continue;
                if (seen.Add(key)) uniq.Add(g);
            }
            uniq.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            SaveCache(uniq);
            return uniq;
        }
        catch { return all; }
    }

    public static List<Game> GetGames(bool force, IEnumerable<string>? extraDirs)
    {
        if (!force)
        {
            var cached = LoadCache();
            if (cached.Count > 0) return cached;
        }
        return ScanAll(extraDirs);
    }

    // ---- Размеры ----

    private static Dictionary<string, long> SteamSizeMap()
    {
        var map = new Dictionary<string, long>();
        try
        {
            foreach (string sa in SteamAppsDirs())
            {
                string[] manifests;
                try { manifests = Directory.GetFiles(sa, "appmanifest_*.acf"); }
                catch { continue; }
                foreach (string acf in manifests)
                {
                    try
                    {
                        string text = File.ReadAllText(acf);
                        string name = Regex.Match(text, "\"name\"\\s*\"([^\"]+)\"").Groups[1].Value.Trim();
                        string sizeRaw = Regex.Match(text, "\"SizeOnDisk\"\\s*\"([^\"]+)\"").Groups[1].Value.Trim();
                        if (name.Length == 0 || !long.TryParse(sizeRaw, out long sz) || sz <= 0) continue;
                        map[Normalize(name)] = sz;
                    }
                    catch { }
                }
            }
        }
        catch { }
        return map;
    }

    private static long DirSize(string folder, DateTime deadline)
    {
        long total = 0;
        try
        {
            var stack = new Stack<string>();
            stack.Push(folder);
            while (stack.Count > 0)
            {
                if (DateTime.UtcNow >= deadline) break;
                string dir = stack.Pop();
                string[] files = Array.Empty<string>();
                string[] subs = Array.Empty<string>();
                try
                {
                    files = Directory.GetFiles(dir);
                    subs = Directory.GetDirectories(dir);
                }
                catch { continue; }
                foreach (string f in files)
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                foreach (string s in subs) stack.Push(s);
            }
        }
        catch { }
        return total;
    }

    public static List<Game> GetSizes(List<Game> games, int timeLimitSec = 60)
    {
        var res = new List<Game>(games);
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeLimitSec);
            var steamMap = SteamSizeMap();
            for (int i = 0; i < res.Count; i++)
            {
                if (DateTime.UtcNow >= deadline) break;
                var g = res[i];
                try
                {
                    if (g.Source == "Steam" && steamMap.TryGetValue(Normalize(g.Name), out long sz) && sz > 0)
                        res[i] = g with { SizeBytes = sz };
                    else if (Directory.Exists(g.Path))
                        res[i] = g with { SizeBytes = DirSize(g.Path, deadline) };
                }
                catch { }
            }
            SaveCache(res);
        }
        catch { }
        return res;
    }

    // ---- Запуск ----

    public static string Launch(Game game)
    {
        try
        {
            // Через explorer.exe: без наследования админ-прав.
            string launch = game.Launch;
            if (launch.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
                return SystemMonitor.OpenDeElevated(launch);
            try
            {
                var psi = new ProcessStartInfo("explorer.exe", "\"" + launch + "\"")
                {
                    UseShellExecute = false,
                };
                try
                {
                    if (Directory.Exists(game.Path)) psi.WorkingDirectory = game.Path;
                }
                catch { }
                Process.Start(psi);
            }
            catch
            {
                return SystemMonitor.OpenDeElevated(launch);
            }
            return $"Запущено: {game.Name}";
        }
        catch (Exception ex)
        {
            return $"Ошибка запуска {game.Name}: {ex.Message}";
        }
    }
}
