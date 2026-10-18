using System.Globalization;
using ForzaTelemetry.App.Resources;

namespace ForzaTelemetry.App;

/// <summary>
/// The one place that turns pedal and steering fractions into readout text, shared by the Analysis tiles and the trace
/// chart's hover panel so the two can never disagree.
/// </summary>
public static class ReadoutFormat
{
    /// <summary>A 0..1 fraction as whole-number percent text, for example <c>87%</c>.</summary>
    public static string Percent(float fraction) =>
        (fraction * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

    /// <summary>"R 35%", "L 12%" or "0%": direction letter plus magnitude, centered on zero like the chart lane.</summary>
    public static string Steer(float steer) => Math.Abs(steer) < 0.005f
        ? "0%"
        : (steer > 0 ? InsightsStrings.SteerRightShort : InsightsStrings.SteerLeftShort) + " " + Percent(Math.Abs(steer));
}
