using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App;

/// <summary>One tire's live readout: temperature, grip slip and suspension compression.</summary>
/// <remarks>
/// The temperature is KEPT in Celsius (<see cref="TemperatureC"/>: the band thresholds and tests are in Celsius) and only
/// converted for display by <see cref="UnitsService"/>, so changing the unit setting never changes what the tire "is".
/// </remarks>
public sealed partial class TireViewModel : ObservableObject
{
    private readonly UnitsService _units;

    /// <param name="units">Converts and labels the temperature; optional (metric) for tests and the designer.</param>
    public TireViewModel(UnitsService? units = null)
    {
        _units = units ?? new UnitsService();

        // The unit can change while this tire is on screen: re-announce the two properties that depend on it.
        _units.PropertyChanged += OnUnitsChanged;
    }

    private void OnUnitsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only a temperature-unit change matters here (a speed-unit switch must not re-render four tires).
        if (!UnitsService.AffectsTemperature(e.PropertyName))
        {
            return;
        }

        OnPropertyChanged(nameof(TemperatureReadout));
        OnPropertyChanged(nameof(TemperatureUnitLabel));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemperatureReadout))]
    private double _temperatureC;

    /// <summary>Combined tire slip: 0 = full grip, above 1 = grip lost (sliding).</summary>
    [ObservableProperty]
    private float _slip;

    /// <summary>Normalized suspension travel: 0 = fully stretched, 1 = fully compressed.</summary>
    [ObservableProperty]
    private float _suspension;

    /// <summary>The view maps this to the wheel color. <see cref="TireTempBand.Unknown"/> until the first packet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemperatureReadout))]
    private TireTempBand _band = TireTempBand.Unknown;

    /// <summary>Whole degrees in the chosen unit, or a dash before any telemetry has arrived.</summary>
    public string TemperatureReadout =>
        Band == TireTempBand.Unknown ? "--" : _units.FormatTemperature(TemperatureC);

    /// <summary>The unit label next to <see cref="TemperatureReadout"/>: <c>°C</c> or <c>°F</c>.</summary>
    public string TemperatureUnitLabel => _units.TemperatureLabel;

    /// <summary>Applies one packet's raw values (temperature in the wire unit).</summary>
    public void Update(float wireTemperature, float combinedSlip, float suspensionTravel)
    {
        TemperatureC = TireTemperatureScale.ToCelsius(wireTemperature);
        Band = TireTemperatureScale.Classify(TemperatureC);
        Slip = combinedSlip;
        Suspension = suspensionTravel;
    }

    /// <summary>Back to the "no telemetry yet" look (dash readout, neutral color), e.g. when a new session starts.</summary>
    public void Reset()
    {
        TemperatureC = 0;
        Band = TireTempBand.Unknown;
        Slip = 0;
        Suspension = 0;
    }
}
