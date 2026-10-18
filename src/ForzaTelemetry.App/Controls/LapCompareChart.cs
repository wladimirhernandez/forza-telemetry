using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.Core.Laps;

namespace ForzaTelemetry.App.Controls;

/// <summary>
/// The lap in progress laid over the best lap by DISTANCE on stacked lanes: speed, throttle, brake, steering and the time gap
/// (green where the current lap is ahead, red where it is behind), with a hover crosshair that reads both laps at one spot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sibling of <see cref="TraceChart"/>, same rules.</b> The view model owns two reusable <see cref="LapTraceSample"/> arrays and
/// refills them in place; the control never copies them. Geometry is rebuilt only when something it depends on changed, and it is
/// split in two: the best lap's lines (stable for a whole lap, rebuilt only when <see cref="BestVersion"/>, the size or the scale
/// changed) and the current lap's lines plus the gap (rebuilt when <see cref="DataVersion"/> changes, i.e. every metre driven).
/// Lines are frozen <see cref="StreamGeometry"/> instances reduced to at most four points per pixel column.
/// </para>
/// <para>
/// <b>Why distance, not time.</b> Two laps are "the same moment" when the car is at the same place, not when the clock agrees:
/// at the brake point of turn 3 the faster lap is already there. The x axis is the distance into the lap, and the gap lane is
/// the difference of the two lap clocks at each distance, which is exactly what the Laps page's live delta shows (both use
/// <see cref="LapTraceMath"/>).
/// </para>
/// <para>
/// <b>Colors.</b> The current lap is the cold accent in every lane and the best lap a muted neutral, so which line is which never
/// depends on the lane; the gap lane alone uses the status colors (green below zero = ahead, red above = behind) like the Laps page.
/// </para>
/// </remarks>
public sealed class LapCompareChart : FrameworkElement
{
    private const double GutterLeft = 62;
    private const double GutterRight = 14;
    private const double PlotTop = 30;
    private const double AxisHeight = 24;
    private const double LaneGap = 10;
    private const double ReadoutWidth = 262;
    private const int LaneSpeed = 0, LaneThrottle = 1, LaneBrake = 2, LaneSteer = 3, LaneDelta = 4;

    public static readonly DependencyProperty CurrentTraceProperty = Register<LapTraceSample[]?>(nameof(CurrentTrace), null);
    public static readonly DependencyProperty CurrentCountProperty = Register(nameof(CurrentCount), 0);
    public static readonly DependencyProperty BestTraceProperty = Register<LapTraceSample[]?>(nameof(BestTrace), null);
    public static readonly DependencyProperty BestCountProperty = Register(nameof(BestCount), 0);

    /// <summary>Bump to tell the chart the current lap's array was refilled in place.</summary>
    public static readonly DependencyProperty DataVersionProperty = Register(nameof(DataVersion), 0);

    /// <summary>Bump to tell the chart the best lap's array was replaced.</summary>
    public static readonly DependencyProperty BestVersionProperty = Register(nameof(BestVersion), 0);

    /// <summary>Multiplier from meters per second to the displayed speed unit (3.6 for km/h).</summary>
    public static readonly DependencyProperty SpeedScaleProperty = DependencyProperty.Register(
        nameof(SpeedScale), typeof(double), typeof(LapCompareChart),
        new FrameworkPropertyMetadata(3.6, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged),
        value => (double)value > 0);

