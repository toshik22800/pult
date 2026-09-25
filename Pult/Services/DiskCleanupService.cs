using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Pult.Services;

// Порт find_duplicates + clean_temp + find_large_files из питона.
public static class DiskCleanupService
{
    public static string Gb(long b) => $"{b / 1024.0 / 1024 / 1024:F1} ГБ";
    public static string Mb(long b) => $"{b / 1024.0 / 1024:F1} МБ";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    // Корзина: без вопросов внутри (вопрос задаёт вызывающий код/ИИ-гейт).
    public static string EmptyRecycleBin()
    {
        try
        {
            int hr = SHEmptyRecycleBin(IntPtr.Zero, null, 0x7);
            return hr == 0 ? "Готово: корзина очищена." : $"Не вышло (код {hr}).";
        }
        catch (Exception ex) { return $"Ошибка: {ex.Message}"; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryRbInfo
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? root, ref QueryRbInfo info);

    // Замер корзины: быстро, без перечисления — итог отдаёт сам SHQueryRecycleBin.
    public static (int Count, long Bytes) RecycleBinInfo()
    {
        try
        {
            var q = new QueryRbInfo { cbSize = (uint)Marshal.SizeOf<QueryRbInfo>() };
            int hr = SHQueryRecycleBin(null, ref q);
            if (hr != 0) return (0, 0);
            return ((int)Math.Min(q.i64NumItems, int.MaxValue), Math.Max(0, q.i64Size));
        }
        catch { return (0, 0); }
    }

    // ---- Общие форматтеры ----

    public static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? Gb(bytes) : Mb(bytes);

    public static string Plural(int n, string one, string few, string many)
    {
        try
        {
            n = Math.Abs(n) % 100;
            int d = n % 10;
            if (n > 10 && n < 20) return many;
            if (d > 1 && d < 5) return few;
            if (d == 1) return one;
            return many;
        }
        catch { return many; }
    }

    // ---- Глубокая очистка: WinSxS (DISM) + кэш обновлений ----

    // Анализ WinSxS: быстро не бывает (30–120 с) — только из фона. Разбор RU+EN.
    public static string DismAnalyze()
    {
        try
        {
            var r = Proc.Run("DISM.exe", "/Online /Cleanup-Image /AnalyzeComponentStore", 300000);
            if (r.TimedOut) return "Ошибка: DISM не ответил за 5 минут.";
            string o = (r.Stdout ?? "") + "\n" + (r.Stderr ?? "");
            if (o.Contains("740") || Regex.IsMatch(o,
                "требует повышения|elevation|отказано в доступе|access denied",
                RegexOptions.IgnoreCase))
                return "Ошибка: нужен запуск от администратора.";
            double GbNum(string pattern)
            {
                try
                {
                    var m = Regex.Match(o, pattern, RegexOptions.IgnoreCase);
                    if (!m.Success) return -1;
                    double v = double.Parse(m.Groups[1].Value.Replace(',', '.'),
                        System.Globalization.CultureInfo.InvariantCulture);
                    string u = m.Groups[2].Value.ToUpperInvariant();
                    if (u.StartsWith("M") || u.StartsWith("М")) v /= 1024;
                    return v;
                }
                catch { return -1; }
            }
            double actual = GbNum(@"(?:Actual Size of Component Store|Фактический размер хранилища компонентов)\s*:\s*([\d\.,]+)\s*(GB|MB|ГБ|МБ)");
            double backups = GbNum(@"(?:Backups and Disabled Features|Резервные копии и отключ[её]нные компоненты)\s*:\s*([\d\.,]+)\s*(GB|MB|ГБ|МБ)");
            double cache = GbNum(@"(?:Cache and Temporary Data|Кэш и временные данные)\s*:\s*([\d\.,]+)\s*(GB|MB|ГБ|МБ)");
            bool rec = Regex.IsMatch(o,
                @"(?:Component Store Cleanup Recommended|Рекомендуется очистка хранилища компонентов)\s*:\s*(Yes|Да)",
                RegexOptions.IgnoreCase);
            if (actual < 0 && backups < 0 && cache < 0)
            {
                string head = o.Trim();
                if (head.Length > 400) head = head[..400] + "…";
                return "Готово: анализ завершён, но вывод разобрать не вышло.\n" + head;
            }
            long reclaim = (long)((Math.Max(0, backups) + Math.Max(0, cache)) * 1024 * 1024 * 1024);
            string total = actual >= 0 ? $"всего ~{FormatSize((long)(actual * 1024 * 1024 * 1024))}, " : "";
            if (reclaim <= 0 && !rec)
                return $"Чистить нечего: WinSxS в порядке ({total.TrimEnd(' ', ',')}).";
            return $"WinSxS: {total}освободить можно ~{FormatSize(reclaim)} (бэкапы и кэш).{(rec ? " Очистка рекомендована." : "")}";
        }
        catch (Exception ex) { return $"Ошибка: {ex.Message}"; }
    }

    public static string DismCleanup()
    {
        try
        {
            var r = Proc.Run("DISM.exe", "/Online /Cleanup-Image /StartComponentCleanup", 1800000);
            if (r.TimedOut) return "Ошибка: DISM не уложился в 30 минут.";
            string o = ((r.Stdout ?? "") + "\n" + (r.Stderr ?? "")).Trim();
            if (Regex.IsMatch(o, "успешно|completed successfully|successfully",
                RegexOptions.IgnoreCase))
                return "Готово: хранилище компонентов очищено.";
            if (r.ExitCode == 0 && o.Length == 0)
                return "Готово: хранилище компонентов очищено.";
            if (Regex.IsMatch(o, "^ошибка|error|сбой|failed",
                RegexOptions.IgnoreCase | RegexOptions.Multiline))
            {
                if (o.Length > 300) o = o[..300] + "…";
                return "Ошибка DISM: " + o;
            }
            if (o.Length > 800) o = o[..800] + "…";
            return "DISM ответил:\n" + o;
        }
        catch (Exception ex) { return $"Ошибка: {ex.Message}"; }
    }

    private static string UpdateCacheDir()
    {
        try
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return Path.Combine(win, "SoftwareDistribution", "Download");
        }
        catch { return ""; }
    }

