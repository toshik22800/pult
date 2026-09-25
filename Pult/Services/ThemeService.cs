using System;
using System.Windows;
using System.Windows.Media;

namespace Pult.Services;

// Персонализация: акцент + фон + панель из настроек летят в ресурсы,
// весь интерфейс на DynamicResource подхватывает мгновенно, без перезапуска.
public static class ThemeService
{
    // Цвета сменились — окна пересобирают свечения под новый акцент.
    public static event Action? Changed;

    public sealed record Preset(string Name, string Accent, string Bg, string Panel);

    public static readonly Preset[] Presets =
    {
        new("Фиолет", "#7C6CF0", "#0A0E1A", "#0E1424"),
        new("Хакер", "#3FB950", "#070B07", "#0C120C"),
        new("Океан", "#22D3EE", "#080E18", "#0C1626"),
        new("Закат", "#F0883E", "#120C08", "#1A100A"),
        new("Снег", "#6D5EF2", "#F4F5FA", "#FFFFFF"),
    };

    public static void Apply()
    {
        try
        {
            var s = AppSettings.Load();
            ApplyColors(s.AccentColor, s.BackgroundColor, s.PanelColor);
        }
        catch { }
    }

    public static void ApplyPreset(Preset p)
    {
        try
        {
            var s = AppSettings.Load();
            s.AccentColor = AppSettings.NormHex(p.Accent, "#7C6CF0");
            s.BackgroundColor = AppSettings.NormHex(p.Bg, "#0A0E1A");
            s.PanelColor = AppSettings.NormHex(p.Panel, "#0E1424");
            s.Save();
            ApplyColors(s.AccentColor, s.BackgroundColor, s.PanelColor);
            AppLog.Info($"Тема: {p.Name}.");
        }
        catch (Exception ex) { AppLog.Error("Тема: " + ex.Message); }
    }

    // Свои цвета из настроек: нормализуем, сохраняем, применяем.
    public static void ApplyCustom(string accentHex, string bgHex, string panelHex)
    {
        try
        {
            var s = AppSettings.Load();
            s.AccentColor = AppSettings.NormHex(accentHex, s.AccentColor);
            s.BackgroundColor = AppSettings.NormHex(bgHex, s.BackgroundColor);
            s.PanelColor = AppSettings.NormHex(panelHex, s.PanelColor);
            s.Save();
            ApplyColors(s.AccentColor, s.BackgroundColor, s.PanelColor);
            AppLog.Info("Тема: свои цвета.");
        }
        catch (Exception ex) { AppLog.Error("Тема: " + ex.Message); }
    }

