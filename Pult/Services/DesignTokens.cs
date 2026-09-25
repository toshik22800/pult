namespace Pult.Services;

// Этап: зафиксированные визуальные параметры по уровням эффектов.
// Ничего не меняет само по себе: следующие этапы будут подменять
// магические числа в UI именно этими константами.
// Minimum = скорость, Normal = обычные, Maximum = максимум эффектов,
// Simple = Простой интерфейс (независимо от эффектов).
public static class DesignTokens
{
    // Радиусы углов карточек: 0 = острые (ретро), 10 = мягкие (Простой).
    // Единый ноль на всех уровнях эффектов сохраняет узнаваемый стиль.
    public const double CornerMinimum = 0;
    public const double CornerNormal = 0;
    public const double CornerMaximum = 0;
    public const double CornerSimple = 10;

    // Толщина рамки карточек: тонкая на слабом железе, обычная дальше.
    public const double BorderMinimum = 1;
    public const double BorderNormal = 2;
    public const double BorderMaximum = 2;

    // Длительность переходов между вкладками, мс.
    // 0 = мгновенно (слабые ПК не ждут анимацию).
    public const int FadeMinimum = 0;
    public const int FadeNormal = 140;
    public const int FadeMaximum = 220;

    // Шрифт заголовков: true = пиксельный (Press Start 2P), false = обычный.
    // Пиксель — фирменный ретро-вид; в Простом читаемость важнее стиля.
    public const bool PixelHeadersMinimum = true;
    public const bool PixelHeadersNormal = true;
    public const bool PixelHeadersMaximum = true;
    public const bool PixelHeadersSimple = false;
}
