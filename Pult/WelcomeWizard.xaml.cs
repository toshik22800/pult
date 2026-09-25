using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Pult.Services;

namespace Pult;

// Мастер первого запуска — три шага: интерфейс → эффекты → что дальше.
// Показываем, только если файла настроек ещё нет.
//
// 🆕 0.14.5 — «нажимаю Начать, ничего не происходит»:
//   1) Save() молча глотал ошибку: в свежих настройках WidgetLeft/WidgetTop
//      равны NaN, System.Text.Json бросает ArgumentException на записи —
//      файла не было, и мастер возвращался при каждом запуске. Теперь
//      после записи проверяем AppSettings.Exists() и говорим честно.
//   2) Вид переделан под язык 0.14.x: безрамочное окно, плитки с глифами,
//      акцент-круг, шаги с прогресс-чипами, живое превью эффектов.
public partial class WelcomeWizard : Window
{
    private AppSettings.InterfaceMode _iface = AppSettings.InterfaceMode.Simple;
    private bool _fxAuto = true;
    private int _step = 1;
    private bool _saved;
    private DoubleAnimation? _previewAnim;

    public WelcomeWizard()
    {
        InitializeComponent();
        // Безрамочное окно: масштаб подгоняем размером окна — контент и так
        // растянут LayoutTransform, обрезки нет. Пол масштаба 1.0: у юзера
        // UiScale=0.8, без пола окно первого входа ужимается и текст
        // нечитаем (баг-репорт «мелковато окно»). Вниз не мельчаем,
        // вверх зумим как обычно.
        double s = Math.Max(Appearance.UiScale, 1.0);
        if (Math.Abs(s - 1.0) > 0.001)
        {
            Width = Math.Round(660 * s);
            Height = Math.Round(640 * s);
        }
        Appearance.ApplyScale(this, 1.0);
        Loaded += (_, _) => DialogFx.Enter(this);
        try
        {
            AutoDesc.Text = "Определю по железу (сейчас: " + (AppSettings.DetectEffects() switch
            {
                AppSettings.EffectsMode.Minimum => "минимум)",
                AppSettings.EffectsMode.Maximum => "максимум)",
                _ => "обычные)",
            });
        }
        catch { }
        Paint();
        ShowStep(1, false);
    }

    private void Titlebar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    // Выбор плиток: акцент-рамка и заливка у выбранной, ●/○ как индикатор.
    private void Paint()
    {
        try
        {
            var accent = (Brush)FindResource("B_Accent");
            var muted = (Brush)FindResource("B_Muted");
            var soft = (Brush)FindResource("B_Soft");
            var tile = (Brush)FindResource("B_Tile");
            var border = (Brush)FindResource("B_Border");

            bool simple = _iface == AppSettings.InterfaceMode.Simple;
            TileSimple.BorderBrush = simple ? accent : border;
            TileSimple.Background = simple ? soft : tile;
            PillSimple.Text = simple ? "\u25CF" : "\u25CB";
            PillSimple.Foreground = simple ? accent : muted;
            TileAdv.BorderBrush = simple ? border : accent;
            TileAdv.Background = simple ? tile : soft;
            PillAdv.Text = simple ? "\u25CB" : "\u25CF";
            PillAdv.Foreground = simple ? muted : accent;

            TileAuto.BorderBrush = _fxAuto ? accent : border;
            TileAuto.Background = _fxAuto ? soft : tile;
            PillAuto.Text = _fxAuto ? "\u25CF" : "\u25CB";
            PillAuto.Foreground = _fxAuto ? accent : muted;
            TileMin.BorderBrush = _fxAuto ? border : accent;
            TileMin.Background = _fxAuto ? tile : soft;
            PillMin.Text = _fxAuto ? "\u25CB" : "\u25CF";
            PillMin.Foreground = _fxAuto ? muted : accent;

            UpdatePreview();
        }
        catch { }
    }

    // Живое превью шага 2: при включённых эффектах полоса дышит, при
    // Минимуме — стоит (та же логика, что у DialogFx: Minimum = статика).
    private void UpdatePreview()
    {
        try
        {
            bool minimum = !_fxAuto ||
                AppSettings.DetectEffects() == AppSettings.EffectsMode.Minimum;
            if (minimum)
            {
                StopPreview();
                PreviewText.Text = "Минимум: без анимаций, максимум скорости";
            }
            else
            {
                PreviewText.Text = "Авто: полосы дышат — эффекты включены";
                StartPreview();
            }
        }
        catch { }
    }

