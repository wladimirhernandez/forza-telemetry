namespace ForzaTelemetry.Core.Laps;

/// <summary>A lap sample that knows how far into the lap it is (what a lookup by distance needs).</summary>
internal interface ILapPoint
{
    /// <summary>Distance into the lap, in meters.</summary>
    double DistanceInLap { get; }
}

/// <summary>A (distance, lap clock) pair: all the live delta needs of a sample.</summary>
internal readonly record struct LapPoint(double Distance, double Time) : ILapPoint
{
    double ILapPoint.DistanceInLap => Distance;
}

/// <summary>
/// The one "where is this distance on the recorded lap" lookup, used by both the live delta (<see cref="LapTracker"/>) and the
/// chart's readouts (<see cref="LapTraceMath"/>), so the number on the page and the number on the chart cannot disagree.
/// </summary>
internal static class LapInterpolation
{
    /// <summary>
    /// Finds the two neighbouring samples around <paramref name="distance"/> by binary search (samples ordered by strictly
    /// increasing distance) and how far between them it lies.
    /// </summary>
    /// <param name="low">Index of the sample at or before the distance.</param>
    /// <param name="high">Index of the next sample.</param>
    /// <param name="t">0 at <paramref name="low"/>, 1 at <paramref name="high"/>; 0 when the two share a distance.</param>
    /// <returns>False when the distance is NaN or outside the recorded range, or there are fewer than two samples.</returns>
    public static bool TryLocate<T>(ReadOnlySpan<T> samples, double distance, out int low, out int high, out double t)
        where T : struct, ILapPoint
    {
        low = high = 0;
        t = 0;

        // Written as a negated >= so NaN (which compares false with everything) is rejected too.
        if (samples.Length < 2 || !(distance >= samples[0].DistanceInLap) || distance > samples[^1].DistanceInLap)
        {
            return false;
        }

        high = samples.Length - 1;
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (samples[mid].DistanceInLap <= distance)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        var span = samples[high].DistanceInLap - samples[low].DistanceInLap;
        t = span <= 0 ? 0 : (distance - samples[low].DistanceInLap) / span;
        return true;
    }
}
