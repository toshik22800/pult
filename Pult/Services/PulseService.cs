using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Pult.Services;

// «Пульс» — здоровье системы одним числом 0..100.
// Этап 1: только логика и хранение, без UI.
public sealed record PulseSnapshot(int Score, DateTime Time,
    int DiskScore, int JunkScore, int UpdatesScore, int ErrorsScore, int PrivacyScore);

// Детали для человеческого разбора (диалог по клику).
public sealed record PulseDetails(PulseSnapshot Snapshot, string DiskName, double DiskUsedPct,
    long TempBytes, int TempCount, int UpdateCount, int ErrorCount, int PrivClosed, int PrivTotal);

public static class PulseService
{
    private const int MaxHistory = 90;
    private static readonly TimeSpan MinInterval = TimeSpan.FromHours(6);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Pult", "pulse.json");

    private sealed record Parts(int Disk, string DiskName, double DiskUsed,
        int Junk, long TempBytes, int TempCount,
        int Updates, int UpdateCount, int Errors, int ErrorCount,
        int Privacy, int PrivClosed, int PrivTotal);

    private static Parts ComputeParts()
    {
        string diskName = "";
        double diskUsed = 0;
        int disk = 80;
        try
        {
            double worstFree = 100;
            bool any = false;
            foreach (var d in SystemMonitor.Disks())
            {
                try
                {
                    if (!d.IsReady || d.TotalBytes <= 0) continue;
                    any = true;
                    double free = 100.0 * d.FreeBytes / d.TotalBytes;
                    if (free < worstFree)
                    {
                        worstFree = free;
                        diskName = d.Name;
                        diskUsed = 100 - free;
                    }
                }
                catch { }
            }
            if (any)
                disk = worstFree > 30 ? 100
                    : (int)Math.Round(Math.Clamp((worstFree - 5) / (30 - 5) * 100, 0, 100));
        }
        catch { }

        long tempBytes = 0;
        int tempCount = 0;
        int junk = 80;
        try
        {
            var (cnt, bytes) = DiskCleanupService.MeasureTemp(24);
            tempCount = cnt;
            tempBytes = bytes;
            const long lo = 500L * 1024 * 1024;
            const long hi = 5L * 1024 * 1024 * 1024;
            junk = bytes < lo ? 100 : bytes > hi ? 40
                : (int)Math.Round(100 - (bytes - lo) / (double)(hi - lo) * 60);
        }
        catch { }

        int updates = 80, updateCount = 0;
        try
        {
            if (WingetService.Available())
            {
                updateCount = WingetService.ListUpdates()?.Count ?? 0;
                updates = updateCount <= 0 ? 100 : 100 - Math.Min(60, updateCount * 10);
            }
        }
        catch { }

        int errors = 80, errorCount = 0;
        try
        {
            errorCount = ExtraInfoService.EventErrors(15)?.Count ?? 0;
            errors = errorCount <= 0 ? 100 : 100 - Math.Min(50, errorCount * 5);
        }
        catch { }

        int priv = 100, closed = 0, total = 0;
        try
        {
            foreach (var tw in PrivacyService.All)
            {
                string st;
                try { st = PrivacyService.GetState(tw.Id); }
                catch { st = "na"; }
                total++;
                if (tw.Id is "cam" or "mic" ? st != "on" : st == "on") closed++;
            }
            if (total > 0) priv = (int)Math.Round(closed * 100.0 / total);
        }
        catch { priv = 80; }

        return new Parts(disk, diskName, diskUsed, junk, tempBytes, tempCount,
            updates, updateCount, errors, errorCount, priv, closed, total);
    }

    public static Task<PulseSnapshot> ComputeAsync() => Task.Run(() =>
    {
        var p = ComputeParts();
        int score = (int)Math.Round((p.Disk + p.Junk + p.Updates + p.Errors + p.Privacy) / 5.0);
        return new PulseSnapshot(
            Math.Clamp(score, 0, 100), DateTime.Now,
            p.Disk, p.Junk, p.Updates, p.Errors, p.Privacy);
    });

    public static Task<PulseDetails> ComputeDetailedAsync() => Task.Run(() =>
    {
        var p = ComputeParts();
        int score = (int)Math.Round((p.Disk + p.Junk + p.Updates + p.Errors + p.Privacy) / 5.0);
        var snap = new PulseSnapshot(
            Math.Clamp(score, 0, 100), DateTime.Now,
            p.Disk, p.Junk, p.Updates, p.Errors, p.Privacy);
        return new PulseDetails(snap, p.DiskName, p.DiskUsed,
            p.TempBytes, p.TempCount, p.UpdateCount, p.ErrorCount, p.PrivClosed, p.PrivTotal);
    });

    public static List<PulseSnapshot> LoadHistory()
    {
        try
        {
            string f = FilePath;
            if (!File.Exists(f)) return new List<PulseSnapshot>();
            var list = JsonSerializer.Deserialize<List<PulseSnapshot>>(File.ReadAllText(f));
            return list ?? new List<PulseSnapshot>();
        }
        catch { return new List<PulseSnapshot>(); }
    }

    // Не чаще раза в 6 часов — иначе частые замеры засорят историю.
    public static void AppendToHistory(PulseSnapshot s)
    {
        try
        {
            var list = LoadHistory();
            var last = list.Count > 0 ? list[^1] : null;
            if (last != null && (s.Time - last.Time) < MinInterval) return;
            list.Add(s);
            while (list.Count > MaxHistory) list.RemoveAt(0);
            string f = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, JsonSerializer.Serialize(list,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Error("Пульс: не сохранился: " + ex.Message); }
    }
}
