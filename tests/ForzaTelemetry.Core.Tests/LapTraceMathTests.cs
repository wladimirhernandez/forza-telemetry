using ForzaTelemetry.Core.Laps;

namespace ForzaTelemetry.Core.Tests;

public class LapTraceMathTests
{
    private static LapTraceSample S(float distance, float time, float speed = 0, float throttle = 0, byte gear = 1) =>
        new() { DistanceMeters = distance, TimeSeconds = time, SpeedMs = speed, Throttle = throttle, Gear = gear };

    private static readonly LapTraceSample[] Trace = [S(0, 0, 10, 0, 2), S(100, 10, 30, 1, 3), S(300, 20, 50, 0.5f, 4)];

    [Fact]
    public void Interpolates_every_field_between_the_neighbouring_samples()
    {
        Assert.True(LapTraceMath.TryInterpolate(Trace, 50, out var at));

        Assert.Equal(50, at.DistanceMeters);
        Assert.Equal(5f, at.TimeSeconds, 3);
        Assert.Equal(20f, at.SpeedMs, 3);
        Assert.Equal(0.5f, at.Throttle, 3);
    }

    [Fact]
    public void The_gear_is_the_lower_samples_gear_not_an_average()
    {
        Assert.True(LapTraceMath.TryInterpolate(Trace, 99, out var at));

        Assert.Equal(2, at.Gear);
    }

    [Fact]
    public void Exact_sample_distances_return_the_sample()
    {
        Assert.True(LapTraceMath.TryInterpolate(Trace, 100, out var at));
        Assert.Equal(10f, at.TimeSeconds, 3);

        Assert.True(LapTraceMath.TryInterpolate(Trace, 300, out var end));
        Assert.Equal(20f, end.TimeSeconds, 3);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(300.1)]
    [InlineData(double.NaN)]
    public void Outside_the_recorded_range_there_is_no_value(double distance)
    {
        Assert.False(LapTraceMath.TryInterpolate(Trace, distance, out _));
    }

    [Fact]
    public void A_trace_with_fewer_than_two_samples_has_no_value()
    {
        Assert.False(LapTraceMath.TryInterpolate(Trace.AsSpan(0, 1), 0, out _));
        Assert.False(LapTraceMath.TryInterpolate([], 0, out _));
    }

    [Fact]
    public void Delta_is_positive_when_the_current_lap_reached_the_distance_later()
    {
        // The reference needed 5 s to get to 50 m; the current lap took 6.5 s.
        Assert.Equal(1.5, LapTraceMath.DeltaSeconds(Trace, 50, 6.5)!.Value, 3);
    }

    [Fact]
    public void Delta_is_negative_when_the_current_lap_was_quicker()
    {
        Assert.Equal(-1.0, LapTraceMath.DeltaSeconds(Trace, 50, 4.0)!.Value, 3);
    }

    [Fact]
    public void Delta_is_unknown_beyond_the_reference_lap()
    {
        Assert.Null(LapTraceMath.DeltaSeconds(Trace, 400, 30));
    }
}
