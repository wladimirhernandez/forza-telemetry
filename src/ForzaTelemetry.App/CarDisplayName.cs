using System.Globalization;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App;

/// <summary>The one place that decides what a car is CALLED on screen: the player's nickname, else the real name from the bundled list, else "Car #1234".</summary>
/// <remarks>
/// Every screen that shows a car (the top bar pill, Performance, the recordings list, Tuning) and every CSV export goes through this, so a
/// rename shows up the same way everywhere and a screen can never invent its own wording for the unnamed case.
/// </remarks>
public static class CarDisplayName
{
    /// <summary>The nickname of <paramref name="carOrdinal"/>, else its catalog name, else the localized "Car #1234" (also when there is no store).</summary>
    public static string For(int carOrdinal, CarNameStore? names) =>
        names?.Find(carOrdinal) ?? names?.CatalogName(carOrdinal) ?? Unnamed(carOrdinal);

    /// <summary>What the car is called when the player has NOT named it: the catalog name, else the localized "Car #1234".</summary>
    public static string Default(int carOrdinal, CarNameStore? names) => names?.CatalogName(carOrdinal) ?? Unnamed(carOrdinal);

    /// <summary>The localized "Car #1234", used for a car nobody has a name for.</summary>
    public static string Unnamed(int carOrdinal) =>
        string.Format(CultureInfo.CurrentCulture, Strings.PerfCarOrdinalFormat, carOrdinal);
}
