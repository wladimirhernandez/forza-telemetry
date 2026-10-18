using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App.Controls;

/// <summary>
/// Motorsport-style stacked traces over a shared time axis: speed, RPM (with a redline band), throttle and brake
/// overlaid, steering centered on zero and the gear as a staircase, with a hover crosshair that reads every lane at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Data flow.</b> The view model owns one reusable <see cref="TelemetrySample"/> array that it refills from the
/// ring buffer on every refresh, then bumps <see cref="DataVersion"/>. The control never copies the data; it reads the
/// array directly. Changing <see cref="DataVersion"/> is the ONLY thing that marks the cached geometry stale, which is
/// what "rebuild only when the data changes" means here: when the game is paused no samples arrive, the version stays
/// put and the chart costs nothing.
/// </para>
/// <para>
/// <b>Drawing.</b> Each lane's line is one <see cref="StreamGeometry"/>: a compact, immutable list of figures that WPF
/// can cache on the GPU side. Building it needs the points in pixel space, so the series is first decimated to the
/// plot width (<see cref="ChartMath.Decimate"/>): 3600 samples never become more than ~4 points per pixel column.
/// Geometries are frozen after building and reused by every render until the next rebuild. Resizing the window or
/// changing the time window also rebuilds, because both change the pixel mapping.
/// </para>
/// <para>
/// <b>Hover.</b> Mouse moves only store the pointer position and ask for a re-render (no geometry work). The render
/// then finds the nearest sample by binary search and draws the crosshair and readout on top of the cached lanes.
/// The pointer is stored in pixels, not as a sample index, so the readout follows the data while it scrolls.
/// </para>
/// </remarks>
public sealed class TraceChart : FrameworkElement
{
    private const double GutterLeft = 62;
    private const double GutterRight = 14;
    private const double PlotTop = 6;
    private const double AxisHeight = 24;
    private const double LaneGap = 10;
    private const double RedlineFraction = 0.95;
    private const double ReadoutWidth = 176;

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(TelemetrySample[]), typeof(TraceChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public static readonly DependencyProperty SampleCountProperty = DependencyProperty.Register(
        nameof(SampleCount), typeof(int), typeof(TraceChart),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    /// <summary>Bump to tell the chart the samples array was refilled in place.</summary>
    public static readonly DependencyProperty DataVersionProperty = DependencyProperty.Register(
        nameof(DataVersion), typeof(int), typeof(TraceChart),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    /// <summary>Visible time window in seconds (10, 30 or 60 in the UI; any positive value works).</summary>
    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(int), typeof(TraceChart),
        new FrameworkPropertyMetadata(30, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged),
        value => (int)value > 0);

    /// <summary>Multiplier from meters per second to the displayed speed unit (3.6 for km/h).</summary>
    public static readonly DependencyProperty SpeedScaleProperty = DependencyProperty.Register(
        nameof(SpeedScale), typeof(double), typeof(TraceChart),
        new FrameworkPropertyMetadata(3.6, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged),
        value => (double)value > 0);

    public static readonly DependencyProperty SpeedUnitProperty = DependencyProperty.Register(
        nameof(SpeedUnit), typeof(string), typeof(TraceChart),
        new FrameworkPropertyMetadata("km/h", FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly ChartText _text = new();
    private readonly List<Point> _points = new(2048);
    private readonly Lane[] _lanes = new Lane[5];

    private bool _dirty = true;
    private Size _builtFor;
    private int _start;
    private int _end;
    private double _originMs;
    private double _pxPerMs;
    private double _plotLeft;
    private double _plotRight;
    private double _plotBottom;
    private double _hoverX = double.NaN;

    private StreamGeometry? _speedLine, _speedFill, _rpmLine, _throttleLine, _throttleFill, _brakeLine, _brakeFill, _steerLine, _gearLine;
    private Rect _redlineBand;

    // Pens and brushes are built once (frozen). Fills are the line color at low alpha.
    private static readonly Pen GridPen = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Hairline, 0.55), 1);
    private static readonly Pen LaneEdgePen = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Hairline), 1);
    private static readonly Pen CrosshairPen = ChartPalette.Pen(ChartPalette.Solid(ChartPalette.Text, 0.55), 1);
    private static readonly Pen SpeedPen = ChartPalette.Pen(ChartPalette.TextBrush, 1.6);
    private static readonly Pen RpmPen = ChartPalette.Pen(ChartPalette.NeutralBrush, 1.4);
    private static readonly Pen ThrottlePen = ChartPalette.Pen(ChartPalette.ThrottleBrush, 1.5);
    private static readonly Pen BrakePen = ChartPalette.Pen(ChartPalette.BrakeBrush, 1.5);
    private static readonly Pen SteerPen = ChartPalette.Pen(ChartPalette.ColdBrush, 1.4);
    private static readonly Pen GearPen = ChartPalette.Pen(ChartPalette.TextBrush, 1.4);
    private static readonly Brush SpeedFillBrush = FadeDown(ChartPalette.Text, 0.16);
    private static readonly Brush ThrottleFillBrush = FadeDown(ChartPalette.Throttle, 0.30);
    private static readonly Brush BrakeFillBrush = FadeDown(ChartPalette.Brake, 0.30);
    private static readonly Brush RedlineBrush = ChartPalette.Solid(ChartPalette.Brake, 0.16);
    private static readonly Brush TitleBackdrop = ChartPalette.Solid(ChartPalette.Card, 0.8);
    private static readonly Brush ReadoutBackground = ChartPalette.Solid(ChartPalette.CardRaised, 0.96);
    private static readonly Pen ReadoutBorder = ChartPalette.Pen(ChartPalette.HairlineBrush, 1);

