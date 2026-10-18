using Microsoft.Extensions.Time.Testing;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App.Tests;

/// <summary>Analysis, Map and Tuning view models: mappings from the engines to bound values, empty states, units.</summary>
public class InsightsViewModelTests
{
    private static TelemetrySample Sample(
        uint timeMs = 0, float speed = 30, float rpm = 4500, byte gear = 3, float throttle = 0.5f, float brake = 0,
        float steer = 0, float x = 0, float z = 0,
        float slipFl = 0, float slipFr = 0, float slipRl = 0, float slipRr = 0,
        float tempFl = 0, float tempFr = 0, float tempRl = 0, float tempRr = 0) => new()
    {
        TimeMs = timeMs, SpeedMs = speed, Rpm = rpm, MaxRpm = 8000, Gear = gear, Throttle = throttle, Brake = brake,
        Steer = steer, PositionX = x, PositionZ = z,
        SlipAngleFrontLeft = slipFl, SlipAngleFrontRight = slipFr, SlipAngleRearLeft = slipRl, SlipAngleRearRight = slipRr,
        TireTempFrontLeft = tempFl, TireTempFrontRight = tempFr, TireTempRearLeft = tempRl, TireTempRearRight = tempRr,
    };

    // ---- Analysis ---------------------------------------------------------------------------------

    [Fact]
    public void Analysis_starts_empty_and_offers_the_three_windows()
    {
        var vm = new AnalysisViewModel(new TelemetryHistory(), new UnitsService());

        vm.Refresh();

        Assert.False(vm.HasData);
        Assert.True(vm.IsEmpty);
        Assert.Equal(0, vm.SampleCount);
        Assert.Equal([10, 30, 60], vm.WindowOptions.Select(o => o.Value));
        Assert.Equal(30, vm.WindowSeconds);
    }

    [Fact]
    public void Analysis_maps_the_newest_sample_to_the_readouts()
    {
        var history = new TelemetryHistory();
        history.Add(Sample(timeMs: 0, speed: 10));
        history.Add(Sample(timeMs: 16, speed: 50, rpm: 6789, gear: 4, throttle: 0.87f, brake: 0.25f, steer: -0.35f));
        var vm = new AnalysisViewModel(history, new UnitsService());

        vm.Refresh();

        Assert.True(vm.HasData);
        Assert.False(vm.IsEmpty);
        Assert.Equal(2, vm.SampleCount);
        Assert.Equal("180", vm.SpeedText); // 50 m/s = 180 km/h
        Assert.Equal("6789", vm.RpmText);
        Assert.Equal("4", vm.GearText);
        Assert.Equal("87%", vm.ThrottleText);
        Assert.Equal("25%", vm.BrakeText);
        Assert.Equal(InsightsStrings.SteerLeftShort + " 35%", vm.SteerText);
    }

    [Fact]
    public void Analysis_bumps_the_data_version_only_when_a_new_sample_arrives()
    {
        var history = new TelemetryHistory();
        history.Add(Sample(timeMs: 0));
        var vm = new AnalysisViewModel(history, new UnitsService());
        vm.Refresh();
        var version = vm.DataVersion;

        vm.Refresh();
        Assert.Equal(version, vm.DataVersion); // nothing new: the chart must not rebuild

        history.Add(Sample(timeMs: 16));
        vm.Refresh();
        Assert.Equal(version + 1, vm.DataVersion);
    }

    [Fact]
    public void Analysis_follows_the_speed_unit()
    {
        var history = new TelemetryHistory();
        history.Add(Sample(speed: 44.704f)); // 100 mph
        var units = new UnitsService();
        var vm = new AnalysisViewModel(history, units);
        vm.Refresh();
        Assert.Equal(3.6, vm.SpeedScale, precision: 9);
        Assert.Equal(units.SpeedLabel, vm.SpeedUnitLabel);

        units.SpeedUnit = SpeedUnit.Mph;
        vm.Refresh();

        Assert.Equal("100", vm.SpeedText);
        Assert.Equal(3.6 / UnitsService.KilometersPerMile, vm.SpeedScale, precision: 9);
        Assert.Equal(units.SpeedLabel, vm.SpeedUnitLabel);
    }

