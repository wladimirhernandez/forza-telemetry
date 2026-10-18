using ForzaTelemetry.Core.Performance;

namespace ForzaTelemetry.Core.Tests;

/// <summary>Tests for the rolling and mph windows added on top of the original 0-100 / 0-200 / quarter-mile results.</summary>
public class AccelerationWindowsTests
{
    private const double Kmh100 = 100.0 / 3.6;
    private const double Kmh200 = 200.0 / 3.6;
    private const double Mph60 = 60.0 * 1.609344 / 3.6;
    private const double Mph130 = 130.0 * 1.609344 / 3.6;

    /// <summary>Stationary lead-in then constant acceleration: speed a*t, so each crossing time is v/a.</summary>
    private static void Launch(AccelerationTimer timer, double a, double durationSeconds, uint stepMs = 20, uint t0 = 0)
    {
        for (var ms = t0; ms < t0 + 1000; ms += stepMs)
        {
            timer.OnPacket(TestPackets.Drive(ms, 0f, 0f));
        }

        for (var ms = t0 + 1000; ms <= t0 + 1000 + (durationSeconds * 1000); ms += stepMs)
        {
            var s = (ms - t0 - 1000) / 1000.0;
            timer.OnPacket(TestPackets.Drive(ms, (float)(a * s), (float)(0.5 * a * s * s)) with { Accel = 255 });
        }
    }

    [Fact]
    public void Rolling_and_mph_windows_match_constant_acceleration_maths()
    {
        const double a = 10;
        var timer = new AccelerationTimer();

        Launch(timer, a, durationSeconds: 12);

        var snap = timer.Current;
        Assert.Equal(AccelerationState.Done, snap.State);
        Assert.Equal((Kmh200 - Kmh100) / a, snap.HundredTo200KmhSeconds!.Value, 0.005);
        Assert.Equal((Mph130 - Mph60) / a, snap.SixtyTo130MphSeconds!.Value, 0.005);
        // 0-60 mph is measured from the 1 km/h clock start, exactly like 0-100 km/h.
        Assert.Equal((Mph60 / a) - ((1.0 / 3.6) / a), snap.ZeroTo60MphSeconds!.Value, 0.005);
    }

    [Fact]
    public void Windows_stay_accurate_with_coarse_packets()
    {
        const double a = 10;
        var timer = new AccelerationTimer();

        Launch(timer, a, durationSeconds: 12, stepMs: 100);

        Assert.Equal((Kmh200 - Kmh100) / a, timer.Current.HundredTo200KmhSeconds!.Value, 0.01);
        Assert.Equal((Mph130 - Mph60) / a, timer.Current.SixtyTo130MphSeconds!.Value, 0.01);
    }

    [Fact]
    public void Run_stays_open_past_200_kmh_until_130_mph_is_reached()
    {
        const double a = 4;
        var timer = new AccelerationTimer();

        // At 4 m/s^2 the quarter mile falls at ~14.2 s (56.7 m/s = 204 km/h). Stopping the feed at 14.5 s
        // (58.0 m/s) is past 200 km/h and the quarter mile but still short of 130 mph (58.1 m/s).
        Launch(timer, a, durationSeconds: 14.5);

        var snap = timer.Current;
        Assert.Equal(AccelerationState.Running, snap.State);
        Assert.NotNull(snap.ZeroTo200KmhSeconds);
        Assert.NotNull(snap.HundredTo200KmhSeconds);
        Assert.Null(snap.SixtyTo130MphSeconds);
    }

    [Fact]
    public void Partial_run_reports_only_the_windows_it_completed()
    {
        const double a = 10;
        var timer = new AccelerationTimer();

        Launch(timer, a, durationSeconds: 4); // 40 m/s = 144 km/h

        var snap = timer.Current;
        Assert.NotNull(snap.ZeroTo60MphSeconds);
        Assert.Null(snap.HundredTo200KmhSeconds);
        Assert.Null(snap.SixtyTo130MphSeconds);
    }