    private void StartPreview()
    {
        try
        {
            if (_previewAnim != null) return;
            var a = new DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(1100))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            _previewAnim = a;
            PreviewBar.BeginAnimation(UIElement.OpacityProperty, a);
        }
        catch { }
    }

    private void StopPreview()
    {
        try
        {
            PreviewBar.BeginAnimation(UIElement.OpacityProperty, null);
            PreviewBar.Opacity = 1.0;
        }
        catch { }
        _previewAnim = null;
    }

    private void ShowStep(int step, bool animate)
    {
        _step = step;
        Step1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            StepLabel.Text = $"ШАГ {step} ИЗ 3";
            var accent = (Brush)FindResource("B_Accent");
            var border = (Brush)FindResource("B_Border");
            Chip1.Background = accent;
            Chip2.Background = step >= 2 ? accent : border;
            Chip3.Background = step >= 3 ? accent : border;
            BackBtn.IsEnabled = step > 1;
            NextBtn.Content = step >= 3 ? "Открыть Пульт" : "Далее \u2192";
        }
        catch { }

        if (step == 2) UpdatePreview(); else StopPreview();
        if (animate && AllowAnim())
            SlideIn(step == 1 ? Step1 : step == 2 ? Step2 : Step3);
    }

    // В Минимуме — как DialogFx: без затей, сразу результат.
    private static bool AllowAnim()
    {
        try { return AppSettings.EffectiveEffects() != AppSettings.EffectsMode.Minimum; }
        catch { return true; }
    }

    // Смена шага: короткий сдвиг влево + фейд (только вперёд, без «туда-обратно»).
    private void SlideIn(FrameworkElement panel)
    {
        try
        {
            var tr = new TranslateTransform(14, 0);
            panel.RenderTransform = tr;
            var x = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            var o = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            tr.BeginAnimation(TranslateTransform.XProperty, x);
            panel.BeginAnimation(UIElement.OpacityProperty, o);
        }
        catch { }
    }

    private void Ui_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        _iface = tag == "ui1" ? AppSettings.InterfaceMode.Advanced : AppSettings.InterfaceMode.Simple;
        Paint();
    }

    private void Fx_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        _fxAuto = tag != "fxM";
        Paint();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 1) ShowStep(_step - 1, true);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < 3) { ShowStep(_step + 1, true); return; }
        Finish();
    }

    // ✕ и Esc: пропустить — но всё равно записать выбор, иначе мастер
    // вернётся при следующем запуске.
    private void Skip_Click(object sender, RoutedEventArgs e) => Finish();

    private void Finish()
    {
        if (!TrySave(interactive: true)) return;
        try { DialogResult = true; }
        catch { try { Close(); } catch { } }
    }

    // Запись + честная проверка: раньше Save() глотал ошибку сам, и юзер
    // видел «окно закрылось, а настроек нет».
    private bool TrySave(bool interactive)
    {
        try
        {
            var s = AppSettings.Load();
            s.Interface = _iface;
            s.Effects = _fxAuto ? AppSettings.EffectsMode.Auto : AppSettings.EffectsMode.Minimum;
            s.Save();
            AppLog.Info($"Мастер первого запуска: {_iface}, эффекты {(_fxAuto ? "авто" : "минимум")}.");
            if (AppSettings.Exists()) { _saved = true; return true; }
            AppLog.Error("Мастер: файл настроек не записался — Save() проглотил ошибку.");
        }
        catch (Exception ex) { AppLog.Error("Мастер: " + ex.Message); }

        if (interactive)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetEnvironmentVariable("APPDATA")
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pult");
                MessageBox.Show(
                    "Не удалось записать настройки: файл не появился, и при следующем запуске мастер покажется снова.\n\n" +
                    "Каталог: " + dir,
                    "Пульт — настройка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        }
        return false;
    }

    private void Key_Press(object sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Skip_Click(sender, e);
            }
            else if (e.Key == Key.Enter)
            {
                // Enter достаётся сфокусированной кнопке — не перехватываем.
                if (Keyboard.FocusedElement is Button) return;
                e.Handled = true;
                Next_Click(sender, e);
            }
        }
        catch { }
    }

    // Закрытие крестиком окна/Alt+F4 — запись всё равно: мастер не должен
    // возвращаться при каждом запуске из-за молчаливого пропуска.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try { if (!_saved) TrySave(interactive: false); } catch { }
        base.OnClosing(e);
    }
}
