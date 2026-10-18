using System.Globalization;

namespace ForzaTelemetry.App;

/// <summary>Turns lap times and deltas, in seconds, into the text the Laps screen shows.</summary>
/// <remarks>
/// <b>Why one class.</b> Every lap number on screen (the live clock, last, best, the list, the delta) must read the same
/// way, and the rounding rules are easy to get subtly wrong, so they live in one tested place instead of in format strings
/// scattered over view models. The decimal separator follows the user's culture, like every other number in the app
/// (<c>1:02.345</c> in English, <c>1:02,345</c> in Spanish).
/// </remarks>
public static class LapTimeFormatter
{
    /// <summary>
    /// The longest time that is still a lap time: one day. Anything beyond it is a corrupt or uninitialised value, not a lap
    /// (the longest endurance stints are hours). The bound also protects the formatter itself: converting a huge finite
    /// number of milliseconds to <see cref="long"/> overflows, and the result of that is unspecified garbage, not an error.
    /// </summary>
    public const double MaxSeconds = 24 * 60 * 60;

    /// <summary>What an unknown or not-yet-available time looks like (same width as a real one).</summary>
    public static string Placeholder => "--:--" + Separator + "---";

    // One entry: the zero text of the culture it was built for. The Laps screen reads Zero on every 20 Hz tick while no lap
    // runs, and rebuilding the same string 20 times a second is pointless. Keyed by culture (not computed once) because the
    // decimal separator belongs to the culture, which tests, and a language switch, can change. The entry is an immutable
    // REFERENCE type: replacing a reference is one atomic write, so a reader sees either the old entry or the new one, never a
    // culture from one and a text from the other. (A nullable value tuple would be copied in several steps, and that tear is
    // exactly what this avoids.) Two threads that miss at once both build the same text and the last write wins: harmless.
    private sealed record ZeroEntry(CultureInfo Culture, string Text);

    private static volatile ZeroEntry? _zero;

    /// <summary>The zero time, <c>0:00.000</c>: what a clock that is not running shows.</summary>
    public static string Zero
    {
        get
        {
            var culture = CultureInfo.CurrentCulture;
            var cached = _zero; // one read: the checks below must all be about the same entry
            if (cached is not null && ReferenceEquals(cached.Culture, culture))
            {
                return cached.Text;
            }

            var text = Format(0);
            _zero = new ZeroEntry(culture, text);
            return text;
        }
    }

    private static string Separator => CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;

    /// <summary>
    /// <c>m:ss.fff</c>. The value is rounded to whole milliseconds FIRST and the parts are taken from that integer, so 59.9996 s
    /// is <c>1:00.000</c> and never <c>0:60.000</c>. Minutes keep growing past 59 (a 1 h 2 min lap reads <c>62:00.000</c>).
    /// Negative, NaN, infinite and absurdly large values (over <see cref="MaxSeconds"/>) are not lap times: they show the
    /// <see cref="Placeholder"/>.
    /// </summary>
    public static string Format(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > MaxSeconds)
        {
            return Placeholder;
        }

        var totalMilliseconds = (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        var minutes = totalMilliseconds / 60_000;
        var wholeSeconds = totalMilliseconds / 1000 % 60;
        var milliseconds = totalMilliseconds % 1000;

        return string.Create(CultureInfo.CurrentCulture, $"{minutes}:{wholeSeconds:00}{Separator}{milliseconds:000}");
    }

    /// <summary>
    /// A signed difference: <c>+0.412</c> (slower) or <c>-0.412</c> (faster), three decimals; <c>0.000</c> when it rounds to
    /// zero (no sign for "level"). A gap of a minute or more reads <c>+1:02.345</c>, the same shape as a lap time. A gap larger
    /// than <see cref="MaxSeconds"/> cannot be real and shows the <see cref="Placeholder"/> (no sign in front of it).
    /// </summary>
    public static string FormatDelta(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            return Placeholder;
        }

        var rounded = Math.Round(seconds, 3, MidpointRounding.AwayFromZero);
        if (rounded == 0)
        {
            return "0" + Separator + "000";
        }

        var magnitude = Math.Abs(rounded);
        if (magnitude > MaxSeconds)
        {
            return Placeholder;
        }

        var sign = rounded > 0 ? "+" : "-";
        return magnitude >= 60
            ? sign + Format(magnitude)
            : sign + magnitude.ToString("0.000", CultureInfo.CurrentCulture);
    }
}
