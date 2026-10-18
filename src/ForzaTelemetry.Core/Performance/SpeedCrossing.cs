namespace ForzaTelemetry.Core.Performance;

/// <summary>
/// Shared maths for "when exactly did speed cross this line?" used by <see cref="AccelerationTimer"/> and
/// <see cref="BrakingTest"/>.
/// </summary>
/// <remarks>
/// WHY one helper: packets arrive every ~16 ms, so reading the first packet past a line is off by up to a
/// packet interval. Both engines instead assume speed changes linearly between the two straddling packets and
/// solve for the fraction of the interval where the line was crossed; the same fraction then gives the
/// interpolated game time and distance. Keeping that in one place guarantees both engines agree on it and on
/// what "standing still" means.
/// </remarks>
internal static class SpeedCrossing
{
    /// <summary>Below this speed (km/h) the car counts as standing still.</summary>
    public const double StandstillKmh = 1.0;

    /// <summary>The standstill threshold in meters per second.</summary>
    public const double StandstillMs = StandstillKmh / 3.6;

    /// <summary>Kilometers per hour in one mile per hour (exact by definition of the international mile).</summary>
    public const double KmhPerMph = 1.609344;

    /// <summary>Converts km/h to m/s.</summary>
    public static double KmhToMs(double kmh) => kmh / 3.6;

    /// <summary>Converts mph to m/s.</summary>
    public static double MphToMs(double mph) => mph * KmhPerMph / 3.6;

    /// <summary>
    /// Fraction (0..1) of the interval from <paramref name="v0"/> to <paramref name="v1"/> at which speed reached
    /// <paramref name="target"/>, for rising or falling speed. Clamped, and 1 when speed did not change.
    /// </summary>
    public static double Fraction(double v0, double v1, double target) =>
        v1 == v0 ? 1 : Math.Clamp((target - v0) / (v1 - v0), 0, 1);

    /// <summary>Linear interpolation between two values.</summary>
    public static double Lerp(double a, double b, double fraction) => a + (fraction * (b - a));

    /// <summary>Interpolated game time (ms) at which speed crossed <paramref name="target"/> between two packets.</summary>
    public static double TimeMs(TelemetryPacket previous, TelemetryPacket current, double target) =>
        Lerp(previous.TimestampMS, current.TimestampMS, Fraction(previous.Speed, current.Speed, target));

    /// <summary>Interpolated distance (m) at which speed crossed <paramref name="target"/> between two packets.</summary>
    public static double Distance(TelemetryPacket previous, TelemetryPacket current, double target) =>
        Lerp(previous.DistanceTraveled, current.DistanceTraveled, Fraction(previous.Speed, current.Speed, target));
}
