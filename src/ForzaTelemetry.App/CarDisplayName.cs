using System.Globalization;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App;

/// <summary>The one place that decides what a car is CALLED on screen: the player's nickname, else the real name from the bundled list, else "Car #1234".</summary>
/// <remarks>
/// Every screen that shows a car (the top bar pill, Performance, the recordings list, Tuning) goes through this (it forwards to
/// <see cref="CarNames.Display"/>), so a rename shows up the same way everywhere and a screen can never invent its own wording for the unnamed case.
/// </remarks>
public static class CarDisplayName
{
    /// <summary>The display name of <paramref name="carOrdinal"/>; the localized "Car #1234" when there is no resolver.</summary>
    public static string For(int carOrdinal, CarNames? names) => names?.Display(carOrdinal) ?? Unnamed(carOrdinal);

    /// <summary>The localized "Car #1234", used for a car nobody has a name for.</summary>
    public static string Unnamed(int carOrdinal) =>
        string.Format(CultureInfo.CurrentCulture, Strings.PerfCarOrdinalFormat, carOrdinal);
}