    public static (int Count, long Bytes) UpdateCacheInfo()
    {
        try
        {
            string dir = UpdateCacheDir();
            if (dir.Length == 0 || !Directory.Exists(dir)) return (0, 0);
            int count = 0;
            long bytes = 0;
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { count++; bytes += new FileInfo(f).Length; }
                catch { }
            }
            return (count, bytes);
        }
        catch { return (0, 0); }
    }

    // Кэш обновлений: файлы — обычные установщики, при нужде Windows докачает.
    // Службы останавливаем, иначе занятые файлы не удалятся; в конце поднимаем.
    public static string UpdateCacheClean()
    {
        try
        {
            string dir = UpdateCacheDir();
            if (dir.Length == 0 || !Directory.Exists(dir)) return "Кэш обновлений пуст.";
            try { SystemMonitor.ServiceControl("wuauserv", "stop"); } catch { }
            try { SystemMonitor.ServiceControl("bits", "stop"); } catch { }
            int deleted = 0;
            long freed = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { long len = new FileInfo(f).Length; File.Delete(f); deleted++; freed += len; }
                    catch { }
                }
            }
            catch { }
            try { SystemMonitor.ServiceControl("wuauserv", "start"); } catch { }
            try { SystemMonitor.ServiceControl("bits", "start"); } catch { }
            if (deleted == 0) return "Кэш обновлений: удалить нечего (или занято системой).";
            return $"Готово: кэш обновлений очищен — {deleted} {Plural(deleted, "файл", "файла", "файлов")}, ~{FormatSize(freed)}.";
        }
        catch (Exception ex) { return $"Ошибка: {ex.Message}"; }
    }

    // ---- Дубликаты (SHA-256, сначала группировка по размеру) ----

    public static string FindDuplicates(string directory)
    {
        try
        {
            var dir = new DirectoryInfo(directory);
            if (!dir.Exists) return $"Ошибка: папка {directory} не найдена.";

            var bySize = new Dictionary<long, List<FileInfo>>();
            foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    // Пустые файлы пропускаем: хеш пустоты одинаковый у всех,
                    // иначе тысячи мусорных «дубликатов» по 0 байт.
                    if (f.Length == 0) continue;
                    if (!bySize.TryGetValue(f.Length, out var list))
                        bySize[f.Length] = list = new List<FileInfo>();
                    list.Add(f);
                }
                catch { }
            }

            var groups = new List<List<string>>();
            using var sha = SHA256.Create();
            foreach (var kv in bySize)
            {
                if (kv.Value.Count < 2) continue;
                var byHash = new Dictionary<string, List<string>>();
                foreach (var f in kv.Value)
                {
                    try
                    {
                        using var s = f.OpenRead();
                        string h = Convert.ToHexString(sha.ComputeHash(s));
                        if (!byHash.TryGetValue(h, out var l)) byHash[h] = l = new List<string>();
                        l.Add(f.FullName);
                    }
                    catch { }
                }
                foreach (var g in byHash.Values)
                    if (g.Count > 1) groups.Add(g);
            }

            if (groups.Count == 0) return $"Дубликатов в {directory} не найдено.";
            // Байты считаем по группам: размер у каждой группы свой.
            long bytes = 0;
            try
            {
                foreach (var g in groups)
                    bytes += new FileInfo(g[0]).Length * (g.Count - 1);
            }
            catch { }
            int dupes = groups.Sum(g => g.Count - 1);
            var lines = new List<string>
            {
                $"Групп дубликатов: {groups.Count}, лишних копий: {dupes}.",
                $"Потенциально освободит: ~{Gb(bytes)}.",
                "",
            };
            foreach (var g in groups.Take(15))
            {
                lines.Add($"Оригинал: {g[0]}");
                foreach (var d in g.Skip(1)) lines.Add($"  • Дубликат: {d}");
            }
            if (groups.Count > 15) lines.Add($"  ... и ещё {groups.Count - 15} групп");
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Ошибка поиска дубликатов: {ex.Message}";
        }
    }

    public static string DeleteDuplicates(string directory)
    {
        // Удаляет все копии кроме первой в каждой группе.
        try
        {
            var dir = new DirectoryInfo(directory);
            if (!dir.Exists) return $"Ошибка: папка {directory} не найдена.";
            // Переиспользуем поиск, затем удаляем дубликаты из отчёта.
            // Проще: считаем заново и удаляем.
            var bySize = new Dictionary<long, List<FileInfo>>();
            foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if (f.Length == 0) continue;
                    if (!bySize.TryGetValue(f.Length, out var list))
                        bySize[f.Length] = list = new List<FileInfo>();
                    list.Add(f);
                }
                catch { }
            }
            int deleted = 0, recycled = 0, permanent = 0;
            long freed = 0;
            using var sha = SHA256.Create();
            foreach (var kv in bySize)
            {
                if (kv.Value.Count < 2) continue;
                var byHash = new Dictionary<string, List<FileInfo>>();
                foreach (var f in kv.Value)
                {
                    try
                    {
                        using var s = f.OpenRead();
                        string h = Convert.ToHexString(sha.ComputeHash(s));
                        if (!byHash.TryGetValue(h, out var l)) byHash[h] = l = new List<FileInfo>();
                        l.Add(f);
                    }
                    catch { }
                }
                foreach (var g in byHash.Values)
                {
                    if (g.Count < 2) continue;
                    foreach (var dup in g.OrderBy(f => f.LastWriteTime).Skip(1))
                    {
                        // Сначала в корзину (можно достать), если не влезло — навсегда, но честно.
                        long len = 0;
                        try { len = dup.Length; } catch { }
                        bool gone = false;
                        try
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                                dup.FullName,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                            recycled++;
                            gone = true;
                        }
                        catch { }
                        if (!gone)
                        {
                            try { dup.Delete(); permanent++; gone = true; }
                            catch { }
                        }
                        if (gone) { freed += len; deleted++; }
                    }
                }
            }
            if (deleted == 0) return "Дубликатов не найдено — удалять нечего.";
            string tail = permanent > 0 ? $" (в корзину: {recycled}, навсегда: {permanent})" : " (всё в корзине — можно достать)";
            return $"Удалено копий: {deleted}{tail}. Освобождено: ~{Gb(freed)}.";
        }
        catch (Exception ex)
        {
            return $"Ошибка удаления дубликатов: {ex.Message}";
        }
    }

    // ---- Temp ----

    private static IEnumerable<string> TempDirs()
    {
        yield return Path.GetTempPath();
        string winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        if (!string.Equals(winTemp, Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            yield return winTemp;
    }

    private static List<(string Path, long Size)> CollectTemp(int minAgeHours)
    {
        var res = new List<(string, long)>();
        DateTime limit = DateTime.Now.AddHours(-minAgeHours);
        foreach (var t in TempDirs())
        {
            DirectoryInfo dir;
            try { dir = new DirectoryInfo(t); if (!dir.Exists) continue; }
            catch { continue; }
            FileInfo[] files;
            try { files = dir.GetFiles("*", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in files)
            {
                try
                {
                    if (f.LastWriteTime < limit) res.Add((f.FullName, f.Length));
                }
                catch { }
            }
        }
        return res;
    }

    public static string PreviewTemp(int minAgeHours = 24)
    {
        try
        {
            var files = CollectTemp(minAgeHours);
            if (files.Count == 0)
                return $"Временных файлов старше {minAgeHours} ч не найдено. Чистить нечего.";
            long total = files.Sum(f => f.Size);
            var lines = new List<string>
            {
                $"Найдено временных файлов: {files.Count}, общий размер ~{Gb(total)}.",
                $"Будет удалено ({minAgeHours} ч+):",
            };
            lines.AddRange(files.Take(20).Select(f => $"  • {f.Path}  ({Mb(f.Size)})"));
            if (files.Count > 20) lines.Add($"  ... и ещё {files.Count - 20}");
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Ошибка: {ex.Message}";
        }
    }

    // Числовой замер для цепочек («нашёл X»): без текста.
    public static (int Count, long Bytes) MeasureTemp(int minAgeHours = 24)
    {
        try
        {
            var files = CollectTemp(minAgeHours);
            return (files.Count, files.Sum(f => f.Size));
        }
        catch { return (0, 0); }
    }

    public static string CleanTemp(int minAgeHours = 24)
    {
        try
        {
            var files = CollectTemp(minAgeHours);
            int deleted = 0;
            long freed = 0;
            foreach (var (path, size) in files)
            {
                try { File.Delete(path); deleted++; freed += size; }
                catch { }
            }
            return deleted == 0 ? "Чистить нечего."
                : $"Удалено файлов: {deleted}, освобождено ~{Gb(freed)} (старше {minAgeHours} ч).";
        }
        catch (Exception ex)
        {
            return $"Ошибка очистки: {ex.Message}";
        }
    }

    // ---- Тяжёлые файлы ----

    public static string LargeFiles(string directory, long minMb = 100, int limit = 30)
    {
        try
        {
            var dir = new DirectoryInfo(directory);
            if (!dir.Exists) return $"Ошибка: папка {directory} не найдена.";
            long minBytes = minMb * 1024 * 1024;
            var top = new List<(string Path, long Size)>();
            foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if (f.Length >= minBytes) top.Add((f.FullName, f.Length));
                }
                catch { }
                if (top.Count > 5000) break;
            }
            var res = top.OrderByDescending(t => t.Size).Take(limit).ToList();
            if (res.Count == 0) return $"Файлов крупнее {minMb} МБ в {directory} нет.";
            var lines = new List<string> { $"Топ-{res.Count} тяжёлых файлов в {directory}:" };
            lines.AddRange(res.Select(t => $"  • {Mb(t.Size),10}  {t.Path}"));
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Ошибка поиска: {ex.Message}";
        }
    }
}
