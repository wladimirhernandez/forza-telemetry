using ForzaTelemetry.App;

namespace ForzaTelemetry.App.Tests;

public class CarInfoFormatterTests
{
    [Theory]
    [InlineData(0, "D")]
    [InlineData(3, "A")]
    [InlineData(4, "S1")]
    [InlineData(5, "S2")]
    [InlineData(7, "X")]
    [InlineData(8, "?")]
    [InlineData(-1, "?")]
    public void ClassLabel_follows_the_assumed_ladder(int carClass, string expected) =>
        Assert.Equal(expected, CarInfoFormatter.ClassLabel(carClass));

    [Theory]
    [InlineData(0, "FWD")]
    [InlineData(1, "RWD")]
    [InlineData(2, "AWD")]
    [InlineData(3, "?")]
    public void DrivetrainLabel_maps_the_three_layouts(int drivetrain, string expected) =>
        Assert.Equal(expected, CarInfoFormatter.DrivetrainLabel(drivetrain));
}
