using System;
using System.Windows;
using System.Windows.Media;

namespace Pult.Services;

// 🆕 0.14.0: шрифт интерфейса и масштаб (зум) UI.
// «Осторожно»: масштаб — только LayoutTransform на корне окна. Контент
// измеряется в размере «окно / scale» и рендерится в scale раз — окно
// заполняется целиком, обрезки нет, текст остаётся чётким (WPF рисует
// шрифт в эффективном размере). Значение зажато в безопасный диапазон,
// при отказе ресурса F_Ui остаётся системный Segoe UI.
public static class Appearance
{
    public const double MinScale = 0.75;
    public const double MaxScale = 1.75;

    public static double UiScale { get; private set; } = 1.0;
    public static string FontKey { get; private set; } = "segoe";

    // Каталог шрифтов для настроек: 4 качества (пиксельный ретро + три
    // системных, обязательно с кириллицей — все три шлются с Windows).
    public static readonly (string Key, string Title)[] Fonts =
    {
        ("retro", "Ретро"),
        ("segoe", "Segoe UI"),
        ("bahnschrift", "Bahnschrift"),
        ("trebuchet", "Trebuchet MS"),
    };

    public static void Init(AppSettings s)
    {
        ApplyFont(NormFontKey(s?.FontKey));
        SetScale(s?.UiScale ?? 1.0);
    }

    // 🆕 0.14.1: нормализация ключа шрифта. 0.14.0 писал в настройки
    // ИНДЕКС чипа ("0".."3") вместо ключа — юзерские файлы с тем релизом
    // содержат цифру (у нашего юзера стоит "0"). Одиночная цифра = номер
    // чипа = Fonts[n].Key (порядок чипов font0..font3 совпадает с Fonts):
    // это был осознённый клик по чипу, восстанавливаем выбор. Неизвестный
    // ключ — тоже в segoe, как его и рисует ApplyFont.
    public static string NormFontKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "segoe";
        key = key.Trim();
        if (key.Length == 1 && key[0] >= '0' && key[0] <= '3')
            return Fonts[key[0] - '0'].Key;
        foreach (var f in Fonts)
            if (string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)) return f.Key;
        return "segoe";
    }

    public static void ApplyFont(string key)
    {
        key = NormFontKey(key);
        FontKey = key;
        try
        {
            FontFamily ff = key switch
            {
                // Ретро = тот же объект FontFamily, что и заголовочный
                // F_Pixel из App.xaml (относительный pack-путь уже собран
                // XAML-загрузчиком — переиспользуем, не парсим сами).
                "retro" => (FontFamily)Application.Current.Resources["F_Pixel"],
                "bahnschrift" => new FontFamily("Bahnschrift"),
                "trebuchet" => new FontFamily("Trebuchet MS"),
                _ => new FontFamily("Segoe UI"),
            };
            Application.Current.Resources["F_Ui"] = ff;
        }
        catch (Exception ex) { AppLog.Error("Шрифт: " + ex.Message); }
    }

    public static void SetScale(double s)
    {
        if (double.IsNaN(s) || double.IsInfinity(s)) s = 1.0;
        UiScale = Math.Clamp(s, MinScale, MaxScale);
        try
        {
            foreach (Window w in Application.Current.Windows) ApplyScale(w);
        }
        catch { }
    }

    // 🆕 0.14.1: режим текста по текущему масштабу (фидбек: «мыло жёсткое»).
    // Display грид-фитит глифы под сетку ЛЕЖАЩЕГО макета, а LayoutTransform
    // потом растягивает уже подогнанные пиксели — двойная интерполяция и
    // размазывает текст при UiScale ≠ 1. Ideal считает подгонку сразу
    // в экранных координатах. При масштабе 1.0 оставляем Display — картина
    // как в XAML окон. Им же пользуется рендер-стенд: свои окна он через
    // Appearance.Init не гоняет.
    public static TextFormattingMode TextMode => ModeFor(UiScale);

    private static TextFormattingMode ModeFor(double scale) =>
        Math.Abs(scale - 1.0) < 0.001
            ? TextFormattingMode.Display
            : TextFormattingMode.Ideal;

    public static void ApplyScale(Window w) => ApplyScale(w, UiScale);

    // 🆕 0.14.5: масштаб с полом для окон первого входа (гейт, мастер).
    // У юзера UiScale=0.8 — без пола окно ужимается до 416×336, и текст
    // мастера нечитаем. Пол 1.0: вниз не мельчаем, вверх — зумим как обычно.
    public static void ApplyScale(Window w, double minScale) => ApplyScaleCore(w, Math.Max(UiScale, minScale));

    private static void ApplyScaleCore(Window w, double scale)
    {
        try
        {
            if (w is null) return;
            var mode = ModeFor(scale);
            TextOptions.SetTextFormattingMode(w, mode);
            TextOptions.SetTextRenderingMode(w, TextRenderingMode.ClearType);
            // LayoutTransform живёт на FrameworkElement (не на UIElement).
            if (w.Content is FrameworkElement root)
            {
                // Локальные значения и на корне контента: наследование от
                // окна теряется, когда рендер-стенд вынимает Content в
                // Border (проверено: Window → Display, после извлечения →
                // default Ideal). Без локального значения на корне рендер
                // поехал бы с реального окна.
                TextOptions.SetTextFormattingMode(root, mode);
                TextOptions.SetTextRenderingMode(root, TextRenderingMode.ClearType);
                root.LayoutTransform = Math.Abs(scale - 1.0) < 0.001
                    ? Transform.Identity
                    : new ScaleTransform(scale, scale);
            }
        }
        catch { }
    }
}
