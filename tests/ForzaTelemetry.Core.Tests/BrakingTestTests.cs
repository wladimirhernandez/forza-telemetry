using ForzaTelemetry.Core.Performance;

namespace ForzaTelemetry.Core.Tests;

public class BrakingTestTests
{
    private const double Kmh100 = 100.0 / 3.6;
    private const double Mph60 = 60.0 * 1.609344 / 3.6;
    private const double Standstill = 1.0 / 3.6;
    private const double Decel = 10; // m/s^2
    private const byte HardBrake = 200;

    /// <summary>
    /// Cruises at <paramref name="cruiseKmh"/> for one second, then brakes with constant deceleration until
    /// <paramref name="brakeSeconds"/> have passed (speed clamps at 0). Constant deceleration gives closed forms:
    /// v = v0 - d*t and s = v0*t - d*t^2/2 (until the stop), so every expectation is exact maths, not a recording.
    /// </summary>
    private static uint Run(
        BrakingTest test,
        double cruiseKmh = 120,
        double brakeSeconds = 5,
        uint stepMs = 20,
        byte brake = HardBrake,
        byte accel = 0,
        byte gear = 3)
    {
        var v0 = cruiseKmh / 3.6;
        const uint cruiseMs = 1000;
        double distance = 0;

        uint ms = 0;
        for (; ms < cruiseMs; ms += stepMs)
        {
            distance = v0 * ms / 1000.0;
            test.OnPacket(TestPackets.Drive(ms, (float)v0, (float)distance, gear));
        }

        var cruiseDistance = v0 * cruiseMs / 1000.0;
        var stopT = v0 / Decel;
        for (var t = 0.0; t <= brakeSeconds + 1e-9; t += stepMs / 1000.0, ms += stepMs)
        {
            var tt = Math.Min(t, stopT);
            var speed = Math.Max(0, v0 - (Decel * t));
            var d = cruiseDistance + (v0 * tt) - (0.5 * Decel * tt * tt);
            test.OnPacket(TestPackets.Drive(ms, (float)speed, (float)d, gear) with { Brake = brake, Accel = accel });
        }

        return ms;
    }

    // Closed forms for a stop from v0 (m/s): the clock runs from the 100 km/h crossing to the 1 km/h crossing.
    private static double ExpectedSeconds(double from) => (from - Standstill) / Decel;

    private static double ExpectedMeters(double from) => ((from * from) - (Standstill * Standstill)) / (2 * Decel);

    [Fact]
    public void Starts_idle_with_nothing_measured()
    {
        var snap = new BrakingTest().Current;

        Assert.Equal(BrakingState.Idle, snap.State);
        Assert.Null(snap.HundredToZeroMeters);
    }

    [Fact]
    public void Measures_exact_time_and_distance_for_both_variants()
    {
        var test = new BrakingTest();

        Run(test);

        var snap = test.Current;
        Assert.Equal(BrakingState.Done, snap.State);
        Assert.Equal(ExpectedSeconds(Kmh100), snap.HundredToZeroSeconds!.Value, 0.002);
        Assert.Equal(ExpectedMeters(Kmh100), snap.HundredToZeroMeters!.Value, 0.02);
        Assert.Equal(ExpectedSeconds(Mph60), snap.SixtyToZeroMphSeconds!.Value, 0.002);
        Assert.Equal(ExpectedMeters(Mph60), snap.SixtyToZeroMphMeters!.Value, 0.02);
        Assert.True(snap.SixtyToZeroMphMeters < snap.HundredToZeroMeters);
    }

    [Fact]
    public void Interpolation_keeps_results_accurate_with_coarse_packets()
    {
        var test = new BrakingTest();

        Run(test, stepMs: 100);

        Assert.Equal(ExpectedSeconds(Kmh100), test.Current.HundredToZeroSeconds!.Value, 0.02);
        Assert.Equal(ExpectedMeters(Kmh100), test.Current.HundredToZeroMeters!.Value, 0.1);
    }

    [Fact]
    public void Arms_just_above_105_kmh_but_not_just_below()
    {
        var slow = new BrakingTest();
        slow.OnPacket(TestPackets.Drive(0, (float)(104.9 / 3.6), 0f));
        var fast = new BrakingTest();
        fast.OnPacket(TestPackets.Drive(0, (float)(105.1 / 3.6), 0f));

        Assert.Equal(BrakingState.Idle, slow.Current.State);
        Assert.Equal(BrakingState.Armed, fast.Current.State);
    }

    [Fact]
    public void Does_not_arm_when_race_is_off_or_in_reverse()
    {
        var off = new BrakingTest();
        off.OnPacket(TestPackets.Drive(0, 40f, 0f) with { IsRaceOn = false });
        var reverse = new BrakingTest();
        reverse.OnPacket(TestPackets.Drive(0, 40f, 0f, gear: 0));

        Assert.Equal(BrakingState.Idle, off.Current.State);
        Assert.Equal(BrakingState.Idle, reverse.Current.State);
    }

    [Fact]
    public void Light_braking_below_50_percent_does_not_start_a_run()
    {
        var test = new BrakingTest();

        Run(test, brake: 127);

        Assert.Equal(BrakingState.Idle, test.Current.State);
        Assert.Null(test.Current.HundredToZeroSeconds);
    }

    [Fact]
    public void Brake_at_exactly_50_percent_starts_a_run()
    {
        var test = new BrakingTest();

        Run(test, brake: 128);

        Assert.Equal(BrakingState.Done, test.Current.State);
    }

