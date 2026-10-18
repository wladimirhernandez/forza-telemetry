namespace ForzaTelemetry.App;

/// <summary>
/// Pure unit conversions and derivations behind the dashboard widgets (shift lights, G-force meter, input bars,
/// engine gauges). No state and no WPF types, so every mapping is unit-tested on its own and the custom controls
/// stay thin: they only draw numbers this class already prepared.
/// </summary>
public static class DashboardMath
{
    /// <summary>Standard gravity in m/s^2; Forza reports acceleration in m/s^2, a G-force meter wants g.</summary>
    public const double StandardGravity = 9.80665;

    /// <summary>Shift lights start to fill at this fraction of the redline RPM.</summary>
    public const double ShiftBandStart = 0.60;

    /// <summary>All shift lights are lit, and blinking, from this fraction of the redline RPM.</summary>
    public const double RedlineFraction = 0.97;

    /// <summary>Mechanical horsepower per kilowatt (the "hp" printed on Forza's own car cards).</summary>
    public const double HorsepowerPerKilowatt = 1.34102209;

    /// <summary>Where each shift-light segment changes colour, as a fraction of the strip (green, then amber, then red).</summary>
    public const double AmberFromFraction = 0.60;

    /// <summary>See <see cref="AmberFromFraction"/>.</summary>
    public const double RedFromFraction = 0.85;

    /// <summary>Clamps to 0..1; a NaN (e.g. 0 / 0) counts as 0 so a bad packet can never draw garbage.</summary>
    public static double Clamp01(double value) =>
        double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);

    /// <summary>Position of <paramref name="value"/> between <paramref name="minimum"/> and <paramref name="maximum"/>, 0..1.</summary>
    public static double Normalize(double value, double minimum, double maximum) =>
        maximum > minimum ? Clamp01((value - minimum) / (maximum - minimum)) : 0;

    /// <summary>Converts an acceleration in m/s^2 to g.</summary>
    public static double ToG(double metersPerSecondSquared) => metersPerSecondSquared / StandardGravity;

    /// <summary>Pedal byte (0..255) to 0..1.</summary>
    public static double NormalizePedal(byte value) => value / 255.0;

    /// <summary>Steering (-127 full left .. 127 full right) to -1..1. The sbyte can also be -128, which counts as full left.</summary>
    public static double NormalizeSteer(sbyte value) => Math.Clamp(value / 127.0, -1, 1);

    /// <summary>Watts to kilowatts, with negative power (engine braking off-throttle) shown as 0.</summary>
    public static double ToKilowatts(double watts) => Math.Max(0, watts) / 1000.0;

    /// <summary>Kilowatts to mechanical horsepower.</summary>
    public static double ToHorsepower(double kilowatts) => kilowatts * HorsepowerPerKilowatt;

    /// <summary>True when the engine is at or above the redline threshold (a missing redline, 0, is never "redline").</summary>
    public static bool IsRedline(double rpm, double maxRpm) =>
        maxRpm > 0 && rpm >= maxRpm * RedlineFraction;

    /// <summary>How far the RPM is through the shift band: 0 below <see cref="ShiftBandStart"/>, 1 at the redline threshold.</summary>
    public static double ShiftProgress(double rpm, double maxRpm) =>
        maxRpm > 0 ? Normalize(rpm / maxRpm, ShiftBandStart, RedlineFraction) : 0;

    /// <summary>
    /// Number of segments to light for a 0..1 progress. Rounds UP so the very first bit of progress already lights
    /// the first segment (a strip that stays dark until 1/15 of the way feels broken).
    /// </summary>
    public static int LitSegments(double progress, int segmentCount) =>
        (int)Math.Ceiling(Clamp01(progress) * segmentCount);

    /// <summary>Colour zone of a shift-light segment: 0 green, 1 amber, 2 red.</summary>
    public static int ShiftZone(int segmentIndex, int segmentCount)
    {
        var position = (double)segmentIndex / segmentCount;
        return position >= RedFromFraction ? 2 : position >= AmberFromFraction ? 1 : 0;
    }

    /// <summary>
    /// Square-wave blink computed from a clock: "on" for the first half of each period. Deriving the state from time
    /// (instead of toggling a flag on a timer) means no timer object, no drift and a blink rate that does not depend
    /// on how often the UI happens to be refreshed.
    /// </summary>
    public static bool IsBlinkOn(double elapsedSeconds, double hertz) =>
        (long)Math.Floor(elapsedSeconds * hertz * 2) % 2 == 0;

    /// <summary>
    /// Maps an acceleration vector (in g) to a point inside a unit circle for the G-force meter: x to the right is
    /// right-hand acceleration, y UP on screen is forward acceleration, so braking moves the dot down. A vector
    /// longer than <paramref name="maxG"/> is scaled back to the circle edge, so the dot never leaves the meter.
    /// </summary>
    /// <returns>x and y in -1..1 in SCREEN orientation: y grows downward, so forward acceleration comes out negative (up).</returns>
    public static (double X, double Y) ToMeterPoint(double lateralG, double longitudinalG, double maxG)
    {
        if (maxG <= 0 || double.IsNaN(lateralG) || double.IsNaN(longitudinalG))
        {
            return (0, 0);
        }

        var x = lateralG / maxG;
        var y = -longitudinalG / maxG; // forward (accelerating) = up = negative screen y
        var length = Math.Sqrt(x * x + y * y);
        return length > 1 ? (x / length, y / length) : (x, y);
    }
}
