using System.Windows;
using System.Windows.Media;

namespace ForzaTelemetry.App.Controls;

/// <summary>
/// Compact horizontal gauge made of thin tick marks; ticks up to the current value are filled, the rest stay dim.
/// Used for the engine card (power, torque, boost, fuel).
/// </summary>
/// <remarks>
/// Ticks are drawn as rectangles in <c>OnRender</c> (one display list, no child elements; see
/// <see cref="ShiftLightStrip"/>). The pitch (distance between tick starts) is the width divided by the tick count, so
/// the gauge fills whatever width it is given and stays crisp at any DPI.
/// </remarks>
public sealed class TickGauge : FrameworkElement
{
    private const double TickWidth = 2;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(TickGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(TickGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(TickGauge),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TickCountProperty = DependencyProperty.Register(
        nameof(TickCount), typeof(int), typeof(TickGauge),
        new FrameworkPropertyMetadata(28, FrameworkPropertyMetadataOptions.AffectsRender),
        value => (int)value >= 1);

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(TickGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(TickGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int TickCount
    {
        get => (int)GetValue(TickCountProperty);
        set => SetValue(TickCountProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var count = TickCount;
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var lit = DashboardMath.LitSegments(DashboardMath.Normalize(Value, Minimum, Maximum), count);

        // The last tick ends exactly at the right edge: pitch spans the width minus one tick.
        var pitch = count > 1 ? (width - TickWidth) / (count - 1) : 0;
        for (var i = 0; i < count; i++)
        {
            var brush = i < lit ? FillBrush : TrackBrush;
            drawingContext.DrawRectangle(brush, null, new Rect(i * pitch, 0, TickWidth, height));
        }
    }
}
