using System.Windows;
using System.Windows.Media;

namespace ForzaTelemetry.App.Controls;

/// <summary>
/// "Traction circle": rings at 0.5 g, 1 g and 1.5 g, a crosshair, a dot for the current acceleration and a fading
/// trail of the last ~1.5 s.
/// </summary>
/// <remarks>
/// <b>Axes:</b> the packet gives car-local acceleration with X = right and Z = forward (see <c>TelemetryPacket</c>).
/// The dot plots the acceleration vector itself: accelerating moves it UP, braking DOWN, turning right moves it RIGHT.
/// (Some meters plot the force the DRIVER feels, which is the opposite side; plotting the car's acceleration keeps
/// "braking = down/back" and matches the numbers shown next to the meter.)
/// <para>
/// <b>The trail:</b> the fading dots live in a <see cref="PointTrail"/> ring buffer sized for ~1.5 s at the 20 Hz UI
/// refresh. A sample is recorded at the START of <see cref="OnRender"/>: WPF renders once after the view model has
/// updated both <see cref="LateralG"/> and <see cref="LongitudinalG"/> (each change invalidates, WPF coalesces them
/// into one render), so each recorded sample is exactly what was just drawn, and nothing is recorded while no new
/// telemetry arrives. The fade uses <c>PushOpacity</c> (a cheap alpha multiplier applied to the dots drawn inside)
/// instead of one pre-built brush per alpha level.
/// </para>
/// </remarks>
public sealed class GForceMeter : FrameworkElement
{
    private const int TrailLength = 30;

    // The rings, as multiples of g. The outermost one is also the edge of the meter (see MaxG).
    private static readonly double[] RingsG = [0.5, 1.0, 1.5];

    public static readonly DependencyProperty LateralGProperty = DependencyProperty.Register(
        nameof(LateralG), typeof(double), typeof(GForceMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LongitudinalGProperty = DependencyProperty.Register(
        nameof(LongitudinalG), typeof(double), typeof(GForceMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The g value at the edge of the meter. Anything beyond it is pinned to the edge.</summary>
    public static readonly DependencyProperty MaxGProperty = DependencyProperty.Register(
        nameof(MaxG), typeof(double), typeof(GForceMeter),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender),
        value => (double)value > 0);

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(GForceMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnRingBrushChanged));

    public static readonly DependencyProperty DotBrushProperty = DependencyProperty.Register(
        nameof(DotBrush), typeof(Brush), typeof(GForceMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly PointTrail _trail = new(TrailLength);
    private Pen? _ringPen;

    /// <summary>Acceleration to the right, in g (negative = left).</summary>
    public double LateralG
    {
        get => (double)GetValue(LateralGProperty);
        set => SetValue(LateralGProperty, value);
    }

    /// <summary>Acceleration forward, in g (negative = braking).</summary>
    public double LongitudinalG
    {
        get => (double)GetValue(LongitudinalGProperty);
        set => SetValue(LongitudinalGProperty, value);
    }

    public double MaxG
    {
        get => (double)GetValue(MaxGProperty);
        set => SetValue(MaxGProperty, value);
    }

    public Brush? RingBrush
    {
        get => (Brush?)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public Brush? DotBrush
    {
        get => (Brush?)GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    private static void OnRingBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((GForceMeter)d)._ringPen = RenderHelpers.CreatePen((Brush?)e.NewValue, 1);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 4)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - 1; // 1 px of room so the outer ring's stroke is not clipped

        var maxG = MaxG;
        foreach (var ring in RingsG)
        {
            drawingContext.DrawEllipse(null, _ringPen, center, radius * ring / maxG, radius * ring / maxG);
        }

        drawingContext.DrawLine(_ringPen, new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
        drawingContext.DrawLine(_ringPen, new Point(center.X, center.Y - radius), new Point(center.X, center.Y + radius));

        var (x, y) = DashboardMath.ToMeterPoint(LateralG, LongitudinalG, maxG);
        _trail.Push(x, y);

        // Oldest first, so newer (more opaque) dots paint over older ones.
        for (var age = _trail.Count - 1; age >= 1; age--)
        {
            var (tx, ty) = _trail.FromNewest(age);
            var fade = 1.0 - (double)age / _trail.Capacity;
            drawingContext.PushOpacity(fade * 0.5);
            drawingContext.DrawEllipse(DotBrush, null, new Point(center.X + tx * radius, center.Y + ty * radius), 3, 3);
            drawingContext.Pop();
        }

        drawingContext.DrawEllipse(DotBrush, null, new Point(center.X + x * radius, center.Y + y * radius), 5.5, 5.5);
    }
}
