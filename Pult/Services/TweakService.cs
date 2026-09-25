using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Pult.Services;

// Системные твики Windows: чтение состояния из реестра/powercfg и применение.
// Только BCL + реестр + процессы. Все методы static, try/catch, строки на русском.
public static class TweakService
{
    public sealed record Tweak(string Id, string Name, string Desc, bool NeedsAdmin);

    public static List<Tweak> All { get; } = new()
    {
        new Tweak("hibernate", "Гибернация", "Файл hiberfil.sys и пункт в меню питания", true),
        new Tweak("ultimateperf", "Макс. производительность", "Схема электропитания Ultimate Performance", true),
        new Tweak("verboseboot", "Подробная загрузка", "Пишет что грузится вместо кружка", false),
        new Tweak("classicmenu", "Классическое меню ПКМ", "Старое контекстное меню Windows 10 (перезапуск проводника)", false),
        new Tweak("fileext", "Расширения файлов", "Показывать .exe/.txt в проводнике", false),
        new Tweak("hiddenfiles", "Скрытые файлы", "Показывать скрытые файлы и папки", false),
        new Tweak("gamedvr", "Game DVR", "Фоновая запись игр (выкл = +FPS)", false),
        new Tweak("searchhl", "Подсветка поиска", "Картинка дня в поиске Windows (выкл = чище)", false),
        new Tweak("wintheme", "Тёмная тема Windows", "Тёмные окна и панель задач", false),
        new Tweak("tasktrans", "Прозрачность Windows", "Полупрозрачная панель задач и окна", false),
        new Tweak("taskalign", "Панель по центру (Win11)", "Значки панели задач по центру, а не слева", false),
    };

    private const string UltimateGuid = "e9a42b02-d6f6-11d4-bf67-0040f6a76810";
    private const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string ClassicMenuPath = @"Software\Classes\CLSID\{86ca1b52-41f0-4c93-9a2f-b2f9d2d016a2}\InprocServer32";
    private const string ExplorerAdvancedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string GameConfigPath = @"System\GameConfigStore";
    private const string SearchSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\SearchSettings";
    private const string VerboseStatusPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string PowerHiberPath = @"SYSTEM\CurrentControlSet\Control\Power";
    private const string PersonalizePath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string TaskbarPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    // ---- Состояние: "on" / "off" / "na" ----

    public static string GetState(string id)
    {
        try
        {
            switch (id)
            {
                case "hibernate":
                {
                    // 1 = включена, 0 = выключена.
                    int v = ReadHklmDword(PowerHiberPath, "HibernateEnabled", -1);
                    if (v < 0) return "off";
                    return v == 1 ? "on" : "off";
                }
                case "ultimateperf":
                {
                    string output = RunProcess("powercfg", "/getactivescheme", 15000);
                    if (string.IsNullOrWhiteSpace(output)) return "na";
                    return output.IndexOf(UltimateGuid, StringComparison.OrdinalIgnoreCase) >= 0 ? "on" : "off";
                }
                case "verboseboot":
                {
                    int v = ReadHklmDword(VerboseStatusPath, "VerboseStatus", 0);
                    return v == 1 ? "on" : "off";
                }
                case "classicmenu":
                {
                    try
                    {
                        using var k = Registry.CurrentUser.OpenSubKey(ClassicMenuPath, false);
                        return k != null ? "on" : "off";
                    }
                    catch { return "na"; }
                }
                case "fileext":
                {
                    // HideFileExt: 0 = показывать (on), 1 = скрывать (off).
                    int v = ReadHkcuDword(ExplorerAdvancedPath, "HideFileExt", 1);
                    return v == 0 ? "on" : "off";
                }
                case "hiddenfiles":
                {
                    // Hidden: 1 = показывать (on), 2 = скрывать (off).
                    int v = ReadHkcuDword(ExplorerAdvancedPath, "Hidden", 2);
                    return v == 1 ? "on" : "off";
                }
                case "gamedvr":
                {
                    // GameDVR_Enabled: 1 = включён (on), 0 = выключен (off).
                    int v = ReadHkcuDword(GameConfigPath, "GameDVR_Enabled", 1);
                    return v == 1 ? "on" : "off";
                }
                case "searchhl":
                {
                    // IsDynamicSearchBoxEnabled: 1 = подсветка есть (on), 0 = нет (off).
                    int v = ReadHkcuDword(SearchSettingsPath, "IsDynamicSearchBoxEnabled", 1);
                    return v == 1 ? "on" : "off";
                }
                case "wintheme":
                {
                    // AppsUseLightTheme: 0 = тёмная (on), 1 = светлая (off).
                    int v = ReadHkcuDword(PersonalizePath, "AppsUseLightTheme", 1);
                    return v == 0 ? "on" : "off";
                }
                case "tasktrans":
                {
                    // EnableTransparency: 1 = прозрачность есть (on).
                    int v = ReadHkcuDword(PersonalizePath, "EnableTransparency", 1);
                    return v == 1 ? "on" : "off";
                }
                case "taskalign":
                {
                    // TaskbarAl: 1 = по центру (on), 0 = слева (off). Ключа нет = Win10.
                    int v = ReadHkcuDword(TaskbarPath, "TaskbarAl", -1);
                    if (v < 0) return "na";
                    return v == 1 ? "on" : "off";
                }
                default:
                    return "na";
            }
        }
        catch
        {
            return "na";
        }
    }

