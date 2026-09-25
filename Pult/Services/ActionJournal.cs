using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Pult.Services;

// Журнал действий с откатом: каждое изменение пишется как «было → стало»
// в %AppData%\Pult\actions.json. Откат умеют: твики, приватность, схемы
// питания, автозагрузка. Удаления (файлы, программы) — только запись,
// откатить их нельзя, помечаются CanUndo=false.
public static class ActionJournal
{
    public sealed record JournalEntry(
        string Id,
        DateTime Time,
        string Kind,   // tweak | privacy | power | startup | info
        string Title,
        string Detail,
        bool CanUndo,
        string Data);  // JSON-пейлоад для отката

    private static readonly object Gate = new();
    private const int MaxEntries = 200;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Pult", "actions.json");

    public static int Count()
    {
        try { return LoadAll().Count; }
        catch { return 0; }
    }

    public static List<JournalEntry> LoadAll()
    {
        lock (Gate)
        {
            try
            {
                string f = FilePath;
                if (!File.Exists(f)) return new List<JournalEntry>();
                var list = JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(f));
                return list ?? new List<JournalEntry>();
            }
            catch { return new List<JournalEntry>(); }
        }
    }

    private static void SaveAll(List<JournalEntry> list)
    {
        try
        {
            string f = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, JsonSerializer.Serialize(list,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Error("Журнал: не сохранился: " + ex.Message); }
    }

    public static void Add(string kind, string title, string detail, bool canUndo, object? data = null)
    {
        try
        {
            lock (Gate)
            {
                var list = LoadAll();
                string payload = "";
                try { payload = data == null ? "" : JsonSerializer.Serialize(data); }
                catch { }
                list.Add(new JournalEntry(
                    Guid.NewGuid().ToString("N"), DateTime.Now,
                    kind, title, detail ?? "", canUndo, payload));
                while (list.Count > MaxEntries) list.RemoveAt(0);
                SaveAll(list);
            }
            AppLog.Info($"Журнал: {kind} — {title}. {detail}");
        }
        catch (Exception ex) { AppLog.Error("Журнал: не записался: " + ex.Message); }
    }

    // Удобные конструкторы записей.
    public static void AddTweak(string name, string id, bool wasOn, bool nowOn) =>
        Add("tweak", $"Твик: {name}",
            $"{OnOff(wasOn)} → {OnOff(nowOn)}", true, new { Id = id, PrevOn = wasOn });

    public static void AddPrivacy(string name, string id, bool prevApplyOn, bool nowApplyOn) =>
        Add("privacy", $"Приватность: {name}",
            $"{OnOff(prevApplyOn)} → {OnOff(nowApplyOn)}", true,
            new { Id = id, PrevApplyOn = prevApplyOn });

    public static void AddPower(string prevName, string prevGuid, string nowName) =>
        Add("power", "Схема питания",
            $"{(prevName.Length > 0 ? prevName : "—")} → {nowName}", true,
            new { PrevGuid = prevGuid });

    public static void AddStartup(string name, string location) =>
        Add("startup", $"Автозагрузка: {name}",
            $"выключено ({location}); бэкап в PultDisabled", true,
            new { Name = name });

    public static void AddInfo(string title, string detail) =>
        Add("info", title, detail, false);

    private static string OnOff(bool on) => on ? "вкл" : "выкл";

    // Откат одной записи. Возвращает текст результата.
    // Откат сам пишется в журнал как info (без права отката — иначе петли).
    public static string Undo(JournalEntry e)
    {
        try
        {
            string result = e.Kind switch
            {
                "tweak" => UndoTweak(e),
                "privacy" => UndoPrivacy(e),
                "power" => UndoPower(e),
                "startup" => UndoStartup(e),
                _ => "Эту запись откатить нельзя.",
            };
            if (result.StartsWith("Готово") || result.StartsWith("Возвращено"))
            {
                Add("info", $"Откат: {e.Title}", result, false);
            }
            AppLog.Info($"Откат «{e.Title}»: {result}");
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Откат «{e.Title}»: {ex.Message}");
            return $"Ошибка отката: {ex.Message}";
        }
    }

    private static T? ReadData<T>(JournalEntry e)
    {
        try
        {
            if (string.IsNullOrEmpty(e.Data)) return default;
            return JsonSerializer.Deserialize<T>(e.Data);
        }
        catch { return default; }
    }

    private sealed record TweakPayload(string Id, bool PrevOn);
    private sealed record PrivacyPayload(string Id, bool PrevApplyOn);
    private sealed record PowerPayload(string PrevGuid);
    private sealed record StartupPayload(string Name);

    private static string UndoTweak(JournalEntry e)
    {
        var d = ReadData<TweakPayload>(e);
        if (d == null || string.IsNullOrEmpty(d.Id)) return "Нет данных для отката.";
        string r = TweakService.Apply(d.Id, d.PrevOn);
        return r.StartsWith("Готово") ? $"Готово: {e.Title} возвращено." : r;
    }

    private static string UndoPrivacy(JournalEntry e)
    {
        var d = ReadData<PrivacyPayload>(e);
        if (d == null || string.IsNullOrEmpty(d.Id)) return "Нет данных для отката.";
        string r = PrivacyService.Apply(d.Id, d.PrevApplyOn);
        return r.StartsWith("Готово") ? $"Готово: {e.Title} возвращено." : r;
    }

    private static string UndoPower(JournalEntry e)
    {
        var d = ReadData<PowerPayload>(e);
        if (d == null || string.IsNullOrEmpty(d.PrevGuid)) return "Нет данных для отката.";
        string r = PrivacyService.SetPowerScheme(d.PrevGuid);
        return r.StartsWith("Готово") ? "Возвращено: предыдущая схема питания." : r;
    }

    private static string UndoStartup(JournalEntry e)
    {
        var d = ReadData<StartupPayload>(e);
        if (d == null || string.IsNullOrEmpty(d.Name)) return "Нет данных для отката.";
        string r = SystemMonitor.EnableStartup(d.Name);
        if (r.StartsWith("Включено:")) return $"Возвращено в автозагрузку: {d.Name}.";
        return r;
    }
}