    public TraceChart()
    {
        // The control paints its own transparent surface in OnRender; this keeps the mouse hit-testing simple and cheap.
        Focusable = false;
        ClipToBounds = true;
    }

    public TelemetrySample[]? Samples
    {
        get => (TelemetrySample[]?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public int SampleCount
    {
        get => (int)GetValue(SampleCountProperty);
        set => SetValue(SampleCountProperty, value);
    }

    public int DataVersion
    {
        get => (int)GetValue(DataVersionProperty);
        set => SetValue(DataVersionProperty, value);
    }

    public int WindowSeconds
    {
        get => (int)GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
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

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TraceChart)d)._dirty = true;

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
        if (width < 200 || height < 140)
        {
            return;
        }

        // Invisible, but it makes the whole control hit-testable so MouseMove fires over empty plot area too.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var span = Samples is { } array ? array.AsSpan(0, Math.Clamp(SampleCount, 0, array.Length)) : default;

        if (_dirty || _builtFor != new Size(width, height))
        {
            Rebuild(span, width, height);
        }

        DrawFrame(dc, span, dpi, width, height);
        if (!span.IsEmpty)
        {
            DrawLanes(dc);
            DrawHover(dc, span, dpi, width);
        }
    }

    // ---- Geometry ---------------------------------------------------------------------------------

    private readonly record struct Lane(double Top, double Bottom, double Min, double Max)
    {
        public double Y(double value) => Bottom - (Math.Clamp(value, Min, Max) - Min) / (Max - Min) * (Bottom - Top);
    }

    private void Rebuild(ReadOnlySpan<TelemetrySample> samples, double width, double height)
    {
        _dirty = false;
        _builtFor = new Size(width, height);
        _plotLeft = GutterLeft;
        _plotRight = width - GutterRight;
        _plotBottom = height - AxisHeight;

        // Lanes share the plot height by weight; the gaps between them carry the separators.
        double[] weights = [1.5, 1.25, 1.15, 0.95, 0.95];
        var available = _plotBottom - PlotTop - LaneGap * (weights.Length - 1);
        var total = weights.Sum();
        var y = PlotTop;

        var windowMs = WindowSeconds * 1000.0;
        _start = ChartMath.WindowStart(samples, (uint)windowMs);
        _end = samples.Length;
        _originMs = samples.IsEmpty ? 0 : samples[^1].TimeMs - windowMs;
        _pxPerMs = (_plotRight - _plotLeft) / windowMs;

        // Axis ranges come from the visible data so the traces use the lane height well, snapped to round numbers.
        double maxSpeed = 0, maxRpm = 0, redline = 0, maxGear = 0, minGear = 0;
        for (var i = _start; i < _end; i++)
        {
            maxSpeed = Math.Max(maxSpeed, samples[i].SpeedMs * SpeedScale);
            maxRpm = Math.Max(maxRpm, samples[i].Rpm);
            redline = Math.Max(redline, samples[i].MaxRpm);
            var level = ChartMath.GearLevel(samples[i].Gear);
            maxGear = Math.Max(maxGear, level);
            minGear = Math.Min(minGear, level);
        }

        var speedAxisMax = ChartMath.NiceCeiling(Math.Max(maxSpeed, SpeedScale * 10), 3).Max;
        var rpmAxisMax = Math.Max(2000, Math.Ceiling(Math.Max(maxRpm, redline) / 1000) * 1000);
        var gearAxisMax = Math.Max(5, maxGear);

        double[,] ranges = { { 0, speedAxisMax }, { 0, rpmAxisMax }, { 0, 1 }, { -1, 1 }, { minGear, gearAxisMax } };
        for (var i = 0; i < _lanes.Length; i++)
        {
            var laneHeight = available * weights[i] / total;
            _lanes[i] = new Lane(y, y + laneHeight, ranges[i, 0], ranges[i, 1]);
            y += laneHeight + LaneGap;
        }

        _redlineBand = redline > 0
            ? new Rect(_plotLeft, _lanes[1].Y(redline), _plotRight - _plotLeft, Math.Max(1, _lanes[1].Y(redline * RedlineFraction) - _lanes[1].Y(redline)))
            : Rect.Empty;

        var scale = SpeedScale;
        (_speedLine, _speedFill) = BuildSeries(samples, s => s.SpeedMs * scale, _lanes[0], fill: true);
        (_rpmLine, _) = BuildSeries(samples, s => s.Rpm, _lanes[1], fill: false);
        (_throttleLine, _throttleFill) = BuildSeries(samples, s => s.Throttle, _lanes[2], fill: true);
        (_brakeLine, _brakeFill) = BuildSeries(samples, s => s.Brake, _lanes[2], fill: true);
        (_steerLine, _) = BuildSeries(samples, s => s.Steer, _lanes[3], fill: false);
        (_gearLine, _) = BuildSeries(samples, s => ChartMath.GearLevel(s.Gear), _lanes[4], fill: false, steps: true);
    }

