using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ForzaTelemetry.App.Resources;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// Turns the packet's metric values into the units the user picked, and names those units. Every screen that shows a
/// speed or a tire temperature asks this one class, so a unit is never converted (or labelled) in two different places.
/// </summary>
/// <remarks>
/// It is an <see cref="ObservableObject"/> because the units can change while a screen is showing: the view models
/// listen to <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> and raise their own
/// notifications, so a bound readout re-renders immediately without any converter in the XAML (a converter would need its
/// own way to learn about the change, and its conversion rule would live in the view layer, away from the tests).
/// </remarks>
public sealed partial class UnitsService : ObservableObject
{
    /// <summary>Kilometers in one international mile (exact by definition).</summary>
    public const double KilometersPerMile = ForzaTelemetry.Core.Performance.SpeedCrossing.KmhPerMph;

    /// <summary>Creates a service in metric units (km/h, °C) that does not follow any settings; for tests and the designer.</summary>
    public UnitsService()
    {
    }

    /// <summary>Creates a service that starts from, and follows, the units in <paramref name="settings"/>.</summary>
    public UnitsService(SettingsService settings)
    {
        Apply(settings.Current);
        settings.Changed += (_, e) => Apply(e.Current);
    }

    // [NotifyPropertyChangedFor]: the label is derived from the unit, so it must announce its own change.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedLabel))]
    [NotifyPropertyChangedFor(nameof(DistanceLabel))]
    private SpeedUnit _speedUnit = SpeedUnit.Kmh;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemperatureLabel))]
    private TemperatureUnit _temperatureUnit = TemperatureUnit.Celsius;

    /// <summary>Localized label of the speed unit: <c>km/h</c> or <c>mph</c>.</summary>
    public string SpeedLabel => SpeedUnit == SpeedUnit.Mph ? Strings.SpeedUnitMph : Strings.SpeedUnit;

    /// <summary>
    /// Localized label of short distances (a braking distance): <c>m</c>, or <c>ft</c> when the speed unit is mph. There is no separate
    /// distance setting: a player who reads speed in mph reads a stopping distance in feet.
    /// </summary>
    public string DistanceLabel => SpeedUnit == SpeedUnit.Mph ? Strings.DistanceUnitFeet : Strings.DistanceUnit;

    /// <summary>Localized label of the temperature unit: <c>°C</c> or <c>°F</c>.</summary>
    public string TemperatureLabel =>
        TemperatureUnit == TemperatureUnit.Fahrenheit ? Strings.TemperatureUnitFahrenheit : Strings.TemperatureUnit;

    /// <summary>
    /// True when a <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> from this service can change a
    /// speed readout or label. Listeners filter with it instead of re-announcing everything on every notification (a
    /// temperature switch must not re-render the speed). A null or empty name means "everything changed" in WPF, so it
    /// counts.
    /// </summary>
    public static bool AffectsSpeed(string? propertyName) =>
        string.IsNullOrEmpty(propertyName) || propertyName is nameof(SpeedUnit) or nameof(SpeedLabel);

    /// <summary>The temperature counterpart of <see cref="AffectsSpeed"/>.</summary>
    public static bool AffectsTemperature(string? propertyName) =>
        string.IsNullOrEmpty(propertyName) || propertyName is nameof(TemperatureUnit) or nameof(TemperatureLabel);

    /// <summary>Feet in one meter (exact: a foot is 0.3048 m by definition).</summary>
    public const double FeetPerMeter = 1 / 0.3048;

    public static double KmhToMph(double kmh) => kmh / KilometersPerMile;

    public static double CelsiusToFahrenheit(double celsius) => celsius * 9.0 / 5.0 + 32.0;

    /// <summary>A speed in km/h expressed in the chosen unit.</summary>
    public double ConvertSpeed(double kmh) => SpeedUnit == SpeedUnit.Mph ? KmhToMph(kmh) : kmh;

    /// <summary>A short distance in meters expressed in the chosen unit (meters, or feet with mph).</summary>
    public double ConvertDistance(double meters) => SpeedUnit == SpeedUnit.Mph ? meters * FeetPerMeter : meters;

    /// <summary>A temperature in Celsius expressed in the chosen unit.</summary>
    public double ConvertTemperature(double celsius) =>
        TemperatureUnit == TemperatureUnit.Fahrenheit ? CelsiusToFahrenheit(celsius) : celsius;

    /// <summary>The converted speed as whole-number text (the dashboard shows no decimals).</summary>
    public string FormatSpeed(double kmh) => ConvertSpeed(kmh).ToString("F0", CultureInfo.CurrentCulture);

    /// <summary>
    /// The converted distance as text: one decimal in meters (a stop is measured in tens of meters, and the tenth separates two tunes),
    /// whole feet (the same precision, and a tenth of a foot would be noise).
    /// </summary>
    public string FormatDistance(double meters) =>
        ConvertDistance(meters).ToString(SpeedUnit == SpeedUnit.Mph ? "F0" : "F1", CultureInfo.CurrentCulture);

    /// <summary>The converted temperature as whole-number text.</summary>
    public string FormatTemperature(double celsius) => ConvertTemperature(celsius).ToString("F0", CultureInfo.CurrentCulture);

    private void Apply(AppSettings settings)
    {
        SpeedUnit = settings.SpeedUnit;
        TemperatureUnit = settings.TemperatureUnit;
    }
}
