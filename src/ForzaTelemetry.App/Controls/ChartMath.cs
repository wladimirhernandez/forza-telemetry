using System.Windows;
using System.Windows.Media;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App.Controls;

/// <summary>
/// The pure math behind the chart controls (no drawing, no WPF elements), kept separate so it can be unit tested:
/// axis ticks, decimation to pixel width, time-window selection, nearest-sample lookup and gear levels.
/// </summary>
public static class ChartMath
{
    /// <summary>A gap between two samples longer than this (game time) is a pause or a menu, not driving: the line breaks.</summary>
    public const uint GapMs = 500;

    /// <summary>
    /// A "nice" axis step (1, 2 or 5 times a power of ten) so that about <paramref name="targetTicks"/> ticks cover
    /// <paramref name="range"/>. Round tick values read as a chart; raw divisions (37.5, 75, 112.5) read as noise.
    /// </summary>
    public static double NiceStep(double range, int targetTicks)
    {
        if (!double.IsFinite(range) || range <= 0 || targetTicks < 1)
        {
            return 1;
        }

        var rough = range / targetTicks;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var fraction = rough / magnitude;
        var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    /// <summary>
    /// The smallest axis maximum that is a whole number of nice steps and is at least <paramref name="value"/>,
    /// using about <paramref name="divisions"/> divisions.
    /// </summary>
    /// <returns>The axis maximum and the step between ticks.</returns>
    public static (double Max, double Step) NiceCeiling(double value, int divisions)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return (divisions, 1);
        }