    // ---- Применение ----

    public static string Apply(string id, bool on)
    {
        try
        {
            switch (id)
            {
                case "hibernate":
                {
                    RunProcess("powercfg", on ? "/hibernate on" : "/hibernate off", 15000);
                    break;
                }
                case "ultimateperf":
                {
                    if (on)
                    {
                        // 1) Если схема уже есть в списке — просто активируем.
                        string list = "";
                        try { list = RunProcess("powercfg", "/list", 15000); } catch { }
                        var mList = Regex.Match(list ?? "", @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
                        bool hasUltimate = (list ?? "").IndexOf(UltimateGuid, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (hasUltimate && mList.Success)
                        {
                            // В списке может быть несколько GUID — ищем строку с Ultimate GUID.
                            RunProcess("powercfg", "/setactive " + UltimateGuid, 15000);
                            break;
                        }
                        // 2) Клонируем схему и активируем GUID из вывода.
                        string dup = RunProcess("powercfg", "-duplicatescheme " + UltimateGuid, 15000);
                        var m = Regex.Match(dup ?? "", @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                        if (m.Success)
                        {
                            RunProcess("powercfg", "/setactive " + m.Value, 15000);
                            break;
                        }
                        // 3) Схема уже была продублирована ранее — забираем GUID из /list.
                        if (hasUltimate)
                        {
                            RunProcess("powercfg", "/setactive " + UltimateGuid, 15000);
                            break;
                        }
                        return string.IsNullOrWhiteSpace(dup) ? "Ошибка: не удалось создать схему Ultimate Performance." : "Ошибка: " + Tail(dup, 500);
                    }
                    else
                    {
                        RunProcess("powercfg", "/setactive " + BalancedGuid, 15000);
                        break;
                    }
                }
                case "verboseboot":
                {
                    WriteHklmDword(VerboseStatusPath, "VerboseStatus", on ? 1 : 0);
                    break;
                }
                case "classicmenu":
                {
                    if (on)
                    {
                        try
                        {
                            using var k = Registry.CurrentUser.OpenSubKey(ClassicMenuPath, true)
                                ?? Registry.CurrentUser.CreateSubKey(ClassicMenuPath);
                            if (k == null) return "Ошибка: не удалось создать ключ реестра.";
                            k.SetValue("", "", RegistryValueKind.String);
                        }
                        catch (Exception ex)
                        {
                            return "Ошибка: " + ex.Message;
                        }
                        RestartExplorer();
                        break;
                    }
                    else
                    {
                        try
                        {
                            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\{86ca1b52-41f0-4c93-9a2f-b2f9d2d016a2}", false);
                        }
                        catch { }
                        RestartExplorer();
                        break;
                    }
                }
                case "fileext":
                {
                    // on = показывать → HideFileExt 0; off → 1.
                    WriteHkcuDword(ExplorerAdvancedPath, "HideFileExt", on ? 0 : 1);
                    break;
                }
                case "hiddenfiles":
                {
                    // on = показывать → Hidden 1; off → 2.
                    WriteHkcuDword(ExplorerAdvancedPath, "Hidden", on ? 1 : 2);
                    break;
                }
                case "gamedvr":
                {
                    WriteHkcuDword(GameConfigPath, "GameDVR_Enabled", on ? 1 : 0);
                    try { WriteHkcuDword(GameConfigPath, "AppCaptureEnabled", on ? 1 : 0); } catch { }
                    break;
                }
                case "searchhl":
                {
                    WriteHkcuDword(SearchSettingsPath, "IsDynamicSearchBoxEnabled", on ? 1 : 0);
                    break;
                }
                case "wintheme":
                {
                    // on = тёмная: оба ключа в 0; off = светлая: в 1.
                    WriteHkcuDword(PersonalizePath, "AppsUseLightTheme", on ? 0 : 1);
                    try { WriteHkcuDword(PersonalizePath, "SystemUsesLightTheme", on ? 0 : 1); } catch { }
                    break;
                }
                case "tasktrans":
                {
                    WriteHkcuDword(PersonalizePath, "EnableTransparency", on ? 1 : 0);
                    break;
                }
                case "taskalign":
                {
                    WriteHkcuDword(TaskbarPath, "TaskbarAl", on ? 1 : 0);
                    RestartExplorer();
                    break;
                }
                default:
                    return "Ошибка: неизвестный твик.";
            }
            // Проверка вслепую не годится: перечитываем состояние и сравниваем.
            string after;
            try { after = GetState(id); }
            catch { after = "na"; }
            if ((after == "on") != on)
                return "Ошибка: не применилось (проверка состояния не сошлась).";
            return "Готово.";
        }
        catch (System.UnauthorizedAccessException)
        {
            return "Нужны права администратора.";
        }
        catch (System.Security.SecurityException)
        {
            return "Нужны права администратора.";
        }
        catch (Exception ex)
        {
            string msg = ex.Message ?? "";
            if (msg.IndexOf("администратор", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("access", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("отказано", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Нужны права администратора.";
            return "Ошибка: " + msg;
        }
    }

    // ---- Приватные помощники ----

    private static int ReadHkcuDword(string path, string name, int def)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(path, false);
            if (k == null) return def;
            object? v = k.GetValue(name);
            if (v == null) return def;
            if (v is int i) return i;
            try { return Convert.ToInt32(v); } catch { return def; }
        }
        catch { return def; }
    }

    private static int ReadHklmDword(string path, string name, int def)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(path, false);
            if (k == null) return def;
            object? v = k.GetValue(name);
            if (v == null) return def;
            if (v is int i) return i;
            try { return Convert.ToInt32(v); } catch { return def; }
        }
        catch { return def; }
    }

    private static void WriteHkcuDword(string path, string name, int value)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(path, true)
                ?? Registry.CurrentUser.CreateSubKey(path);
            if (k == null) throw new Exception("не удалось открыть ключ реестра.");
            k.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            throw new Exception(ex.Message, ex);
        }
    }

    private static void WriteHklmDword(string path, string name, int value)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(path, true)
                ?? Registry.LocalMachine.CreateSubKey(path);
            if (k == null) throw new Exception("не удалось открыть ключ реестра.");
            k.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch (System.UnauthorizedAccessException)
        {
            throw;
        }
        catch (System.Security.SecurityException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exception(ex.Message, ex);
        }
    }

    private static void RestartExplorer()
    {
        try
        {
            RunProcess("taskkill", "/F /IM explorer.exe", 15000);
            // explorer.exe перезапускается сам после taskkill.
        }
        catch { }
    }

    private static string RunProcess(string file, string args, int timeoutMs)
    {
        var r = Proc.Run(file, args, timeoutMs);
        if (r.TimedOut) throw new Exception("Превышено время ожидания.");
        if (r.ExitCode != 0)
        {
            string err = (r.Stderr ?? "").Trim();
            if (err.Length == 0) err = (r.Stdout ?? "").Trim();
            throw new Exception(err.Length > 0 ? Tail(err, 300) : $"{file} завершил с кодом {r.ExitCode}.");
        }
        if (!string.IsNullOrEmpty(r.Stderr) && string.IsNullOrWhiteSpace(r.Stdout))
            return r.Stderr;
        if (!string.IsNullOrEmpty(r.Stderr))
            return r.Stdout + "\n" + r.Stderr;
        return r.Stdout ?? "";
    }

    private static string Tail(string s, int n)
    {
        try
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            if (s.Length <= n) return s;
            return s.Substring(s.Length - n);
        }
        catch { return s ?? ""; }
    }
}
