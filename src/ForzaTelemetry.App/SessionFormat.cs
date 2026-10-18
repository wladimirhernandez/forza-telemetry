using System.Globalization;
using ForzaTelemetry.App.Resources;

namespace ForzaTelemetry.App;

/// <summary>Turns the durations, sizes and cars of the Sessions page into text.</summary>
/// <remarks>
/// One tested place, like <see cref="LapTimeFormatter"/>: the recording panel, the library rows and the player bar all show
/// the same kinds of numbers and must read the same way. Numbers follow the user's culture (<c>1,5 MB</c> in Spanish).
/// </remarks>
public static class SessionFormat
{
    private const double Kilo = 1024;

    /// <summary>
    /// <c>m:ss</c>, or <c>h:mm:ss</c> from one hour on. Whole seconds, rounded DOWN: a clock that rounds up reads "0:01" at 0.6 s
    /// and ends up ahead of the recording. Negative values read as zero.
    /// </summary>
    public static string Duration(TimeSpan duration)
    {
        var totalSeconds = (long)Math.Floor(Math.Max(0, duration.TotalSeconds));
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds / 60 % 60;
        var seconds = totalSeconds % 60;
        return hours > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.CurrentCulture, $"{minutes}:{seconds:00}");
    }

    /// <summary>
    /// A file size in the largest sensible unit (<c>812 B</c>, <c>41.2 KB</c>, <c>1.3 MB</c>, <c>2.1 GB</c>), binary steps of 1024,
    /// one decimal from KB up. Negative values read as zero. The unit is chosen AFTER rounding to the one decimal that is shown,
    /// so a size just under a boundary reads <c>1.0 MB</c> and never the nonsensical <c>1024.0 KB</c>.
    /// </summary>
    public static string Size(long bytes)
    {
        double value = Math.Max(0, bytes);
        if (value < Kilo)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{value:0} B");
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = 0;
        value /= Kilo;

        // Compare what will be DISPLAYED, not the raw value: 1023.96 KB prints as "1024.0", which is already a megabyte.
        while (Math.Round(value, 1, MidpointRounding.AwayFromZero) >= Kilo && unit < units.Length - 1)
        {
            value /= Kilo;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.0} {units[unit]}");
    }

    /// <summary>
    /// The cars of a recording, as the cars are named elsewhere (<c>Car #1234</c>): the first one, then how many more. The
    /// recording does not store the performance index, so only the ordinal can be shown. A recording with no race in it
    /// (only menus) says so instead of showing an empty cell.
    /// </summary>
    public static string Cars(IReadOnlyList<int> ordinals)
    {
        if (ordinals.Count == 0)
        {
            return SessionsStrings.SessionsNoCars;
        }

        var first = string.Format(CultureInfo.CurrentCulture, Strings.PerfCarOrdinalFormat, ordinals[0]);
        return ordinals.Count == 1
            ? first
            : string.Format(CultureInfo.CurrentCulture, SessionsStrings.SessionsCarsMoreFormat, first, ordinals.Count - 1);
    }

    /// <summary>The start of a recording for a list, in the user's short date and time format.</summary>
    public static string Started(DateTimeOffset startedAt) => startedAt.ToString("g", CultureInfo.CurrentCulture);

    /// <summary>The label the top bar quotes while replaying: <c>2026-10-18 01:23</c>, unambiguous in every culture.</summary>
    public static string ReplayLabel(DateTimeOffset startedAt) =>
        startedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
