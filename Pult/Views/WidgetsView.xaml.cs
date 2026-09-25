using System;
using System.Windows;
using System.Windows.Controls;
using Pult.Services;

namespace Pult.Views;

// Вкладка «Виджеты»: управление виджетом рабочего стола.
// Само окно живёт в MainWindow — сюда только кнопки через колбэки.
public partial class WidgetsView : UserControl
{
    public Action<string> Report = _ => { };
    public Func<bool> IsShown = () => false;
    public Action Toggle = () => { };
    public Action ResetPosition = () => { };
    public Func<bool> GetTopmost = () => true;
    public Action<bool> SetTopmost = _ => { };

    public WidgetsView()
    {
        InitializeComponent();
        // 🆕 0.14.3: полоса карточки — тихий разделитель (у секции нет
        // состояния); в Минимуме — прозрачный B_Bar, контракт как в
        // SettingsView (fxswitch/settingsTall_min завязаны на B_Bar).
        try { PaintStrip(); } catch { }
        // 🆕 0.14.1: живая полоса-разделитель (как в Настройках) — хук в
        // ctor, чтобы headless-рендер стенда её тоже навешивал.
        try { SheenFx.AnimateBars(this); } catch { }
        Loaded += (_, _) => Reload();
    }

    // 🆕 0.14.3
    private void PaintStrip()
    {
        try
        {
            if (WStrip == null) return;
            if (AppSettings.EffectiveEffects() == AppSettings.EffectsMode.Minimum)
                WStrip.SetResourceReference(Border.BackgroundProperty, "B_Bar");
            else
                WStrip.SetResourceReference(Border.BackgroundProperty, "B_Border");
        }
        catch { }
    }

    public void Reload()
    {
        try
        {
            bool shown = false;
            try { shown = IsShown(); } catch { }
            WidgetState.Text = shown ? "На столе" : "Скрыт";
            ToggleBtn.Content = shown ? "Скрыть" : "Показать";
            TopmostBox.Checked -= Topmost_Changed;
            TopmostBox.Unchecked -= Topmost_Changed;
            try { TopmostBox.IsChecked = GetTopmost(); } catch { }
            TopmostBox.Checked += Topmost_Changed;
            TopmostBox.Unchecked += Topmost_Changed;
        }
        catch (Exception ex)
        {
            try { Report("Виджеты не прочитались: " + ex.Message); } catch { }
        }
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        try { Toggle(); } catch { }
        Reload();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        try { ResetPosition(); } catch { }
        Reload();
    }

    private void Topmost_Changed(object sender, RoutedEventArgs e)
    {
        try { SetTopmost(TopmostBox.IsChecked == true); } catch { }
    }
}
