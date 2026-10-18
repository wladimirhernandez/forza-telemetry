using System.Windows;
using System.Windows.Media;
using ForzaTelemetry.App.Controls;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App.Tests;

/// <summary>Color ramp, viewport fit/zoom and track statistics used by the map.</summary>
public class MapMathTests
{
    [Fact]
    public void SpeedRamp_starts_cold_and_ends_at_the_brake_color()
    {
        Assert.Equal(ChartPalette.Cold, SpeedRamp.Evaluate(0));
        Assert.Equal(ChartPalette.Brake, SpeedRamp.Evaluate(1));
    }

    [Fact]
    public void SpeedRamp_passes_through_the_token_colors_in_order()
    {
        Assert.Equal(ChartPalette.Throttle, SpeedRamp.Evaluate(0.40));
        Assert.Equal(ChartPalette.Warm, SpeedRamp.Evaluate(0.75));
    }

    [Fact]
    public void SpeedRamp_clamps_out_of_range_and_non_numeric_input()
    {
        Assert.Equal(SpeedRamp.Evaluate(0), SpeedRamp.Evaluate(-3));
        Assert.Equal(SpeedRamp.Evaluate(1), SpeedRamp.Evaluate(7));
        Assert.Equal(SpeedRamp.Evaluate(0), SpeedRamp.Evaluate(double.NaN));
    }

    [Fact]
    public void SpeedRamp_blends_linearly_between_stops()
    {
        var mid = SpeedRamp.Evaluate(0.2); // halfway between Cold (0) and Throttle (0.4)

        Assert.Equal((byte)Math.Round((ChartPalette.Cold.R + ChartPalette.Throttle.R) / 2.0), mid.R);
        Assert.Equal((byte)Math.Round((ChartPalette.Cold.G + ChartPalette.Throttle.G) / 2.0), mid.G);
        Assert.Equal((byte)Math.Round((ChartPalette.Cold.B + ChartPalette.Throttle.B) / 2.0), mid.B);
    }

    [Theory]
    [InlineData(10, 10, 50, 0)]
    [InlineData(50, 10, 50, 1)]
    [InlineData(30, 10, 50, 0.5)]
    [InlineData(5, 10, 50, 0)]
    [InlineData(99, 10, 50, 1)]
    public void Normalize_maps_a_speed_into_the_session_range(double value, double min, double max, double expected) =>
        Assert.Equal(expected, SpeedRamp.Normalize(value, min, max), precision: 9);

