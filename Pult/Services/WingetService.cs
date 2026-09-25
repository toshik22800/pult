using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Pult.Services;

// Установка и обновление программ через winget (встроен в Windows 10/11).
// Только BCL + процессы. Все методы static, try/catch.
public static class WingetService
{
    public sealed record WingetUpdate(string Name, string Id, string Version, string Available);

    public static bool Available()
    {
        try
        {
            using var p = new Process();
            p.StartInfo.FileName = "cmd";
            p.StartInfo.Arguments = "/c where winget";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.Start();
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            string all = (stdout ?? "") + "\n" + (stderr ?? "");
            return all.IndexOf("winget.exe", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    public static List<WingetUpdate> ListUpdates()
    {
        try
        {
            // --accept-source-agreements: иначе первый запуск ждёт ввода.
            string output = RunWinget("upgrade --accept-source-agreements", 120000);
            var res = new List<WingetUpdate>();
            if (string.IsNullOrWhiteSpace(output)) return res;
            string[] lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            // Позиции колонок — по строке-разделителю из дефисов.
            // Заголовки локализованы, поэтому по именам не ищем.
            int dashIdx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = (lines[i] ?? "").TrimEnd();
                if (l.Length > 10 && l.Contains("---") && l.Trim('-', ' ').Length == 0)
                {
                    dashIdx = i;
                    break;
                }
            }
            if (dashIdx < 0) return res;
            string dashLine = lines[dashIdx].TrimEnd();
            var bounds = new List<(int Start, int End)>();
            int k = 0;
            while (k < dashLine.Length)
            {
                while (k < dashLine.Length && dashLine[k] != '-') k++;
                if (k >= dashLine.Length) break;
                int s = k;
                while (k < dashLine.Length && dashLine[k] == '-') k++;
                bounds.Add((s, k));
            }
            if (bounds.Count < 4) return res;
            static string Cell(string line, (int Start, int End) b)
            {
                if (b.Start >= line.Length) return "";
                int len = Math.Min(b.End, line.Length) - b.Start;
                if (len <= 0) return "";
                return line.Substring(b.Start, len).Trim();
            }
            for (int i = dashIdx + 1; i < lines.Length; i++)
            {
                string line = (lines[i] ?? "");
                if (string.IsNullOrWhiteSpace(line)) continue;
                string t = line.Trim();
                if (t.Trim('-', ' ').Length == 0) continue;
                string name = Cell(line, bounds[0]);
                string id = Cell(line, bounds[1]);
                string ver = Cell(line, bounds[2]);
                string avail = Cell(line, bounds[3]);
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                if (string.IsNullOrWhiteSpace(ver) && string.IsNullOrWhiteSpace(avail)) continue;
                res.Add(new WingetUpdate(name, id, ver, avail));
            }
            return res;
        }
        catch
        {
            return new List<WingetUpdate>();
        }
    }

    public static string UpgradeAll()
    {
        try
        {
            string output = RunWinget("upgrade --all --accept-package-agreements --accept-source-agreements --silent", 1200000);
            if (string.IsNullOrWhiteSpace(output)) return "Готово.";
            return Tail(output, 800);
        }
        catch (Exception ex)
        {
            return "Ошибка: " + ex.Message;
        }
    }

    public static string Install(string query)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query)) return "Ошибка: пустой запрос.";
            string q = query.Trim();
            // Закавычить запрос с пробелами, если ещё не в кавычках.
            if (q.Contains(" ") && !(q.StartsWith("\"") && q.EndsWith("\"")))
                q = "\"" + q.Replace("\"", "") + "\"";
            string output = RunWinget("install " + q + " --accept-package-agreements --accept-source-agreements", 1200000);
            if (string.IsNullOrWhiteSpace(output)) return "Готово.";
            return Tail(output, 800);
        }
        catch (Exception ex)
        {
            return "Ошибка: " + ex.Message;
        }
    }

    private static string RunWinget(string args, int timeoutMs)
    {
        try
        {
            // winget при редиректе пишет в UTF-8.
            var r = Proc.Run("winget", args, timeoutMs, System.Text.Encoding.UTF8);
            if (r.TimedOut) return "Ошибка: превышено время ожидания.";
            string combined = (r.Stdout ?? "");
            if (!string.IsNullOrWhiteSpace(r.Stderr))
                combined += "\n" + r.Stderr;
            combined = combined.Trim();
            // Обрезать слишком длинный вывод (оставить хвост).
            const int maxLen = 8000;
            if (combined.Length > maxLen)
                combined = combined.Substring(combined.Length - maxLen);
            return combined;
        }
        catch (Exception ex)
        {
            return "Ошибка: " + ex.Message;
        }
    }

    private static string Tail(string s, int n)
    {
        try
        {
            if (string.IsNullOrEmpty(s)) return "Готово.";
            s = s.Trim();
            if (string.IsNullOrEmpty(s)) return "Готово.";
            if (s.Length <= n) return s;
            return s.Substring(s.Length - n);
        }
        catch
        {
            return s ?? "Готово.";
        }
    }
}
