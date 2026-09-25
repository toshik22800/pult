using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pult.Services;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace Pult;

public sealed record ActionItem(string Title, string Hint, int Num, string Color, string Code, string Kind);

public partial class MainWindow : Window
{
    private readonly List<ActionItem> _actions = new()
    {
        new("Разложить Загрузки", "Разложит файлы по категориям", 1, "#A594F9", "\uE8CB", "organize"),
        new("Найти дубликаты", "SHA-256 по всем файлам в Загрузках", 2, "#8F7FFA", "\uE8C8", "duplicates"),
        new("Место на дисках", "Свободное место по всем дискам", 3, "#7C6CF0", "\uE74E", "disks"),
        new("Очистить Temp", "Временные файлы старше 24 часов", 4, "#6B5B95", "\uE74D", "temp"),
        new("Мои игры", "Список установленных игр", 5, "#7C6CF0", "\uE7FC", "games"),
        new("Загрузка системы", "CPU, RAM, диски, топ процессов", 6, "#A594F9", "\uE7F8", "system"),
        new("Тяжёлые файлы", "Крупные файлы в указанной папке", 7, "#8F7FFA", "\uE8F4", "large"),
        new("Спросить ассистента", "Открыть чат с моделью", 8, "#8F7FFA", "\uE8F2", "chat"),
    };

    private bool _collapsed = true;
    private static DropShadowEffect? _heroGlow;
    private static DropShadowEffect? _ringGlow1;
    private static DropShadowEffect? _ringGlow2;
    private static DropShadowEffect? _ringGlow3;
    private static DropShadowEffect? _heroGlowStatic;
    // Ультра-визуал hero: движущееся пятно, спарклайны, прокрутка чисел.
    private bool _ultraFx;
    private readonly List<double> _cpuHist = new();
    private readonly List<double> _ramHist = new();
    private readonly List<double> _diskHist = new();
    private readonly List<double> _pulseHist = new();

    private static DropShadowEffect NewRingGlow() => new()
    {
        Color = ThemeService.CurrentAccent(),
        BlurRadius = 12, ShadowDepth = 0, Opacity = 0.6,
    };
    private static System.Windows.Media.Animation.Storyboard? _rgbStory;