    /// <summary>Builds the stroked line (and optionally the filled area under it) for one series, breaking at gaps.</summary>
    private (StreamGeometry? Line, StreamGeometry? Fill) BuildSeries(
        ReadOnlySpan<TelemetrySample> samples, Func<TelemetrySample, double> value, Lane lane, bool fill, bool steps = false)
    {
        if (_end - _start < 1)
        {
            return (null, null);
        }

        var line = new StreamGeometry();
        var area = fill ? new StreamGeometry() : null;
        using (var lineContext = line.Open())
        using (var areaContext = area?.Open())
        {
            var runStart = _start;
            for (var i = _start + 1; i <= _end; i++)
            {
                // A run ends at a time gap (pause/menu), at a clock reset, or at the end of the data.
                var endsRun = i == _end
                    || samples[i].TimeMs < samples[i - 1].TimeMs
                    || samples[i].TimeMs - samples[i - 1].TimeMs > ChartMath.GapMs;
                if (!endsRun)
                {
                    continue;
                }

                FillPoints(samples, runStart, i, value, lane, steps);
                EmitRun(lineContext, areaContext, lane.Bottom);
                runStart = i;
            }
        }

        line.Freeze();
        area?.Freeze();
        return (line, area);
    }

    /// <summary>Fills <see cref="_points"/> with the pixel-space points of one contiguous run.</summary>
    private void FillPoints(ReadOnlySpan<TelemetrySample> samples, int from, int to, Func<TelemetrySample, double> value, Lane lane, bool steps)
    {
        if (steps)
        {
            // A staircase: hold the previous level until the exact moment of the change, then jump. Only changes
            // produce points, so no decimation is needed.
            _points.Clear();
            var previousY = lane.Y(value(samples[from]));
            _points.Add(new Point(TimeToX(samples[from].TimeMs), previousY));
            for (var i = from + 1; i < to; i++)
            {
                var y = lane.Y(value(samples[i]));
                if (Math.Abs(y - previousY) > 0.01)
                {
                    var x = TimeToX(samples[i].TimeMs);
                    _points.Add(new Point(x, previousY));
                    _points.Add(new Point(x, y));
                    previousY = y;
                }
            }

            _points.Add(new Point(TimeToX(samples[to - 1].TimeMs), previousY));
            return;
        }

        ChartMath.Decimate(samples, from, to, value, _originMs, _pxPerMs, _points);
        for (var i = 0; i < _points.Count; i++)
        {
            _points[i] = new Point(_plotLeft + _points[i].X, lane.Y(_points[i].Y));
        }
    }

    private double TimeToX(double timeMs) => _plotLeft + (timeMs - _originMs) * _pxPerMs;

