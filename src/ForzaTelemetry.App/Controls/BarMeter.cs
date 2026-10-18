using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ForzaTelemetry.App.Controls;

/// <summary>
/// One straight bar for pedal and steering inputs: a track with a fill that grows from the bottom (vertical), from the
/// left (horizontal) or, when <see cref="IsBipolar"/>, outward from the middle (steering: left of center = left).
/// </summary>
/// <remarks>
/// A single control covers throttle, brake, clutch, handbrake and steering by changing three properties, instead of
/// five near-identical templates. It draws with <c>OnRender</c> (see <see cref="ShiftLightStrip"/> for why).
/// <c>Orientation</c> is the same enum StackPanel uses; it is a dependency property so a style could switch it.
/// </remarks>
public sealed class BarMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(BarMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(BarMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(BarMeter),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(BarMeter),
        new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fill grows from the middle of the range instead of from the start.</summary>
    public static readonly DependencyProperty IsBipolarProperty = DependencyProperty.Register(
        nameof(IsBipolar), typeof(bool), typeof(BarMeter),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(BarMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(BarMeter),
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

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public bool IsBipolar
    {
        get => (bool)GetValue(IsBipolarProperty);
        set => SetValue(IsBipolarProperty, value);
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
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var vertical = Orientation == Orientation.Vertical;
        var length = vertical ? height : width;
        var thickness = vertical ? width : height;
        var radius = thickness / 2;

        drawingContext.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, width, height), radius, radius);

        // Fill as a span [start, end] along the bar, measured from the low end (left, or bottom).
        var ratio = DashboardMath.Normalize(Value, Minimum, Maximum);
        double start;
        double end;
        if (IsBipolar)
        {
            var middle = length / 2;
            var tip = ratio * length;
            start = Math.Min(middle, tip);
            end = Math.Max(middle, tip);
        }
        else
        {
            start = 0;
            end = ratio * length;
        }

        var span = end - start;
        if (span < 0.5)
        {
            return;
        }

        var fillRadius = Math.Min(radius, span / 2);
        var rect = vertical
            ? new Rect(0, height - end, width, span) // vertical bars fill upward: screen y grows downward
            : new Rect(start, 0, span, height);
        drawingContext.DrawRoundedRectangle(FillBrush, null, rect, fillRadius, fillRadius);
    }
}
