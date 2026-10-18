using System.Windows;
using System.Windows.Media;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App.Controls;

/// <summary>
/// A horizontal bar centered on zero that shows the understeer/oversteer balance: the fill grows from the middle toward
/// the side that is sliding (left = oversteer, right = understeer) and its color says how much (green = balanced, amber
/// = noticeable, red = strong).
/// </summary>
/// <remarks>
/// The balance index itself comes from <see cref="BalanceAnalyzer"/>; this control only draws it. The shaded center band
/// is the analyzer's "neutral" zone (|index| below <see cref="BalanceAnalyzer.BalanceThreshold"/>), so the picture and the
/// classification can never disagree.
/// </remarks>
public sealed class BalanceBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(BalanceBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>False while there is not enough slip to judge: the thumb and fill are hidden instead of lying.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(BalanceBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush TrackBrush = ChartPalette.Solid(ChartPalette.Hairline, 0.7);
    private static readonly Brush NeutralBand = ChartPalette.Solid(ChartPalette.Throttle, 0.14);
    private static readonly Pen CenterPen = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Muted, 0.8), 1);
    private static readonly Pen ThumbRing = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Card), 2);
    private static readonly Brush OkFill = ChartPalette.Solid(ChartPalette.Throttle, 0.85);
    private static readonly Brush WarnFill = ChartPalette.Solid(ChartPalette.Warm, 0.9);
    private static readonly Brush StrongFill = ChartPalette.Solid(ChartPalette.Brake, 0.9);

    /// <summary>-1 (full oversteer) .. 0 .. +1 (full understeer).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The fill color for an index: green inside the neutral zone, amber up to 0.5, red beyond.</summary>
    public static Brush FillFor(double value)
    {
        var magnitude = Math.Abs(value);
        return magnitude < BalanceAnalyzer.BalanceThreshold ? OkFill : magnitude < 0.5 ? WarnFill : StrongFill;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 40 || height < 8)
        {
            return;
        }

        const double barHeight = 10;
        var top = (height - barHeight) / 2;
        var half = width / 2;
        var track = new Rect(0, top, width, barHeight);
        dc.DrawRoundedRectangle(TrackBrush, null, track, barHeight / 2, barHeight / 2);

        var band = half * BalanceAnalyzer.BalanceThreshold;
        dc.DrawRectangle(NeutralBand, null, new Rect(half - band, top, band * 2, barHeight));

        var value = double.IsFinite(Value) ? Math.Clamp(Value, -1, 1) : 0;
        if (IsActive)
        {
            var x = half + value * half;
            var fill = new Rect(Math.Min(half, x), top, Math.Abs(x - half), barHeight);
            if (fill.Width > 0.5)
            {
                dc.DrawRoundedRectangle(FillFor(value), null, fill, barHeight / 2, barHeight / 2);
            }
        }

        dc.DrawLine(CenterPen, new Point(Math.Floor(half) + 0.5, top - 4), new Point(Math.Floor(half) + 0.5, top + barHeight + 4));

        if (IsActive)
        {
            var thumbX = Math.Clamp(half + value * half, 7, width - 7);
            dc.DrawEllipse(ChartPalette.TextBrush, ThumbRing, new Point(thumbX, height / 2), 7, 7);
        }
    }
}
