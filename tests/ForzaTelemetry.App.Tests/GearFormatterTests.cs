using ForzaTelemetry.App.ViewModels;

namespace ForzaTelemetry.App.Tests;

public class GearFormatterTests
{
    [Theory]
    [InlineData(0, "R")] // Reverse — confirmed on live telemetry.
    [InlineData(11, "N")] // Neutral — CONFIRMED on real FH6 traffic.
    [InlineData(3, "3")] // An ordinary forward gear is shown as its plain number.
    [InlineData(1, "1")]
    public void Format_returns_the_expected_label(byte gear, string expected)
    {
        Assert.Equal(expected, GearFormatter.Format(gear));
    }
}
