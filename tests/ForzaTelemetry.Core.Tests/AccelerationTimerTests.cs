using ForzaTelemetry.Core.Performance;

namespace ForzaTelemetry.Core.Tests;

public class AccelerationTimerTests
{
    private const double Kmh100 = 100.0 / 3.6;
    private const double Kmh200 = 200.0 / 3.6;
    private const double Standstill = 1.0 / 3.6;

    /// <summary>
    /// Feeds a stationary lead-in (speed 0) then a constant-acceleration launch. With constant acceleration
    /// <c>a</c> speed is a*t and distance is a*t^2/2, so every expected value has a closed form.
    /// </summary>
    private static void Launch(
        AccelerationTimer timer, double a, double durationSeconds, uint stepMs = 20, byte gear = 1, uint launchMs = 1000)
    {
        for (uint ms = 0; ms < launchMs; ms += stepMs)
        {
            timer.OnPacket(TestPackets.Drive(ms, 0f, 0f));
        }

        for (var ms = launchMs; ms <= launchMs + (durationSeconds * 1000); ms += stepMs)
        {
            var s = (ms - launchMs) / 1000.0;
            timer.OnPacket(TestPackets.Drive(ms, (float)(a * s), (float)(0.5 * a * s * s), gear));
        }
    }

    // The timer starts its clock when speed crosses 1 km/h, so expected times are measured from there.
    private static double StartOffset(double a) => Standstill / a;

    [Fact]
    public void Starts_idle_with_nothing_measured()
    {
        var timer = new AccelerationTimer();

        Assert.Equal(AccelerationState.Idle, timer.Current.State);
        Assert.Null(timer.Current.ZeroTo100KmhSeconds);
    }

    [Fact]
    public void Arms_when_standing_still_with_race_on()
    {
        var timer = new AccelerationTimer();

        timer.OnPacket(TestPackets.Drive(0, 0f, 0f));

        Assert.Equal(AccelerationState.Armed, timer.Current.State);
    }

    [Fact]
    public void Does_not_arm_when_race_is_off()
    {
        var timer = new AccelerationTimer();

        timer.OnPacket(TestPackets.Drive(0, 0f, 0f) with { IsRaceOn = false });

        Assert.Equal(AccelerationState.Idle, timer.Current.State);
    }

    [Fact]
    public void Does_not_arm_while_moving()
    {
        var timer = new AccelerationTimer();

        timer.OnPacket(TestPackets.Drive(0, 20f, 0f));

        Assert.Equal(AccelerationState.Idle, timer.Current.State);
    }

    [Fact]
    public void Measures_all_three_results_for_constant_acceleration()
    {
        const double a = 10;
        var timer = new AccelerationTimer();

        Launch(timer, a, durationSeconds: 12);

        var snap = timer.Current;
        Assert.Equal(AccelerationState.Done, snap.State);
        Assert.Equal((Kmh100 / a) - StartOffset(a), snap.ZeroTo100KmhSeconds!.Value, 0.005);
        Assert.Equal((Kmh200 / a) - StartOffset(a), snap.ZeroTo200KmhSeconds!.Value, 0.005);

        // Quarter mile is measured from where the 1 km/h clock started.
        var distanceAtStart = 0.5 * a * StartOffset(a) * StartOffset(a);
        var tLine = Math.Sqrt(2 * (AccelerationTimer.QuarterMileMeters + distanceAtStart) / a);
        Assert.Equal(tLine - StartOffset(a), snap.QuarterMileSeconds!.Value, 0.005);
        Assert.Equal(a * tLine * 3.6, snap.QuarterMileTrapSpeedKmh!.Value, 0.2);
    }

    [Fact]
    public void Interpolation_keeps_results_accurate_with_coarse_packets()
    {
        // A packet every 100 ms would be off by up to 0.1 s without interpolation.
        const double a = 10;
        var timer = new AccelerationTimer();

        Launch(timer, a, durationSeconds: 12, stepMs: 100);

        Assert.Equal((Kmh100 / a) - StartOffset(a), timer.Current.ZeroTo100KmhSeconds!.Value, 0.01);
    }

