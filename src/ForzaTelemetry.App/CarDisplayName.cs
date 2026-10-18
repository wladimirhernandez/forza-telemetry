using System.Globalization;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App;

/// <summary>The one place that decides what a car is CALLED on screen: its nickname, or "Car #1234" until the player names it.</summary>
/// <remarks>
/// Every screen that shows a car (the top bar pill, Performance, the recordings list, Tuning) goes through this, so a rename shows up
/// the same way everywhere and a screen can never invent its own wording for the unnamed case.
/// </remarks>
public static class CarDisplayName
{
    /// <summary>The nickname of <paramref name="carOrdinal"/>, or the localized "Car #1234" when it has none (or there is no store).</summary>
    public static string For(int carOrdinal, CarNameStore? names) => names?.Find(carOrdinal) ?? Unnamed(carOrdinal);

    /// <summary>The localized "Car #1234", used for a car without a nickname.</summary>
    public static string Unnamed(int carOrdinal) =>
        string.Format(CultureInfo.CurrentCulture, Strings.PerfCarOrdinalFormat, carOrdinal);
}
