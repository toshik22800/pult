using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Pult.Services;

// Приватность, часы активности, доставка обновлений, схемы питания.
// Всё через реестр и powercfg. Изменения — только по кнопкам из UI.
public static class PrivacyService
{
    // ---- Низкоуровневые хелперы ----

    private static int? ReadDword(RegistryKey root, string sub, string name)
    {
        try
        {
            using var k = root.OpenSubKey(sub);
            object? v = k?.GetValue(name);
            if (v is int i) return i;
            if (v is long l) return (int)l;
            return null;
        }
        catch { return null; }
    }

    private static string WriteDword(RegistryKey root, string sub, string name, int value)
    {
        try
        {
            using var k = root.OpenSubKey(sub, true) ?? root.CreateSubKey(sub);
            if (k == null) return "Нужны права администратора.";
            k.SetValue(name, value, RegistryValueKind.DWord);
            return "Готово.";
        }
        catch (Exception ex) { return $"Ошибка: {ex.Message}"; }
    }

    private static string DeleteValue(RegistryKey root, string sub, string name)
    {
        try
        {
            using var k = root.OpenSubKey(sub, true);
            k?.DeleteValue(name, false);
            return "Готово.";
        }
        catch (Exception ex) { return $"Ошибка: {ex.Message}"; }
    }

    // ---- Приватность: твики ----

    public sealed record PrivacyTweak(string Id, string Name, string Desc);

    public static List<PrivacyTweak> All { get; } = new()
    {
        new("telemetry", "Телеметрия", "Отправка диагностических данных — только обязательные."),
        new("activity", "Журнал действий в облако", "Не отправлять ленту активности в Microsoft."),
        new("advertid", "Рекламный ID", "Не выдавать приложениям идентификатор для рекламы."),
        new("tailored", "Персонализация", "Не использовать диагностику для рекомендаций."),
        new("feedback", "Просьбы оценить", "Не показывать уведомления «оцените Windows»."),
        new("cam", "Камера для приложений", "Глобальный доступ приложений к камере."),
        new("mic", "Микрофон для приложений", "Глобальный доступ приложений к микрофону."),
        new("location", "Местоположение", "Определение местоположения системой и приложениями."),
    };