    public static readonly DependencyProperty SpeedUnitProperty = DependencyProperty.Register(
        nameof(SpeedUnit), typeof(string), typeof(LapCompareChart),
        new FrameworkPropertyMetadata("km/h", FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    /// <summary>Multiplier from meters to the distance axis unit.</summary>
    public static readonly DependencyProperty DistanceScaleProperty = DependencyProperty.Register(
        nameof(DistanceScale), typeof(double), typeof(LapCompareChart),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged),
        value => (double)value > 0);

    public static readonly DependencyProperty DistanceUnitProperty = DependencyProperty.Register(
        nameof(DistanceUnit), typeof(string), typeof(LapCompareChart),
        new FrameworkPropertyMetadata("m", FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    private static readonly Pen GridPen = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Hairline, 0.55), 1);
    private static readonly Pen LaneEdgePen = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Hairline), 1);
    private static readonly Pen ZeroPen = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Text, 0.35), 1);
    private static readonly Pen CrosshairPen = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Text, 0.55), 1);
    private static readonly Pen CurrentPen = ChartPalette.Pen(ChartPalette.ColdBrush, 1.8);
    private static readonly Pen BestPen = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Neutral, 0.8), 1.4);
    private static readonly Pen AheadPen = ChartPalette.Pen(ChartPalette.ThrottleBrush, 1.6);
    private static readonly Pen BehindPen = ChartPalette.Pen(ChartPalette.BrakeBrush, 1.6);
    private static readonly Brush CurrentFill = FadeDown(ChartPalette.Cold, 0.22);
    private static readonly Brush AheadFill = ChartPalette.Solid(ChartPalette.Throttle, 0.20);
    private static readonly Brush BehindFill = ChartPalette.Solid(ChartPalette.Brake, 0.20);
    private static readonly Brush TitleBackdrop = ChartPalette.Solid(ChartPalette.Card, 0.8);
    private static readonly Brush ReadoutBackground = ChartPalette.Solid(ChartPalette.CardRaised, 0.96);
    private static readonly Pen ReadoutBorder = ChartPalette.Pen(ChartPalette.HairlineBrush, 1);

    private readonly ChartText _text = new();
    private readonly List<Point> _points = new(2048);
    private readonly Lane[] _lanes = new Lane[5];

    private bool _bestDirty = true;
    private bool _currentDirty = true;
    private Size _builtFor;
    private double _plotLeft, _plotRight, _plotBottom;
    private double _axisMeters = 1;
    private double _pxPerMeter = 1;
    private double _deltaRange = 0.5;
    private (double AxisMeters, double SpeedAxisMax, Size Size) _bestKey;

    // The gap to the best lap at every sample of the lap in progress, computed once per rebuild (see UpdateGap).
    private double[] _gap = [];
    private int _gapCount;
    private LapTraceSample[]? _gapBestTrace;
    private int _gapBestVersion = -1;
    private int _gapBestCount;
    private float _gapLastDistance, _gapLastTime;
    private double _hoverX = double.NaN;

    private StreamGeometry? _bestSpeed, _bestThrottle, _bestBrake, _bestSteer;
    private StreamGeometry? _curSpeed, _curSpeedFill, _curThrottle, _curThrottleFill, _curBrake, _curBrakeFill, _curSteer;
    private StreamGeometry? _deltaLine, _deltaArea;

    public LapCompareChart()
    {
        // The control paints its own transparent surface in OnRender; this keeps the mouse hit-testing simple and cheap.
        Focusable = false;
        ClipToBounds = true;
    }

    public LapTraceSample[]? CurrentTrace
    {
        get => (LapTraceSample[]?)GetValue(CurrentTraceProperty);
        set => SetValue(CurrentTraceProperty, value);
    }

    public int CurrentCount
    {
        get => (int)GetValue(CurrentCountProperty);
        set => SetValue(CurrentCountProperty, value);
    }

    public LapTraceSample[]? BestTrace
    {
        get => (LapTraceSample[]?)GetValue(BestTraceProperty);
        set => SetValue(BestTraceProperty, value);
    }

    public int BestCount
    {
        get => (int)GetValue(BestCountProperty);
        set => SetValue(BestCountProperty, value);
    }

    public int DataVersion
    {
        get => (int)GetValue(DataVersionProperty);
        set => SetValue(DataVersionProperty, value);
    }

    public int BestVersion
    {
        get => (int)GetValue(BestVersionProperty);
        set => SetValue(BestVersionProperty, value);
    }

    public double SpeedScale
    {
        get => (double)GetValue(SpeedScaleProperty);
        set => SetValue(SpeedScaleProperty, value);
    }

    public string SpeedUnit
    {
        get => (string)GetValue(SpeedUnitProperty);
        set => SetValue(SpeedUnitProperty, value);
    }

    public double DistanceScale
    {
        get => (double)GetValue(DistanceScaleProperty);
        set => SetValue(DistanceScaleProperty, value);
    }

    public string DistanceUnit
    {
        get => (string)GetValue(DistanceUnitProperty);
        set => SetValue(DistanceUnitProperty, value);
    }

    private static DependencyProperty Register<T>(string name, T defaultValue) => DependencyProperty.Register(
        name, typeof(T), typeof(LapCompareChart),
        new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Everything that can change the best lap's pixels (its samples, the scales, the units) also invalidates the current lap's;
        // only a new current sample (DataVersion / CurrentCount) leaves the best geometry alone.
        var chart = (LapCompareChart)d;
        chart._currentDirty = true;
        if (e.Property != DataVersionProperty && e.Property != CurrentCountProperty && e.Property != CurrentTraceProperty)
        {
            chart._bestDirty = true;
        }
    }

    private static Brush FadeDown(Color color, double topAlpha)
    {
        // Area fills fade to transparent at the bottom so stacked lanes stay calm instead of becoming solid blocks.
        var brush = new LinearGradientBrush(
            ChartPalette.WithAlpha(color, topAlpha), ChartPalette.WithAlpha(color, 0), new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverX = double.NaN;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 320 || height < 220)
        {
            return;
        }

        // Invisible, but it makes the whole control hit-testable so MouseMove fires over empty plot area too.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var best = BestTrace is { } b ? b.AsSpan(0, Math.Clamp(BestCount, 0, b.Length)) : default;
        var current = CurrentTrace is { } c ? c.AsSpan(0, Math.Clamp(CurrentCount, 0, c.Length)) : default;
        if (best.Length < 2)
        {
            return; // the page shows its empty state instead of this control
        }

        var size = new Size(width, height);
        if (_builtFor != size)
        {
            _bestDirty = _currentDirty = true;
        }

        if (_bestDirty || _currentDirty)
        {
            Rebuild(best, current, size);
        }

        DrawFrame(dc, dpi);
        DrawLanes(dc);
        DrawLegend(dc, dpi);
        DrawHover(dc, best, current, dpi, width);
    }

    // ---- Geometry ---------------------------------------------------------------------------------

    private readonly record struct Lane(double Top, double Bottom, double Min, double Max)
    {
        public double Y(double value) => Bottom - ((Math.Clamp(value, Min, Max) - Min) / (Max - Min) * (Bottom - Top));
    }

    private void Rebuild(ReadOnlySpan<LapTraceSample> best, ReadOnlySpan<LapTraceSample> current, Size size)
    {
        _builtFor = size;
        _plotLeft = GutterLeft;
        _plotRight = size.Width - GutterRight;
        _plotBottom = size.Height - AxisHeight;

        // The axis is the best lap's length, widened in steps only when the lap in progress runs past it (see ChartMath.StableAxisMeters):
        // the best lap's pixels depend on this scale, and a scale that grew with every metre driven would rebuild them every time.
        _axisMeters = ChartMath.StableAxisMeters(best[^1].DistanceMeters, current.IsEmpty ? 0 : current[^1].DistanceMeters);
        _pxPerMeter = (_plotRight - _plotLeft) / _axisMeters;

        // Lanes share the plot height by weight; the gaps between them carry the separators.
        double[] weights = [1.5, 0.8, 0.8, 0.9, 1.2];
        var available = _plotBottom - PlotTop - (LaneGap * (weights.Length - 1));
        var total = weights.Sum();

        double maxSpeed = 0;
        foreach (var s in best)
        {
            maxSpeed = Math.Max(maxSpeed, s.SpeedMs * SpeedScale);
        }

        foreach (var s in current)
        {
            maxSpeed = Math.Max(maxSpeed, s.SpeedMs * SpeedScale);
        }

        var speedAxisMax = ChartMath.NiceCeiling(Math.Max(maxSpeed, SpeedScale * 10), 3).Max;

        // The gap lane is symmetric about zero so "ahead" and "behind" get the same room; never tighter than half a second.
        UpdateGap(best, current);
        double maxAbs = 0;
        for (var i = 0; i < current.Length; i++)
        {
            if (!double.IsNaN(_gap[i]))
            {
                maxAbs = Math.Max(maxAbs, Math.Abs(_gap[i]));
            }
        }

        _deltaRange = ChartMath.NiceCeiling(Math.Max(0.5, maxAbs), 2).Max;

        double[,] ranges = { { 0, speedAxisMax }, { 0, 1 }, { 0, 1 }, { -1, 1 }, { -_deltaRange, _deltaRange } };
        var y = PlotTop;
        for (var i = 0; i < _lanes.Length; i++)
        {
            var laneHeight = available * weights[i] / total;
            _lanes[i] = new Lane(y, y + laneHeight, ranges[i, 0], ranges[i, 1]);
            y += laneHeight + LaneGap;
        }

        // The best lap's lines depend on the pixel mapping too, not only on its own samples: a current lap that outgrows the axis
        // widens it (in steps) and a faster one raises the speed axis, and either moves the best lap's pixels. Both move rarely.
        var bestKey = (_axisMeters, speedAxisMax, size);
        if (_bestDirty || bestKey != _bestKey)
        {
            _bestDirty = false;
            _bestKey = bestKey;
            var scale = SpeedScale;
            (_bestSpeed, _) = BuildSeries(best, (_, s) => s.SpeedMs * scale, _lanes[LaneSpeed], fill: false);
            (_bestThrottle, _) = BuildSeries(best, (_, s) => s.Throttle, _lanes[LaneThrottle], fill: false);
            (_bestBrake, _) = BuildSeries(best, (_, s) => s.Brake, _lanes[LaneBrake], fill: false);
            (_bestSteer, _) = BuildSeries(best, (_, s) => s.Steer, _lanes[LaneSteer], fill: false);
        }

        _currentDirty = false;
        var speedScale = SpeedScale;
        (_curSpeed, _curSpeedFill) = BuildSeries(current, (_, s) => s.SpeedMs * speedScale, _lanes[LaneSpeed], fill: true);
        (_curThrottle, _curThrottleFill) = BuildSeries(current, (_, s) => s.Throttle, _lanes[LaneThrottle], fill: true);
        (_curBrake, _curBrakeFill) = BuildSeries(current, (_, s) => s.Brake, _lanes[LaneBrake], fill: true);
        (_curSteer, _) = BuildSeries(current, (_, s) => s.Steer, _lanes[LaneSteer], fill: false);

        // The gap lane reads the buffer filled above: no second (or third) binary search per sample.
        (_deltaLine, _deltaArea) = BuildSeries(current, (i, _) => _gap[i], _lanes[LaneDelta], fill: true, baselineValue: 0);
    }

    /// <summary>
    /// Fills <see cref="_gap"/> with the lap in progress's gap to the best lap at each of its samples (NaN where the best lap does
    /// not cover that distance). While the same best lap is shown and the lap in progress only grew (new samples appended), the
    /// values already computed are kept and only the new tail is looked up; anything else (a new best lap, a new lap, a reset)
    /// recomputes from the start. "Only grew" is checked on the last kept sample, not assumed: a lap change between two rebuilds
    /// must never leave the previous lap's gaps under the new lap's samples.
    /// </summary>
    private void UpdateGap(ReadOnlySpan<LapTraceSample> best, ReadOnlySpan<LapTraceSample> current)
    {
        var keep = 0;
        if (_gapCount > 0
            && ReferenceEquals(_gapBestTrace, BestTrace) && _gapBestVersion == BestVersion && _gapBestCount == best.Length
            && current.Length >= _gapCount
            && current[_gapCount - 1].DistanceMeters == _gapLastDistance && current[_gapCount - 1].TimeSeconds == _gapLastTime)
        {
            keep = _gapCount;
        }

        if (_gap.Length < current.Length)
        {
            Array.Resize(ref _gap, Math.Max(current.Length, _gap.Length * 2)); // keeps the values already computed
        }

        for (var i = keep; i < current.Length; i++)
        {
            _gap[i] = LapTraceMath.DeltaSeconds(best, current[i].DistanceMeters, current[i].TimeSeconds) ?? double.NaN;
        }

        _gapCount = current.Length;
        _gapBestTrace = BestTrace;
        _gapBestVersion = BestVersion;
        _gapBestCount = best.Length;
        if (current.Length > 0)
        {
            _gapLastDistance = current[^1].DistanceMeters;
            _gapLastTime = current[^1].TimeSeconds;
        }
    }

    /// <summary>Builds the stroked line (and optionally the area between it and the baseline) of one series.</summary>
    private (StreamGeometry? Line, StreamGeometry? Fill) BuildSeries(
        ReadOnlySpan<LapTraceSample> trace, Func<int, LapTraceSample, double> value, Lane lane, bool fill, double? baselineValue = null)
    {
        if (trace.Length < 1)
        {
            return (null, null);
        }

        FillPoints(trace, value, lane);
        if (_points.Count == 0)
        {
            return (null, null);
        }

        if (_points.Count == 1)
        {
            // A lone sample would draw nothing; give it a hair of length so round caps render a dot.
            _points.Add(new Point(_points[0].X + 0.1, _points[0].Y));
        }

        var baseline = baselineValue is { } v ? lane.Y(v) : lane.Bottom;
        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(_points[0], isFilled: false, isClosed: false);
            context.PolyLineTo(_points, isStroked: true, isSmoothJoin: true);
        }

        line.Freeze();

        StreamGeometry? area = null;
        if (fill)
        {
            area = new StreamGeometry();
            using var context = area.Open();
            context.BeginFigure(new Point(_points[0].X, baseline), isFilled: true, isClosed: true);
            context.PolyLineTo(_points, isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(_points[^1].X, baseline), isStroked: false, isSmoothJoin: false);
            area.Freeze();
        }

        return (line, area);
    }

    /// <summary>
    /// Fills <see cref="_points"/> with the pixel-space points of one series, keeping at most four per pixel column (the first, the
    /// lowest, the highest and the last, in drive order): a 6000-sample lap on a 1000 px plot stays a faithful, light polyline.
    /// Samples whose value is NaN (a gap lane with no reference there) are skipped. The value function gets the sample's index
    /// as well, so a series computed once per rebuild (the gap) is read from its buffer instead of recomputed per call.
    /// </summary>
    private void FillPoints(ReadOnlySpan<LapTraceSample> trace, Func<int, LapTraceSample, double> value, Lane lane)
    {
        _points.Clear();
        Span<int> picks = stackalloc int[4];

        var i = 0;
        while (i < trace.Length)
        {
            var column = Math.Floor(trace[i].DistanceMeters * _pxPerMeter);
            var j = i;
            var first = -1;
            var last = -1;
            int lowAt = -1, highAt = -1;
            double low = double.PositiveInfinity, high = double.NegativeInfinity;
            while (j < trace.Length && Math.Floor(trace[j].DistanceMeters * _pxPerMeter) == column)
            {
                var v = value(j, trace[j]);
                if (!double.IsNaN(v))
                {
                    first = first < 0 ? j : first;
                    last = j;
                    if (v < low)
                    {
                        low = v;
                        lowAt = j;
                    }

                    if (v > high)
                    {
                        high = v;
                        highAt = j;
                    }
                }

                j++;
            }

            if (first >= 0)
            {
                picks[0] = first;
                picks[1] = lowAt;
                picks[2] = highAt;
                picks[3] = last;
                picks.Sort();
                var previous = -1;
                foreach (var pick in picks)
                {
                    if (pick == previous)
                    {
                        continue;
                    }

                    previous = pick;
                    _points.Add(new Point(DistanceToX(trace[pick].DistanceMeters), lane.Y(value(pick, trace[pick]))));
                }
            }

            i = j;
        }
    }

    private double DistanceToX(double meters) => _plotLeft + (meters * _pxPerMeter);

    // ---- Drawing ----------------------------------------------------------------------------------

    private void DrawFrame(DrawingContext dc, double dpi)
    {
        var plotWidth = _plotRight - _plotLeft;
        var axisDisplay = _axisMeters * DistanceScale;

        // The vertical distance grid shared by every lane, with its labels under the last one.
        var step = ChartMath.NiceStep(axisDisplay, 6);
        var format = step >= 1 ? "0" : step >= 0.1 ? "0.0" : "0.00";
        for (var tick = 0.0; tick <= axisDisplay + 1e-9; tick += step)
        {
            var x = Snap(_plotLeft + (tick / axisDisplay * plotWidth));
            if (tick > 0)
            {
                dc.DrawLine(GridPen, new Point(x, PlotTop), new Point(x, _plotBottom));
            }

            _text.DrawCentered(
                dc, tick.ToString(format, CultureInfo.CurrentCulture), Math.Max(x, _plotLeft + 4), _plotBottom + 6, 11, ChartPalette.MutedBrush, dpi, numeral: true);
        }

        _text.DrawRight(dc, DistanceUnit, _plotLeft - 26, _plotBottom + 6, 11, ChartPalette.MutedBrush, dpi);

        string[] titles =
        [
            string.Format(CultureInfo.CurrentCulture, InsightsStrings.LaneSpeedUnitFormat, SpeedUnit),
            InsightsStrings.ThrottleLabel,
            InsightsStrings.BrakeLabel,
            InsightsStrings.LaneSteering,
            InsightsStrings.CompareLaneDelta
        ];

        for (var i = 0; i < _lanes.Length; i++)
        {
            var lane = _lanes[i];
            dc.DrawLine(LaneEdgePen, new Point(_plotLeft, Snap(lane.Bottom)), new Point(_plotRight, Snap(lane.Bottom)));
            var midY = Snap((lane.Top + lane.Bottom) / 2);
            dc.DrawLine(i == LaneDelta ? ZeroPen : GridPen, new Point(_plotLeft, midY), new Point(_plotRight, midY));

            // A translucent chip behind the title keeps it readable when a trace runs underneath it.
            var title = _text.Get(titles[i], 11, ChartPalette.MutedBrush, dpi, bold: true);
            dc.DrawRoundedRectangle(TitleBackdrop, null, new Rect(_plotLeft + 4, lane.Top + 1, title.Width + 12, title.Height + 2), 5, 5);
            dc.DrawText(title, new Point(_plotLeft + 10, lane.Top + 2));
            DrawAxisLabels(dc, i, lane, dpi);
        }
    }

    private void DrawAxisLabels(DrawingContext dc, int laneIndex, Lane lane, double dpi)
    {
        var right = _plotLeft - 8;
        string top, bottom;
        switch (laneIndex)
        {
            case LaneSpeed: top = lane.Max.ToString("0", CultureInfo.CurrentCulture); bottom = "0"; break;
            case LaneSteer: top = InsightsStrings.SteerRightShort; bottom = InsightsStrings.SteerLeftShort; break;
            case LaneDelta: top = "+" + FormatSeconds(lane.Max); bottom = "−" + FormatSeconds(lane.Max); break;
            default: top = "100"; bottom = "0"; break;
        }

        _text.DrawRight(dc, top, right, lane.Top - 1, 11, ChartPalette.MutedBrush, dpi, numeral: true);
        _text.DrawRight(dc, bottom, right, lane.Bottom - 13, 11, ChartPalette.MutedBrush, dpi, numeral: true);
        if (laneIndex is LaneSteer or LaneDelta)
        {
            _text.DrawRight(dc, "0", right, ((lane.Top + lane.Bottom) / 2) - 7, 11, ChartPalette.MutedBrush, dpi, numeral: true);
        }

        if (laneIndex == LaneDelta)
        {
            // Which side is which, said in words, so the colors are never the only signal.
            _text.DrawRight(dc, InsightsStrings.CompareSlower, _plotRight - 6, lane.Top + 2, 11, ChartPalette.MutedBrush, dpi);
            _text.DrawRight(dc, InsightsStrings.CompareFaster, _plotRight - 6, lane.Bottom - 16, 11, ChartPalette.MutedBrush, dpi);
        }
    }

    private void DrawLanes(DrawingContext dc)
    {
        // The best lap first, so the lap in progress is always on top of it.
        dc.DrawGeometry(null, BestPen, _bestSpeed);
        dc.DrawGeometry(null, BestPen, _bestThrottle);
        dc.DrawGeometry(null, BestPen, _bestBrake);
        dc.DrawGeometry(null, BestPen, _bestSteer);

        dc.DrawGeometry(CurrentFill, null, _curSpeedFill);
        dc.DrawGeometry(CurrentFill, null, _curThrottleFill);
        dc.DrawGeometry(CurrentFill, null, _curBrakeFill);
        dc.DrawGeometry(null, CurrentPen, _curSpeed);
        dc.DrawGeometry(null, CurrentPen, _curThrottle);
        dc.DrawGeometry(null, CurrentPen, _curBrake);
        dc.DrawGeometry(null, CurrentPen, _curSteer);

        // The gap: one polygon and one line, drawn twice through a clip on each side of the zero line, so the color switches
        // exactly where the line crosses zero without computing the crossings.
        var lane = _lanes[LaneDelta];
        var zeroY = lane.Y(0);
        var width = _plotRight - _plotLeft;
        var behind = new Rect(_plotLeft, lane.Top, width, Math.Max(0, zeroY - lane.Top));
        var ahead = new Rect(_plotLeft, zeroY, width, Math.Max(0, lane.Bottom - zeroY));
        DrawClipped(dc, behind, BehindFill, BehindPen);
        DrawClipped(dc, ahead, AheadFill, AheadPen);
    }

    private void DrawClipped(DrawingContext dc, Rect region, Brush fill, Pen pen)
    {
        dc.PushClip(new RectangleGeometry(region));
        dc.DrawGeometry(fill, null, _deltaArea);
        dc.DrawGeometry(null, pen, _deltaLine);
        dc.Pop();
    }

    private void DrawLegend(DrawingContext dc, double dpi)
    {
        // Top right, above the first lane: a short swatch and the name, for each lap.
        var current = _text.Get(InsightsStrings.CompareLegendCurrent, 12, ChartPalette.TextBrush, dpi);
        var best = _text.Get(InsightsStrings.CompareLegendBest, 12, ChartPalette.TextBrush, dpi);
        const double swatch = 18;
        const double gap = 8;
        const double between = 22;
        var x = _plotRight - best.Width - gap - swatch - between - current.Width - gap - swatch;
        const double y = 6;

        dc.DrawLine(CurrentPen, new Point(x, y + 8), new Point(x + swatch, y + 8));
        dc.DrawText(current, new Point(x + swatch + gap, y));
        x += swatch + gap + current.Width + between;
        dc.DrawLine(BestPen, new Point(x, y + 8), new Point(x + swatch, y + 8));
        dc.DrawText(best, new Point(x + swatch + gap, y));
    }

    private void DrawHover(DrawingContext dc, ReadOnlySpan<LapTraceSample> best, ReadOnlySpan<LapTraceSample> current, double dpi, double width)
    {
        if (double.IsNaN(_hoverX) || _hoverX < _plotLeft || _hoverX > _plotRight)
        {
            return;
        }

        var meters = (_hoverX - _plotLeft) / _pxPerMeter;
        var hasCurrent = LapTraceMath.TryInterpolate(current, meters, out var cur);
        var hasBest = LapTraceMath.TryInterpolate(best, meters, out var bst);
        if (!hasCurrent && !hasBest)
        {
            return;
        }

        var x = Snap(_hoverX);
        dc.DrawLine(CrosshairPen, new Point(x, PlotTop), new Point(x, _plotBottom));

        // One marker per lap per lane, so the eye can read the crosshair across the whole stack.
        if (hasBest)
        {
            Marker(dc, x, _lanes[LaneSpeed].Y(bst.SpeedMs * SpeedScale), ChartPalette.NeutralBrush);
            Marker(dc, x, _lanes[LaneThrottle].Y(bst.Throttle), ChartPalette.NeutralBrush);
            Marker(dc, x, _lanes[LaneBrake].Y(bst.Brake), ChartPalette.NeutralBrush);
            Marker(dc, x, _lanes[LaneSteer].Y(bst.Steer), ChartPalette.NeutralBrush);
        }

        double? gap = hasCurrent && hasBest ? cur.TimeSeconds - bst.TimeSeconds : null;
        if (hasCurrent)
        {
            Marker(dc, x, _lanes[LaneSpeed].Y(cur.SpeedMs * SpeedScale), ChartPalette.ColdBrush);
            Marker(dc, x, _lanes[LaneThrottle].Y(cur.Throttle), ChartPalette.ColdBrush);
            Marker(dc, x, _lanes[LaneBrake].Y(cur.Brake), ChartPalette.ColdBrush);
            Marker(dc, x, _lanes[LaneSteer].Y(cur.Steer), ChartPalette.ColdBrush);
            if (gap is { } g)
            {
                Marker(dc, x, _lanes[LaneDelta].Y(g), g <= 0 ? ChartPalette.ThrottleBrush : ChartPalette.BrakeBrush);
            }
        }

        const string none = "-";
        string[] names = [InsightsStrings.LaneSpeed, InsightsStrings.ThrottleLabel, InsightsStrings.BrakeLabel, InsightsStrings.LaneSteering, InsightsStrings.LaneGear];
        string[] currentValues = hasCurrent
            ? [FormatSpeed(cur), ReadoutFormat.Percent(cur.Throttle), ReadoutFormat.Percent(cur.Brake), ReadoutFormat.Steer(cur.Steer), GearFormatter.Format(cur.Gear)]
            : [none, none, none, none, none];
        string[] bestValues = hasBest
            ? [FormatSpeed(bst), ReadoutFormat.Percent(bst.Throttle), ReadoutFormat.Percent(bst.Brake), ReadoutFormat.Steer(bst.Steer), GearFormatter.Format(bst.Gear)]
            : [none, none, none, none, none];

        const double rowHeight = 19;
        const double headerHeight = 54;
        var panelHeight = headerHeight + (rowHeight * (names.Length + 1)) + 12;
        var panelX = x + 14 + ReadoutWidth > width - 4 ? x - 14 - ReadoutWidth : x + 14;
        var panel = new Rect(panelX, PlotTop + 4, ReadoutWidth, panelHeight);
        dc.DrawRoundedRectangle(ReadoutBackground, ReadoutBorder, panel, 10, 10);

        var header = string.Format(
            CultureInfo.CurrentCulture, InsightsStrings.CompareAtFormat,
            (meters * DistanceScale).ToString(DistanceScale < 0.01 ? "0.00" : "0", CultureInfo.CurrentCulture), DistanceUnit);
        _text.Draw(dc, header, panel.X + 14, panel.Y + 10, 12, ChartPalette.MutedBrush, dpi, numeral: true);

        // Column heads, each over its own values: the current lap in the accent, the best lap in the neutral.
        var currentRight = panel.Right - 96;
        var bestRight = panel.Right - 14;
        _text.DrawRight(dc, InsightsStrings.CompareHeadCurrent, currentRight, panel.Y + 32, 11, ChartPalette.ColdBrush, dpi, bold: true);
        _text.DrawRight(dc, InsightsStrings.CompareHeadBest, bestRight, panel.Y + 32, 11, ChartPalette.NeutralBrush, dpi, bold: true);

        for (var i = 0; i < names.Length; i++)
        {
            var rowY = panel.Y + headerHeight + (i * rowHeight);
            _text.Draw(dc, names[i], panel.X + 14, rowY, 12, ChartPalette.MutedBrush, dpi);
            _text.DrawRight(dc, currentValues[i], currentRight, rowY - 1, 13, ChartPalette.TextBrush, dpi, numeral: true);
            _text.DrawRight(dc, bestValues[i], bestRight, rowY - 1, 13, ChartPalette.TextBrush, dpi, numeral: true);
        }

        // The gap, last and colored: it is the answer the other rows explain.
        var gapY = panel.Y + headerHeight + (names.Length * rowHeight) + 4;
        dc.DrawLine(GridPen, new Point(panel.X + 14, gapY - 3), new Point(panel.Right - 14, gapY - 3));
        _text.Draw(dc, InsightsStrings.CompareTileGap, panel.X + 14, gapY, 12, ChartPalette.MutedBrush, dpi);
        var gapBrush = gap is null ? ChartPalette.TextBrush : gap.Value > 0.0005 ? ChartPalette.BrakeBrush : gap.Value < -0.0005 ? ChartPalette.ThrottleBrush : ChartPalette.TextBrush;
        _text.DrawRight(dc, gap is { } shown ? LapTimeFormatter.FormatDelta(shown) : none, bestRight, gapY - 1, 13, gapBrush, dpi, numeral: true);
    }

    private string FormatSpeed(LapTraceSample s) => (s.SpeedMs * SpeedScale).ToString("0", CultureInfo.CurrentCulture) + " " + SpeedUnit;

    private static string FormatSeconds(double seconds) => seconds.ToString(seconds % 1 == 0 ? "0" : "0.0#", CultureInfo.CurrentCulture);

    private static void Marker(DrawingContext dc, double x, double y, Brush brush)
    {
        // Dark ring + colored core: visible over any line or fill underneath.
        dc.DrawEllipse(brush, ReadoutBorder, new Point(x, y), 3.5, 3.5);
    }

    /// <summary>Rounds to the pixel center so 1 px lines stay crisp instead of blurring over two pixels.</summary>
    private static double Snap(double v) => Math.Floor(v) + 0.5;
}
