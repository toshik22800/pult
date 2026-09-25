using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Pult.Services;

// Встроенный мусор (AppX) — список и удаление. Только BCL + PowerShell.
public static class AppxService
{
    public sealed record AppxApp(string Name, string FullName);

    private static string RunPs(string script, int timeoutMs = 60000)
    {
        var r = Proc.Run("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + script,
            timeoutMs);
        if (r.TimedOut) throw new TimeoutException($"Превышено время ожидания ({timeoutMs / 1000} с).");
        return r.Stdout;
    }

    public static List<AppxApp> ListApps()
    {
        var res = new List<AppxApp>();
        try
        {
            string output = RunPs(
                "Get-AppxPackage | ForEach-Object { $_.Name + '|' + $_.PackageFullName }", 60000);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in output.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0 || !t.Contains('|')) continue;
                int sep = t.IndexOf('|');
                string name = t[..sep].Trim();
                string full = t[(sep + 1)..].Trim();
                if (name.Length == 0 || full.Length == 0 || !seen.Add(full)) continue;
                // Системные фреймворки не трогаем вообще.
                string nl = name.ToLowerInvariant();
                if (nl.StartsWith("microsoft.windows.shell") || nl.Contains("framework")
                    || nl.Contains("vclibs") || nl.Contains("net.native")
                    || nl == "microsoft.windows.startmenuexperiencehost"
                    || nl == "microsoft.windows.shellexperiencehost"
                    || nl.Contains("immersivecontrolpanel")
                    || nl.Contains("windows.security") || nl.Contains("bioenrollment")
                    || nl.Contains("windowspackagemanager")) continue;
                res.Add(new AppxApp(name, full));
            }
        }
        catch (TimeoutException) { throw; }
        catch { }
        return res.OrderBy(a => a.Name).ToList();
    }

    public static string Remove(string fullName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "Пустое имя пакета.";
            string safe = fullName.Replace("'", "''");
            string output = RunPs(
                $"Remove-AppxPackage -Package '{safe}' 2>&1 | Out-String", 120000);
            string t = output.Trim();
            if (t.Length == 0) return "Готово.";
            if (t.Length > 400) t = t[..400] + "…";
            return t;
        }
        catch (TimeoutException ex) { return $"Ошибка: {ex.Message}"; }
        catch (Exception ex) { return $"Ошибка: {ex.Message}"; }
    }
}