        var step = NiceStep(value, divisions);
        return (Math.Ceiling(value / step - 1e-9) * step, step);
    }

    /// <summary>
    /// The length the distance axis spans: <paramref name="baseLengthMeters"/> (the reference lap), widened in steps of a tenth of it
    /// only when <paramref name="currentLengthMeters"/> runs past it. A scale that followed the growing lap metre by metre would
    /// move everything drawn on it (the reference lap included) every time; this one changes at most ten times for a lap twice as long.
    /// </summary>
    public static double StableAxisMeters(double baseLengthMeters, double currentLengthMeters)
    {
        var baseLength = double.IsFinite(baseLengthMeters) ? Math.Max(1, baseLengthMeters) : 1;
        if (!(currentLengthMeters > baseLength) || !double.IsFinite(currentLengthMeters))
        {
            return baseLength;
        }

        var step = baseLength / 10;
        return baseLength + (Math.Ceiling(((currentLengthMeters - baseLength) / step) - 1e-9) * step);
    }

    /// <summary>
    /// Two value axes that share ONE grid: the same number of divisions for both, each with a round step (1, 2 or 5
    /// times a power of ten) whose top is at least the given value.
    /// </summary>
    /// <remarks>
    /// The dyno chart draws power (left) and torque (right) over the same horizontal gridlines, so both axes must have
    /// the same number of divisions. Fixing it at five wastes space when the peaks land just above a "round" step (a
    /// 307 kW peak on a 500 kW axis uses 60% of the height), so the division count is searched between 4 and 8 and the
    /// one that wastes the least height on the worse of the two axes wins (ties prefer five, the calmest grid).
    /// </remarks>
    /// <returns>The shared division count and the step of each axis.</returns>
    public static (int Divisions, double StepA, double StepB) FitSharedAxes(double a, double b)
    {
        a = double.IsFinite(a) && a > 0 ? a : 1;
        b = double.IsFinite(b) && b > 0 ? b : 1;

        var best = (Divisions: 5, StepA: SmallestStep(a, 5), StepB: SmallestStep(b, 5));
        var bestWaste = Math.Max(best.StepA * 5 / a, best.StepB * 5 / b);
        foreach (var divisions in new[] { 6, 4, 7, 8 })
        {
            var stepA = SmallestStep(a, divisions);
            var stepB = SmallestStep(b, divisions);
            var waste = Math.Max(stepA * divisions / a, stepB * divisions / b);
            if (waste < bestWaste - 1e-9)
            {
                best = (divisions, stepA, stepB);
                bestWaste = waste;
            }
        }

        return best;
    }

    private static double SmallestStep(double value, int divisions)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value / divisions)));
        foreach (var mantissa in new[] { 1.0, 2.0, 5.0, 10.0 })
        {
            if (mantissa * magnitude * divisions >= value - 1e-9)
            {
                return mantissa * magnitude;
            }
        }

        return 10 * magnitude;
    }

    /// <summary>
    /// Index of the first sample inside the last <paramref name="windowMs"/> of game time, walking back from the
    /// newest sample. Stops early where time goes backwards (the game restarted its clock), so a new session never
    /// draws a line back through the old one.
    /// </summary>
    public static int WindowStart(ReadOnlySpan<TelemetrySample> samples, uint windowMs)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        var newest = samples[^1].TimeMs;
        var start = samples.Length - 1;
        while (start > 0)
        {
            var previous = samples[start - 1].TimeMs;
            if (previous > samples[start].TimeMs || newest - previous > windowMs)
            {
                break;
            }

            start--;
        }

        return start;
    }

    /// <summary>
    /// Index of the sample whose time is closest to <paramref name="timeMs"/> within [<paramref name="start"/>, end).
    /// Binary search: the window is sorted by time, and this runs on every mouse move.
    /// </summary>
    public static int NearestIndex(ReadOnlySpan<TelemetrySample> samples, int start, int end, double timeMs)
    {
        if (end <= start)
        {
            return -1;
        }

        int lo = start, hi = end - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (samples[mid].TimeMs < timeMs)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        // lo is the first sample at or after the target; the one before it may be closer.
        if (lo > start && Math.Abs(samples[lo - 1].TimeMs - timeMs) <= Math.Abs(samples[lo].TimeMs - timeMs))
        {
            lo--;
        }

        return lo;
    }

    /// <summary>
    /// A gear byte as a chart level: reverse is -1, neutral 0 and forward gears their own number, so the gear lane
    /// reads as a staircase going up with the gears.
    /// </summary>
    public static double GearLevel(byte gear) => gear switch
    {
        0 => -1,
        11 => 0,
        _ => gear
    };

    /// <summary>
    /// Min/max decimation of a time series to pixel resolution. Output points are (x in pixels, value), in time order.
    /// </summary>
    /// <remarks>
    /// A 60 s window holds 3600 samples but the plot is ~900 px wide: drawing every sample builds a polyline whose
    /// extra points land on pixels already painted. The classic fix is to keep, per pixel column, only the first,
    /// lowest, highest and last sample: the line looks IDENTICAL (no spike is ever lost, which plain "take every Nth
    /// sample" would do) with at most four points per column. When data is sparser than one sample per pixel nothing is
    /// dropped. <paramref name="output"/> is cleared and refilled so callers reuse one list and allocate nothing.
    /// </remarks>
    /// <param name="samples">Samples sorted by time.</param>
    /// <param name="from">First sample of the run (inclusive).</param>
    /// <param name="to">End of the run (exclusive).</param>
    /// <param name="value">Selects the plotted value of a sample.</param>
    /// <param name="originMs">Game time at x = 0.</param>
    /// <param name="pixelsPerMs">Horizontal scale.</param>
    /// <param name="output">Receives the points.</param>
    public static void Decimate(
        ReadOnlySpan<TelemetrySample> samples,
        int from,
        int to,
        Func<TelemetrySample, double> value,
        double originMs,
        double pixelsPerMs,
        List<Point> output)
    {
        output.Clear();
        if (to <= from)
        {
            return;
        }

        var column = int.MinValue;
        int first = 0, min = 0, max = 0, last = 0;
        double minValue = 0, maxValue = 0;

        for (var i = from; i < to; i++)
        {
            var v = value(samples[i]);
            var c = (int)Math.Floor((samples[i].TimeMs - originMs) * pixelsPerMs);
            if (c != column)
            {
                if (column != int.MinValue)
                {
                    Flush(samples, first, min, max, last, value, originMs, pixelsPerMs, output);
                }

                column = c;
                first = min = max = last = i;
                minValue = maxValue = v;
                continue;
            }

            last = i;
            if (v < minValue)
            {
                minValue = v;
                min = i;
            }

            if (v > maxValue)
            {
                maxValue = v;
                max = i;
            }
        }

        Flush(samples, first, min, max, last, value, originMs, pixelsPerMs, output);
    }

    private static void Flush(
        ReadOnlySpan<TelemetrySample> samples, int first, int min, int max, int last,
        Func<TelemetrySample, double> value, double originMs, double pixelsPerMs, List<Point> output)
    {
        // Up to four distinct indices, emitted in time order (first <= {min,max} <= last).
        Span<int> indices = [first, Math.Min(min, max), Math.Max(min, max), last];
        var previous = -1;
        foreach (var index in indices)
        {
            if (index != previous)
            {
                output.Add(new Point((samples[index].TimeMs - originMs) * pixelsPerMs, value(samples[index])));
                previous = index;
            }
        }
    }
}