    [Fact]
    public void Normalize_of_a_flat_range_is_the_middle_of_the_ramp()
    {
        Assert.Equal(0.5, SpeedRamp.Normalize(20, 20, 20));
        Assert.Equal(0.5, SpeedRamp.Normalize(double.NaN, 0, 10));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 16)]
    [InlineData(1, 31)]
    [InlineData(-1, 0)]
    [InlineData(2, 31)]
    public void Bucket_quantizes_into_the_available_colors(double t, int expected) =>
        Assert.Equal(expected, SpeedRamp.Bucket(t, 32));

    [Fact]
    public void Fit_preserves_the_aspect_ratio_and_centers_the_path()
    {
        // A 1000 x 500 m area into a 1000 x 1000 px box with 50 px padding: the width limits the scale.
        var view = MapViewport.Fit(0, 1000, 0, 500, 1000, 1000, 50);

        Assert.Equal(0.9, view.Scale, precision: 9);
        var left = view.ToScreen(0, 250);
        var right = view.ToScreen(1000, 250);
        Assert.Equal(50, left.X, precision: 6);
        Assert.Equal(950, right.X, precision: 6);
        // Vertically centered: the area's middle lands in the middle of the box.
        Assert.Equal(500, left.Y, precision: 6);
    }

    [Fact]
    public void Fit_draws_north_up_so_a_larger_z_is_higher_on_screen()
    {
        var view = MapViewport.Fit(0, 100, 0, 100, 400, 400, 20);

        Assert.True(view.ToScreen(0, 90).Y < view.ToScreen(0, 10).Y);
    }

    [Fact]
    public void Fit_survives_a_single_point_without_dividing_by_zero()
    {
        var view = MapViewport.Fit(5, 5, 7, 7, 400, 300, 20);

        Assert.True(double.IsFinite(view.Scale) && view.Scale > 0);
        var p = view.ToScreen(5, 7);
        Assert.Equal(200, p.X, precision: 6);
        Assert.Equal(150, p.Y, precision: 6);
    }

    [Fact]
    public void ZoomAt_keeps_the_world_point_under_the_cursor_fixed()
    {
        var view = MapViewport.Fit(0, 1000, 0, 500, 800, 600, 40);
        var cursor = new Point(300, 200);
        // The world point currently under the cursor.
        var worldX = (cursor.X - view.OffsetX) / view.Scale;
        var worldZ = (view.OffsetY - cursor.Y) / view.Scale;

        var zoomed = view.ZoomAt(cursor, 3, 0.01, 100);

        Assert.Equal(view.Scale * 3, zoomed.Scale, precision: 9);
        var after = zoomed.ToScreen(worldX, worldZ);
        Assert.Equal(cursor.X, after.X, precision: 6);
        Assert.Equal(cursor.Y, after.Y, precision: 6);
    }

    [Fact]
    public void ZoomAt_respects_the_scale_limits()
    {
        var view = new MapViewport(1, 0, 0);

        Assert.Equal(4, view.ZoomAt(new Point(0, 0), 100, 0.5, 4).Scale);
        Assert.Equal(0.5, view.ZoomAt(new Point(0, 0), 0.001, 0.5, 4).Scale);
    }

    [Fact]
    public void Pan_moves_the_view_by_the_drag_delta()
    {
        var view = new MapViewport(2, 10, 20).Pan(5, -3);

        Assert.Equal(new MapViewport(2, 15, 17), view);
    }

    private static TrackPoint P(float x, float z, float speed = 10, bool braking = false, bool starts = false) =>
        new(x, z, speed, braking, starts);

    [Fact]
    public void TrackStats_of_an_empty_path_is_zero() =>
        Assert.Equal(default, TrackStats.Compute([]));

    [Fact]
    public void TrackStats_sums_segment_lengths_and_reports_the_speed_range()
    {
        TrackPoint[] points = [P(0, 0, 5), P(3, 4, 20), P(3, 14, 12)];

        var stats = TrackStats.Compute(points);

        Assert.Equal(15, stats.DistanceMeters, precision: 6); // 5 m + 10 m
        Assert.Equal(5, stats.MinSpeedMs);
        Assert.Equal(20, stats.MaxSpeedMs);
    }

    [Fact]
    public void TrackStats_does_not_measure_across_a_jump()
    {
        TrackPoint[] points = [P(0, 0), P(10, 0), P(5000, 5000, starts: true), P(5010, 5000)];

        Assert.Equal(20, TrackStats.Compute(points).DistanceMeters, precision: 6);
    }

    [Fact]
    public void TrackStats_counts_each_braking_zone_once()
    {
        TrackPoint[] points =
        [
            P(0, 0), P(1, 0, braking: true), P(2, 0, braking: true), P(3, 0), // zone 1
            P(4, 0, braking: true), P(5, 0),                                   // zone 2
            P(6, 0, braking: true, starts: true),                               // zone 3: a braking point right after a jump
        ];

        Assert.Equal(3, TrackStats.Compute(points).BrakingZones);
    }

    [Fact]
    public void TrackStats_counts_a_zone_that_is_braking_from_the_first_point() =>
        Assert.Equal(1, TrackStats.Compute([P(0, 0, braking: true), P(1, 0, braking: true)]).BrakingZones);

    [Fact]
    public void Smooth_averages_linked_neighbors_and_never_bridges_a_gap()
    {
        DynoPoint[] curve =
        [
            new(3000, 100_000, 300), new(3100, 130_000, 330), new(3200, 100_000, 300), // linked run
            new(6000, 200_000, 400),                                                    // isolated: gap > 250 rpm on both sides
        ];

        var smooth = DynoMath.Smooth(curve);

        Assert.Equal(4, smooth.Length);
        Assert.Equal(115_000, smooth[0].PowerWatts, precision: 0); // (100k + 130k) / 2: end point uses its one neighbor
        Assert.Equal(110_000, smooth[1].PowerWatts, precision: 0); // (100k + 130k + 100k) / 3
        Assert.Equal(200_000, smooth[3].PowerWatts);                // untouched: nothing to average with
        Assert.Equal(curve.Select(p => p.Rpm), smooth.Select(p => p.Rpm));
    }

    [Fact]
    public void FindPeaks_returns_the_highest_power_and_torque_indexes()
    {
        DynoPoint[] curve = [new(3000, 100_000, 450), new(5000, 220_000, 400), new(7000, 200_000, 300)];

        Assert.Equal((1, 0), DynoMath.FindPeaks(curve));
        Assert.Equal((-1, -1), DynoMath.FindPeaks([]));
    }

    [Fact]
    public void ToHorsepower_uses_the_mechanical_horsepower() =>
        Assert.Equal(1, DynoMath.ToHorsepower(745.69987158227), precision: 9);
}
