using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace ForzaTelemetry.App.Controls;

/// <summary>
/// Full-width row of shift lights: segments light from the left as RPM climbs, green then amber then red, and ALL of
/// them blink red once the engine reaches the redline.
/// </summary>
/// <remarks>
/// <b>Why a custom FrameworkElement that draws itself (OnRender) instead of an ItemsControl of rectangles:</b> XAML
/// composition builds one real element per segment (layout, hit testing, bindings, data templates...). That is great
/// for interactive UI but wasteful for a purely visual gauge that changes 20 times a second. With <c>OnRender</c> the
/// control records a handful of drawing commands into a <see cref="DrawingContext"/> (a retained display list: WPF
/// keeps it and redraws without calling us again until something invalidates it). No child elements, no layout pass
/// per segment, and nothing to allocate per frame except a few <c>Rect</c> structs.
/// <para>
/// <b>Dependency properties:</b> a CLR property is a plain field; a <see cref="DependencyProperty"/> is registered with
/// WPF, which makes it bindable, styleable and animatable. <see cref="FrameworkPropertyMetadataOptions.AffectsRender"/>
/// tells WPF "when this value changes, call OnRender again", so the view model only has to change a bound value.
/// </para>
/// <para>
/// <b>Blink:</b> the on/off state is computed from a clock (<see cref="DashboardMath.IsBlinkOn"/>), not stored. While
/// <see cref="IsRedline"/> is true the control listens to <see cref="CompositionTarget.Rendering"/> (fired once per
/// displayed frame) and re-renders only when the computed state FLIPS, ~12 times a second at 6 Hz. When the redline
/// ends it unsubscribes, so an idle dashboard costs nothing.
/// </para>
/// </remarks>
public sealed class ShiftLightStrip : FrameworkElement
{
    private const double BlinkHertz = 6;
    private const double SegmentGap = 4;
    private const double SegmentRadius = 4;

    // One shared clock origin; only differences matter.
    private static readonly long ClockStart = Stopwatch.GetTimestamp();

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ShiftLightStrip),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ShiftLightStrip),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // The last argument is a validation callback: WPF rejects a segment count below 1 before it is ever stored.
    public static readonly DependencyProperty SegmentCountProperty = DependencyProperty.Register(
        nameof(SegmentCount), typeof(int), typeof(ShiftLightStrip),
        new FrameworkPropertyMetadata(15, FrameworkPropertyMetadataOptions.AffectsRender),
        value => (int)value >= 1);

    public static readonly DependencyProperty IsRedlineProperty = DependencyProperty.Register(
        nameof(IsRedline), typeof(bool), typeof(ShiftLightStrip),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsRedlineChanged));

    public static readonly DependencyProperty LowBrushProperty = BrushProperty(nameof(LowBrush));
    public static readonly DependencyProperty MidBrushProperty = BrushProperty(nameof(MidBrush));
    public static readonly DependencyProperty HighBrushProperty = BrushProperty(nameof(HighBrush));
    public static readonly DependencyProperty OffBrushProperty = BrushProperty(nameof(OffBrush));

    private bool _lastBlinkOn = true;
    private bool _listening;

    public ShiftLightStrip()
    {
        // Safety net: never leave a Rendering handler behind when the control leaves the visual tree.
        Unloaded += (_, _) => StopListening();
    }

    /// <summary>Current engine RPM.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Redline RPM: the scale the shift band is measured against.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int SegmentCount
    {
        get => (int)GetValue(SegmentCountProperty);
        set => SetValue(SegmentCountProperty, value);
    }

    /// <summary>When true every segment blinks red, whatever <see cref="Value"/> is.</summary>
    public bool IsRedline
    {
        get => (bool)GetValue(IsRedlineProperty);
        set => SetValue(IsRedlineProperty, value);
    }

    public Brush? LowBrush
    {
        get => (Brush?)GetValue(LowBrushProperty);
        set => SetValue(LowBrushProperty, value);
    }

    public Brush? MidBrush
    {
        get => (Brush?)GetValue(MidBrushProperty);
        set => SetValue(MidBrushProperty, value);
    }

    public Brush? HighBrush
    {
        get => (Brush?)GetValue(HighBrushProperty);
        set => SetValue(HighBrushProperty, value);
    }

    public Brush? OffBrush
    {
        get => (Brush?)GetValue(OffBrushProperty);
        set => SetValue(OffBrushProperty, value);
    }

    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(ShiftLightStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static void OnIsRedlineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var strip = (ShiftLightStrip)d;
        if ((bool)e.NewValue)
        {
            strip.StartListening();
        }
        else
        {
            strip.StopListening();
        }
    }

    private void StartListening()
    {
        if (!_listening)
        {
            CompositionTarget.Rendering += OnCompositionRendering;
            _listening = true;
        }
    }

    private void StopListening()
    {
        if (_listening)
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
            _listening = false;
        }
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        // Redraw only when the blink phase flipped since the last drawn frame.
        if (CurrentBlinkOn() != _lastBlinkOn)
        {
            InvalidateVisual();
        }
    }

    private static bool CurrentBlinkOn() =>
        DashboardMath.IsBlinkOn(Stopwatch.GetElapsedTime(ClockStart).TotalSeconds, BlinkHertz);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var count = SegmentCount;
        var width = ActualWidth;
        var height = ActualHeight;
        var segmentWidth = (width - SegmentGap * (count - 1)) / count;
        if (segmentWidth <= 0 || height <= 0)
        {
            return;
        }

        var redline = IsRedline;
        _lastBlinkOn = CurrentBlinkOn();
        var lit = redline ? count : DashboardMath.LitSegments(DashboardMath.ShiftProgress(Value, Maximum), count);

        for (var i = 0; i < count; i++)
        {
            Brush? brush;
            if (i >= lit)
            {
                brush = OffBrush;
            }
            else if (redline)
            {
                brush = _lastBlinkOn ? HighBrush : OffBrush;
            }
            else
            {
                brush = DashboardMath.ShiftZone(i, count) switch
                {
                    2 => HighBrush,
                    1 => MidBrush,
                    _ => LowBrush
                };
            }

            var x = i * (segmentWidth + SegmentGap);
            drawingContext.DrawRoundedRectangle(brush, null, new Rect(x, 0, segmentWidth, height), SegmentRadius, SegmentRadius);
        }
    }
}
