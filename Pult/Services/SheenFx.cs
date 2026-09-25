using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pult.Services;

// 🆕 0.14.1: «живые» блики (фидбек 0.14.0: «блики статичные, блики не
// дышат, было бы прикольно анимировать»). Полоса (Height=2) по-прежнему
// НЕ двигается — виляние убрали в 0.14.0 — зато по ней бежит узкий свет,
// а сама она дышит прозрачностью.
//
// Рендер-безопасность (головной рендер стенда диспетчер не тикает):
//   * дыхание стартует с From == текущая прозрачность элемента —
//     первый кадр совпадает со статикой;
//   * позиция блика при старте ЗА левым краем полосы, ClipToBounds
//     не даёт ему вылезти за неё — в кадре блика не видно.
// Проверки 0.14.0 (SECTIONS/MIN/LEGACY) на таких рендерах не едут.
public static class SheenFx
{
    /// <summary>
    /// Дыхание прозрачности: from → to, AutoReverse, бесконечно.
    /// from выставляется и как база элемента — чтобы headless-рендер
    /// (где анимация не стартует/не тикает) получил ровно те же пиксели.
    /// </summary>
    public static void Breathe(FrameworkElement el, double from, double to,
        double sec, double delayMs = 0)
    {
        try
        {
            if (el is null) return;
            el.Opacity = from;
            var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(sec))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            if (delayMs > 0) a.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            el.BeginAnimation(UIElement.OpacityProperty, a);
        }
        catch { }
    }

    /// <summary>
    /// Бегущий свет внутри полосы: дочерний Border с градиентом
    /// transparent→белый→transparent + TranslateTransform. Цикл: пауза
    /// слева (650мс), проезд за 1.4с, снова пауза. Старт трансформа —
    /// целиком за левым краем (X = −width), полоса ClipToBounds, поэтому
    /// в headless-рендере пиксели не меняются. Идемпотентно: повторный
    /// хук второй блик не плодит.
    /// </summary>
    public static void Sweep(Border strip, double width, double delayMs = 0)
    {
        try
        {
            if (strip is null) return;
            double w = width > 1 ? width
                : (double.IsNaN(strip.Width) ? 200 : strip.Width);
            if (w < 8) return;
            strip.ClipToBounds = true;
            if (strip.Child != null) return;
            double gw = Math.Max(28, w * 0.42);
            var g = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
            };
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(70, 255, 255, 255), 0.40));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(215, 255, 255, 255), 0.50));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(70, 255, 255, 255), 0.60));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
            g.Freeze();
            var glint = new Border
            {
                Width = gw,
                Background = g,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
            };
            var tx = new TranslateTransform(-gw, 0);
            glint.RenderTransform = tx;
            strip.Child = glint;

            var kf = new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            kf.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                -gw, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            kf.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                -gw, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650))));
            kf.KeyFrames.Add(new EasingDoubleKeyFrame(
                w, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2050)))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
            if (delayMs > 0) kf.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            tx.BeginAnimation(TranslateTransform.XProperty, kf);
        }
        catch { }
    }

    /// <summary>
    /// Оживляет статичные полосы-разделители (Height=2, Width=200) во всём
    /// дереве вьюхи: дыхание 1.0→0.5 + бегущий свет, стаггер по порядку
    /// обхода (волна сверху вниз). Хук — из конструктора вьюхи: в
    /// headless-рендере стенда Loaded не стреляет. В Минимуме B_Bar
    /// прозрачен — анимировать нечего, выходим (settingsTall_min не меняется).
    /// </summary>
    public static void AnimateBars(DependencyObject root, double staggerMs = 140)
    {
        try
        {
            if (root is null) return;
            if (AppSettings.EffectiveEffects() == AppSettings.EffectsMode.Minimum)
                return;
            var seen = new HashSet<DependencyObject>();
            int i = 0;
            Walk(root);

            void Walk(DependencyObject d)
            {
                if (d is null || !seen.Add(d)) return;
                if (d is Border b && b.Height == 2 && !double.IsNaN(b.Width)
                    && Math.Abs(b.Width - 200) < 0.5)
                {
                    double delay = i++ * staggerMs;
                    Breathe(b, b.Opacity, Math.Min(b.Opacity, 0.5), 2.4, delay);
                    Sweep(b, 200, delay + 260);
                }
                int n = 0;
                try { n = VisualTreeHelper.GetChildrenCount(d); } catch { }
                for (int k = 0; k < n; k++) Walk(VisualTreeHelper.GetChild(d, k));
                if (d is FrameworkElement fe)
                    foreach (var c in LogicalTreeHelper.GetChildren(fe))
                        if (c is DependencyObject cd) Walk(cd);
            }
        }
        catch { }
    }
}
