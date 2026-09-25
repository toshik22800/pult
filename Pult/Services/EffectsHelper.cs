using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pult.Services;

// Появление контента: Минимум — сразу, Обычные — fade 140мс,
// Максимум — fade 220мс + плавный подъём снизу.
// Та же логика, что была приватной в MainWindow: уровень берём
// через AppSettings.EffectiveEffects() внутри метода.
public static class EffectsHelper
{
    public static void FadeIn(UIElement el)
    {
        try
        {
            if (el == null) return;
            var mode = AppSettings.EffectsMode.Normal;
            try { mode = AppSettings.EffectiveEffects(); } catch { }
            if (mode == AppSettings.EffectsMode.Minimum) { el.Opacity = 1; return; }
            bool ultra = mode == AppSettings.EffectsMode.Maximum;
            el.Opacity = 0;
            if (ultra)
            {
                el.RenderTransform = new TranslateTransform(0, 14);
                var sb = new System.Windows.Media.Animation.Storyboard();
                var fade = new System.Windows.Media.Animation.DoubleAnimation(
                    0, 1, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
                };
                System.Windows.Media.Animation.Storyboard.SetTarget(fade, el);
                System.Windows.Media.Animation.Storyboard.SetTargetProperty(fade,
                    new PropertyPath(UIElement.OpacityProperty));
                var slide = new System.Windows.Media.Animation.DoubleAnimation(
                    14, 0, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
                };
                System.Windows.Media.Animation.Storyboard.SetTarget(slide, el);
                System.Windows.Media.Animation.Storyboard.SetTargetProperty(slide,
                    new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
                sb.Children.Add(fade);
                sb.Children.Add(slide);
                sb.Begin();
                return;
            }
            var anim = new System.Windows.Media.Animation.DoubleAnimation(
                0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            };
            el.BeginAnimation(UIElement.OpacityProperty, anim);
        }
        catch { try { el.Opacity = 1; } catch { } }
    }

    // Пульс-волна: плавное «сердцебиение» 0.85→1.0→0.85, только в Максимуме.
    // В остальных режимах — статичная единица, как было.
    public static void PulseAnimate(UIElement el)
    {
        try
        {
            if (el == null) return;
            bool ultra = false;
            try { ultra = AppSettings.EffectiveEffects() == AppSettings.EffectsMode.Maximum; }
            catch { }
            if (!ultra)
            {
                try { el.BeginAnimation(UIElement.OpacityProperty, null); } catch { }
                el.Opacity = 1;
                return;
            }
            var anim = new DoubleAnimation(0.85, 1.0, TimeSpan.FromMilliseconds(1600))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            el.BeginAnimation(UIElement.OpacityProperty, anim);
        }
        catch { try { el.Opacity = 1; } catch { } }
    }

    // Сердцебиение числа: дышит только масштаб цифры. Статус и полоса —
    // всегда статичны (полоса рисует кардиограмму, ей дрожание не нужно).
    // Только в Максимуме, остальным — статичная единица везде.
    public static void PulseBeat(UIElement number, UIElement status, UIElement spark)
    {
        try
        {
            if (number == null) return;
            bool ultra = false;
            try { ultra = AppSettings.EffectiveEffects() == AppSettings.EffectsMode.Maximum; }
            catch { }
            var scale = number.RenderTransform as ScaleTransform;
            if (scale == null || scale.CenterX != 0.5 || scale.CenterY != 0.5)
            {
                scale = new ScaleTransform(1, 1, 0.5, 0.5);
                try { number.RenderTransform = scale; } catch { }
            }
            try { number.RenderTransformOrigin = new Point(0.5, 0.5); } catch { }
            // Статус и полоса — в статику всегда (сброс старых часов тоже).
            try
            {
                if (status != null)
                {
                    try { status.BeginAnimation(UIElement.OpacityProperty, null); } catch { }
                    status.Opacity = 1;
                }
                if (spark != null)
                {
                    try { spark.BeginAnimation(UIElement.OpacityProperty, null); } catch { }
                    spark.Opacity = 1;
                }
            }
            catch { }
            if (!ultra)
            {
                try { scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); } catch { }
                try { scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); } catch { }
                try { scale.ScaleX = 1; scale.ScaleY = 1; } catch { }
                return;
            }
            var beat = new SineEase { EasingMode = EasingMode.EaseInOut };
            var dur = TimeSpan.FromMilliseconds(1400);
            var sx = new DoubleAnimation(1.0, 1.06, dur)
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = beat,
            };
            var sy = new DoubleAnimation(1.0, 1.06, dur)
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = beat,
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
        }
        catch
        {
            try
            {
                if (number.RenderTransform is ScaleTransform s) { s.ScaleX = 1; s.ScaleY = 1; }
                number.Opacity = 1;
                if (status != null) status.Opacity = 1;
                if (spark != null) spark.Opacity = 1;
            }
            catch { }
        }
    }
}
