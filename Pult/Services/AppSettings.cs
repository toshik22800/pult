using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Pult.Services;

public sealed class LlmSettings
{
    public string Type { get; set; } = "ollama"; // ollama | openai_compatible
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434/v1";
    public string ApiKey { get; set; } = "ollama";
    public string Model { get; set; } = "qwen2.5:7b";
}

public sealed class AppSettings
{
    // Этап 2: эффекты и интерфейс — две независимые оси.
    public enum EffectsMode { Auto = 0, Minimum = 1, Normal = 2, Maximum = 3 }
    public enum InterfaceMode { Simple = 0, Advanced = 1 }

    public LlmSettings Llm { get; set; } = new();
    public bool ExperimentalEnabled { get; set; }
    public List<string> ExtraGameDirs { get; set; } = new();
    public bool AutoOrganizeEnabled { get; set; }
    public int AutoOrganizeIntervalMin { get; set; } = 60;
    public List<string> AutoOrganizeDirs { get; set; } = new()
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
    };
    public EffectsMode Effects { get; set; } = EffectsMode.Auto;
    public InterfaceMode Interface { get; set; } = InterfaceMode.Simple;
    public bool StrictPrivacyMode { get; set; }
    public double WidgetLeft { get; set; } = double.NaN;
    public double WidgetTop { get; set; } = double.NaN;
    public bool WidgetVisible { get; set; }
    public bool WidgetTopmost { get; set; } = true;
    public string AccentColor { get; set; } = "#7C6CF0";
    public string BackgroundColor { get; set; } = "#0A0E1A";
    public string PanelColor { get; set; } = "#0E1424";
    // 🆕 0.14.0: основной шрифт интерфейса (Вид → Шрифт) и масштаб UI
    // (Вид → Масштаб). UiScale зажат Appearance.SetScale в 0.75..1.75.
    public string FontKey { get; set; } = "segoe";
    public double UiScale { get; set; } = 1.0;

    // 🆕 0.14.0: каталог настроек. GetFolderPath(ApplicationData) в .NET не
    // учитывает переменную окружения APPDATA (проверено рендером: оверрайды
    // из копии настроек не доходили до стенда). Поэтому сначала читаем %APPDATA%
    // напрямую — как и делают обычные Windows-приложения; в штатном запуске она
    // совпадает с GetFolderPath, а тестовый драйвер render_adv подменяет каталог,
    // не трогая файл юзера. Если переменной нет — прежний путь как фолбэк.
    private static string Dir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrWhiteSpace(env)) return Path.Combine(env, "Pult");
            return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "Pult");
        }
    }

    private static string File => Path.Combine(Dir, "settings.json");

    // 🆕 0.14.5: JSON, который переживает NaN. WidgetLeft/WidgetTop по
    // умолчанию NaN («виджет не размещён»), а System.Text.Json без этого
    // флага бросает ArgumentException ИМЕННО на записи — свежие настройки
    // не сохранялись вообще, файл не появлялся и мастер первого запуска
    // возвращался при каждом запуске («нажал Начать — ничего»). Набор один
    // и на запись, и на чтение: файл с "NaN" читается обратно, старые файлы
    // с обычными числами — тоже.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // Load() зовут десятки раз за сессию — ошибку чтения логируем один раз,
    // без спама в app.log.
    private static bool _loadErrLogged;

    private static void LogLoadError(Exception ex)
    {
        if (_loadErrLogged) return;
        _loadErrLogged = true;
        AppLog.Error("Чтение настроек: " + ex.Message);
    }

    public static bool Exists()
    {
        try { return System.IO.File.Exists(File); }
        catch { return false; }
    }

    public static AppSettings Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
            {
                string json = System.IO.File.ReadAllText(File);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s != null)
                {
                    s.Llm ??= new LlmSettings();
                    s.ExtraGameDirs ??= new List<string>();
                    s.AutoOrganizeDirs ??= new List<string>();
                    s.AccentColor = NormHex(s.AccentColor, "#7C6CF0");
                    s.BackgroundColor = NormHex(s.BackgroundColor, "#0A0E1A");
                    s.PanelColor = NormHex(s.PanelColor, "#0E1424");
                    // 🆕 0.14.1: миграция мусорного FontKey из 0.14.0 —
                    // Font_Click писал в файл индекс чипа ("0".."3").
                    // Нормализуем только в памяти (файл юзера не трогаем,
                    // Save сохранит валидный ключ при следующем сохранении).
                    s.FontKey = Appearance.NormFontKey(s.FontKey);
                    s.MigrateLegacy(json);
                    return s;
                }
            }
        }
        catch (Exception ex) { LogLoadError(ex); }
        return new AppSettings();
    }

    // Кривой hex из файла — дефолт, а не падение.
    public static string NormHex(string? v, string def)
    {
        try
        {
            v = (v ?? "").Trim();
            if (v.StartsWith("#")) v = v[1..];
            if (v.Length == 6 && v.All(c =>
                (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return "#" + v.ToUpperInvariant();
        }
        catch { }
        return def;
    }
    // "LightMode" (bool) и "SimpleMode" (bool), а "Effects"/"Interface" нет.
    // Новые файлы содержат только новые ключи — старые больше не пишем.
    // Миграция старых файлов: там ключи "Mode", "LightMode" и "SimpleMode",
    // а "Effects"/"Interface" нет. Новые файлы содержат только новые ключи.
    private void MigrateLegacy(string json)
    {
        try
        {
            // Записанный нами NaN уходит в файл строкой ("NaN") — это валидный
            // JSON, JsonDocument его ест и миграция легаси не страдает; кривой
            // токен от ручной правки глотает catch миграции (не критично:
            // легаси-ключи есть только в старых файлах без координат виджета).
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Effects", out _))
            {
                if (root.TryGetProperty("Mode", out var m) && m.ValueKind == JsonValueKind.Number)
                {
                    Effects = m.GetInt32() switch
                    {
                        1 => EffectsMode.Minimum, // Лёгкий
                        2 => EffectsMode.Maximum, // Ультра
                        _ => EffectsMode.Normal,  // Полный
                    };
                }
                else if (root.TryGetProperty("LightMode", out var l)
                    && (l.ValueKind == JsonValueKind.True || l.ValueKind == JsonValueKind.False))
                {
                    Effects = l.GetBoolean() ? EffectsMode.Minimum : EffectsMode.Normal;
                }
            }
            if (!root.TryGetProperty("Interface", out _))
            {
                if (root.TryGetProperty("SimpleMode", out var sm)
                    && (sm.ValueKind == JsonValueKind.True || sm.ValueKind == JsonValueKind.False))
                {
                    Interface = sm.GetBoolean() ? InterfaceMode.Simple : InterfaceMode.Advanced;
                }
            }
        }
        catch { }
    }

    // Экспериментальное работает только в расширенном интерфейсе.
    public static bool ExpOn()
    {
        try
        {
            var s = Load();
            return s.ExperimentalEnabled && s.Interface == InterfaceMode.Advanced;
        }
        catch { return false; }
    }

    // Авто-подбор эффектов. Сигналы читаются тут, пороги — в чистой
    // DecideEffects (стенд дампит таблицу решений без мока ОС).
    public static EffectsMode DetectEffects()
    {
        try
        {
            int tier = 0;
            try { tier = RenderCapability.Tier >> 16; } catch { }
            bool anim = true;
            try { anim = SystemParameters.ClientAreaAnimation; } catch { }
            bool battery = false;
            try { battery = SystemParameters.PowerLineStatus == PowerLineStatus.Offline; }
            catch { }
            return DecideEffects(tier, anim, battery);
        }
        catch { return EffectsMode.Normal; }
    }

    // 🆕 0.14.2: явные пороги Авто (баг-репорт 0.14.1 просил задокументировать
    // критерии). Все три сигнала участвуют, порядок — от жёсткого к мягкому:
    //   1) Tier < 2 (референс/графика уровня DX9) ИЛИ ОС запретила анимации
    //      (ClientAreaAnimation = false)             → Minimum;
    //   2) иначе, но питание от батарея              → Normal;
    //   3) иначе (Tier ≥ 2, анимации разрешены, сеть) → Maximum.
    // Порог Tier ≥ 2 осознан: DX9-уровень тянет плоские фейды, но не
    // свечения/градиенты Максимума.
    public static EffectsMode DecideEffects(int tier, bool anim, bool battery)
    {
        if (tier < 2 || !anim) return EffectsMode.Minimum;
        if (battery) return EffectsMode.Normal;
        return EffectsMode.Maximum;
    }

    public static EffectsMode EffectiveEffects()
    {
        try
        {
            var s = Load();
            return s.Effects == EffectsMode.Auto ? DetectEffects() : s.Effects;
        }
        catch { return EffectsMode.Normal; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // Запись через JsonOpts: без AllowNamedFloatingPointLiterals
            // NaN-координаты виджета роняли сериализацию, а catch молчал —
            // юзер правил настройки, а файла не было.
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex) { AppLog.Error("Сохранение настроек: " + ex.Message); }
    }
}