/// <summary>
/// The speed color ramp shared by the map path and its legend: Cold (slow) to Throttle (cruising) to Warm (fast) to
/// Brake (flat out).
/// </summary>
/// <remarks>
/// Piecewise linear interpolation between four token colors, done in plain sRGB. A perceptually uniform space would
/// be smoother, but with four well-separated hues the difference is invisible and the math stays one line per channel.
/// </remarks>
public static class SpeedRamp
{
    private static readonly (double At, Color Color)[] Stops =
    [
        (0.00, ChartPalette.Cold),
        (0.40, ChartPalette.Throttle),
        (0.75, ChartPalette.Warm),
        (1.00, ChartPalette.Brake),
    ];

    /// <summary>Position of <paramref name="value"/> in [min, max] as 0..1; a flat range maps to the middle of the ramp.</summary>
    public static double Normalize(double value, double min, double max)
    {
        if (!double.IsFinite(value) || !double.IsFinite(min) || !double.IsFinite(max) || max - min < 1e-9)
        {
            return 0.5;
        }

        return Math.Clamp((value - min) / (max - min), 0, 1);
    }

    /// <summary>The ramp color at <paramref name="t"/> (clamped to 0..1).</summary>
    public static Color Evaluate(double t)
    {
        t = Math.Clamp(double.IsNaN(t) ? 0 : t, 0, 1);
        for (var i = 1; i < Stops.Length; i++)
        {
            if (t <= Stops[i].At)
            {
                var (a, b) = (Stops[i - 1], Stops[i]);
                return Lerp(a.Color, b.Color, (t - a.At) / (b.At - a.At));
            }
        }

        return Stops[^1].Color;
    }

    /// <summary>Quantizes <paramref name="t"/> to one of <paramref name="buckets"/> colors (0..buckets-1).</summary>
    public static int Bucket(double t, int buckets) =>
        Math.Clamp((int)(Math.Clamp(t, 0, 1) * buckets), 0, buckets - 1);

    /// <summary>Linear interpolation between two colors, channel by channel.</summary>
    public static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte Channel(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(Channel(a.A, b.A), Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B));
    }
}

