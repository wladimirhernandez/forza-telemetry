using System.Windows;
using ForzaTelemetry.App.Controls;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App.Tests;

/// <summary>The pure math behind the trace chart: axis ticks, time windows, nearest sample, decimation.</summary>
public class ChartMathTests
{
    private static TelemetrySample At(uint timeMs, float speed = 0, byte gear = 3) =>
        new() { TimeMs = timeMs, SpeedMs = speed, Gear = gear };

    private static TelemetrySample[] Series(int count, uint stepMs = 16) =>
        [.. Enumerable.Range(0, count).Select(i => At((uint)(i * stepMs), i))];

    [Theory]
    [InlineData(30, 5, 10)]
    [InlineData(10, 5, 2)]
    [InlineData(60, 5, 20)]
    [InlineData(100, 5, 20)]
    [InlineData(0.9, 3, 0.5)]
    public void NiceStep_rounds_up_to_1_2_5_times_a_power_of_ten(double range, int ticks, double expected) =>
        Assert.Equal(expected, ChartMath.NiceStep(range, ticks), precision: 9);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void NiceStep_falls_back_to_one_for_unusable_ranges(double range) =>
        Assert.Equal(1, ChartMath.NiceStep(range, 5));

    [Theory]
    [InlineData(307, 514)]
    [InlineData(95, 140)]
    [InlineData(1200, 2100)]
    [InlineData(0.8, 0.3)]
    public void FitSharedAxes_covers_both_values_with_round_steps_and_little_waste(double a, double b)
    {
        var (divisions, stepA, stepB) = ChartMath.FitSharedAxes(a, b);

        Assert.InRange(divisions, 4, 8);
        Assert.True(stepA * divisions >= a && stepB * divisions >= b);
        foreach (var step in new[] { stepA, stepB })
        {
            var mantissa = Math.Round(step / Math.Pow(10, Math.Floor(Math.Log10(step))), 6);
            Assert.Contains(mantissa, new[] { 1.0, 2.0, 5.0 });
        }

        // No axis is more than ~1.6x taller than needed (a fixed five-division grid would give 1.63x on 307).
        Assert.True(stepA * divisions / a < 1.6 && stepB * divisions / b < 1.6);
    }

    [Fact]
    public void FitSharedAxes_survives_unusable_values()
    {
        var (divisions, stepA, stepB) = ChartMath.FitSharedAxes(0, double.NaN);

        Assert.InRange(divisions, 4, 8);
        Assert.True(stepA > 0 && stepB > 0);
    }

    [Fact]
    public void NiceCeiling_never_returns_less_than_the_value()
    {
        foreach (var value in new[] { 1.0, 37.0, 120.5, 299.9, 301.0 })
        {
            Assert.True(ChartMath.NiceCeiling(value, 4).Max >= value);
        }
    }

    [Fact]
    public void WindowStart_keeps_only_the_samples_inside_the_window()
    {
        var samples = Series(100, 100); // 0 .. 9900 ms

        var start = ChartMath.WindowStart(samples, 2_000);

        // Newest is 9900 ms, so 7900 ms (index 79) is the oldest sample inside a 2 s window.
        Assert.Equal(79, start);
    }

    [Fact]
    public void WindowStart_returns_the_first_sample_when_everything_fits()
    {
        Assert.Equal(0, ChartMath.WindowStart(Series(10), 60_000));
        Assert.Equal(0, ChartMath.WindowStart([], 60_000));
    }

    [Fact]
    public void WindowStart_stops_where_the_game_clock_restarts()
    {
        // Old session 0..1000 ms, then a new session whose clock starts again at 0.
        TelemetrySample[] samples = [At(0), At(500), At(1000), At(0), At(100), At(200)];

        Assert.Equal(3, ChartMath.WindowStart(samples, 60_000));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(149, 1)]
    [InlineData(151, 2)]
    [InlineData(10_000, 9)]
    [InlineData(-50, 0)]
    public void NearestIndex_finds_the_closest_sample_in_time(double timeMs, int expected)
    {
        var samples = Series(10, 100); // 0, 100, ... 900 ms

        // 149 is nearer to 100 (index 1); 151 is nearer to 200 (index 2); outside the data it clamps to the ends.
        Assert.Equal(expected, ChartMath.NearestIndex(samples, 0, samples.Length, timeMs));
    }