    [Fact]
    public void Existing_snapshot_fields_and_positional_constructor_are_unchanged()
    {
        const double a = 10;
        var timer = new AccelerationTimer();
        Launch(timer, a, durationSeconds: 12);
        var snap = timer.Current;

        Assert.Equal((Kmh100 - (1.0 / 3.6)) / a, snap.ZeroTo100KmhSeconds!.Value, 0.005);
        Assert.Equal((Kmh200 - (1.0 / 3.6)) / a, snap.ZeroTo200KmhSeconds!.Value, 0.005);
        Assert.NotNull(snap.QuarterMileSeconds);
        Assert.NotNull(snap.QuarterMileTrapSpeedKmh);

        // The six-argument form used before the windows existed still compiles and leaves them null.
        var legacy = new AccelerationSnapshot(AccelerationState.Done, 0, 1.0, 2.0, 3.0, 4.0);
        Assert.Null(legacy.HundredTo200KmhSeconds);
        Assert.Null(legacy.ZeroTo60MphSeconds);
        Assert.Null(legacy.SixtyTo130MphSeconds);
        Assert.Equal(AccelerationSnapshot.Empty.ZeroTo60MphSeconds, legacy.ZeroTo60MphSeconds);
    }

    [Fact]
    public void Reset_clears_the_new_windows()
    {
        var timer = new AccelerationTimer();
        Launch(timer, 10, durationSeconds: 12);

        timer.Reset();

        Assert.Equal(AccelerationSnapshot.Empty, timer.Current with { CompletedRuns = 0 }); // the counter never goes down
    }

    // A car that tops out at 204 km/h: it crosses 200 km/h and the quarter mile but can never reach 130 mph.
    private static AccelerationTimer ToppedOutAt204()
    {
        var timer = new AccelerationTimer();
        Launch(timer, 4, durationSeconds: 14.5); // ends at ~58.0 m/s, throttle still pinned
        return timer;
    }

    [Fact]
    public void Speed_dropping_5_kmh_below_the_peak_ends_the_run_and_leaves_130_mph_null()
    {
        var timer = ToppedOutAt204();
        Assert.Equal(AccelerationState.Running, timer.Current.State);

        timer.OnPacket(TestPackets.Drive(15600, 56f, 400f) with { Accel = 255 }); // ~7 km/h under the peak

        var snap = timer.Current;
        Assert.Equal(AccelerationState.Done, snap.State);
        Assert.NotNull(snap.ZeroTo200KmhSeconds);
        Assert.NotNull(snap.QuarterMileSeconds);
        Assert.Null(snap.SixtyTo130MphSeconds);
    }

    [Fact]
    public void Throttle_lifted_for_half_a_second_ends_the_run()
    {
        var timer = ToppedOutAt204();

        for (uint ms = 15520; ms <= 16100; ms += 20)
        {
            timer.OnPacket(TestPackets.Drive(ms, 58f, 400f + ((ms - 15500) * 0.058f)) with { Accel = 0 });
        }

        Assert.Equal(AccelerationState.Done, timer.Current.State);
        Assert.Null(timer.Current.SixtyTo130MphSeconds);
    }

    [Fact]
    public void A_short_throttle_lift_like_a_gear_change_does_not_end_the_run()
    {
        var timer = ToppedOutAt204();

        for (uint ms = 15520; ms <= 15800; ms += 20) // 280 ms
        {
            timer.OnPacket(TestPackets.Drive(ms, 58f, 400f + ((ms - 15500) * 0.058f)) with { Accel = 0 });
        }

        Assert.Equal(AccelerationState.Running, timer.Current.State);
    }

    [Fact]
    public void A_run_with_only_mph_windows_is_kept_and_shows_its_own_values()
    {
        var timer = new AccelerationTimer();
        Launch(timer, 10, durationSeconds: 12);
        var first = timer.Current;
        Assert.Equal(AccelerationState.Done, first.State);
        timer.OnPacket(TestPackets.Drive(30000, 0f, 9000f)); // stop and re-arm

        // 2.72 s at 10 m/s^2 = 97.9 km/h: past 60 mph (96.56) but short of 100 km/h.
        Launch(timer, 10, durationSeconds: 2.72, t0: 31000);
        Assert.NotNull(timer.Current.ZeroTo60MphSeconds);
        timer.OnPacket(TestPackets.Drive(34000, 15f, 9100f) with { Accel = 255 }); // speed collapses: lift

        // A mph player's 0-60 is a real result even though 100 km/h was never reached: the run is KEPT. The completed snapshot is
        // this run's (nulls for what it did not measure); older bests live in the per-car history, which merges per field.
        var snap = timer.Current;
        Assert.NotEqual(AccelerationState.Running, snap.State);
        Assert.NotNull(snap.ZeroTo60MphSeconds);
        Assert.Null(snap.ZeroTo100KmhSeconds);
        Assert.Equal(first.CompletedRuns + 1, snap.CompletedRuns);
    }

