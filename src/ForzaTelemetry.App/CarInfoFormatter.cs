namespace ForzaTelemetry.App;

/// <summary>Turns the raw car class and drivetrain integers of a packet into the short labels shown in the car pill.</summary>
/// <remarks>
/// ASSUMPTION (not verified against the FH6 spec: support.forza.net returned 403/502 when this was written):
/// the packet doc says <c>CarClass</c> runs 0 (D, worst) to 7 (X, best). The table below is the usual Forza ladder
/// D, C, B, A, S1, S2 with R for the slot between S2 and X; if live data shows a different letter for a class, fix
/// the table here and nowhere else. An out-of-range value shows "?" rather than throwing.
/// </remarks>
public static class CarInfoFormatter
{
    private static readonly string[] ClassLabels = ["D", "C", "B", "A", "S1", "S2", "R", "X"];

    /// <summary>Class letter(s) for <c>CarClass</c>, or "?" when out of range.</summary>
    public static string ClassLabel(int carClass) =>
        (uint)carClass < (uint)ClassLabels.Length ? ClassLabels[carClass] : "?";

    /// <summary>"FWD", "RWD" or "AWD" for <c>DrivetrainType</c> (0, 1, 2), or "?" when unknown. Universal acronyms, so not localized.</summary>
    public static string DrivetrainLabel(int drivetrain) => drivetrain switch
    {
        0 => "FWD",
        1 => "RWD",
        2 => "AWD",
        _ => "?"
    };
}
