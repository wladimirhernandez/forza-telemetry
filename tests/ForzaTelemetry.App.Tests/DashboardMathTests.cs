using ForzaTelemetry.App;

namespace ForzaTelemetry.App.Tests;

public class DashboardMathTests
{
    [Fact]
    public void ToG_divides_by_standard_gravity()
    {
        Assert.Equal(1.0, DashboardMath.ToG(9.80665), precision: 6);
        Assert.Equal(-0.5, DashboardMath.ToG(-4.903325), precision: 6);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(255, 1.0)]
    public void NormalizePedal_maps_0_255_to_0_1(byte raw, double expected) =>
        Assert.Equal(expected, DashboardMath.NormalizePedal(raw), precision: 6);

    [Theory]
    [InlineData(-127, -1.0)]
    [InlineData(-128, -1.0)] // the sbyte minimum is clamped, never below -1
    [InlineData(0, 0.0)]
    [InlineData(127, 1.0)]
    public void NormalizeSteer_maps_to_minus_one_to_one(sbyte raw, double expected) =>
        Assert.Equal(expected, DashboardMath.NormalizeSteer(raw), precision: 6);

    [Fact]
    public void ToKilowatts_clamps_negative_power_to_zero()
    {
        Assert.Equal(0, DashboardMath.ToKilowatts(-35_000));
        Assert.Equal(212.5, DashboardMath.ToKilowatts(212_500), precision: 6);
    }

    [Fact]
    public void ToHorsepower_converts_kilowatts_to_mechanical_horsepower() =>
        Assert.Equal(134.102, DashboardMath.ToHorsepower(100), precision: 3);

    [Theory]
    [InlineData(5_000, 8_000, false)]
    [InlineData(7_759, 8_000, false)] // just under 97%
    [InlineData(7_760, 8_000, true)] // exactly 97%
    [InlineData(8_000, 8_000, true)]
    [InlineData(5_000, 0, false)] // no redline known yet
    public void IsRedline_starts_at_97_percent_of_max(double rpm, double max, bool expected) =>
        Assert.Equal(expected, DashboardMath.IsRedline(rpm, max));

    [Theory]
    [InlineData(4_000, 8_000, 0.0)] // 50%: below the band
    [InlineData(4_800, 8_000, 0.0)] // exactly 60%: band start
    [InlineData(7_760, 8_000, 1.0)] // 97%: band end
    [InlineData(8_000, 8_000, 1.0)] // above: clamped
    public void ShiftProgress_spans_60_to_97_percent(double rpm, double max, double expected) =>
        Assert.Equal(expected, DashboardMath.ShiftProgress(rpm, max), precision: 6);

    [Fact]
    public void ShiftProgress_is_halfway_in_the_middle_of_the_band() =>
        Assert.Equal(0.5, DashboardMath.ShiftProgress(0.785 * 8_000, 8_000), precision: 6);

    [Theory]
    [InlineData(0.0, 15, 0)]
    [InlineData(0.01, 15, 1)] // any progress lights the first segment
    [InlineData(0.5, 15, 8)]
    [InlineData(1.0, 15, 15)]
    [InlineData(2.0, 15, 15)]
    [InlineData(0.2, 15, 3)] // 0.2 * 15 = 3.0000000000000004: an exact boundary must not light a 4th segment
    [InlineData(0.6, 5, 3)]
    public void LitSegments_rounds_up_and_clamps(double progress, int count, int expected) =>
        Assert.Equal(expected, DashboardMath.LitSegments(progress, count));

    [Fact]
    public void ShiftZone_goes_green_then_amber_then_red_for_15_segments()
    {
        var zones = Enumerable.Range(0, 15).Select(i => DashboardMath.ShiftZone(i, 15)).ToArray();

        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2], zones);
    }

    [Theory]
    [InlineData(0.00, true)]
    [InlineData(0.05, true)] // 6 Hz: period 166.7 ms, on for the first 83.3 ms
    [InlineData(0.09, false)]
    [InlineData(0.17, true)] // next period
    public void IsBlinkOn_is_a_square_wave_of_the_given_frequency(double seconds, bool expected) =>
        Assert.Equal(expected, DashboardMath.IsBlinkOn(seconds, 6));

    [Fact]
    public void ToMeterPoint_braking_moves_the_dot_down_and_right_turns_move_it_right()
    {
        var braking = DashboardMath.ToMeterPoint(0, -0.75, 1.5);
        var turningRight = DashboardMath.ToMeterPoint(0.75, 0, 1.5);
        var accelerating = DashboardMath.ToMeterPoint(0, 0.75, 1.5);

        Assert.Equal(0.5, braking.Y, precision: 6); // screen y grows downward
        Assert.Equal(0.5, turningRight.X, precision: 6);
        Assert.Equal(-0.5, accelerating.Y, precision: 6);
    }

    [Fact]
    public void ToMeterPoint_pins_extreme_values_to_the_circle_edge()
    {
        var (x, y) = DashboardMath.ToMeterPoint(3, -4, 1.5);

        Assert.Equal(1.0, Math.Sqrt(x * x + y * y), precision: 6);
        Assert.True(x > 0 && y > 0);
    }

    [Theory]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity)]
    public void ToMeterPoint_returns_the_center_for_Infinity(double lateral, double longitudinal) =>
        Assert.Equal((0.0, 0.0), DashboardMath.ToMeterPoint(lateral, longitudinal, 1.5));

    [Fact]
    public void ToMeterPoint_returns_the_center_for_NaN() =>
        Assert.Equal((0.0, 0.0), DashboardMath.ToMeterPoint(double.NaN, 1, 1.5));

    [Fact]
    public void Normalize_clamps_and_survives_a_degenerate_range()
    {
        Assert.Equal(0.5, DashboardMath.Normalize(5, 0, 10));
        Assert.Equal(1.0, DashboardMath.Normalize(50, 0, 10));
        Assert.Equal(0.0, DashboardMath.Normalize(-5, 0, 10));
        Assert.Equal(0.0, DashboardMath.Normalize(5, 10, 10));
    }
}