    private void EmitRun(StreamGeometryContext line, StreamGeometryContext? area, double baseline)
    {
        if (_points.Count == 0)
        {
            return;
        }

        if (_points.Count == 1)
        {
            // A lone sample would draw nothing; give it a hair of length so round caps render a dot.
            _points.Add(new Point(_points[0].X + 0.1, _points[0].Y));
        }

        line.BeginFigure(_points[0], isFilled: false, isClosed: false);
        line.PolyLineTo(_points, isStroked: true, isSmoothJoin: true);

        if (area is not null)
        {
            area.BeginFigure(new Point(_points[0].X, baseline), isFilled: true, isClosed: true);
            area.PolyLineTo(_points, isStroked: false, isSmoothJoin: false);
            area.LineTo(new Point(_points[^1].X, baseline), isStroked: false, isSmoothJoin: false);
        }
    }

    // ---- Drawing ----------------------------------------------------------------------------------

    private void DrawFrame(DrawingContext dc, ReadOnlySpan<TelemetrySample> samples, double dpi, double width, double height)
    {
        var plotWidth = _plotRight - _plotLeft;
        var windowSeconds = (double)WindowSeconds;

        // Lane separators, and the vertical time grid shared by every lane.
        var step = ChartMath.NiceStep(windowSeconds, 5);
        for (var seconds = 0.0; seconds <= windowSeconds + 1e-6; seconds += step)
        {
            var x = Snap(_plotRight - seconds / windowSeconds * plotWidth);
            if (seconds > 0)
            {
                dc.DrawLine(GridPen, new Point(x, PlotTop), new Point(x, _plotBottom));
            }

            var label = seconds == 0 ? InsightsStrings.TimeNow : "−" + seconds.ToString("0", CultureInfo.CurrentCulture);
            _text.DrawCentered(dc, label, Math.Min(x, _plotRight - 8), _plotBottom + 6, 11, ChartPalette.MutedBrush, dpi, numeral: true);
        }

        _text.DrawRight(dc, "s", _plotLeft - 26, _plotBottom + 6, 11, ChartPalette.MutedBrush, dpi);

        string[] titles = [string.Format(CultureInfo.CurrentCulture, InsightsStrings.LaneSpeedUnitFormat, SpeedUnit), InsightsStrings.LaneRpm, InsightsStrings.LanePedals, InsightsStrings.LaneSteering, InsightsStrings.LaneGear];
        for (var i = 0; i < _lanes.Length; i++)
        {
            var lane = _lanes[i];
            dc.DrawLine(LaneEdgePen, new Point(_plotLeft, Snap(lane.Bottom)), new Point(_plotRight, Snap(lane.Bottom)));
            dc.DrawLine(GridPen, new Point(_plotLeft, Snap((lane.Top + lane.Bottom) / 2)), new Point(_plotRight, Snap((lane.Top + lane.Bottom) / 2)));
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
            case 0: top = lane.Max.ToString("0", CultureInfo.CurrentCulture); bottom = "0"; break;
            case 1: top = FormatRpm(lane.Max); bottom = "0"; break;
            case 2: top = "100"; bottom = "0"; break;
            case 3: top = InsightsStrings.SteerRightShort; bottom = InsightsStrings.SteerLeftShort; break;
            default: top = FormatGear(lane.Max); bottom = FormatGear(lane.Min); break;
        }

        _text.DrawRight(dc, top, right, lane.Top - 1, 11, ChartPalette.MutedBrush, dpi, numeral: true);
        _text.DrawRight(dc, bottom, right, lane.Bottom - 13, 11, ChartPalette.MutedBrush, dpi, numeral: true);
        if (laneIndex == 3)
        {
            _text.DrawRight(dc, "0", right, (lane.Top + lane.Bottom) / 2 - 7, 11, ChartPalette.MutedBrush, dpi, numeral: true);
        }
    }

    private void DrawLanes(DrawingContext dc)
    {
        if (!_redlineBand.IsEmpty)
        {
            dc.DrawRectangle(RedlineBrush, null, _redlineBand);
        }

        dc.DrawGeometry(SpeedFillBrush, null, _speedFill);
        dc.DrawGeometry(null, SpeedPen, _speedLine);
        dc.DrawGeometry(null, RpmPen, _rpmLine);
        dc.DrawGeometry(BrakeFillBrush, null, _brakeFill);
        dc.DrawGeometry(ThrottleFillBrush, null, _throttleFill);
        dc.DrawGeometry(null, BrakePen, _brakeLine);
        dc.DrawGeometry(null, ThrottlePen, _throttleLine);
        dc.DrawGeometry(null, SteerPen, _steerLine);
        dc.DrawGeometry(null, GearPen, _gearLine);
    }