    // state: true = приватно (слежка выкл) / для cam/mic/location true = доступ РАЗРЕШЁН.
    public static string GetState(string id)
    {
        try
        {
            switch (id)
            {
                case "telemetry":
                {
                    int? v = ReadDword(Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry");
                    return v == null ? "na" : v <= 1 ? "on" : "off";
                }
                case "activity":
                {
                    int? a = ReadDword(Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed");
                    int? b = ReadDword(Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities");
                    if (a == null && b == null) return "na";
                    return (a is 0 && b is 0) ? "on" : "off";
                }
                case "advertid":
                {
                    int? v = ReadDword(Registry.CurrentUser,
                        @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled");
                    return v == null ? "na" : v == 0 ? "on" : "off";
                }
                case "tailored":
                {
                    int? v = ReadDword(Registry.CurrentUser,
                        @"Software\Microsoft\Windows\CurrentVersion\Privacy",
                        "TailoredExperiencesWithDiagnosticDataEnabled");
                    return v == null ? "na" : v == 0 ? "on" : "off";
                }
                case "feedback":
                {
                    int? v = ReadDword(Registry.CurrentUser,
                        @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod");
                    return v == null ? "na" : v == 0 ? "on" : "off";
                }
                case "cam":
                {
                    string? v = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam",
                        "Value", null) as string;
                    if (v == null) return "na";
                    return v.Equals("Allow", StringComparison.OrdinalIgnoreCase) ? "on" : "off";
                }
                case "mic":
                {
                    string? v = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone",
                        "Value", null) as string;
                    if (v == null) return "na";
                    return v.Equals("Allow", StringComparison.OrdinalIgnoreCase) ? "on" : "off";
                }
                case "location":
                {
                    int? v = ReadDword(Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                        "DisableLocation");
                    return v == null ? "na" : v == 1 ? "on" : "off";
                }
            }
        }
        catch { }
        return "na";
    }

    // on=true: telemetry/activity/advertid/tailored/feedback/location — ЗАКРЫТЬ слежку;
    // cam/mic on=true — РАЗРЕШИТЬ доступ.
    public static string Apply(string id, bool on)
    {
        try
        {
            switch (id)
            {
                case "telemetry":
                    return WriteDword(Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                        "AllowTelemetry", on ? 0 : 3);
                case "activity":
                {
                    string r1 = WriteDword(Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\System",
                        "EnableActivityFeed", on ? 0 : 1);
                    if (!r1.StartsWith("Готово")) return r1;
                    string r2 = WriteDword(Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\System",
                        "UploadUserActivities", on ? 0 : 1);
                    if (!r2.StartsWith("Готово")) return r2;
                    return WriteDword(Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\System",
                        "PublishUserActivities", on ? 0 : 1);
                }
                case "advertid":
                    return WriteDword(Registry.CurrentUser,
                        @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                        "Enabled", on ? 0 : 1);
                case "tailored":
                    return WriteDword(Registry.CurrentUser,
                        @"Software\Microsoft\Windows\CurrentVersion\Privacy",
                        "TailoredExperiencesWithDiagnosticDataEnabled", on ? 0 : 1);
                case "feedback":
                    return WriteDword(Registry.CurrentUser,
                        @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", on ? 0 : 1);
                case "cam":
                case "mic":
                {
                    string store = id == "cam" ? "webcam" : "microphone";
                    try
                    {
                        Registry.SetValue(
                            $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{store}",
                            "Value", on ? "Allow" : "Deny");
                        return "Готово.";
                    }
                    catch (Exception ex) { return $"Ошибка: {ex.Message}"; }
                }
                case "location":
                    return WriteDword(Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                        "DisableLocation", on ? 1 : 0);
            }
        }
        catch (Exception ex) { return $"Ошибка: {ex.Message}"; }
        return "Неизвестный твик.";
    }

    // ---- Часы активности обновлений ----

    public static (int Start, int End) ActiveHours()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings");
            object? s = k?.GetValue("ActiveHoursStart");
            object? e = k?.GetValue("ActiveHoursEnd");
            int start = s is int si ? si : 8;
            int end = e is int ei ? ei : 17;
            return (Math.Clamp(start, 0, 23), Math.Clamp(end, 0, 23));
        }
        catch { return (8, 17); }
    }

    public static string SetActiveHours(int start, int end)
    {
        try
        {
            start = Math.Clamp(start, 0, 23);
            end = Math.Clamp(end, 0, 23);
            string r1 = WriteDword(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "ActiveHoursStart", start);
            if (!r1.StartsWith("Готово")) return r1;
            return WriteDword(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "ActiveHoursEnd", end);
        }
        catch (Exception ex) { return $"Ошибка: {ex.Message}"; }
    }

    // ---- Оптимизация доставки (P2P-раздача обновлений) ----

    public static string DeliveryState()
    {
        try
        {
            int? v = ReadDword(Registry.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode");
            if (v == null) return "na";
            return v is 0 or 100 ? "off" : "on";
        }
        catch { return "na"; }
    }

    public static string SetDelivery(bool enableP2p)
    {
        // 0 = только Microsoft (без P2P), 1 = LAN. Удаление ключа = по умолчанию.
        if (enableP2p)
            return DeleteValue(Registry.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode");
        return WriteDword(Registry.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0);
    }

    // ---- Схемы питания ----

    public sealed record PowerScheme(string Guid, string Name, bool Active);

    public static List<PowerScheme> PowerSchemes()
    {
        var res = new List<PowerScheme>();
        try
        {
            string active = "";
            try
            {
                string output = SystemMonitor.RunCmd("powercfg.exe", "/getactivescheme", 10000);
                var m = System.Text.RegularExpressions.Regex.Match(output,
                    @"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
                if (m.Success) active = m.Value.ToLowerInvariant();
            }
            catch { }
            string list = SystemMonitor.RunCmd("powercfg.exe", "/list", 10000);
            foreach (string line in list.Split('\n'))
            {
                try
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line,
                        @"([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\s*\(([^)]+)\)");
                    if (!m.Success) continue;
                    string guid = m.Groups[1].Value.ToLowerInvariant();
                    string name = m.Groups[2].Value.Trim().TrimEnd('*').Trim();
                    if (name.Length == 0) continue;
                    res.Add(new PowerScheme(guid, name, guid == active));
                }
                catch { }
            }
        }
        catch { }
        return res;
    }

    public static string SetPowerScheme(string guid)
    {
        try
        {
            string output = SystemMonitor.RunCmd("powercfg.exe", "/setactive " + guid, 15000);
            if (output.Contains("Недопустимые") || output.Contains("Invalid"))
                return "Не вышло переключить схему.";
            return "Готово.";
        }
        catch (Exception ex) { return $"Ошибка: {ex.Message}"; }
    }
}