    [Fact]
    public void Reports_live_elapsed_time_and_partial_results_while_running()
    {
        const double a = 10;
        var timer = new AccelerationTimer();

        // Stop at 4 s: past 100 km/h (2.78 s) but before 200 km/h (5.56 s) and the quarter mile.
        Launch(timer, a, durationSeconds: 4);

        var snap = timer.Current;
        Assert.Equal(AccelerationState.Running, snap.State);
        Assert.Equal(4 - StartOffset(a), snap.ElapsedSeconds, 0.03);
        Assert.NotNull(snap.ZeroTo100KmhSeconds);
        Assert.Null(snap.ZeroTo200KmhSeconds);
        Assert.Null(snap.QuarterMileSeconds);
    }

    [Fact]
    public void Slow_car_that_stops_after_the_quarter_mile_finishes_without_a_200_time()
    {
        const double a = 3;
        var timer = new AccelerationTimer();
        Launch(timer, a, durationSeconds: 18);
        Assert.Equal(AccelerationState.Running, timer.Current.State); // waiting for a 200 km/h that never comes

        // Braking to a halt completes the run instead of discarding the quarter mile.
        timer.OnPacket(TestPackets.Drive(19001, 0f, 600f));

        var snap = timer.Current;
        Assert.Equal(AccelerationState.Armed, snap.State); // finished with partial results, then re-armed at standstill
        Assert.NotNull(snap.QuarterMileSeconds);
        Assert.NotNull(snap.ZeroTo100KmhSeconds);
        Assert.Null(snap.ZeroTo200KmhSeconds);
    }

    [Fact]
    public void Stays_done_while_still_moving_then_rearms_at_standstill()
    {
        var timer = new AccelerationTimer();
        Launch(timer, 10, durationSeconds: 12);
        timer.OnPacket(TestPackets.Drive(14000, 100f, 3000f));
        Assert.Equal(AccelerationState.Done, timer.Current.State);

        timer.OnPacket(TestPackets.Drive(30000, 0f, 3500f));

        Assert.Equal(AccelerationState.Armed, timer.Current.State);
        Assert.NotNull(timer.Current.ZeroTo100KmhSeconds); // result stays visible while armed
    }

    [Fact]
    public void Reverse_gear_aborts_a_running_run()
    {
        var timer = new AccelerationTimer();
        Launch(timer, 10, durationSeconds: 1.5);
        Assert.Equal(AccelerationState.Running, timer.Current.State);

        timer.OnPacket(TestPackets.Drive(3000, 8f, 20f, gear: 0));

        Assert.Equal(AccelerationState.Idle, timer.Current.State);
        Assert.Null(timer.Current.ZeroTo100KmhSeconds);
    }

    [Fact]
    public void Moving_off_in_reverse_never_starts_a_run()
    {
        var timer = new AccelerationTimer();
        timer.OnPacket(TestPackets.Drive(0, 0f, 0f));

        timer.OnPacket(TestPackets.Drive(20, 3f, 0.1f, gear: 0));

        Assert.Equal(AccelerationState.Idle, timer.Current.State);
    }

    [Fact]
    public void Race_off_aborts_a_running_run()
    {
        var timer = new AccelerationTimer();
        Launch(timer, 10, durationSeconds: 1.5);

        timer.OnPacket(TestPackets.Drive(3000, 12f, 20f) with { IsRaceOn = false });

        Assert.Equal(AccelerationState.Idle, timer.Current.State);
    }

    [Fact]
    public void Returning_to_standstill_before_the_quarter_mile_aborts_and_keeps_the_previous_result()
    {
        var timer = new AccelerationTimer();
        Launch(timer, 10, durationSeconds: 12);
        var firstRun = timer.Current;
        timer.OnPacket(TestPackets.Drive(20000, 0f, 5000f)); // re-arm
        Assert.Equal(AccelerationState.Armed, timer.Current.State);

        // Second attempt: launch, then stop dead before reaching the line.
        timer.OnPacket(TestPackets.Drive(20020, 5f, 5000.1f));
        timer.OnPacket(TestPackets.Drive(20040, 10f, 5000.3f));
        timer.OnPacket(TestPackets.Drive(20060, 0f, 5000.4f));

        var snap = timer.Current;
        Assert.Equal(AccelerationState.Armed, snap.State);
        Assert.Equal(firstRun.ZeroTo100KmhSeconds, snap.ZeroTo100KmhSeconds);
        Assert.Equal(firstRun.QuarterMileSeconds, snap.QuarterMileSeconds);
    }

