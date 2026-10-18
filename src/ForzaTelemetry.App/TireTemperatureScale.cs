namespace ForzaTelemetry.App;

/// <summary>Coarse tire temperature state; each band maps to one wheel color in the view.</summary>
public enum TireTempBand
{
    /// <summary>No telemetry received yet (never produced by <see cref="TireTemperatureScale.Classify"/>).</summary>
    Unknown = 0,
    Cold,
    Ideal,
    Warm,
    Hot
}

/// <summary>
/// Converts the wire tire temperature to Celsius and classifies it into a <see cref="TireTempBand"/>.
/// </summary>
/// <remarks>
/// INITIAL GUESSES: the thresholds below are placeholders until they are tuned with live telemetry from
/// different cars and tire compounds. Change them here only; nothing else hard-codes a temperature.
/// <para>
/// Unit: Forza's Data Out has historically reported tire temperature in degrees Fahrenheit. The FH6 spec
/// (see <c>TelemetryPacket</c>) could not be fetched when this was written, so <see cref="WireIsFahrenheit"/>
/// is an assumption to verify against live values (a warm tire should read roughly 150-220 on the wire).
/// </para>
/// </remarks>
public static class TireTemperatureScale
{
    /// <summary>Assumed unit of <c>TireTemp*</c> on the wire; flip to false if live data shows Celsius.</summary>
    public const bool WireIsFahrenheit = true;

    /// <summary>Below this (Celsius) the tire is cold: little grip.</summary>
    public const double IdealFromC = 60;

    /// <summary>From this (Celsius) the tire is past its ideal window.</summary>
    public const double WarmFromC = 95;

    /// <summary>From this (Celsius) the tire is overheating.</summary>
    public const double HotFromC = 110;

    /// <summary>Converts a raw wire value to Celsius according to <see cref="WireIsFahrenheit"/>.</summary>
    public static double ToCelsius(double wireValue) =>
        WireIsFahrenheit ? (wireValue - 32.0) * 5.0 / 9.0 : wireValue;

    /// <summary>Classifies a temperature in Celsius. Lower bounds are inclusive.</summary>
    public static TireTempBand Classify(double celsius) => celsius switch
    {
        >= HotFromC => TireTempBand.Hot,
        >= WarmFromC => TireTempBand.Warm,
        >= IdealFromC => TireTempBand.Ideal,
        _ => TireTempBand.Cold
    };
}
