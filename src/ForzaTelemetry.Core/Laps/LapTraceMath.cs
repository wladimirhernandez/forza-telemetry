namespace ForzaTelemetry.Core.Laps;

/// <summary>
/// Pure lookups over a lap trace (samples ordered by strictly increasing distance): the value at an arbitrary distance and
/// the time gap between two laps at that distance. Shared by the comparison chart (hover readout, delta lane) and the page's
/// live gap, so the number on the chart and the number in the tile can never disagree.
/// </summary>
public static class LapTraceMath
{
    /// <summary>
    /// The trace at <paramref name="distanceMeters"/>, linearly interpolated between the two neighbouring samples
    /// (the gear is the lower sample's: a gear does not have in-betweens).
    /// </summary>
    /// <returns>False when the distance is outside the recorded range or the trace has fewer than two samples.</returns>
    public static bool TryInterpolate(ReadOnlySpan<LapTraceSample> trace, double distanceMeters, out LapTraceSample sample)
    {
        sample = default;

        // The lookup is shared with the live delta (LapInterpolation), NaN handling included.
        if (!LapInterpolation.TryLocate(trace, distanceMeters, out var low, out var high, out var position))
        {
            return false;
        }

        var a = trace[low];
        var b = trace[high];
        var t = (float)position;
        sample = new LapTraceSample
        {
            DistanceMeters = (float)distanceMeters,
            TimeSeconds = a.TimeSeconds + ((b.TimeSeconds - a.TimeSeconds) * t),
            SpeedMs = a.SpeedMs + ((b.SpeedMs - a.SpeedMs) * t),
            Throttle = a.Throttle + ((b.Throttle - a.Throttle) * t),
            Brake = a.Brake + ((b.Brake - a.Brake) * t),
            Steer = a.Steer + ((b.Steer - a.Steer) * t),
            Gear = a.Gear,
        };
        return true;
    }

    /// <summary>
    /// How much later (positive) or earlier (negative) the lap in progress reached <paramref name="distanceMeters"/> than the
    /// reference lap did, given the clock of the lap in progress there.
    /// </summary>
    /// <returns>Null when the reference lap does not cover that distance.</returns>
    public static double? DeltaSeconds(ReadOnlySpan<LapTraceSample> reference, double distanceMeters, double currentTimeSeconds) =>
        TryInterpolate(reference, distanceMeters, out var at) ? currentTimeSeconds - at.TimeSeconds : null;
}
