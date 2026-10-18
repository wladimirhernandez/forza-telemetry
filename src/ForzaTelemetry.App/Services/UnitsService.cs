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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerLabel))]
    [NotifyPropertyChangedFor(nameof(AltPowerLabel))]
    private PowerUnit _powerUnit = PowerUnit.Kw;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoostLabel))]
    private BoostUnit _boostUnit = BoostUnit.Psi;

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

    /// <summary>Localized label of the power unit: <c>kW</c> or <c>hp</c>.</summary>
    public string PowerLabel => PowerUnit == PowerUnit.Hp ? Strings.PowerUnitHp : Strings.PowerUnitKw;

    /// <summary>Label of the OTHER power unit, the one shown beside the chosen one as a secondary reading.</summary>
    public string AltPowerLabel => PowerUnit == PowerUnit.Hp ? Strings.PowerUnitKw : Strings.PowerUnitHp;

    /// <summary>Localized label of the boost unit: <c>psi</c> or <c>bar</c>.</summary>
    public string BoostLabel => BoostUnit == BoostUnit.Bar ? Strings.BoostUnitBar : Strings.BoostUnit;

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

    /// <summary>The power counterpart of <see cref="AffectsSpeed"/> (covers the secondary reading and its label too).</summary>
    public static bool AffectsPower(string? propertyName) =>
        string.IsNullOrEmpty(propertyName) || propertyName is nameof(PowerUnit) or nameof(PowerLabel) or nameof(AltPowerLabel);

    /// <summary>The boost counterpart of <see cref="AffectsSpeed"/>.</summary>
    public static bool AffectsBoost(string? propertyName) =>
        string.IsNullOrEmpty(propertyName) || propertyName is nameof(BoostUnit) or nameof(BoostLabel);

    /// <summary>Psi in one bar (1 bar = 100 kPa; 1 psi = 6894.757 Pa).</summary>
    public const double PsiPerBar = 14.5037738;

    /// <summary>Mechanical horsepower in one kilowatt: the same constant the dashboard uses, so there is one definition.</summary>
    public const double HorsepowerPerKilowatt = DashboardMath.HorsepowerPerKilowatt;

    public static double PsiToBar(double psi) => psi / PsiPerBar;

    public static double KilowattsToHorsepower(double kilowatts) => kilowatts * HorsepowerPerKilowatt;

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

    /// <summary>A power in kilowatts expressed in the chosen unit.</summary>
    public double ConvertPower(double kilowatts) => PowerUnit == PowerUnit.Hp ? KilowattsToHorsepower(kilowatts) : kilowatts;

    /// <summary>A power in kilowatts expressed in the OTHER unit (hp when kW is chosen, kW when hp is).</summary>
    public double ConvertAltPower(double kilowatts) => PowerUnit == PowerUnit.Hp ? kilowatts : KilowattsToHorsepower(kilowatts);

    /// <summary>A boost in psi (the unit the game sends) expressed in the chosen unit.</summary>
    public double ConvertBoost(double psi) => BoostUnit == BoostUnit.Bar ? PsiToBar(psi) : psi;

    /// <summary>The converted power as whole-number text.</summary>
    public string FormatPower(double kilowatts) => ConvertPower(kilowatts).ToString("F0", CultureInfo.CurrentCulture);

    /// <summary>The power in the other unit as whole-number text.</summary>
    public string FormatAltPower(double kilowatts) => ConvertAltPower(kilowatts).ToString("F0", CultureInfo.CurrentCulture);

    /// <summary>
    /// The converted boost as text: one decimal in psi (a turbo moves in tenths), two in bar (0.95 bar and 1.0 bar are visibly different, a
    /// single decimal would round a whole psi of boost away).
    /// </summary>
    public string FormatBoost(double psi) =>
        ConvertBoost(psi).ToString(BoostUnit == BoostUnit.Bar ? "F2" : "F1", CultureInfo.CurrentCulture);

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
        PowerUnit = settings.PowerUnit;
        BoostUnit = settings.BoostUnit;
    }
}