    [Fact]
    public void A_clock_rewind_aborts_the_run()
    {
        var timer = new AccelerationTimer();
        Launch(timer, 10, durationSeconds: 1.5);

        timer.OnPacket(TestPackets.Drive(5, 20f, 30f));

        Assert.NotEqual(AccelerationState.Running, timer.Current.State);
    }

    [Fact]
    public void Reset_clears_results_on_the_next_packet()
    {
        var timer = new AccelerationTimer();
        Launch(timer, 10, durationSeconds: 12);

        timer.Reset();
        timer.OnPacket(TestPackets.Drive(40000, 0f, 6000f));

        Assert.Equal(AccelerationState.Armed, timer.Current.State);
        Assert.Null(timer.Current.ZeroTo100KmhSeconds);
        Assert.Null(timer.Current.QuarterMileSeconds);
    }

    [Fact]
    public void Reset_empties_the_snapshot_immediately_without_waiting_for_a_packet()
    {
        var timer = new AccelerationTimer();
        Launch(timer, 10, durationSeconds: 12);
        Assert.NotNull(timer.Current.ZeroTo100KmhSeconds);

        timer.Reset();

        Assert.Equal(AccelerationSnapshot.Empty, timer.Current);
    }

    [Fact]
    public void Reset_in_the_middle_of_a_run_drops_it_and_the_run_does_not_resume()
    {
        var timer = new AccelerationTimer();
        Launch(timer, 10, durationSeconds: 3); // still running: 0-100 km/h is reached at ~2.8 s, the other lines are not
        timer.Reset();

        // The car is still moving fast: after the reset it must not count as a new run (no standstill, so not armed).
        timer.OnPacket(TestPackets.Drive(9000, 40f, 200f));
        timer.OnPacket(TestPackets.Drive(9020, 40.2f, 208f));

        Assert.Equal(AccelerationState.Idle, timer.Current.State);
        Assert.Null(timer.Current.ZeroTo100KmhSeconds);
    }

    [Fact]
    public void Ignores_non_finite_values()
    {
        var timer = new AccelerationTimer();
        timer.OnPacket(TestPackets.Drive(0, 0f, 0f));

        timer.OnPacket(TestPackets.Drive(20, float.NaN, 0f));

        Assert.Equal(AccelerationState.Armed, timer.Current.State);
    }

    [Fact]
    public void Stopping_after_0_100_but_before_the_quarter_mile_keeps_the_partial_result()
    {
        const double a = 10;
        var timer = new AccelerationTimer();
        Launch(timer, a, durationSeconds: 4); // past 100 km/h at 2.78 s, quarter mile not reached

        timer.OnPacket(TestPackets.Drive(5000, 0f, 90f)); // stopped

        var snap = timer.Current;
        Assert.Equal(AccelerationState.Armed, snap.State);
        Assert.Equal((Kmh100 / a) - StartOffset(a), snap.ZeroTo100KmhSeconds!.Value, 0.005);
        Assert.Null(snap.QuarterMileSeconds);
    }

    [Fact]
    public void Reverse_gear_after_the_quarter_mile_keeps_the_measured_results()
    {
        var timer = new AccelerationTimer();
        Launch(timer, 3, durationSeconds: 18); // quarter mile done, 200 km/h never reached, still running

        timer.OnPacket(TestPackets.Drive(19500, 40f, 700f, gear: 0));

        var snap = timer.Current;
        Assert.Equal(AccelerationState.Done, snap.State);
        Assert.NotNull(snap.QuarterMileSeconds);
    }

    [Fact]
    public void Current_is_equal_between_reads_when_no_packet_arrived()
    {
        var timer = new AccelerationTimer();
        Launch(timer, 10, durationSeconds: 1.5);

        Assert.Equal(timer.Current, timer.Current);
    }
}