    private void DrawHover(DrawingContext dc, ReadOnlySpan<TelemetrySample> samples, double dpi, double width)
    {
        if (double.IsNaN(_hoverX) || _hoverX < _plotLeft || _hoverX > _plotRight || _end <= _start)
        {
            return;
        }

        var timeMs = _originMs + (_hoverX - _plotLeft) / _pxPerMs;
        var index = ChartMath.NearestIndex(samples, _start, _end, timeMs);
        if (index < 0 || Math.Abs(samples[index].TimeMs - timeMs) > 400)
        {
            return;
        }

        var s = samples[index];
        var x = Snap(TimeToX(s.TimeMs));
        dc.DrawLine(CrosshairPen, new Point(x, PlotTop), new Point(x, _plotBottom));

        // One marker per series, on its own lane, so the eye can read the crosshair across the whole stack.
        Marker(dc, x, _lanes[0].Y(s.SpeedMs * SpeedScale), ChartPalette.TextBrush);
        Marker(dc, x, _lanes[1].Y(s.Rpm), ChartPalette.NeutralBrush);
        Marker(dc, x, _lanes[2].Y(s.Throttle), ChartPalette.ThrottleBrush);
        Marker(dc, x, _lanes[2].Y(s.Brake), ChartPalette.BrakeBrush);
        Marker(dc, x, _lanes[3].Y(s.Steer), ChartPalette.ColdBrush);
        Marker(dc, x, _lanes[4].Y(ChartMath.GearLevel(s.Gear)), ChartPalette.TextBrush);

        var secondsAgo = (samples[^1].TimeMs - s.TimeMs) / 1000.0;
        string[] names = [InsightsStrings.LaneSpeed, InsightsStrings.LaneRpm, InsightsStrings.ThrottleLabel, InsightsStrings.BrakeLabel, InsightsStrings.LaneSteering, InsightsStrings.LaneGear];
        string[] values =
        [
            (s.SpeedMs * SpeedScale).ToString("0", CultureInfo.CurrentCulture) + " " + SpeedUnit,
            s.Rpm.ToString("0", CultureInfo.CurrentCulture),
            ReadoutFormat.Percent(s.Throttle),
            ReadoutFormat.Percent(s.Brake),
            ReadoutFormat.Steer(s.Steer),
            GearFormatter.Format(s.Gear),
        ];
        Brush[] swatches = [ChartPalette.TextBrush, ChartPalette.NeutralBrush, ChartPalette.ThrottleBrush, ChartPalette.BrakeBrush, ChartPalette.ColdBrush, ChartPalette.TextBrush];

        const double rowHeight = 19;
        var panelHeight = 36 + rowHeight * names.Length;
        var panelX = x + 14 + ReadoutWidth > width - 4 ? x - 14 - ReadoutWidth : x + 14;
        var panel = new Rect(panelX, PlotTop + 4, ReadoutWidth, panelHeight);
        dc.DrawRoundedRectangle(ReadoutBackground, ReadoutBorder, panel, 10, 10);

        var header = secondsAgo < 0.05 ? InsightsStrings.TimeNow : string.Format(CultureInfo.CurrentCulture, InsightsStrings.SecondsAgoFormat, secondsAgo);
        _text.Draw(dc, header, panel.X + 14, panel.Y + 10, 12, ChartPalette.MutedBrush, dpi, numeral: true);
        for (var i = 0; i < names.Length; i++)
        {
            var rowY = panel.Y + 34 + i * rowHeight;
            dc.DrawEllipse(swatches[i], null, new Point(panel.X + 18, rowY + 8), 3, 3);
            _text.Draw(dc, names[i], panel.X + 30, rowY, 12, ChartPalette.MutedBrush, dpi);
            _text.DrawRight(dc, values[i], panel.Right - 14, rowY - 1, 13, ChartPalette.TextBrush, dpi, numeral: true);
        }
    }

    private static void Marker(DrawingContext dc, double x, double y, Brush brush)
    {
        // Dark ring + colored core: visible over any line or fill underneath.
        dc.DrawEllipse(brush, ReadoutBorder, new Point(x, y), 3.5, 3.5);
    }

    private static string FormatRpm(double rpm) => rpm >= 1000
        ? (rpm / 1000).ToString(rpm % 1000 == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + "k"
        : rpm.ToString("0", CultureInfo.CurrentCulture);

    private static string FormatGear(double level) => level switch
    {
        < 0 => "R",
        0 => "N",
        _ => level.ToString("0", CultureInfo.CurrentCulture)
    };

    /// <summary>Rounds to the pixel center so 1 px lines stay crisp instead of blurring over two pixels.</summary>
    private static double Snap(double v) => Math.Floor(v) + 0.5;
}
