using System.Globalization;

namespace ForzaTelemetry.App.Tests;

public class LapTimeFormatterTests
{
    // The tests run in whatever language the machine uses, so the expected text builds its decimal separator the same way
    // the formatter does, and the culture-specific tests pin a culture explicitly.
    private static string Sep => CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;

    private static void InCulture(string name, Action action)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0.0, "0:00{0}000")]
    [InlineData(5.0, "0:05{0}000")]
    [InlineData(62.345, "1:02{0}345")]
    [InlineData(83.4, "1:23{0}400")]
    [InlineData(599.9994, "9:59{0}999")]
    [InlineData(3599.999, "59:59{0}999")]
    [InlineData(3725.5, "62:05{0}500")] // minutes keep counting past an hour
    [InlineData(0.0004, "0:00{0}000")] // under half a millisecond rounds down
    [InlineData(0.0005, "0:00{0}001")] // half a millisecond rounds up
    public void A_time_reads_minutes_seconds_and_milliseconds(double seconds, string expectedPattern) =>
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, expectedPattern, Sep), LapTimeFormatter.Format(seconds));

    [Theory]
    [InlineData(59.9996, "1:00{0}000")] // rounded as a whole, never "0:60.000"
    [InlineData(119.9999, "2:00{0}000")]
    [InlineData(59.9994, "0:59{0}999")]
    public void Rounding_carries_into_the_next_unit_instead_of_printing_sixty_seconds(double seconds, string expectedPattern) =>
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, expectedPattern, Sep), LapTimeFormatter.Format(seconds));

    [Theory]
    [InlineData(-0.001)]
    [InlineData(-30.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Values_that_cannot_be_lap_times_show_the_placeholder(double seconds) =>
        Assert.Equal(LapTimeFormatter.Placeholder, LapTimeFormatter.Format(seconds));

    [Fact]
    public void The_placeholder_has_the_shape_of_a_time_with_two_digit_minutes()
    {
        Assert.Equal("--:--" + Sep + "---", LapTimeFormatter.Placeholder);
        Assert.Equal(LapTimeFormatter.Format(683.456).Length, LapTimeFormatter.Placeholder.Length); // 11:23.456
    }

    [Fact]
    public void The_decimal_separator_follows_the_culture()
    {
        InCulture("en-US", () =>
        {
            Assert.Equal("1:02.345", LapTimeFormatter.Format(62.345));
            Assert.Equal("--:--.---", LapTimeFormatter.Placeholder);
            Assert.Equal("+0.412", LapTimeFormatter.FormatDelta(0.412));
        });
        InCulture("es-ES", () =>
        {
            Assert.Equal("1:02,345", LapTimeFormatter.Format(62.345));
            Assert.Equal("--:--,---", LapTimeFormatter.Placeholder);
            Assert.Equal("+0,412", LapTimeFormatter.FormatDelta(0.412));
        });
    }

    [Theory]
    [InlineData(0.412, "+0{0}412")]
    [InlineData(-0.412, "-0{0}412")]
    [InlineData(12.3456, "+12{0}346")]
    [InlineData(-5.0, "-5{0}000")]
    [InlineData(59.9994, "+59{0}999")]
    public void A_delta_is_signed_with_three_decimals(double seconds, string expectedPattern) =>
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, expectedPattern, Sep), LapTimeFormatter.FormatDelta(seconds));

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.0004)]
    [InlineData(-0.0004)] // never "-0.000"
    public void A_delta_that_rounds_to_zero_has_no_sign(double seconds) =>
        Assert.Equal("0" + Sep + "000", LapTimeFormatter.FormatDelta(seconds));

    [Theory]
    [InlineData(59.9996, "+1:00{0}000")]
    [InlineData(62.345, "+1:02{0}345")]
    [InlineData(-75.5, "-1:15{0}500")]
    public void A_gap_of_a_minute_or_more_reads_like_a_lap_time(double seconds, string expectedPattern) =>
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, expectedPattern, Sep), LapTimeFormatter.FormatDelta(seconds));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_delta_that_is_not_a_number_shows_the_placeholder(double seconds) =>
        Assert.Equal(LapTimeFormatter.Placeholder, LapTimeFormatter.FormatDelta(seconds));
}