    [Fact]
    public void NearestIndex_respects_the_start_of_the_range_and_empty_ranges()
    {
        var samples = Series(10, 100);

        Assert.Equal(5, ChartMath.NearestIndex(samples, 5, 10, 0));
        Assert.Equal(-1, ChartMath.NearestIndex(samples, 4, 4, 0));
    }

    [Theory]
    [InlineData(0, -1)] // reverse
    [InlineData(11, 0)] // neutral
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    public void GearLevel_puts_reverse_below_neutral_below_first(byte gear, double expected) =>
        Assert.Equal(expected, ChartMath.GearLevel(gear));

    [Fact]
    public void Decimate_keeps_every_sample_when_data_is_sparser_than_a_pixel()
    {
        var samples = Series(20, 100); // 1 sample per 100 ms
        var output = new List<Point>();

        // 0.01 px/ms: 100 ms is one pixel, so each sample has a column of its own.
        ChartMath.Decimate(samples, 0, samples.Length, s => s.SpeedMs, 0, 0.01, output);

        Assert.Equal(20, output.Count);
    }

    [Fact]
    public void Decimate_bounds_the_points_per_pixel_column_and_never_loses_a_spike()
    {
        // 3600 samples (60 s at 60 Hz) onto 100 px, with one narrow spike hidden in the middle.
        var samples = Series(3600);
        var spikeIndex = 1801;
        samples[spikeIndex] = At(samples[spikeIndex].TimeMs, speed: 9999);
        var output = new List<Point>();
        var pixelsPerMs = 100.0 / (3600 * 16);

        ChartMath.Decimate(samples, 0, samples.Length, s => s.SpeedMs, 0, pixelsPerMs, output);

        Assert.True(output.Count <= 4 * 101, $"{output.Count} points is not decimated");
        Assert.Contains(output, p => p.Y == 9999);
        Assert.True(output.Zip(output.Skip(1)).All(pair => pair.Second.X >= pair.First.X), "points must stay in time order");
    }

    [Fact]
    public void Decimate_clears_the_output_and_handles_an_empty_run()
    {
        var output = new List<Point> { new(1, 1) };

        ChartMath.Decimate(Series(5), 2, 2, s => s.SpeedMs, 0, 1, output);

        Assert.Empty(output);
    }

    [Theory]
    [InlineData(1000, 0, 1000)] // no lap in progress yet
    [InlineData(1000, 400, 1000)] // a shorter lap in progress never changes the axis
    [InlineData(1000, 1000, 1000)]
    [InlineData(1000, 1001, 1100)] // past the end: widened by a tenth of the best lap, not by the metre
    [InlineData(1000, 1100, 1100)]
    [InlineData(1000, 1101, 1200)]
    [InlineData(0, 0, 1)] // a best lap of no length still gives a usable axis
    [InlineData(double.NaN, 0, 1)]
    public void The_distance_axis_follows_the_best_lap_and_widens_only_in_steps(double best, double current, double expected)
    {
        Assert.Equal(expected, ChartMath.StableAxisMeters(best, current), 6);
    }

    [Fact]
    public void A_lap_in_progress_growing_metre_by_metre_changes_the_axis_only_a_few_times()
    {
        var distinct = new HashSet<double>();

        for (var meters = 0; meters <= 2000; meters++)
        {
            distinct.Add(ChartMath.StableAxisMeters(1000, meters));
        }

        Assert.Equal(11, distinct.Count); // 1000 .. 2000 in tenths, instead of 2001 different scales
    }
}