    // Ультра: свечение героя переливается фиолет→циан→маджента по кругу.
    private static void StartRgbGlow()
    {
        try
        {
            StopRgbGlow();
            if (_heroGlow == null) return;
            // Цикл строится от текущего акцента темы: он сам, +120° и +240°.
            var accent = ThemeService.CurrentAccent();
            var anim = new System.Windows.Media.Animation.ColorAnimationUsingKeyFrames
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                Duration = TimeSpan.FromSeconds(8),
            };
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(
                accent, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(
                ThemeService.HueShift(accent, 120), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.6))));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(
                ThemeService.HueShift(accent, 240), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(5.2))));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(
                accent, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(8))));
            System.Windows.Media.Animation.Storyboard.SetTarget(anim, _heroGlow);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(anim,
                new PropertyPath(DropShadowEffect.ColorProperty));
            _rgbStory = new System.Windows.Media.Animation.Storyboard();
            _rgbStory.Children.Add(anim);
            _rgbStory.Begin();
        }
        catch { }
    }

    private static void StopRgbGlow()
    {
        try
        {
            _rgbStory?.Stop();
            _rgbStory = null;
            if (_heroGlow != null)
                _heroGlow.Color = ThemeService.CurrentAccent();
        }
        catch { }
    }
    private readonly SystemMonitor _mon = new();
    private readonly DispatcherTimer _metricsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _statusBase = "Готов";
    private readonly DispatcherTimer _autoOrgTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private DateTime _autoOrgLastRun = DateTime.Now;
    private bool _autoOrgBusy;
    private readonly Dictionary<string, DateTime> _autoOrgLast = new();

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch { /* Win10 1809+; иначе останется светлая шапка */ }
        try
        {
            // Иконка окна — белая молния Material Icons по прозрачному фону.
            var bolt = new TextBlock
            {
                Text = "\uE3E7",
                FontFamily = (WpfFontFamily)FindResource("F_Icon"),
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                Background = null,
                Width = 16, Height = 16,
                TextAlignment = TextAlignment.Center,
            };
            bolt.Measure(new System.Windows.Size(16, 16));
            bolt.Arrange(new System.Windows.Rect(0, 0, 16, 16));
            var bmp = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(bolt);
            Icon = bmp;
        }
        catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        // 🆕 0.14.0: шрифт интерфейса и масштаб UI — до первой отрисовки.
        try { Appearance.Init(AppSettings.Load()); } catch { }
        GreetingText.Text = DateTime.Now.Hour switch
        {
            < 5 => "Доброй ночи",
            < 12 => "Доброе утро",
            < 18 => "Добрый день",
            _ => "Добрый вечер",
        };
        DateText.Text = DateTime.Now.ToString("dddd, d MMMM", new System.Globalization.CultureInfo("ru-RU"));
        BuildCards();
        BuildHotkeys();
        try { RefreshNavActive(); } catch { }
        _metricsTimer.Tick += (_, _) => RefreshMetrics();
        _metricsTimer.Start();
        RefreshMetrics();
        if (AdminGate.IsAdmin()) Title = "Пульт [админ]";
        try
        {
            // Чип версии в статус-строке — из сборки, без ручного дубля.
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            VersionChip.Text = ver == null ? "v0.12.0"
                : $"v{ver.Major}.{ver.Minor}.{ver.Build}";
        }
        catch { }
        StateChanged += MainWindow_StateChanged;
        Loaded += (_, _) => { try { RefreshNavActive(); } catch { } };
        ApplyAppearance();
        try { ThemeService.Changed += OnThemeChanged; } catch { }
        _autoOrgTimer.Tick += (_, _) => AutoOrgTick();
        _autoOrgTimer.Start();
        try { Pult.Services.AppLog.Info("Старт: " + Title); } catch { }
        // Простой интерфейс стартует сразу с Главной.
        try { if (IsSimpleUI()) ShowSimpleHome(); } catch { }
        // Виджет: вернуть, если был открыт.
        try
        {
            if (AppSettings.Load().WidgetVisible)
                ToggleWidget();
        }
        catch { }
        Closed += (_, _) =>
        {
            try { ThemeService.Changed -= OnThemeChanged; } catch { }
            try { _widget?.Close(); } catch { }
        };
        // Пульс: посчитать в фоне, записать в историю, показать в hero.
        try
        {
            Task.Run(async () =>
            {
                try
                {
                    var snap = await Pult.Services.PulseService.ComputeAsync();
                    Pult.Services.PulseService.AppendToHistory(snap);
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            if (_ultraFx) RollRing(PulseRing, PulsePct, snap.Score, "");
                            else StopRing(PulseRing, PulsePct, snap.Score, "");
                        }
                        catch { }
                    });
                }
                catch { }
            });
        }
        catch { }
    }

    // Автопорядок: таймер был создан, но никогда не стартовал — чиним.
    // Разбирает папки из настроек в фоне, пишет итог в app.log.
    private void AutoOrgTick()
    {
        try
        {
            var s = AppSettings.Load();
            if (!s.AutoOrganizeEnabled || _autoOrgBusy) return;
            int mins = Math.Clamp(s.AutoOrganizeIntervalMin, 5, 24 * 60);
            if ((DateTime.Now - _autoOrgLastRun).TotalMinutes < mins) return;
            var dirs = s.AutoOrganizeDirs
                .Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
            if (dirs.Count == 0) return;
            _autoOrgBusy = true;
            Task.Run(() =>
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (string d in dirs)
                    {
                        try { sb.Append(FileService.OrganizeDirectory(d).Replace('\r', ' ').Replace('\n', ' ')).Append(" | "); }
                        catch (Exception ex) { sb.Append(d).Append(": ").Append(ex.Message).Append(" | "); }
                    }
                    Pult.Services.AppLog.Info("Автопорядок: " + sb.ToString().Trim());
                }
                catch (Exception ex)
                {
                    try { Pult.Services.AppLog.Error("Автопорядок: " + ex.Message); } catch { }
                }
                finally
                {
                    _autoOrgLastRun = DateTime.Now;
                    _autoOrgBusy = false;
                }
            });
        }
        catch { _autoOrgBusy = false; }
    }

    // 🆕 Сменилась тема — свечения героя и колец пересобираем под новый
    // акцент: старые экземпляры эффектов отпускаем, ApplyAppearance
    // создаст свежие. Смена идёт через кроссфейд — без щелчка.
    private void OnThemeChanged()
    {
        try
        {
            _heroGlow = null;
            _heroGlowStatic = null;
            _ringGlow1 = null;
            _ringGlow2 = null;
            _ringGlow3 = null;
            Crossfade(() =>
            {
                ApplyAppearance();
                // Карточки и иконки навигации читают кисти из ресурса при
                // создании, а при смене темы ресурсы заменяются (заморожены) —
                // пересобираем, иначе акцент остался бы старым.
                try { BuildCards(); } catch { }
                try { RefreshNavActive(); } catch { }
            });
        }
        catch { }
    }

    // 🆕 Плавная смена темы: кадр «старого» вида ложится поверх окна,
    // тема применяется мгновенно под ним, кадр гаснет за 420мс — глаза
    // видят живой перелив цветов, а не щелчок. Кисти в ресурсах
    // замораживаются WPF-ом (дамп стенда), анимировать их по месту нельзя —
    // поэтому кроссфейд на уровне окна. В Минимуме и до готовности окна —
    // честный мгновенный переключатель.
    private void Crossfade(Action apply)
    {
        try
        {
            if (!IsLoaded || ActualWidth < 4 || ActualHeight < 4
                || AppSettings.EffectiveEffects() == AppSettings.EffectsMode.Minimum)
            {
                apply();
                return;
            }
            var bmp = new RenderTargetBitmap(
                (int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(this);
            var ghost = new Image
            {
                Source = bmp,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
            };
            Grid.SetRowSpan(ghost, 2);
            Grid.SetColumnSpan(ghost, 2);
            Panel.SetZIndex(ghost, 9999);
            RootGrid.Children.Add(ghost);
            apply();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            fade.Completed += (_, _) => RootGrid.Children.Remove(ghost);
            ghost.BeginAnimation(Image.OpacityProperty, fade);
        }
        catch { try { apply(); } catch { } }
    }

    // Режимы вида: Полный — вся красота; Лёгкий — без зерна/анимаций, реже опрос;
    // Ультра — усиленное зерно, свечение героя, плавные переходы, опрос каждую секунду.
    public void ApplyAppearance()
    {
        try
        {
            var mode = AppSettings.EffectsMode.Normal;
            try { mode = AppSettings.EffectiveEffects(); } catch { }
            bool light = mode == AppSettings.EffectsMode.Minimum;
            bool ultra = mode == AppSettings.EffectsMode.Maximum;
            _ultraFx = ultra;
            try
            {
                // Ресурсная ссылка вместо кэша объекта: при смене темы ресурс
                // заменяется (кисти заморожены), локальный захват остался бы
                // старым и перекрыл бы DynamicResource.
                ActionsView.SetResourceReference(Control.BackgroundProperty,
                    light ? "B_Bg" : "DotGrid");
            }
            catch { }
            // Сканлайн-оверлей удалён: лежал поверх текста и мылил его.
            // Точечная сетка фона (под контентом) осталась.
            // 🆕 Максимум «все соки»: аура-фон — мягкое акцентное свечение
            // сверху под контентом, дышит. Обычные/Минимум — без неё.
            try
            {
                if (ultra)
                {
                    var ac = ThemeService.CurrentAccent();
                    // 🆕 Геометрия без обрезки: ядро на 14% высоты, радиус
                    // подобран так, что и бока (dx .5/.54 ≈ .93), и низ
                    // (reach .14+.48 = .62) достраивают градиент ровно до
                    // нулевой прозрачности ВНУТРИ границ. Раньше радиус
                    // вылезал за фиксированный Height=620 — отсюда жёсткая
                    // линия поперёк половины экрана.
                    var aura = new RadialGradientBrush
                    {
                        Center = new Point(0.5, 0.14),
                        GradientOrigin = new Point(0.5, 0.14),
                        RadiusX = 0.54, RadiusY = 0.48,
                    };
                    aura.GradientStops.Add(new GradientStop(
                        Color.FromArgb(108, ac.R, ac.G, ac.B), 0));
                    aura.GradientStops.Add(new GradientStop(
                        Color.FromArgb(64, ac.R, ac.G, ac.B), 0.40));
                    aura.GradientStops.Add(new GradientStop(
                        Color.FromArgb(22, ac.R, ac.G, ac.B), 0.72));
                    aura.GradientStops.Add(new GradientStop(
                        Color.FromArgb(0, ac.R, ac.G, ac.B), 1));
                    AuraGlow.Background = aura;
                    AuraGlow.Visibility = Visibility.Visible;
                    var breathe = new DoubleAnimation(0.5, 1.0, TimeSpan.FromSeconds(6))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    };
                    AuraGlow.BeginAnimation(OpacityProperty, breathe);
                    // 🆕 Живой свет: ядро медленно гуляет влево-вправо.
                    try
                    {
                        var drift = new PointAnimation
                        {
                            From = new Point(0.46, 0.14),
                            To = new Point(0.54, 0.14),
                            Duration = TimeSpan.FromSeconds(11),
                            AutoReverse = true,
                            RepeatBehavior = RepeatBehavior.Forever,
                            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                        };
                        aura.BeginAnimation(RadialGradientBrush.GradientOriginProperty, drift);
                    }
                    catch { }
                }
                else
                {
                    AuraGlow.BeginAnimation(OpacityProperty, null);
                    AuraGlow.Opacity = 1;
                    AuraGlow.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
            try
            {
                if (ultra && _heroGlow == null)
                {
                    // Не Freeze: в Ультре цвет крутится RGB-циклом.
                    _heroGlow = new DropShadowEffect
                    {
                        Color = ThemeService.CurrentAccent(),
                        BlurRadius = 26, ShadowDepth = 0, Opacity = 0.55,
                    };
                }
            }
            catch { }
            try { HeroGlowFrame.Effect = ultra ? _heroGlow : null; }
            catch { }
            // Обычные: мягкая статичная тень (глубина без движения).
            // Максимум — живое RGB-свечение выше; Минимум — ничего.
            try
            {
                if (!ultra && mode == AppSettings.EffectsMode.Normal)
                {
                    if (_heroGlowStatic == null)
                    {
                        var e = new DropShadowEffect
                        {
                            Color = ThemeService.CurrentAccent(),
                            BlurRadius = 18, ShadowDepth = 0, Opacity = 0.30,
                        };
                        e.Freeze();
                        _heroGlowStatic = e;
                    }
                    HeroGlowFrame.Effect = _heroGlowStatic;
                }
                else if (!ultra)
                    HeroGlowFrame.Effect = null;
            }
            catch { }
            // Спарклайны колец видны только в Максимуме.
            try
            {
                var sparkVis = ultra ? Visibility.Visible : Visibility.Collapsed;
                CpuSpark.Visibility = sparkVis;
                RamSpark.Visibility = sparkVis;
                DiskSpark.Visibility = sparkVis;
                PulseSpark.Visibility = sparkVis;
            }
            catch { }
            // 🆕 Кольца героя в палитре метрик — та же логика цвета, что бары
            // Системы: CPU/RAM/Диск разведены по колесу от акцента темы
            // вместо одинакового B_Accent. Пульс остаётся чистым акцентом.
            try
            {
                var cpu = new SolidColorBrush(ThemeService.MetricPair("cpu").From);
                var ram = new SolidColorBrush(ThemeService.MetricPair("ram").From);
                var disk = new SolidColorBrush(ThemeService.MetricPair("disk").From);
                cpu.Freeze(); ram.Freeze(); disk.Freeze();
                CpuRing.Fill = cpu;
                RamRing.Fill = ram;
                DiskRing.Fill = disk;
                CpuSpark.Stroke = cpu;
                RamSpark.Stroke = ram;
                DiskSpark.Stroke = disk;
            }
            catch { }
            // 🆕 0.14.0: блик героя больше НЕ ездит туда-обратно («виляние»
            // убрали по фидбеку). Идея та же, подача другая: в Максимуме —
            // статичная акцентная полоса с мягким дыханием по прозрачности,
            // движение в пространстве отсутствует вовсе.
            try
            {
                if (ultra)
                {
                    HeroSheen.Visibility = Visibility.Visible;
                    HeroSheen.RenderTransform = null;
                    // 🆕 0.14.1 (фидбек: «блики статичные»): дыхание быстрее и
                    // глубже (0.3→1.0 за 2.8с вместо 0.45→1.0 за 5с) плюс
                    // бегущий по полосе свет. Позиция самой полосы не меняется.
                    SheenFx.Breathe(HeroSheen, 0.3, 1.0, 2.8);
                    SheenFx.Sweep(HeroSheen, 260);
                }
                else
                {
                    HeroSheen.BeginAnimation(OpacityProperty, null);
                    HeroSheen.Opacity = 1;
                    HeroSheen.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
            // Кольца светятся в Ультре (текст поверх — отдельный элемент, не мылится).
            // Эффект нельзя шарить между элементами — по экземпляру на кольцо.
            try
            {
                if (ultra && _ringGlow1 == null)
                {
                    _ringGlow1 = NewRingGlow();
                    _ringGlow2 = NewRingGlow();
                    _ringGlow3 = NewRingGlow();
                }
                CpuRing.Effect = ultra ? _ringGlow1 : null;
                RamRing.Effect = ultra ? _ringGlow2 : null;
                DiskRing.Effect = ultra ? _ringGlow3 : null;
            }
            catch { }
            // Кольцо Пульса дышит только в Максимуме (остальным — статичная единица).
            try { EffectsHelper.PulseAnimate(PulseRing); } catch { }
            // Ховер-свечения убраны сознательно: Effect на элементе с текстом
            // переводит его в растр — отсюда было «мыло» при наведении.
            try
            {
                if (ultra) StartRgbGlow();
                else StopRgbGlow();
            }
            catch { }
            // Дыхание рамки: мягкая пульсация тени, только Максимум.
            // Цвет крутится RGB-циклом выше — тут только прозрачность.
            try
            {
                if (ultra && _heroGlow != null)
                {
                    var breathe = new DoubleAnimation(0.55, 0.28, TimeSpan.FromSeconds(4))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    };
                    _heroGlow.BeginAnimation(DropShadowEffect.OpacityProperty, breathe);
                }
            }
            catch { }
            try
            {
                if (ultra)
                {
                    var pulse = new System.Windows.Media.Animation.DoubleAnimation(
                        0.35, 1.0, TimeSpan.FromSeconds(1.2))
                    {
                        AutoReverse = true,
                        RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                    };
                    StatusDot.BeginAnimation(OpacityProperty, pulse);
                }
                else
                {
                    StatusDot.BeginAnimation(OpacityProperty, null);
                    StatusDot.Opacity = 1;
                }
            }
            catch { }
            try
            {
                _metricsTimer.Interval = TimeSpan.FromSeconds(light ? 5 : ultra ? 1 : 2);
                BottomStatus.Text = _statusBase;
            }
            catch { }
            try
            {
                foreach (var v in _views.Values)
                    if (v is Views.SystemView yv)
                    {
                        yv.ApplySpeed();
                        yv.ApplySimpleMode();
                        // 🆕 Смена темы/режима — Система перекрашивается из
                        // колеса акцента мгновенно (ApplyPalette агент-редизайна).
                        try { yv.ApplyPalette(); } catch { }
                    }
            }
            catch { }
            try { RefreshNav(); } catch { }
            // Пересборка карточек: блик и ховер-подъём зависят от режима
            // эффектов — без этого смена режима не применялась бы до перезапуска.
            try { BuildCards(); } catch { }
            try
            {
                // Кольцо Пульса — только в Расширенном.
                PulseRingBox.Visibility = IsSimpleUI() ? Visibility.Collapsed : Visibility.Visible;
            }
            catch { }
            try
            {
                // В Простом «Системы» нет: если были там — на Главную.
                // Без флага из чата тоже уходим: он экспериментальный.
                if (IsSimpleUI() && _currentTag == "system") ShowView("actions");
                else if (!ExpOn() && _currentTag == "chat") ShowView("actions");
            }
            catch { }
        }
        catch { }
    }

    // Простой интерфейс: прячем «Систему», «История» зовётся «Что я сделал».
    // Чат без флага «Экспериментальное» прячем целиком: кнопку, плитку и настройки.
    private void RefreshNav()
    {
        try
        {
            bool simple = IsSimpleUI();
            NavBtnSystem.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
            // 🆕 0.14.3: в Простом этот пункт открывает Дом — называем его
            // по спеке (PROJECT: «дом вместо Действий»), иначе простой
            // режим читается как обычный (фидбек: «разницы почти нет»).
            NavLblActions.Text = simple ? "Главная" : "Действия";
            NavBtnActions.ToolTip = simple ? "Главная" : "Действия";
            NavLblHistory.Text = simple ? "Что я сделал" : "История";
            NavBtnHistory.ToolTip = simple ? "Что я сделал" : "История";
            bool exp = ExpOn();
            NavBtnChat.Visibility = exp ? Visibility.Visible : Visibility.Collapsed;
            NavBtnChat.ToolTip = exp
                ? "Ассистент"
                : "Ассистент (нужен флаг «Экспериментальное» в Настройках)";
            // Подсказка про хоткеи честная: скрытого чата в списке нет.
            try { ActionsHint.Text = $"Эти действия работают мгновенно и без интернета. Горячие клавиши: Ctrl+Alt+1…{(exp ? 8 : 7)}."; }
            catch { }
            try
            {
                foreach (var ch in CardsGrid.Children)
                    if (ch is Border b && b.Tag is ActionItem a && a.Kind == "chat")
                        b.Visibility = exp ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }
        catch { }
    }

    // Активный пункт навигации подсвечен акцентом, остальные приглушены.
    private void RefreshNavActive()
    {
        try
        {
            var accent = (Brush)FindResource("B_Accent");
            var muted = (Brush)FindResource("B_Muted");
            foreach (var ch in NavPanel.Children)
            {
                if (ch is not Button b) continue;
                bool active = (b.Tag as string ?? "") == _currentTag;
                try
                {
                    // Активный — подложка ресурсом (следует за темой);
                    // неактивный — сброс к стилю, чтобы работал hover-триггер.
                    if (active) b.SetResourceReference(Control.BackgroundProperty, "B_Soft");
                    else b.ClearValue(Control.BackgroundProperty);
                }
                catch { }
                try
                {
                    // Левый рельс NavInd из шаблона: виден только у активного.
                    // 🆕 Появление рельса — мягкий fade (в Минимуме мгновенно).
                    b.ApplyTemplate();
                    if (b.Template.FindName("NavInd", b) is FrameworkElement rail)
                    {
                        double cur = rail.Opacity;
                        rail.BeginAnimation(UIElement.OpacityProperty, null);
                        rail.Opacity = active ? 1 : 0;
                        bool flat = AppSettings.EffectiveEffects() == AppSettings.EffectsMode.Minimum;
                        if (!flat && Math.Abs(cur - rail.Opacity) > 0.01)
                        {
                            var fade = new DoubleAnimation(cur, rail.Opacity,
                                TimeSpan.FromMilliseconds(active ? 220 : 140))
                            {
                                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                            };
                            rail.BeginAnimation(UIElement.OpacityProperty, fade);
                        }
                    }
                }
                catch { }
                try
                {
                    foreach (var icon in FindIcons(b))
                    {
                        try
                        {
                            if (icon.FontFamily != null
                                && icon.FontFamily.Source.Contains("MDL2"))
                                icon.Foreground = active ? accent : muted;
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static List<TextBlock> FindIcons(DependencyObject d)
    {
        var res = new List<TextBlock>();
        try
        {
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is TextBlock tb) res.Add(tb);
                res.AddRange(FindIcons(c));
            }
        }
        catch { }
        return res;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // При сворачивании останавливаем фоновые замеры — иначе очередь
        // Dispatcher.Invoke копится и окно виснет/падает при разворачивании.
        try
        {
            bool minimized = WindowState == WindowState.Minimized;
            if (minimized)
            {
                _metricsTimer.Stop();
            }
            else
            {
                if (!_metricsTimer.IsEnabled) { RefreshMetrics(); _metricsTimer.Start(); }
            }
        }
        catch { }
    }

    private void SetStatus(string text)
    {
        _statusBase = text;
        BottomStatus.Text = text;
    }

    // Плавная прокрутка числа: анимируем Value кольца, текст догоняет каждый кадр.
    private static void RollRing(Ring needle, TextBlock label, double target, string suffix)
    {
        try
        {
            double from = needle.Value;
            if (Math.Abs(from - target) < 0.5)
            {
                try { needle.BeginAnimation(Ring.ValueProperty, null); } catch { }
                needle.Value = target;
                label.Text = $"{target:F0}{suffix}";
                return;
            }
            var anim = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(400));
            anim.CurrentTimeInvalidated += (_, _) =>
            {
                try { label.Text = $"{needle.Value:F0}{suffix}"; } catch { }
            };
            needle.BeginAnimation(Ring.ValueProperty, anim);
        }
        catch
        {
            try { needle.BeginAnimation(Ring.ValueProperty, null); } catch { }
            try { needle.Value = target; label.Text = $"{target:F0}{suffix}"; } catch { }
        }
    }

    private static void StopRing(Ring needle, TextBlock label, double target, string suffix)
    {
        try
        {
            try { needle.BeginAnimation(Ring.ValueProperty, null); } catch { }
            needle.Value = target;
            label.Text = $"{target:F0}{suffix}";
        }
        catch { }
    }

    // История кольца: последние 20 значений той же шкалы 0..100.
    private static void PushSpark(Polyline line, List<double> hist, double v)
    {
        try
        {
            hist.Add(v);
            while (hist.Count > 20) hist.RemoveAt(0);
            line.Points.Clear();
            if (hist.Count < 2) return;
            const double w = 76, h = 30;
            for (int i = 0; i < hist.Count; i++)
            {
                double x = i * (w / 19);
                if (hist.Count < 20) x = w - (hist.Count - 1 - i) * (w / 19);
                double y = h - Math.Clamp(hist[i], 0, 100) / 100.0 * h;
                line.Points.Add(new Point(x, y));
            }
        }
        catch { }
    }

    private void RefreshMetrics()
    {
        try
        {
            double cpu = _mon.CpuPercent();
            var (memPct, memUsed, memTotal) = _mon.Memory();
            var disks = SystemMonitor.Disks();
            double maxDisk = 0;
            foreach (var d in disks) maxDisk = Math.Max(maxDisk, d.UsedPct);

            if (_ultraFx)
            {
                RollRing(CpuRing, CpuPct, cpu, "%");
                RollRing(RamRing, RamPct, memPct, "%");
                RollRing(DiskRing, DiskPct, maxDisk, "%");
                PushSpark(CpuSpark, _cpuHist, cpu);
                PushSpark(RamSpark, _ramHist, memPct);
                PushSpark(DiskSpark, _diskHist, maxDisk);
                PushSpark(PulseSpark, _pulseHist, PulseRing.Value);
            }
            else
            {
                StopRing(CpuRing, CpuPct, cpu, "%");
                StopRing(RamRing, RamPct, memPct, "%");
                StopRing(DiskRing, DiskPct, maxDisk, "%");
            }
            RamPct.ToolTip = memTotal > 0
                ? $"{DiskInfo.Gb(memUsed)} из {DiskInfo.Gb(memTotal)}" : null;
            try { DateText.Text = DateTime.Now.ToString("dddd, d MMMM • HH:mm", new System.Globalization.CultureInfo("ru-RU")); }
            catch { }
            // Живые метрики в статус-строке: подпись обновляется вместе с кольцами.
            try { StatusStats.Text = $"CPU {cpu:F0}%   RAM {memPct:F0}%"; }
            catch { }
        }
        catch { /* замер не удался — оставили старые значения */ }
    }

    private void BuildCards()
    {
        // Пересборка по смене темы: кисти визуалы кэшируют при создании.
        CardsGrid.Children.Clear();
        var tile = (Brush)FindResource("B_Tile");
        var tileH = (Brush)FindResource("B_TileH");
        var border = (Brush)FindResource("B_Border");
        var soft = (Brush)FindResource("B_Soft");
        var borderH = (Brush)FindResource("B_BorderH");
        var text = (Brush)FindResource("B_Text");
        var muted = (Brush)FindResource("B_Muted");

        // 🆕 0.14.1: стаггер бликов карточек — свет бежит «волной» слева
        // направо, а не всеми восемь сразу.
        int sheenDelay = 0;
        foreach (var a in _actions)
        {
            int sheenAt = sheenDelay;
            sheenDelay += 140;
            // 🆕 Цвет иконки — из колеса акцента темы (раньше застывшие
            // фиолетовые hex не переключались при смене темы). Кисть дальше
            // только читается — замораживаем.
            var color = new SolidColorBrush(ThemeService.ActionAccent(a.Num));
            color.Freeze();
            var card = new Border
            {
                Style = (Style)FindResource("ActionCard"),
                Height = 152,
                Margin = new Thickness(8),
                Cursor = Cursors.Hand,
                Tag = a,
                ToolTip = $"{a.Title}\n{a.Hint}\nГорячая клавиша: Ctrl+Alt+{a.Num}.",
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Блик сверху
            var sheen = new Border
            {
                Background = borderH, Height = 2, CornerRadius = new CornerRadius(0),
                HorizontalAlignment = HorizontalAlignment.Center, Width = 200,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetRowSpan(sheen, 4);
            grid.Children.Add(sheen);
            // 🆕 0.14.0: блик карточки больше НЕ сканирует её туда-обратно
            // (фидбек: «палочки виляют»). Минимум — без блика; Максимум —
            // статичный акцентный градиент сверху с дыханием прозрачности
            // (позиция и ширина не меняются — только яркость).
            try
            {
                var fx = AppSettings.EffectiveEffects();
                if (fx == AppSettings.EffectsMode.Minimum)
                    sheen.Visibility = Visibility.Collapsed;
                else if (fx == AppSettings.EffectsMode.Maximum)
                {
                    // В Максимуме блик — акцентный градиент, а не серая полоса.
                    try { sheen.Background = (Brush)FindResource("B_Bar"); } catch { }
                    // 🆕 0.14.1: дыхание быстрее и глубже (0.35→1.0 за 2.6с
                    // вместо 0.55→1.0 за 4.6с) + бегущий свет со стаггером
                    // по карточкам. Сама полоса не двигается.
                    SheenFx.Breathe(sheen, 0.35, 1.0, 2.6, sheenAt);
                    SheenFx.Sweep(sheen, 200, sheenAt + 300);
                }
            }
            catch { }

            // 🆕 Плитка иконки — диагональный тинт цвета самой карточки:
            // восемь карточек светятся своими оттенками темы (раньше — общая
            // плоская плашка поверх застывших фиолетовых глифов).
            var iconTint = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
            };
            iconTint.GradientStops.Add(new GradientStop(
                Color.FromArgb(74, color.Color.R, color.Color.G, color.Color.B), 0));
            iconTint.GradientStops.Add(new GradientStop(
                Color.FromArgb(14, color.Color.R, color.Color.G, color.Color.B), 1));
            iconTint.Freeze();
            var iconBox = new Border
            {
                Background = iconTint, Width = 44, Height = 44, CornerRadius = new CornerRadius(0),
                Margin = new Thickness(20, 18, 0, 10), HorizontalAlignment = HorizontalAlignment.Left,
            };
            var icon = new TextBlock
            {
                Text = a.Code,
                FontFamily = new WpfFontFamily("Segoe MDL2 Assets"),
                FontSize = 24,
                Foreground = color,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            iconBox.Child = icon;
            grid.Children.Add(iconBox);

            var badge = new Border
            {
                Background = soft, Width = 24, Height = 24, CornerRadius = new CornerRadius(0),
                BorderBrush = borderH, BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 14, 16, 0),
            };
            var badgeNum = new TextBlock
            {
                Text = a.Num.ToString(),
                FontSize = 13, Foreground = muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            badgeNum.SetResourceReference(TextBlock.FontFamilyProperty, "F_Ui");
            badge.Child = badgeNum;
            Grid.SetRowSpan(badge, 2);
            grid.Children.Add(badge);

            var title = new TextBlock
            {
                Text = a.Title,
                FontSize = 11, Foreground = text, Margin = new Thickness(20, 0, 20, 0),
            };
            title.SetResourceReference(TextBlock.FontFamilyProperty, "F_Ui");
            Grid.SetRow(title, 1);
            grid.Children.Add(title);

            var hint = new TextBlock
            {
                Text = a.Hint, FontSize = 12, Foreground = muted,
                Margin = new Thickness(20, 2, 20, 0), TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(hint, 2);
            grid.Children.Add(hint);

            var hot = new TextBlock
            {
                Text = $"Ctrl+Alt+{a.Num}", FontSize = 10, Foreground = muted,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 20, 10),
            };
            Grid.SetRow(hot, 3);
            grid.Children.Add(hot);

            card.Child = grid;
            card.MouseLeftButtonUp += (_, _) => RunAction(a);
            // 🆕 Максимум: карточка приподнимается при наведении —
            // только трансформ, без Effect (текст не мылится).
            try
            {
                if (AppSettings.EffectiveEffects() == AppSettings.EffectsMode.Maximum)
                {
                    var lift = new TranslateTransform();
                    card.RenderTransform = lift;
                    card.MouseEnter += (_, _) => lift.BeginAnimation(
                        TranslateTransform.YProperty,
                        new DoubleAnimation(-3, TimeSpan.FromMilliseconds(140)));
                    card.MouseLeave += (_, _) => lift.BeginAnimation(
                        TranslateTransform.YProperty,
                        new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
                }
            }
            catch { }
            CardsGrid.Children.Add(card);
        }
    }

    private void BuildHotkeys()
    {
        for (int i = 0; i < _actions.Count; i++)
        {
            var a = _actions[i];
            var gesture = new KeyGesture(Key.D1 + i, ModifierKeys.Control | ModifierKeys.Alt);
            var binding = new KeyBinding(new RelayCommand(() => RunAction(a)), gesture);
            InputBindings.Add(binding);
        }
    }

    private void RunAction(ActionItem a)
    {
        if (a.Kind == "disks")
        {
            SetStatus("Считаю диски и корзину…");
            Task.Run(() =>
            {
                string rep = SystemMonitor.DiskReport();
                var (rc, rb) = DiskCleanupService.RecycleBinInfo();
                return (rep, rc, rb);
            }).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        string rep = t.IsFaulted ? "Ошибка: не вышло посчитать диски." : t.Result.rep;
                        int rc = t.IsFaulted ? 0 : t.Result.rc;
                        long rb = t.IsFaulted ? 0 : t.Result.rb;
                        string text = rep + "\n" + (rc <= 0 || rb <= 0
                            ? "• Корзина пуста."
                            : $"• Корзина: ~{DiskCleanupService.FormatSize(rb)} ({rc} {DiskCleanupService.Plural(rc, "файл", "файла", "файлов")}) — вынесу.");
                        ResultDialog.ShowResult(this, "\uE74E", "Место на дисках", text,
                            "Освободить", () =>
                            {
                                string c = DiskCleanupService.CleanTemp(24);
                                string r = (rc > 0 && rb > 0)
                                    ? DiskCleanupService.EmptyRecycleBin()
                                    : "Корзина: трогать нечего.";
                                string res = c + "\n" + r;
                                try { ActionJournal.AddInfo("Освобождение места", res.Split('\n')[0].Trim()); }
                                catch { }
                                return res;
                            });
                        SetStatus("Готов");
                    }
                    catch { SetStatus("Готов"); }
                });
            });
            return;
        }
        if (a.Kind == "organize")
        {
            string dir = FileService.Downloads();
            var owner = Window.GetWindow(this);
            if (!ConfirmDialog.Ask(owner, "Разложить Загрузки?",
                $"Файлы из {dir} разъедутся по папкам-категориям.")) return;
            SetStatus("Раскладываю Загрузки…");
            Task.Run(() => FileService.OrganizeDirectory(dir)).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        string res = t.IsFaulted ? $"Ошибка: {t.Exception?.GetBaseException().Message}" : t.Result;
                        ResultDialog.ShowResult(this, "\uE8CB", "Порядок в Загрузках", res);
                        if (!t.IsFaulted && !res.StartsWith("Ошибка"))
                            ActionJournal.AddInfo("Порядок в Загрузках", res.Split('\n')[0].Trim());
                        SetStatus("Готов");
                    }
                    catch { SetStatus("Готов"); }
                });
            });
            return;
        }
        if (a.Kind == "duplicates")
        {
            SetStatus("Ищу дубликаты (SHA-256)…");
            string dir = FileService.Downloads();
            bool exp = ExpOn();
            Task.Run(() => DiskCleanupService.FindDuplicates(dir)).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    string res = t.Result;
                    if (exp)
                        ResultDialog.ShowResult(this, "\uE8C8", "Дубликаты",
                            res, "Удалить дубликаты",
                            () => DiskCleanupService.DeleteDuplicates(dir));
                    else
                        ResultDialog.ShowResult(this, "\uE8C8", "Дубликаты",
                            res + "\n\nУдаление выключено: Настройки → Экспериментальное.");
                    SetStatus("Готов");
                });
            });
            return;
        }
        if (a.Kind == "temp")
        {
            SetStatus("Сканирую Temp…");
            bool exp = ExpOn();
            Task.Run(() => DiskCleanupService.PreviewTemp(24)).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (exp)
                        ResultDialog.ShowResult(this, "\uE74D", "Временные файлы",
                            t.Result, "Очистить всё",
                            () => DiskCleanupService.CleanTemp(24));
                    else
                        ResultDialog.ShowResult(this, "\uE74D", "Временные файлы",
                            t.Result + "\n\nУдаление выключено: Настройки → Экспериментальное.");
                    SetStatus("Готов");
                });
            });
            return;
        }
        if (a.Kind == "large")
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Где искать тяжёлые файлы?",
            };
            if (dlg.ShowDialog() != true) return;
            string dir = dlg.FolderName;
            SetStatus("Ищу тяжёлые файлы…");
            Task.Run(() => DiskCleanupService.LargeFiles(dir)).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    ResultDialog.ShowResult(this, "\uE8F4", "Тяжёлые файлы", t.Result);
                    SetStatus("Готов");
                });
            });
            return;
        }
        if (a.Kind == "games")
        {
            ShowView("games");
            return;
        }
        if (a.Kind == "system")
        {
            // В Простом пункт меню скрыт — хоткей тоже ведёт на Главную.
            if (IsSimpleUI()) { ShowView("actions"); return; }
            ShowView("system");
            return;
        }
        if (a.Kind == "chat")
        {
            if (!ExpOn()) { ShowChatHint(); return; }
            ShowView("chat");
            return;
        }
        SetStatus($"Выполняю: {a.Title}… (мок — логика следующим шагом)");
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        ShowView(((Button)sender).Tag as string ?? "actions");
    }

    private WidgetWindow? _widget;

    private void ToggleWidget()
    {
        try
        {
            if (_widget != null)
            {
                try { _widget.Close(); } catch { }
                _widget = null;
                return;
            }
            _widget = new WidgetWindow();
            _widget.Closed += (_, _) => { _widget = null; };
            _widget.Show();
            try
            {
                var s = AppSettings.Load();
                s.WidgetVisible = true;
                s.Save();
            }
            catch { }
        }
        catch (Exception ex)
        {
            try { Pult.Services.AppLog.Error("Виджет: " + ex.Message); } catch { }
        }
    }

    private void ResetWidgetPosition()
    {
        try
        {
            var s = AppSettings.Load();
            s.WidgetLeft = double.NaN;
            s.WidgetTop = double.NaN;
            s.Save();
            if (_widget != null)
            {
                try
                {
                    var wa = SystemParameters.WorkArea;
                    _widget.Left = wa.Right - _widget.Width - 24;
                    _widget.Top = wa.Bottom - _widget.ActualHeight - 24;
                }
                catch { }
            }
        }
        catch { }
    }

    public void GoTo(string tag) => ShowView(tag);

    private void ShowChatHint()
    {
        try
        {
            bool go = ConfirmDialog.Ask(this,
                "Ассистент — экспериментальная функция.",
                "Чат с ИИ включается флагом «Экспериментальное»: Настройки → Вид → Интерфейс «Расширенный» → галочка. Открыть Настройки?");
            if (go) ShowView("settings");
        }
        catch { }
    }

    private readonly Dictionary<string, UIElement> _views = new();
    private string _currentTag = "actions";

    private static bool IsSimpleUI()
    {
        try { return AppSettings.Load().Interface == AppSettings.InterfaceMode.Simple; }
        catch { return true; }
    }

    private void ShowSimpleHome()
    {
        if (!_views.TryGetValue("simplehome", out var view))
        {
            var created = new Views.SimpleHomeView
            {
                Report = SetStatus,
                GoChat = () => ShowView("chat"),
                GoHistory = () => ShowView("history"),
                GoAppearance = () =>
                {
                    ShowView("settings");
                    try
                    {
                        if (_views.TryGetValue("settings", out var sv)
                            && sv is Views.SettingsView st) st.ScrollToAppearance();
                    }
                    catch { }
                },
            };
            created.Visibility = Visibility.Collapsed;
            ViewHost.Children.Add(created);
            _views["simplehome"] = created;
            view = created;
        }
        ActionsView.Visibility = Visibility.Collapsed;
        foreach (var v in _views.Values) v.Visibility = Visibility.Collapsed;
        view.Visibility = Visibility.Visible;
        EffectsHelper.FadeIn(view);
        if (view is Views.SimpleHomeView sh) sh.Reload();
        try { RefreshNavActive(); } catch { }
        SetStatus("Готов");
    }

    private void ShowView(string tag)
    {
        // Чат — экспериментальный: без флага только намёк, куда идти.
        if (tag == "chat" && !ExpOn())
        {
            ShowChatHint();
            return;
        }
        _currentTag = tag;
        // Простой интерфейс: вместо Действий — домашний экран.
        if (tag == "actions" && IsSimpleUI())
        {
            ShowSimpleHome();
            return;
        }
        if (!_views.TryGetValue(tag, out var view))
        {
            UIElement? created = tag switch
            {
                "settings" => new Views.SettingsView(),
                "history" => new Views.HistoryView { Report = SetStatus },
                "widgets" => new Views.WidgetsView
                {
                    Report = SetStatus,
                    IsShown = () => _widget != null,
                    Toggle = ToggleWidget,
                    ResetPosition = ResetWidgetPosition,
                    GetTopmost = () =>
                    {
                        try
                        {
                            if (_widget != null) return _widget.Topmost;
                            return AppSettings.Load().WidgetTopmost;
                        }
                        catch { return true; }
                    },
                    SetTopmost = top =>
                    {
                        try
                        {
                            if (_widget != null) _widget.Topmost = top;
                            var s = AppSettings.Load();
                            s.WidgetTopmost = top;
                            s.Save();
                        }
                        catch { }
                    },
                },
                "system" => new Views.SystemView { Report = SetStatus },
                "games" => new Views.GamesView { Report = SetStatus },
                "chat" => new Views.ChatView { Report = SetStatus },
                _ => null,
            };
            if (created == null)
            {
                string label = tag switch
                {
                    "actions" => "Действия", "chat" => "Ассистент", "games" => "Игры",
                    "system" => "Система", "settings" => "Настройки",
                    "history" => "История", _ => tag,
                };
                ActionsView.Visibility = tag == "actions" ? Visibility.Visible : Visibility.Collapsed;
                foreach (var v in _views.Values) v.Visibility = Visibility.Collapsed;
                // 🆕 Раньше здесь был голый return: RefreshNavActive не успевал,
                // «Действия» не подсвечивались, а прошлая вкладка оставалась
                // активной — казалось, что ты «в другом разделе». Подсветка —
                // до выхода из ветки.
                try { RefreshNavActive(); } catch { }
                SetStatus(tag == "actions" ? "Готов" : $"Вкладка «{label}» — следующим шагом");
                return;
            }
            created.Visibility = Visibility.Collapsed;
            ViewHost.Children.Add(created);
            _views[tag] = created;
            view = created;
        }
        ActionsView.Visibility = Visibility.Collapsed;
        foreach (var v in _views.Values) v.Visibility = Visibility.Collapsed;
        view.Visibility = Visibility.Visible;
        EffectsHelper.FadeIn(view);
        if (view is Views.SettingsView sv) sv.Reload();
        if (view is Views.HistoryView hv) hv.Reload();
        if (view is Views.WidgetsView wv) wv.Reload();
        if (view is Views.SystemView yv) yv.Refresh();
        if (view is Views.GamesView gv) gv.Refresh(false);
        try { RefreshNavActive(); } catch { }
        SetStatus("Готов");
    }

    private static bool ExpOn() => AppSettings.ExpOn();

    private void ContentPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // На широком окне — 4 колонки, иначе 3. Пустоты на весь экран нет.
        CardsGrid.Columns = e.NewSize.Width > 1400 ? 4 : 3;
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        SidebarColumn.Width = new GridLength(_collapsed ? 84 : 240);
        var vis = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        BrandText.Visibility = vis;
        NavLblActions.Visibility = vis;
        NavLblChat.Visibility = vis;
        NavLblGames.Visibility = vis;
        NavLblSystem.Visibility = vis;
        NavLblSettings.Visibility = vis;
        NavLblHistory.Visibility = vis;
        NavLblWidgets.Visibility = vis;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _run;
    public RelayCommand(Action run) => _run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _run();
}
