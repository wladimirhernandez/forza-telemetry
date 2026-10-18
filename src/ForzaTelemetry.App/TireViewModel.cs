using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ForzaTelemetry.App;

/// <summary>One tire's live readout: temperature (Celsius), grip slip and suspension compression.</summary>
public sealed partial class TireViewModel : ObservableObject
{
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

    /// <summary>Whole degrees, or a dash before any telemetry has arrived.</summary>
    public string TemperatureReadout =>
        Band == TireTempBand.Unknown ? "--" : TemperatureC.ToString("F0", CultureInfo.CurrentCulture);

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
