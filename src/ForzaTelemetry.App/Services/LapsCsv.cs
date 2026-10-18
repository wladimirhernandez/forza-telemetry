using System.IO;
using ForzaTelemetry.Core.Export;
using ForzaTelemetry.Core.Laps;

namespace ForzaTelemetry.App.Services;

/// <summary>The laps of a session as a CSV table: one row per finished lap, oldest first.</summary>
/// <remarks>
/// <b>Columns:</b> <c>lap</c> (the game's lap number, the same one the Laps page lists), <c>time_s</c> (seconds, three decimals) and
/// <c>gap_to_best_s</c> (the lap's time minus the session's best lap, so the best lap is <c>0.000</c> and a slower one positive: the
/// same comparison as the page's "Gap" column, as a number a spreadsheet can sort). A lap whose time the game never reported has
/// both time cells empty: an unknown time must not read as a zero-second lap.
/// </remarks>
public static class LapsCsv
{
    /// <summary>The header row.</summary>
    public static readonly string[] Columns = ["lap", "time_s", "gap_to_best_s"];

    /// <summary>Writes the table (header and rows) to <paramref name="stream"/>, which stays open.</summary>
    public static void Write(Stream stream, IReadOnlyList<LapRecord> laps)
    {
        using var csv = CsvWriter.Create(stream, leaveOpen: true);
        csv.Row(Columns);

        // The fastest lap with a real time; ties go to the earlier lap, like the page (a lap with time 0 has no time at all).
        var best = laps.Where(lap => lap.TimeSeconds > 0).MinBy(lap => lap.TimeSeconds);

        foreach (var lap in laps.OrderBy(lap => lap.Number))
        {
            var hasTime = lap.TimeSeconds > 0 && best is not null;
            csv.Field(lap.Number)
                .Field(hasTime ? lap.TimeSeconds : null, "0.000")
                .Field(hasTime ? lap.TimeSeconds - best!.TimeSeconds : null, "0.000")
                .EndRow();
        }
    }
}
