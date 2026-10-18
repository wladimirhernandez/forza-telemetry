using ForzaTelemetry.App;

namespace ForzaTelemetry.App.Tests;

public class TireTemperatureScaleTests
{
    [Theory]
    [InlineData(-10, TireTempBand.Cold)]
    [InlineData(59.9, TireTempBand.Cold)]
    [InlineData(60, TireTempBand.Ideal)]
    [InlineData(94.9, TireTempBand.Ideal)]
    [InlineData(95, TireTempBand.Warm)]
    [InlineData(109.9, TireTempBand.Warm)]
    [InlineData(110, TireTempBand.Hot)]
    [InlineData(200, TireTempBand.Hot)]
    public void Classify_uses_inclusive_lower_bounds(double celsius, TireTempBand expected) =>
        Assert.Equal(expected, TireTemperatureScale.Classify(celsius));

    [Fact]
    public void ToCelsius_converts_from_the_assumed_wire_unit()
    {
        Assert.True(TireTemperatureScale.WireIsFahrenheit);
        Assert.Equal(0, TireTemperatureScale.ToCelsius(32), precision: 6);
        Assert.Equal(100, TireTemperatureScale.ToCelsius(212), precision: 6);
    }
}
