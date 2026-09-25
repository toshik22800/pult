using System;

namespace Pult.Services;

// Точка восстановления Windows (Checkpoint-Computer) через powershell.
// Вызывать с фонового потока: занимает десятки секунд.
public static class RestorePointService
{
    public static string Create(string description = "Пульт: перед изменениями")
    {
        try
        {
            string desc = (description ?? "").Replace("\"", "").Replace("'", "").Trim();
            if (desc.Length == 0) desc = "Пульт: перед изменениями";
            if (desc.Length > 100) desc = desc[..100];
            var r = Proc.Run("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description '{desc}' -RestorePointType 'MODIFY_SETTINGS'\"",
                180000);
            if (r.TimedOut) return "Превышено время ожидания (3 мин).";
            string all = ((r.Stdout ?? "") + "\n" + (r.Stderr ?? "")).Trim();
            if (all.IndexOf("в течение 24 часов", StringComparison.OrdinalIgnoreCase) >= 0
                || all.IndexOf("24 hour", StringComparison.OrdinalIgnoreCase) >= 0
                || all.IndexOf("815345001", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Точка уже создавалась за последние 24 часа — новая не нужна.";
            if (all.Length == 0) return "Готово: точка восстановления создана.";
            if (r.ExitCode == 0) return "Готово: точка восстановления создана.";
            return "Не вышло: " + Tail(all, 300);
        }
        catch (Exception ex) { return $"Не вышло: {ex.Message}"; }
    }

    private static string Tail(string s, int n)
    {
        s = (s ?? "").Trim();
        return s.Length <= n ? s : "…" + s[^n..];
    }
}