/// <summary>A world-to-screen mapping for the map: <c>screenX = x * Scale + OffsetX</c>, <c>screenY = OffsetY - z * Scale</c> (north up).</summary>
public readonly record struct MapViewport(double Scale, double OffsetX, double OffsetY)
{
    /// <summary>Zoom limits relative to nothing in particular: keeps the map from collapsing to a dot or exploding.</summary>
    public const double MaxZoomFactor = 60;

    /// <summary>
    /// Fits a world rectangle into a pixel area with padding, preserving aspect ratio (one scale for both axes, so a
    /// corner stays a corner) and centering the leftover space.
    /// </summary>
    public static MapViewport Fit(double minX, double maxX, double minZ, double maxZ, double width, double height, double padding)
    {
        var availableW = Math.Max(1, width - 2 * padding);
        var availableH = Math.Max(1, height - 2 * padding);
        var spanX = Math.Max(maxX - minX, 1e-3);
        var spanZ = Math.Max(maxZ - minZ, 1e-3);

        // A single point or a perfectly straight line has a zero span on one axis; the other axis decides.
        // A floor of 20 m per span keeps the first few meters of driving from zooming absurdly far in.
        spanX = Math.Max(spanX, 20);
        spanZ = Math.Max(spanZ, 20);

        var scale = Math.Min(availableW / spanX, availableH / spanZ);
        var centerX = (minX + maxX) / 2;
        var centerZ = (minZ + maxZ) / 2;
        return new MapViewport(scale, width / 2 - centerX * scale, height / 2 + centerZ * scale);
    }

    /// <summary>World to screen.</summary>
    public Point ToScreen(double x, double z) => new(x * Scale + OffsetX, OffsetY - z * Scale);

    /// <summary>Zooms by <paramref name="factor"/> keeping the world point under <paramref name="anchor"/> still (zoom to cursor).</summary>
    public MapViewport ZoomAt(Point anchor, double factor, double minScale, double maxScale)
    {
        // A view that was never fitted (scale 0) or a degenerate gesture must not turn offsets into NaN/Infinity.
        if (!(Scale > 0) || !double.IsFinite(Scale) || !double.IsFinite(factor) || factor <= 0
            || !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y) || !(minScale > 0) || minScale > maxScale)
        {
            return this;
        }

        var newScale = Math.Clamp(Scale * factor, minScale, maxScale);
        var applied = newScale / Scale;
        return new MapViewport(
            newScale,
            anchor.X - (anchor.X - OffsetX) * applied,
            anchor.Y - (anchor.Y - OffsetY) * applied);
    }

    /// <summary>Moves the view by a screen-space delta (drag to pan).</summary>
    public MapViewport Pan(double dx, double dy) =>
        double.IsFinite(dx) && double.IsFinite(dy) && double.IsFinite(OffsetX + dx) && double.IsFinite(OffsetY + dy)
            ? this with { OffsetX = OffsetX + dx, OffsetY = OffsetY + dy }
            : this;
}

/// <summary>
/// Pan/zoom state of the map: the current viewport, whether the user took over from auto-fit, and the auto-fit scale
/// the zoom limits are relative to. A plain object so the view model can own it (it outlives the view) and reset it.
/// </summary>
public sealed class MapViewState
{
    public MapViewport Viewport { get; set; }

    /// <summary>True once the user zoomed or panned; auto-fit then stops until <see cref="Reset"/> or a double-click.</summary>
    public bool Manual { get; set; }

    public double FitScale { get; set; } = 1;

    /// <summary>True when <see cref="Viewport"/> has been fitted at least once and holds finite numbers.</summary>
    public bool HasUsableView =>
        Viewport.Scale > 0 && double.IsFinite(Viewport.Scale) && double.IsFinite(Viewport.OffsetX)
        && double.IsFinite(Viewport.OffsetY) && FitScale > 0 && double.IsFinite(FitScale);

    /// <summary>Back to auto-fit (a cleared path or a new session).</summary>
    public void Reset()
    {
        Viewport = default;
        Manual = false;
        FitScale = 1;
    }
}