    [Fact]
    public void Coasting_through_100_disarms_so_a_later_brake_at_60_is_not_a_run()
    {
        var test = new BrakingTest();
        test.OnPacket(TestPackets.Drive(0, 120f / 3.6f, 0f));
        test.OnPacket(TestPackets.Drive(20, 99f / 3.6f, 10f)); // crossed 100 with no brake
        Assert.Equal(BrakingState.Idle, test.Current.State);

        test.OnPacket(TestPackets.Drive(40, 60f / 3.6f, 20f) with { Brake = HardBrake });
        test.OnPacket(TestPackets.Drive(60, 0f, 25f) with { Brake = HardBrake });

        Assert.Null(test.Current.HundredToZeroSeconds);
    }

    [Fact]
    public void Reports_live_elapsed_time_while_braking()
    {
        var test = new BrakingTest();

        // 120 km/h brakes through 100 km/h at 0.556 s; stop the stream at 2 s of braking.
        Run(test, brakeSeconds: 2);

        var snap = test.Current;
        Assert.Equal(BrakingState.Braking, snap.State);
        Assert.Equal(2 - ((120 / 3.6 - Kmh100) / Decel), snap.ElapsedSeconds, 0.03);
        Assert.Null(snap.HundredToZeroSeconds);
    }

    [Fact]
    public void Throttle_over_10_percent_aborts_and_keeps_the_previous_result()
    {
        var test = new BrakingTest();
        Run(test);
        var previous = test.Current.HundredToZeroMeters;

        Run(test, brakeSeconds: 2, accel: 26); // 26/255 > 10%

        var snap = test.Current;
        Assert.NotEqual(BrakingState.Braking, snap.State);
        Assert.Equal(previous, snap.HundredToZeroMeters);
    }

    [Fact]
    public void Throttle_at_10_percent_or_less_does_not_abort()
    {
        var test = new BrakingTest();

        Run(test, accel: 25);

        Assert.Equal(BrakingState.Done, test.Current.State);
    }

    [Fact]
    public void Reverse_gear_aborts_a_running_stop()
    {
        var test = new BrakingTest();
        Run(test, brakeSeconds: 2);
        Assert.Equal(BrakingState.Braking, test.Current.State);

        test.OnPacket(TestPackets.Drive(10_000, 10f, 100f, gear: 0) with { Brake = HardBrake });

        Assert.Equal(BrakingState.Idle, test.Current.State);
        Assert.Null(test.Current.HundredToZeroSeconds);
    }

    [Fact]
    public void Race_off_aborts_a_running_stop()
    {
        var test = new BrakingTest();
        var ms = Run(test, brakeSeconds: 2);

        test.OnPacket(TestPackets.Drive(ms, 10f, 100f) with { IsRaceOn = false, Brake = HardBrake });

        Assert.Equal(BrakingState.Idle, test.Current.State);
    }

    [Fact]
    public void Speed_rising_above_100_again_aborts_the_run()
    {
        var test = new BrakingTest();
        var ms = Run(test, brakeSeconds: 1); // braking, around 110 km/h -> ~84 km/h
        Assert.Equal(BrakingState.Braking, test.Current.State);

        test.OnPacket(TestPackets.Drive(ms, 31f, 100f) with { Brake = HardBrake }); // 111.6 km/h again

        Assert.NotEqual(BrakingState.Braking, test.Current.State);
        Assert.Null(test.Current.HundredToZeroSeconds);
    }

    [Fact]
    public void A_clock_rewind_aborts_the_run()
    {
        var test = new BrakingTest();
        Run(test, brakeSeconds: 1);

        test.OnPacket(TestPackets.Drive(5, 15f, 100f) with { Brake = HardBrake });

        Assert.NotEqual(BrakingState.Braking, test.Current.State);
    }

    [Fact]
    public void Rearms_after_a_completed_run_when_speed_exceeds_105_again()
    {
        var test = new BrakingTest();
        Run(test);
        var done = test.Current;
        Assert.Equal(BrakingState.Done, done.State);

        test.OnPacket(TestPackets.Drive(100_000, 110f / 3.6f, 0f));

        var snap = test.Current;
        Assert.Equal(BrakingState.Armed, snap.State);
        Assert.Equal(done.HundredToZeroMeters, snap.HundredToZeroMeters); // result stays on screen
    }

    [Fact]
    public void Reset_empties_the_snapshot_immediately()
    {
        var test = new BrakingTest();
        Run(test);

        test.Reset();

        Assert.Equal(BrakingSnapshot.Empty, test.Current);
    }

    [Fact]
    public void Reset_in_the_middle_of_a_run_drops_it()
    {
        var test = new BrakingTest();
        var ms = Run(test, brakeSeconds: 2);

        test.Reset();
        test.OnPacket(TestPackets.Drive(ms, 10f, 100f) with { Brake = HardBrake });
        test.OnPacket(TestPackets.Drive(ms + 20, 0f, 101f) with { Brake = HardBrake });

        Assert.Null(test.Current.HundredToZeroSeconds);
    }

    [Fact]
    public void Ignores_non_finite_values()
    {
        var test = new BrakingTest();
        test.OnPacket(TestPackets.Drive(0, 120f / 3.6f, 0f));

        test.OnPacket(TestPackets.Drive(20, float.NaN, 1f));
        test.OnPacket(TestPackets.Drive(40, 30f, float.PositiveInfinity));

        Assert.Equal(BrakingState.Armed, test.Current.State);
    }

    [Fact]
    public void Current_is_equal_between_reads_when_no_packet_arrived()
    {
        var test = new BrakingTest();
        Run(test);

        Assert.Equal(test.Current, test.Current);
    }
}
