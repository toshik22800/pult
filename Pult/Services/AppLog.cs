using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace Pult.Services;

// Обычный файловый лог (не только краши): %LocalAppData%\Pult\app.log
// + сборка диагностического текста для кнопки «Скопировать диагностику».
public static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static string FilePath
    {
        get
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pult");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "app.log");
            }
            catch { return Path.Combine(Path.GetTempPath(), "pult-app.log"); }
        }
    }

    public static void Info(string what)
    {
        Write("INFO", what);
    }

    public static void Error(string what)
    {
        Write("ERROR", what);
    }

    private static void Write(string level, string what)
    {
        try
        {
            lock (Gate)
            {
                string path = FilePath;
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        // Ротация: оставляем вторую половину файла.
                        string all = File.ReadAllText(path);
                        File.WriteAllText(path, all[^((int)MaxBytes / 2)..]);
                    }
                }
                catch { }
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level}: {what}\n");
            }
        }
        catch { }
    }

    private static string Tail(string path, int maxChars)
    {
        try
        {
            if (!File.Exists(path)) return "(нет файла)";
            string t = File.ReadAllText(path);
            if (t.Length > maxChars) t = "…" + t[^maxChars..];
            return t.Trim().Length == 0 ? "(пусто)" : t.Trim();
        }
        catch (Exception ex) { return $"(не прочиталось: {ex.Message})"; }
    }

    public static string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        try
        {
            string ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "—";
            sb.AppendLine($"Пульт {ver}");
            sb.AppendLine($"ОС: {Environment.OSVersion} (64-bit: {Environment.Is64BitOperatingSystem})");
            sb.AppendLine($"Админ: {(AdminGate.IsAdmin() ? "да" : "нет")}");
            try
            {
                var s = AppSettings.Load();
                string key = string.IsNullOrWhiteSpace(s.Llm.ApiKey) ? "(пусто)" : "***";
                sb.AppendLine($"Модель: {s.Llm.Type} {s.Llm.BaseUrl} / {s.Llm.Model} / ключ {key}");
                sb.AppendLine($"Экспериментальное: {(s.ExperimentalEnabled ? "вкл" : "выкл")}");
            }
            catch (Exception ex) { sb.AppendLine($"Настройки: не прочитались ({ex.Message})"); }
            try { sb.AppendLine($"Записей журнала: {ActionJournal.Count()}"); }
            catch { }
            sb.AppendLine("--- app.log (хвост) ---");
            sb.AppendLine(Tail(FilePath, 3000));
            sb.AppendLine("--- crash.log (хвост) ---");
            sb.AppendLine(Tail(CrashLog.Path, 3000));
        }
        catch (Exception ex) { sb.AppendLine($"Сбор диагностики упал: {ex.Message}"); }
        return sb.ToString();
    }

    public static string CopyDiagnostics()
    {
        try
        {
            string text = BuildDiagnostics();
            Clipboard.SetText(text);
            Info("Диагностика скопирована в буфер.");
            return "Диагностика скопирована в буфер обмена.";
        }
        catch (Exception ex)
        {
            Error("Копирование диагностики: " + ex.Message);
            return $"Не вышло скопировать: {ex.Message}";
        }
    }
}