/// <summary>Summary numbers of a driven path.</summary>
/// <param name="DistanceMeters">Path length (chords between stored points; segments across jumps excluded).</param>
/// <param name="MinSpeedMs">Slowest stored point, m/s (0 when empty).</param>
/// <param name="MaxSpeedMs">Fastest stored point, m/s (0 when empty).</param>
/// <param name="BrakingZones">Number of separate braking zones (a braking point after a non-braking one).</param>
public readonly record struct TrackStats(double DistanceMeters, double MinSpeedMs, double MaxSpeedMs, int BrakingZones)
{
    /// <summary>Computes the stats of the first <paramref name="count"/> points.</summary>
    public static TrackStats Compute(ReadOnlySpan<TrackPoint> points)
    {
        if (points.IsEmpty)
        {
            return default;
        }

        double distance = 0, min = double.MaxValue, max = 0;
        var zones = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            min = Math.Min(min, p.SpeedMs);
            max = Math.Max(max, p.SpeedMs);

            var startsZone = p.Braking && (i == 0 || p.StartsSegment || !points[i - 1].Braking);
            if (startsZone)
            {
                zones++;
            }

            if (i > 0 && !p.StartsSegment)
            {
                var dx = p.X - points[i - 1].X;
                var dz = p.Z - points[i - 1].Z;
                distance += Math.Sqrt(dx * dx + dz * dz);
            }
        }

        return new TrackStats(distance, min, max, zones);
    }
}

/// <summary>Math for the dyno chart: smoothing and peak finding on the captured curve.</summary>
public static class DynoMath
{
    /// <summary>Two bins further apart than this (rpm) are not neighbors: the line breaks and smoothing does not bridge them.</summary>
    public const float MaxLinkedGapRpm = 250;

    /// <summary>
    /// A 3-point centered moving average of power and torque over LINKED neighbors only. End points and points next to
    /// a gap use whichever neighbors exist, so the curve keeps its length and never invents values across a gap.
    /// </summary>
    /// <remarks>
    /// Why smooth at all: each 100 rpm bin keeps the MAXIMUM it ever saw, so adjacent bins can differ by a few percent
    /// from transients, which draws a jagged line on a curve that is physically smooth. A 3-point average removes that
    /// without shifting peaks by more than one bin (100 rpm). Anything stronger would hide real features such as a
    /// turbo spool step, which is why the raw bins are also drawn as small dots.
    /// </remarks>
    public static DynoPoint[] Smooth(IReadOnlyList<DynoPoint> curve)
    {
        var result = new DynoPoint[curve.Count];
        for (var i = 0; i < curve.Count; i++)
        {
            double power = curve[i].PowerWatts, torque = curve[i].TorqueNewtonMeters;
            var n = 1;
            if (i > 0 && curve[i].Rpm - curve[i - 1].Rpm <= MaxLinkedGapRpm)
            {
                power += curve[i - 1].PowerWatts;
                torque += curve[i - 1].TorqueNewtonMeters;
                n++;
            }

            if (i < curve.Count - 1 && curve[i + 1].Rpm - curve[i].Rpm <= MaxLinkedGapRpm)
            {
                power += curve[i + 1].PowerWatts;
                torque += curve[i + 1].TorqueNewtonMeters;
                n++;
            }

            result[i] = new DynoPoint(curve[i].Rpm, (float)(power / n), (float)(torque / n));
        }

        return result;
    }

    /// <summary>Indexes of the highest power and highest torque points; -1 for both when the curve is empty.</summary>
    public static (int Power, int Torque) FindPeaks(IReadOnlyList<DynoPoint> curve)
    {
        int bestPower = -1, bestTorque = -1;
        for (var i = 0; i < curve.Count; i++)
        {
            if (bestPower < 0 || curve[i].PowerWatts > curve[bestPower].PowerWatts)
            {
                bestPower = i;
            }

            if (bestTorque < 0 || curve[i].TorqueNewtonMeters > curve[bestTorque].TorqueNewtonMeters)
            {
                bestTorque = i;
            }
        }

        return (bestPower, bestTorque);
    }

    /// <summary>Watts in one mechanical horsepower (the "hp" of the imperial/US convention Forza's UI uses).</summary>
    public const double WattsPerHorsepower = 745.69987158227;

    /// <summary>Watts to mechanical horsepower (hp).</summary>
    public static double ToHorsepower(double watts) => watts / WattsPerHorsepower;
}