    // A slow car: ramps to 25 m/s (90 km/h) over 5 s and holds it, so it covers the quarter mile without ever reaching 100 km/h.
    private static void SlowCarRun(AccelerationTimer timer, double seconds)
    {
        for (uint ms = 0; ms < 1000; ms += 20)
        {
            timer.OnPacket(TestPackets.Drive(ms, 0f, 0f));
        }

        for (var ms = 1000u; ms <= 1000 + (seconds * 1000); ms += 20)
        {
            var s = (ms - 1000) / 1000.0;
            var speed = Math.Min(5 * s, 25);
            var distance = s <= 5 ? 2.5 * s * s : 62.5 + (25 * (s - 5));
            timer.OnPacket(TestPackets.Drive(ms, (float)speed, (float)distance) with { Accel = 255 });
        }
    }

    [Fact]
    public void A_slow_car_keeps_its_quarter_mile_even_though_it_never_reaches_100_kmh()
    {
        var timer = new AccelerationTimer();
        SlowCarRun(timer, seconds: 20);
        Assert.Equal(AccelerationState.Running, timer.Current.State);

        for (uint ms = 21020; ms <= 21700; ms += 20) // throttle lifted for over half a second: the run ends
        {
            timer.OnPacket(TestPackets.Drive(ms, 25f, 62.5f + (25 * (float)((ms - 1000) / 1000.0 - 5))) with { Accel = 0 });
        }

        var snap = timer.Current;
        Assert.Equal(AccelerationState.Done, snap.State);
        Assert.NotNull(snap.QuarterMileSeconds);
        Assert.Null(snap.ZeroTo100KmhSeconds);
        Assert.Equal(1, snap.CompletedRuns);
    }

    [Fact]
    public void A_run_that_measured_nothing_is_dropped_and_not_counted()
    {
        var timer = new AccelerationTimer();
        for (uint ms = 0; ms < 1000; ms += 20)
        {
            timer.OnPacket(TestPackets.Drive(ms, 0f, 0f));
        }

        for (uint ms = 1000; ms < 1500; ms += 20) // creeps at 7 km/h, then stops again
        {
            timer.OnPacket(TestPackets.Drive(ms, 2f, (ms - 1000) * 0.002f) with { Accel = 255 });
        }

        timer.OnPacket(TestPackets.Drive(1500, 0f, 1f));

        Assert.Equal(0, timer.Current.CompletedRuns);
        Assert.Null(timer.Current.ZeroTo100KmhSeconds);
    }

    [Fact]
    public void A_rolling_start_with_the_throttle_still_off_does_not_end_the_run()
    {
        var timer = new AccelerationTimer();
        for (uint ms = 0; ms < 1000; ms += 20)
        {
            timer.OnPacket(TestPackets.Drive(ms, 0f, 0f));
        }

        // Rolls off at 6 km/h with the pedal untouched for 0.8 s (over the old 0.5 s lift hold), then floors it.
        for (var ms = 1000u; ms < 1800; ms += 20)
        {
            timer.OnPacket(TestPackets.Drive(ms, 1.7f, (ms - 1000) * 0.0017f) with { Accel = 0 });
        }

        for (var ms = 1800u; ms <= 3000; ms += 20)
        {
            var s = (ms - 1800) / 1000.0;
            timer.OnPacket(TestPackets.Drive(ms, (float)(1.7 + (10 * s)), (float)(1.4 + (1.7 * s) + (5 * s * s))) with { Accel = 255 });
        }

        Assert.Equal(AccelerationState.Running, timer.Current.State);
    }

    [Fact]
    public void Completed_runs_only_ever_grows_and_survives_reset()
    {
        var timer = new AccelerationTimer();
        Launch(timer, 10, durationSeconds: 12);
        Assert.Equal(1, timer.Current.CompletedRuns);

        timer.Reset();
        Assert.Equal(1, timer.Current.CompletedRuns);

        timer.OnPacket(TestPackets.Drive(30000, 0f, 9000f));
        Launch(timer, 10, durationSeconds: 12, t0: 31000);
        Assert.Equal(2, timer.Current.CompletedRuns);
    }
}
