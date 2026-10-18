using ForzaTelemetry.App;

namespace ForzaTelemetry.App.Tests;

public class TireViewModelTests
{
    [Fact]
    public void A_new_tire_has_no_data()
    {
        var tire = new TireViewModel();

        Assert.Equal(TireTempBand.Unknown, tire.Band);
        Assert.Equal("--", tire.TemperatureReadout);
    }

    [Fact]
    public void Update_converts_temperature_and_classifies_it()
    {
        var tire = new TireViewModel();

        tire.Update(wireTemperature: 212f, combinedSlip: 0.5f, suspensionTravel: 0.25f);

        Assert.Equal(100, tire.TemperatureC, precision: 3);
        Assert.Equal(TireTempBand.Warm, tire.Band);
        Assert.Equal(0.5f, tire.Slip);
        Assert.Equal(0.25f, tire.Suspension);
        Assert.NotEqual("--", tire.TemperatureReadout);
    }
}
