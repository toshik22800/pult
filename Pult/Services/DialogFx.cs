using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Pult.Services;

// Появление окон: Минимум — сразу, Обычные — fade 140мс,
// Максимум — fade 220мс. Без свечений: текст в окнах не мылится.
public static class DialogFx
{
    public static void Enter(Window w)
    {
        try
        {
            var fx = AppSettings.EffectsMode.Normal;
            try { fx = AppSettings.EffectiveEffects(); } catch { }
            if (fx == AppSettings.EffectsMode.Minimum) return;
            bool ultra = fx == AppSettings.EffectsMode.Maximum;
            w.Opacity = 0;
            var anim = new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(ultra ? 220 : 140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            w.BeginAnimation(UIElement.OpacityProperty, anim);
        }
        catch { try { w.Opacity = 1; } catch { } }
    }
}
