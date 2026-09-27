namespace ForzaTelemetry.App.ViewModels;

/// <summary>
/// Converts the raw <c>Gear</c> byte from a telemetry packet into a short display label. A static,
/// pure function — no state, no dependencies — so it is trivially unit-testable on its own, without
/// needing a view model, a service, or any packet other than the byte itself.
/// </summary>
/// <remarks>
/// Gear byte meaning, from this project's own observations of real FH6 traffic (not just the spec):
/// <list type="bullet">
/// <item><description>11 = neutral. CONFIRMED — observed directly on real Xbox telemetry.</description></item>
/// <item><description>0 = reverse. CONFIRMED — observed directly on real Xbox telemetry (the dashboard
/// showed "R" while reversing).</description></item>
/// <item><description>Anything else = the forward gear number, shown as-is (e.g. 3 = third gear).</description></item>
/// </list>
/// </remarks>
public static class GearFormatter
{
    private const byte ReverseGear = 0; // CONFIRMED — see remarks above.
    private const byte NeutralGear = 11; // CONFIRMED — see remarks above.

    public static string Format(byte gear) => gear switch
    {
        ReverseGear => "R",
        NeutralGear => "N",
        _ => gear.ToString()
    };
}