    [Fact]
    public void Analysis_does_not_copy_again_when_no_new_sample_arrived()
    {
        var history = new TelemetryHistory();
        history.Add(Sample(timeMs: 0, speed: 20));
        var vm = new AnalysisViewModel(history, new UnitsService());
        vm.Refresh();
        Assert.Equal(20, vm.Samples[0].SpeedMs);

        vm.Samples[0] = default; // a marker: a second copy would overwrite it
        vm.Refresh();

        Assert.Equal(0, vm.Samples[0].SpeedMs);
        Assert.Equal(1, vm.SampleCount);
    }

    [Fact]
    public void Analysis_copies_only_the_visible_window_at_a_normal_packet_rate()
    {
        var history = new TelemetryHistory();
        for (var i = 0; i < history.Capacity; i++)
        {
            history.Add(Sample(timeMs: (uint)(i * 16)));
        }

        var vm = new AnalysisViewModel(history, new UnitsService()) { WindowSeconds = 10 };
        vm.Refresh();

        Assert.True(vm.SampleCount < history.Capacity / 2, $"copied {vm.SampleCount} of {history.Capacity}");
        // Whatever was copied still covers the whole 10 s window.
        var span = vm.Samples[vm.SampleCount - 1].TimeMs - vm.Samples[0].TimeMs;
        Assert.True(span >= 10_000, $"{span} ms is not a full window");
    }

    [Fact]
    public void Analysis_copies_more_when_the_stream_is_faster_than_assumed()
    {
        var history = new TelemetryHistory();
        for (var i = 0; i < history.Capacity; i++)
        {
            history.Add(Sample(timeMs: (uint)i)); // 1000 Hz: the first guess covers well under 10 s
        }

        var vm = new AnalysisViewModel(history, new UnitsService()) { WindowSeconds = 10 };
        vm.Refresh();

        Assert.Equal(history.Capacity, vm.SampleCount);
    }

    [Theory]
    [InlineData(0f, "0%")]
    [InlineData(0.004f, "0%")]
    [InlineData(1f, "R 100%")]
    [InlineData(-0.5f, "L 50%")]
    public void FormatSteer_shows_direction_and_magnitude(float steer, string expected)
    {
        var text = ReadoutFormat.Steer(steer)
            .Replace(InsightsStrings.SteerRightShort, "R").Replace(InsightsStrings.SteerLeftShort, "L");

        Assert.Equal(expected, text);
    }

    // ---- Map --------------------------------------------------------------------------------------

