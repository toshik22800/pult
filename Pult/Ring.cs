using System;
using System.Windows;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace Pult;

// Кольцо-индикатор (CPU/RAM/Диск). Рисуется GPU, без дёрганья.
public sealed class Ring : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(Ring),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender,
                null, CoerceValue));

    private static object CoerceValue(DependencyObject d, object baseValue)
    {
        double v = baseValue is double dv ? dv : 0.0;
        if (double.IsNaN(v) || double.IsInfinity(v)) return 0.0;
        return Math.Clamp(v, 0, 100);
    }

    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.Register(nameof(Track), typeof(WpfBrush), typeof(Ring),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(WpfBrush), typeof(Ring),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public WpfBrush Track
    {
        get => (WpfBrush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public WpfBrush Fill
    {
        get => (WpfBrush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public static readonly DependencyProperty ThicknessProperty =
        DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(Ring),
            new FrameworkPropertyMetadata(7.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <summary>0 — гладкая дуга, N — ретро-сегменты (закрашено по значению).</summary>
    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.Register(nameof(Segments), typeof(int), typeof(Ring),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Segments
    {
        get => (int)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        double r = (size - Thickness) / 2;
        var center = new WpfPoint(ActualWidth / 2, ActualHeight / 2);

        if (Segments > 1)
        {
            RenderSegmented(dc, center, r);
            return;
        }

        var trackPen = new Pen(Track, Thickness)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var fillPen = new Pen(Fill, Thickness)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        // Фон-кольцо целиком
        dc.DrawEllipse(null, trackPen, center, r, r);

        if (Value <= 0) return;
        if (Value >= 100)
        {
            // Дуга 360° вырождается (начало = конец) — рисуем полный круг.
            dc.DrawEllipse(null, fillPen, center, r, r);
            return;
        }

        // Дуга значения от 12 часов по часовой
        double endAngle = Value / 100 * 360;
        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            WpfPoint start = new(center.X, center.Y - r);
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(ArcPoint(center, r, endAngle), new WpfSize(r, r),
                0, endAngle > 180, SweepDirection.Clockwise, true, false);
        }
        geom.Freeze();
        dc.DrawGeometry(null, fillPen, geom);
    }

    private void RenderSegmented(DrawingContext dc, WpfPoint center, double r)
    {
        // Ретро-LED: крупные блоки с зазорами, цвет зоны по позиции —
        // фиолет → жёлтый → красный, как шкала прибора.
        double gap = 360.0 / Segments * 0.35;
        double span = 360.0 / Segments - gap;
        var zoneMid = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));
        var zoneHi = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
        zoneMid.Freeze();
        zoneHi.Freeze();
        for (int i = 0; i < Segments; i++)
        {
            double startAngle = i * 360.0 / Segments + gap / 2;
            double frac = (i + 0.5) / Segments;
            bool on = frac * 100 <= Value;
            WpfBrush brush = Track;
            if (on)
                brush = frac < 0.6 ? Fill : frac < 0.85 ? zoneMid : zoneHi;
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(ArcPoint(center, r, startAngle), false, false);
                ctx.ArcTo(ArcPoint(center, r, startAngle + span), new WpfSize(r, r),
                    0, span > 180, SweepDirection.Clockwise, true, false);
            }
            geom.Freeze();
            dc.DrawGeometry(null, new Pen(brush, Thickness)
                { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat }, geom);
        }
    }

    private static WpfPoint ArcPoint(WpfPoint c, double r, double degrees)
    {
        double rad = (degrees - 90) * Math.PI / 180;
        return new WpfPoint(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }
}