    // Полная палитра из трёх базовых цветов: рамки, плитки, сайдбар и подписи
    // выводятся из акцента/панели/фона — тема «одевает» весь интерфейс целиком,
    // остаточной синевы (старые рамки, полоски, плитки) не остаётся.
    // Кисти в словаре ресурсов заморожены — смена ВСЕГДА подменяет объект
    // (дамп стенда: и исходные, и записанные — frozen=True). Статические
    // ссылки и локальные захваты подмены не видят: весь код, читающий кисти
    // вручную (BuildCards, RefreshNavActive), перечитывает ресурсы в
    // OnThemeChanged, а весь UI — на DynamicResource.
    public static void ApplyColors(string accentHex, string bgHex, string panelHex)
    {
        try
        {
            var res = Application.Current.Resources;
            var accent = ParseHex(accentHex, "#7C6CF0");
            var bg = ParseHex(bgHex, "#0A0E1A");
            var panel = ParseHex(panelHex, "#0E1424");
            var black = Color.FromRgb(0, 0, 0);
            // 🆕 Логика светлого и тёмного: раньше текст ВСЕГДА брался
            // почти-белым, сайдбар ВСЕГДА темнее фона — на светлом фоне
            // половина элементов «не переключалась» и слипалась. Теперь
            // яркость фона решает: светлый фон → тёмный текст, более
            // контрастные рамки (белые плитки иначе не видно), приглушённый
            // сайдбар едва темнее фона.
            bool light = IsLightBg(bg);
            // 🆕 0.14.0: Минимум — плоский режим: карточка без градиента,
            // блик-полоса выключена, точечная сетка скрыта. Так режим и
            // выглядит иначе (жёстче, «служебнее» тёплого Простого), и
            // дешевле рендерится — ради чего он и существует.
            var flat = AppSettings.EffectiveEffects() == AppSettings.EffectsMode.Minimum;
            var text = light
                ? Mix(Color.FromRgb(0x1B, 0x1E, 0x2B), accent, 0.10)
                : Mix(Color.FromRgb(0xE8, 0xEC, 0xFF), accent, 0.08);
            double bd = light ? 0.34 : 0.16;
            double bdh = light ? 0.55 : 0.34;
            var cardTop = Lighten(panel, 0.04);
            var cardBottom = Mix(panel, bg, light ? 0.45 : 0.55);

            (string CKey, string BKey, Color V)[] palette =
            {
                ("C_Accent", "B_Accent", accent),
                ("C_Bg", "B_Bg", bg),
                ("C_Panel", "B_Panel", panel),
                ("C_Soft", "B_Soft", Mix(panel, accent, light ? 0.20 : 0.16)),
                ("C_Tile", "B_Tile", light ? Mix(panel, black, 0.03) : Lighten(panel, 0.05)),
                ("C_TileH", "B_TileH", Mix(Lighten(panel, light ? 0.0 : 0.12), accent, light ? 0.22 : 0.15)),
                ("C_Border", "B_Border", Mix(panel, accent, bd)),
                ("C_BorderH", "B_BorderH", Mix(panel, accent, bdh)),
                ("C_Sidebar", "B_Sidebar", light ? Mix(bg, black, 0.06) : Mix(bg, black, 0.35)),
                ("C_Text", "B_Text", text),
                ("C_Dim", "B_Dim", Mix(text, panel, light ? 0.30 : 0.18)),
                ("C_Muted", "B_Muted", Mix(panel, text, light ? 0.62 : 0.44)),
            };
            foreach (var (ckey, bkey, value) in palette)
            {
                res[ckey] = value;
                if (res[bkey] is SolidColorBrush brush)
                {
                    try
                    {
                        if (brush.IsFrozen) throw new InvalidOperationException();
                        brush.Color = value;
                    }
                    catch { res[bkey] = new SolidColorBrush(value); }
                }
                else res[bkey] = new SolidColorBrush(value);
            }

            // Карточки: лёгкий вертикальный градиент — глубина вместо
            // плоскости; в Минимуме — сплошная плашка (режим «служебный»).
            if (flat) res["B_Card"] = new SolidColorBrush(cardTop);
            else if (res["B_Card"] is LinearGradientBrush card && card.GradientStops.Count >= 2)
            {
                try
                {
                    if (card.IsFrozen) throw new InvalidOperationException();
                    card.GradientStops[0].Color = cardTop;
                    card.GradientStops[1].Color = cardBottom;
                }
                catch { res["B_Card"] = MakeCardBrush(cardTop, cardBottom); }
            }
            else res["B_Card"] = MakeCardBrush(cardTop, cardBottom);

            // Блик-полоска над карточками: цвет акцента темы, гаснет вправо;
            // в Минимуме — прозрачная (декоративные полосы не показываем).
            if (flat) res["B_Bar"] = new SolidColorBrush(Color.FromArgb(0, accent.R, accent.G, accent.B));
            else if (res["B_Bar"] is LinearGradientBrush bar && bar.GradientStops.Count >= 2)
            {
                try
                {
                    if (bar.IsFrozen) throw new InvalidOperationException();
                    bar.GradientStops[0].Color = accent;
                    bar.GradientStops[1].Color = Color.FromArgb(0, accent.R, accent.G, accent.B);
                }
                catch { res["B_Bar"] = MakeBarBrush(accent); }
            }
            else res["B_Bar"] = MakeBarBrush(accent);

            // Пиксель-сетка фона: точки в тон теме (были хардкод-синие —
            // давали холодный оттенок на тёплых и зелёных темах).
            var dotBase = Mix(panel, accent, light ? 0.26 : 0.18);
            // В Минимуме сетка полностью прозрачна (тип кисти сохраняем —
            // код ниже продолжает находить DrawingBrush и перекрашивать).
            var dot = flat ? Color.FromArgb(0, dotBase.R, dotBase.G, dotBase.B) : dotBase;
            if (res["DotGrid"] is DrawingBrush grid && grid.Drawing is GeometryDrawing draw)
            {
                try
                {
                    if (grid.IsFrozen) throw new InvalidOperationException();
                    draw.Brush = new SolidColorBrush(dot);
                }
                catch { res["DotGrid"] = MakeDotGrid(dot); }
            }
            else res["DotGrid"] = MakeDotGrid(dot);

            try { Changed?.Invoke(); } catch { }
        }
        catch (Exception ex) { AppLog.Error("Тема: " + ex.Message); }
    }

    private static Color ParseHex(string hex, string fallback) =>
        (Color)ColorConverter.ConvertFromString(AppSettings.NormHex(hex, fallback));