    [Fact]
    public void Map_starts_empty()
    {
        var vm = new MapViewModel(new TrackPath(), new TelemetryHistory(), new UnitsService());

        vm.Refresh();

        Assert.False(vm.HasData);
        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasCurrent);
        Assert.Equal(0, vm.BrakingZones);
    }

    [Fact]
    public void Map_copies_the_path_and_reports_its_statistics()
    {
        var path = new TrackPath(minSpacingMeters: 1f);
        var history = new TelemetryHistory();
        // 0, 10, 20, 30 m along X at increasing speed; braking on the last two.
        for (var i = 0; i < 4; i++)
        {
            path.OnPacket(InsightPackets.At(i * 10, 0, speed: 10 + (i * 10), brake: i >= 2 ? (byte)200 : (byte)0));
        }

        history.Add(Sample(x: 30, z: 5));
        var vm = new MapViewModel(path, history, new UnitsService());

        vm.Refresh();

        Assert.True(vm.HasData);
        Assert.Equal(4, vm.PointCount);
        Assert.Equal(10, vm.MinSpeedMs);
        Assert.Equal(40, vm.MaxSpeedMs);
        Assert.Equal(1, vm.BrakingZones);
        Assert.Equal("0.03 km", vm.DistanceText.Replace(',', '.'));
        Assert.Equal("144 km/h", vm.TopSpeedText); // 40 m/s
        Assert.True(vm.HasCurrent);
        Assert.Equal(30, vm.CurrentX);
        Assert.Equal(5, vm.CurrentZ);
    }

    [Fact]
    public void Map_rebuilds_only_when_the_path_changed()
    {
        var clock = new FakeTimeProvider();
        var path = new TrackPath(minSpacingMeters: 1f);
        path.OnPacket(InsightPackets.At(0, 0));
        path.OnPacket(InsightPackets.At(10, 0));
        var vm = new MapViewModel(path, new TelemetryHistory(), new UnitsService(), clock);
        vm.Refresh();
        var version = vm.DataVersion;

        vm.Refresh();
        Assert.Equal(version, vm.DataVersion);

        path.OnPacket(InsightPackets.At(20, 0));
        clock.Advance(MapViewModel.CopyInterval);
        vm.Refresh();
        Assert.Equal(version + 1, vm.DataVersion);
        Assert.Equal(3, vm.PointCount);
    }

    [Fact]
    public void Map_throttles_path_copies_but_applies_a_clear_at_once()
    {
        var clock = new FakeTimeProvider();
        var path = new TrackPath(minSpacingMeters: 1f);
        path.OnPacket(InsightPackets.At(0, 0));
        path.OnPacket(InsightPackets.At(10, 0));
        var vm = new MapViewModel(path, new TelemetryHistory(), new UnitsService(), clock);
        vm.Refresh();
        Assert.Equal(2, vm.PointCount);

        path.OnPacket(InsightPackets.At(20, 0));
        vm.Refresh(); // inside the interval: no O(n) copy
        Assert.Equal(2, vm.PointCount);

        clock.Advance(MapViewModel.CopyInterval);
        vm.Refresh();
        Assert.Equal(3, vm.PointCount);

        vm.ClearCommand.Execute(null);
        Assert.Equal(0, vm.PointCount);
    }

    [Fact]
    public void Map_Clear_resets_the_view_and_the_position_dot_until_newer_data_arrives()
    {
        var path = new TrackPath(minSpacingMeters: 1f);
        var history = new TelemetryHistory();
        path.OnPacket(InsightPackets.At(0, 0));
        path.OnPacket(InsightPackets.At(10, 0));
        history.Add(Sample(timeMs: 5, x: 10, z: 0));
        var vm = new MapViewModel(path, history, new UnitsService());
        vm.Refresh();
        vm.ViewState.Viewport = new Controls.MapViewport(2, 1, 1);
        vm.ViewState.FitScale = 2;
        vm.ViewState.Manual = true;
        Assert.True(vm.HasCurrent);

        vm.ClearCommand.Execute(null);

        Assert.False(vm.ViewState.Manual);
        Assert.False(vm.HasCurrent);
        Assert.Equal(0, vm.CurrentX);
        vm.Refresh();
        Assert.False(vm.HasCurrent); // the old sample is not redrawn

        history.Add(Sample(timeMs: 50, x: 3, z: 4));
        vm.Refresh();
        Assert.True(vm.HasCurrent);
        Assert.Equal(3, vm.CurrentX);
    }

    [Fact]
    public void Map_resets_the_view_when_the_path_was_cleared_elsewhere()
    {
        var clock = new FakeTimeProvider();
        var path = new TrackPath(minSpacingMeters: 1f);
        for (var i = 0; i < 40; i++)
        {
            path.OnPacket(InsightPackets.At(i * 2f, 0));
        }

        var vm = new MapViewModel(path, new TelemetryHistory(), new UnitsService(), clock);
        vm.Refresh();
        vm.ViewState.Manual = true;

        path.Clear();
        path.OnPacket(InsightPackets.At(0, 0));
        path.OnPacket(InsightPackets.At(5, 0));
        clock.Advance(MapViewModel.CopyInterval);
        vm.Refresh();

        Assert.False(vm.ViewState.Manual);
    }

    [Fact]
    public void Map_Clear_empties_the_path_immediately()
    {
        var path = new TrackPath(minSpacingMeters: 1f);
        path.OnPacket(InsightPackets.At(0, 0));
        path.OnPacket(InsightPackets.At(10, 0));
        var vm = new MapViewModel(path, new TelemetryHistory(), new UnitsService());
        vm.Refresh();
        Assert.True(vm.HasData);

        vm.ClearCommand.Execute(null);

        Assert.Equal(0, path.Count);
        Assert.False(vm.HasData);
        Assert.Equal(0, vm.PointCount);
    }

    [Fact]
    public void Map_grows_its_copy_buffer_for_long_paths()
    {
        var path = new TrackPath(minSpacingMeters: 1f);
        for (var i = 0; i < 600; i++)
        {
            path.OnPacket(InsightPackets.At(i * 2f, 0));
        }

        var vm = new MapViewModel(path, new TelemetryHistory(), new UnitsService());
        vm.Refresh();

        Assert.Equal(path.Count, vm.PointCount);
        Assert.True(vm.Points.Length >= vm.PointCount);
    }

    [Theory]
    [InlineData(0, SpeedUnit.Kmh, "0.00 km")]
    [InlineData(2500, SpeedUnit.Kmh, "2.50 km")]
    [InlineData(25000, SpeedUnit.Kmh, "25.0 km")]
    [InlineData(1609.344, SpeedUnit.Mph, "1.00 mi")]
    public void FormatDistance_follows_the_unit_system(double meters, SpeedUnit unit, string expected) =>
        Assert.Equal(expected, MapViewModel.FormatDistance(meters, unit).Replace(',', '.'));

    // ---- Tuning -----------------------------------------------------------------------------------

    private static TuningViewModel NewTuning(
        out DynoCapture dyno, out TelemetryHistory history, out SuspensionBottomingCounter bottoming,
        UnitsService? units = null, TimeProvider? time = null)
    {
        dyno = new DynoCapture();
        history = new TelemetryHistory();
        bottoming = new SuspensionBottomingCounter();
        return new TuningViewModel(dyno, history, bottoming, units ?? new UnitsService(), time);
    }

    [Fact]
    public void Tuning_starts_with_calm_empty_states()
    {
        var vm = NewTuning(out _, out _, out _);

        vm.Refresh();

        Assert.True(vm.IsEmpty);
        Assert.True(vm.HasNoCurve);
        Assert.False(vm.BalanceActive);
        Assert.Equal(InsightsStrings.NoCarYet, vm.CarText);
        Assert.False(vm.HasCar);
        Assert.Equal(InsightsStrings.BalanceUnknown, vm.BalanceAdvice);
        Assert.Equal([InsightsStrings.TiresNoData], vm.TireLines);
        Assert.Equal(InsightsStrings.SuspensionAdviceNone, vm.SuspensionAdvice);
    }

    [Fact]
    public void Tuning_exposes_the_dyno_curve_of_the_current_car_and_can_reset_it()
    {
        var vm = NewTuning(out var dyno, out _, out _);
        for (var rpm = 3000; rpm <= 6000; rpm += 100)
        {
            dyno.OnPacket(InsightPackets.Pull(rpm, powerWatts: rpm * 40f, torque: 300, carOrdinal: 2211, performanceIndex: 612));
        }

        vm.Refresh();

        Assert.True(vm.HasCurve);
        Assert.Equal(31, vm.Curve.Count);
        Assert.True(vm.HasCar);
        Assert.Contains("2211", vm.CarText);
        Assert.Contains("612", vm.PiText);
        Assert.DoesNotContain("612", vm.CarText); // car and PI are separate pills, not one joined string
        var version = vm.CurveVersion;

        vm.Refresh();
        Assert.Equal(version, vm.CurveVersion); // unchanged curve: the chart does not rebuild

        vm.ResetDynoCommand.Execute(null);
        Assert.True(vm.HasNoCurve);
        Assert.Empty(vm.Curve);
        Assert.True(vm.CurveVersion > version);
    }

    [Fact]
    public void Tuning_reads_understeer_from_a_front_that_slides_more_and_explains_it()
    {
        var vm = NewTuning(out _, out var history, out _);
        history.Add(Sample(timeMs: 1, slipFl: 0.40f, slipFr: 0.36f, slipRl: 0.15f, slipRr: 0.17f));

        vm.Refresh();

        Assert.True(vm.BalanceActive);
        Assert.True(vm.BalanceValue > BalanceAnalyzer.BalanceThreshold);
        Assert.Equal(BalanceState.Understeer, vm.BalanceState);
        Assert.Equal(InsightsStrings.BalanceUndersteer, vm.BalanceLabel);
        Assert.Equal(InsightsStrings.BalanceAdviceUndersteer, vm.BalanceAdvice);
    }

    [Fact]
    public void Tuning_reads_oversteer_from_a_rear_that_slides_more()
    {
        var vm = NewTuning(out _, out var history, out _);
        history.Add(Sample(timeMs: 1, slipFl: 0.10f, slipFr: 0.12f, slipRl: 0.40f, slipRr: 0.38f));

        vm.Refresh();

        Assert.Equal(BalanceState.Oversteer, vm.BalanceState);
        Assert.True(vm.BalanceValue < -BalanceAnalyzer.BalanceThreshold);
        Assert.Equal(InsightsStrings.BalanceAdviceOversteer, vm.BalanceAdvice);
    }

    [Fact]
    public void Tuning_smooths_the_balance_and_hides_it_after_the_hold_time_without_a_reading()
    {
        var clock = new FakeTimeProvider();
        var vm = NewTuning(out _, out var history, out _, time: clock);
        history.Add(Sample(timeMs: 1, slipFl: 0.5f, slipFr: 0.5f, slipRl: 0.1f, slipRr: 0.1f)); // strong understeer: index +0.67
        vm.Refresh();
        var first = vm.BalanceValue;

        // A new, opposite reading moves the value only part of the way (exponential smoothing).
        clock.Advance(TimeSpan.FromMilliseconds(50));
        history.Add(Sample(timeMs: 20, slipFl: 0.1f, slipFr: 0.1f, slipRl: 0.5f, slipRr: 0.5f)); // index -0.67
        vm.Refresh();
        Assert.True(vm.BalanceValue < first && vm.BalanceValue > -first);

        // No new samples: the last value is held while the clock is inside the hold, then hidden.
        clock.Advance(TuningViewModel.BalanceHold - TimeSpan.FromMilliseconds(100));
        vm.Refresh();
        Assert.True(vm.BalanceActive);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        vm.Refresh();
        Assert.False(vm.BalanceActive);
        Assert.Equal(BalanceState.Unknown, vm.BalanceState);
        Assert.Equal(InsightsStrings.BalanceUnknown, vm.BalanceAdvice);
    }

    [Fact]
    public void Tuning_does_not_blend_into_a_stale_balance_after_returning_to_the_page()
    {
        var clock = new FakeTimeProvider();
        var vm = NewTuning(out _, out var history, out _, time: clock);
        history.Add(Sample(timeMs: 1, slipFl: 0.5f, slipFr: 0.5f, slipRl: 0.1f, slipRr: 0.1f)); // understeer
        vm.Refresh();

        // The user is on another tab for a minute (no refreshes), then comes back to an oversteering car.
        clock.Advance(TimeSpan.FromMinutes(1));
        history.Add(Sample(timeMs: 60_000, slipFl: 0.1f, slipFr: 0.1f, slipRl: 0.5f, slipRr: 0.5f));
        vm.Refresh();

        Assert.Equal(BalanceState.Oversteer, vm.BalanceState);
        Assert.True(vm.BalanceValue < -0.6, "the first reading after a stale gap must snap, not blend with the old value");
    }

    [Fact]
    public void Tuning_polls_the_dyno_at_a_low_rate_but_reset_applies_at_once()
    {
        var clock = new FakeTimeProvider();
        var vm = NewTuning(out var dyno, out _, out _, time: clock);
        dyno.OnPacket(InsightPackets.Pull(3000, 100_000, 300, 1, 1));
        vm.Refresh();
        Assert.Single(vm.Curve);

        dyno.OnPacket(InsightPackets.Pull(3100, 110_000, 310, 1, 1));
        vm.Refresh(); // same instant: polled recently, not read again
        Assert.Single(vm.Curve);

        clock.Advance(TuningViewModel.DynoPollInterval);
        vm.Refresh();
        Assert.Equal(2, vm.Curve.Count);

        vm.ResetDynoCommand.Execute(null); // no waiting for the next poll
        Assert.Empty(vm.Curve);
    }

    [Fact]
    public void Tuning_reports_bottoming_per_corner_and_resets()
    {
        var vm = NewTuning(out _, out var history, out var bottoming);
        history.Add(Sample(timeMs: 1)); // some sample so the page has data
        for (var i = 0; i < 4; i++)
        {
            bottoming.OnPacket(InsightPackets.Suspension(frontLeft: 0.99f));
            bottoming.OnPacket(InsightPackets.Suspension(frontLeft: 0.2f));
        }

        bottoming.OnPacket(InsightPackets.Suspension(rearRight: 0.99f));

        vm.Refresh();

        Assert.Equal(4, vm.BottomFrontLeft);
        Assert.Equal(1, vm.BottomRearRight);
        Assert.Equal(0, vm.BottomFrontRight);
        Assert.Equal(2, vm.SeverityFrontLeft); // frequent
        Assert.Equal(1, vm.SeverityRearRight); // occasional
        Assert.Equal(0, vm.SeverityRearLeft);
        Assert.Equal(InsightsStrings.SuspensionAdviceFront, vm.SuspensionAdvice);

        vm.ResetBottomingCommand.Execute(null);
        bottoming.OnPacket(InsightPackets.Suspension()); // the writer applies the reset on its next packet
        vm.Refresh();
        Assert.Equal(0, vm.BottomFrontLeft);
        Assert.Equal(InsightsStrings.SuspensionAdviceNone, vm.SuspensionAdvice);
    }

    [Fact]
    public void Tuning_formats_tire_temperatures_with_the_chosen_unit_and_colors_them_by_band()
    {
        var units = new UnitsService();
        var vm = NewTuning(out _, out var history, out _, units);
        // Wire values are Fahrenheit: 68 F = 20 C (cold), 176 F = 80 C (ideal), 248 F = 120 C (hot).
        history.Add(Sample(timeMs: 1, tempFl: 68, tempFr: 176, tempRl: 176, tempRr: 248));

        vm.Refresh();

        Assert.Equal("20 " + units.TemperatureLabel, vm.TempFrontLeft);
        Assert.Equal("80 " + units.TemperatureLabel, vm.TempFrontRight);
        Assert.Equal(TireTempBand.Cold, vm.BandFrontLeft);
        Assert.Equal(TireTempBand.Ideal, vm.BandFrontRight);
        Assert.Equal(TireTempBand.Hot, vm.BandRearRight);

        units.TemperatureUnit = TemperatureUnit.Fahrenheit;
        vm.Refresh();
        Assert.Equal("68 " + units.TemperatureLabel, vm.TempFrontLeft);
    }

    [Fact]
    public void Tuning_gives_tire_advice_for_a_hot_front_axle()
    {
        var vm = NewTuning(out _, out var history, out _);
        history.Add(Sample(timeMs: 1, tempFl: 230, tempFr: 228, tempRl: 190, tempRr: 188));

        vm.Refresh();

        Assert.Contains(InsightsStrings.TiresAdviceFrontHotter, vm.TireLines);
    }

    [Fact]
    public void Tuning_says_tires_are_balanced_when_no_flag_is_set()
    {
        var vm = NewTuning(out _, out var history, out _);
        history.Add(Sample(timeMs: 1, tempFl: 180, tempFr: 182, tempRl: 178, tempRr: 180));

        vm.Refresh();

        Assert.Equal([InsightsStrings.TiresAdviceBalanced], vm.TireLines);
    }

    [Fact]
    public void Tuning_treats_all_zero_tire_temperatures_as_no_data()
    {
        var vm = NewTuning(out _, out var history, out _);
        history.Add(Sample(timeMs: 1));

        vm.Refresh();

        Assert.Equal("-", vm.TempFrontLeft);
        Assert.Equal(TireTempBand.Unknown, vm.BandFrontLeft);
        Assert.Equal([InsightsStrings.TiresNoData], vm.TireLines);
    }
}
