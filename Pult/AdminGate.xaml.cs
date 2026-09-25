using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Pult;

// Гейт: без прав администратора дальше не пускаем.
// 🆕 0.14.5 — «доступ закрыть тоже прокачать»: вместо мигающей красной рамки
// (600мс, резало глаза) — спокойный пульс щита, человеческий заголовок и
// список «что без прав не работает». В Минимуме щит стоит статично.
public partial class AdminGate : Window
{
    public AdminGate()
    {
        InitializeComponent();
        // Безрамочное окно: масштаб подгоняем размером окна (контент и так
        // растянут LayoutTransform — обрезки нет). Пол масштаба 1.0: без него
        // окно у юзера (UiScale=0.8) сжимается до нечитаемых 416×336 —
        // баг-репорт «мелковато окно». Вниз не мельчаем, вверх зумим.
        double s = Math.Max(Services.Appearance.UiScale, 1.0);
        if (Math.Abs(s - 1.0) > 0.001)
        {
            Width = Math.Round(560 * s);
            Height = Math.Round(440 * s);
        }
        Services.Appearance.ApplyScale(this, 1.0);
        Loaded += (_, _) =>
        {
            Services.DialogFx.Enter(this);
            StartPulse();
        };
    }

    // Медленный пульс щита вместо мигания рамки: 3.4с цикл, мягкий синус.
    private void StartPulse()
    {
        try
        {
            if (Services.AppSettings.EffectiveEffects() ==
                Services.AppSettings.EffectsMode.Minimum) return;
            var a = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(1700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Shield.BeginAnimation(UIElement.OpacityProperty, a);
        }
        catch { }
    }

    public static bool IsAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private void Titlebar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? "", "")
            {
                Verb = "runas",
                UseShellExecute = true,
            };
            var p = Process.Start(psi);
            if (p == null) return; // не стартовал — остаёмся на экране
        }
        catch { return; } // отказ в UAC или ошибка — остаёмся, окно не закрываем
        try { DialogResult = true; }
        catch { try { Close(); } catch { } }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; }
        catch { try { Close(); } catch { } }
    }

    // Enter — перезапуск, Esc — выход. Если фокус на кнопке, Enter достаётся
    // ей самой (на «Выйти» это выход, не перезапуск).
    private void Key_Press(object sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Exit_Click(sender, e);
            }
            else if (e.Key == Key.Enter)
            {
                if (Keyboard.FocusedElement is Button) return;
                e.Handled = true;
                Restart_Click(sender, e);
            }
        }
        catch { }
    }
}