    private static LinearGradientBrush MakeCardBrush(Color top, Color bottom)
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        b.GradientStops.Add(new GradientStop(top, 0));
        b.GradientStops.Add(new GradientStop(bottom, 1));
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush MakeBarBrush(Color accent)
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
        };
        b.GradientStops.Add(new GradientStop(accent, 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1));
        b.Freeze();
        return b;
    }

    // Пиксель-сетка фона: та же геометрия, что в App.xaml, — цвет точек под тему.
    private static DrawingBrush MakeDotGrid(Color dot)
    {
        // Viewbox строго по координатам тайла: без него (Relative по умолчанию)
        // геометрия растягивалась на весь тайл и сетка рендрилась сплошной
        // пластиковой плашкой — «сырой» фон Расширенного.
        var b = new DrawingBrush
        {
            TileMode = TileMode.Tile,
            Viewbox = new Rect(0, 0, 28, 28),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 28, 28),
            ViewportUnits = BrushMappingMode.Absolute,
            Drawing = new GeometryDrawing(
                new SolidColorBrush(dot),
                null,
                Geometry.Parse("M25,25 L28,25 L28,28 L25,28 Z")),
        };
        b.Freeze();
        return b;
    }

    public static Color CurrentAccent()
    {
        try
        {
            if (Application.Current.Resources["B_Accent"] is SolidColorBrush b)
                return b.Color;
        }
        catch { }
        return Color.FromRgb(0x7C, 0x6C, 0xF0);
    }

    // 🆕 Палитра метрик: колесо оттенков ОТ текущего акцента — бары Системы
    // и кольца героя переключаются темой целиком (фиолет даёт фиолет-розово-
    // синий, зелёный — зелёно-бирюзовый) вместо застывшего фиолетового
    // хардкода. "hot" — семантика (перегрев ≠ тема), всегда тёплая.
    public static (Color From, Color To) MetricPair(string key)
    {
        if (string.Equals(key, "hot", StringComparison.OrdinalIgnoreCase))
            return (Color.FromRgb(0xFD, 0xBA, 0x74), Color.FromRgb(0xF8, 0x71, 0x71));
        var a = CurrentAccent();
        if (string.Equals(key, "accent", StringComparison.OrdinalIgnoreCase))
            return (a, Lighten(a, 0.38));
        RgbToHsl(a, out double h, out double s, out _);
        s = Math.Clamp(Math.Max(s, 0.62), 0.62, 0.82);
        double shift = key.ToLowerInvariant() switch
        {
            "cpu" => 0,      // сам акцент
            "ram" => 38,     // сосед по колесу — тёплый сдвиг
            "disk" => -34,   // холодный сдвиг
            "net" => -68,    // глубокий холод
            "wifi" => 72,    // тёплый край
            "folder" => 104, // контрастный акцент
            _ => 0,
        };
        h = (h + shift + 360) % 360;
        return (HslToRgb(h, s, 0.66), HslToRgb(h, Math.Max(s - 0.10, 0.30), 0.80));
    }

    // 🆕 Цвет иконок карточек Действий: разброс по колесу от акцента —
    // восемь карточек живут в семье темы, а не в застывших фиолетовых hex.
    public static Color ActionAccent(int num)
    {
        var a = CurrentAccent();
        RgbToHsl(a, out double h, out double s, out _);
        s = Math.Clamp(Math.Max(s, 0.58), 0.58, 0.80);
        double[] shifts = { 8, -24, 34, -42, 52, -60, 72, -78 };
        double shift = shifts[((num - 1) % shifts.Length + shifts.Length) % shifts.Length];
        h = (h + shift + 360) % 360;
        // На светлом фоне glyph должен быть темнее — иначе светлая иконка
        // на белой плашке сольётся.
        double l = IsLightBg(BgColor()) ? 0.56 : 0.72;
        return HslToRgb(h, s, l);
    }

    // Осветление цвета «в белое» — окончания баров, светлые тинты.
    public static Color Tint(Color c, double t) => Lighten(c, t);

    // Фон темы из ресурса — для решений «светлое/тёмное» вне ApplyColors.
    public static Color BgColor()
    {
        try
        {
            if (Application.Current.Resources["C_Bg"] is Color c) return c;
        }
        catch { }
        return Color.FromRgb(0x0A, 0x0E, 0x1A);
    }

    // Светлый ли фон темы (яркость по WCAG) — от неё зависит цвет текста
    // и контраст рамок.
    public static bool IsLightBg(Color bg)
    {
        double y = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
        return y > 0.5;
    }

    // Поворот оттенка для RGB-цикла: от текущего акцента через +120°/+240°.
    public static Color HueShift(Color c, double degrees)
    {
        try
        {
            RgbToHsl(c, out double h, out double s, out double l);
            h = (h + degrees) % 360;
            if (h < 0) h += 360;
            return HslToRgb(h, s, l);
        }
        catch { return c; }
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static Color Lighten(Color c, double t) => Mix(c,
        Color.FromRgb(0xFF, 0xFF, 0xFF), t);

    private static void RgbToHsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        if (max == min) { h = 0; s = 0; return; }
        double d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) * 60;
        else if (max == g) h = ((b - r) / d + 2) * 60;
        else h = ((r - g) / d + 4) * 60;
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromRgb(
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }
}
