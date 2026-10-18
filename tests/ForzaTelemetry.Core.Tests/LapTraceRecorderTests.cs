using ForzaTelemetry.Core.Laps;

namespace ForzaTelemetry.Core.Tests;

public class LapTraceRecorderTests
{
    private const double Length = 1000; // meters per lap in the synthetic track

    private static uint _clock;

    private static TelemetryPacket Packet(
        int lapsCompleted, double lapTime, double distance, double lastLap = 0, byte accel = 0, byte brake = 0, sbyte steer = 0, byte gear = 3) =>
        TestPackets.Blank with
        {
            IsRaceOn = true,
            TimestampMS = _clock += 20,
            LapNumber = (ushort)lapsCompleted,
            CurrentLap = (float)lapTime,
            DistanceTraveled = (float)distance,
            LastLap = (float)lastLap,
            Speed = (float)(Length / 20),
            Accel = accel,
            Brake = brake,
            Steer = steer,
            Gear = gear,
        };

    /// <summary>One lap at constant speed (length / lapSeconds), 100 ms per packet.</summary>
    private static void DriveLap(
        LapTraceRecorder recorder, int lapsCompleted, double lapSeconds, double startDistance, double lastLap = 0,
        double? stopAtSeconds = null, double startAtSeconds = 0)
    {
        var speed = Length / lapSeconds;
        var end = stopAtSeconds ?? lapSeconds;
        for (var t = startAtSeconds; t <= end + 1e-9; t += 0.1)
        {
            recorder.OnPacket(Packet(lapsCompleted, t, startDistance + (speed * t), lastLap));
        }
    }

    private static LapTraceSample[] Best(LapTraceRecorder recorder)
    {
        var buffer = new LapTraceSample[LapTraceRecorder.MaxSamplesPerLap];
        return buffer[..recorder.CopyBestTo(buffer)];
    }

    private static LapTraceSample[] Current(LapTraceRecorder recorder)
    {
        var buffer = new LapTraceSample[LapTraceRecorder.MaxSamplesPerLap];
        return buffer[..recorder.CopyCurrentTo(buffer)];
    }

    [Fact]
    public void Starts_empty()
    {
        var status = new LapTraceRecorder().Status;

        Assert.Equal(0, status.CurrentCount);
        Assert.Equal(0, status.BestCount);
        Assert.Null(status.BestLapSeconds);
        Assert.False(status.CurrentIsComparable);
    }

    [Fact]
    public void Records_one_sample_per_meter_with_the_driving_inputs()
    {
        var recorder = new LapTraceRecorder();

        recorder.OnPacket(Packet(0, 0.0, 0, accel: 255, brake: 0, steer: 127, gear: 2));
        recorder.OnPacket(Packet(0, 0.1, 0.4, accel: 255)); // under a meter from the last sample: skipped
        recorder.OnPacket(Packet(0, 0.2, 5, accel: 0, brake: 255, steer: -127, gear: 3));

        var samples = Current(recorder);
        Assert.Equal(2, samples.Length);
        Assert.Equal(0, samples[0].DistanceMeters);
        Assert.Equal(1f, samples[0].Throttle);
        Assert.Equal(1f, samples[0].Steer);
        Assert.Equal(2, samples[0].Gear);
        Assert.Equal(5, samples[1].DistanceMeters);
        Assert.Equal(0.2f, samples[1].TimeSeconds, 3);
        Assert.Equal(1f, samples[1].Brake);
        Assert.Equal(-1f, samples[1].Steer);
        Assert.True(recorder.Status.CurrentIsComparable);
    }

    [Fact]
    public void A_completed_lap_becomes_the_best_trace_and_the_next_lap_starts_empty()
    {
        var recorder = new LapTraceRecorder();
        DriveLap(recorder, 0, 20, 0);

        recorder.OnPacket(Packet(1, 0.0, 1000, lastLap: 20));

        var status = recorder.Status;
        Assert.Equal(20, status.BestLapSeconds);
        Assert.Equal(1, status.BestLapNumber);
        Assert.Equal(2, status.CurrentLapNumber);
        Assert.True(status.BestCount > 100);
        Assert.Equal(1, status.CurrentCount); // the first packet of lap 2
        var best = Best(recorder);
        Assert.Equal(0, best[0].DistanceMeters);
        Assert.Equal(10.0, LapTraceMath.TryInterpolate(best, 500, out var mid) ? mid.TimeSeconds : double.NaN, 2);
    }

    [Fact]
    public void A_slower_lap_does_not_replace_the_best_trace()
    {
        var recorder = new LapTraceRecorder();
        DriveLap(recorder, 0, 20, 0);
        DriveLap(recorder, 1, 22, 1000, lastLap: 20);
        var versionBefore = recorder.Status.BestVersion;

        recorder.OnPacket(Packet(2, 0.0, 2000, lastLap: 22));

        Assert.Equal(20, recorder.Status.BestLapSeconds);
        Assert.Equal(1, recorder.Status.BestLapNumber);
        Assert.Equal(versionBefore, recorder.Status.BestVersion);
    }

    [Fact]
    public void A_faster_lap_replaces_the_best_trace_and_bumps_the_version()
    {
        var recorder = new LapTraceRecorder();
        DriveLap(recorder, 0, 22, 0);
        DriveLap(recorder, 1, 20, 1000, lastLap: 22);
        var versionBefore = recorder.Status.BestVersion;

        recorder.OnPacket(Packet(2, 0.0, 2000, lastLap: 20));

        var status = recorder.Status;
        Assert.Equal(20, status.BestLapSeconds);
        Assert.Equal(2, status.BestLapNumber);
        Assert.NotEqual(versionBefore, status.BestVersion);
    }

    [Fact]
    public void A_lap_joined_midway_is_not_comparable_and_never_becomes_the_best()
    {
        var recorder = new LapTraceRecorder();
        DriveLap(recorder, 0, 20, 500, startAtSeconds: 10); // first packet at lap clock 10 s

        Assert.False(recorder.Status.CurrentIsComparable);

        recorder.OnPacket(Packet(1, 0.0, 1000, lastLap: 20));

        Assert.Equal(0, recorder.Status.BestCount);
        Assert.Null(recorder.Status.BestLapSeconds);
    }

    [Fact]
    public void A_jump_of_several_laps_never_promotes_the_lap_in_hand()
    {
        var recorder = new LapTraceRecorder();
        DriveLap(recorder, 0, 20, 0);

        recorder.OnPacket(Packet(2, 0.0, 2000, lastLap: 19));

        Assert.Equal(0, recorder.Status.BestCount);
    }

    [Fact]
    public void Falls_back_to_the_last_lap_clock_when_last_lap_is_zero()
    {
        var recorder = new LapTraceRecorder();
        DriveLap(recorder, 0, 20, 0, stopAtSeconds: 5);

        recorder.OnPacket(Packet(1, 0.0, 1000, lastLap: 0));

        Assert.Equal(5.0, recorder.Status.BestLapSeconds!.Value, 1);
    }

    [Fact]
    public void Restarting_the_lap_discards_its_samples()
    {
        var recorder = new LapTraceRecorder();
        DriveLap(recorder, 0, 20, 0, stopAtSeconds: 8);

        recorder.OnPacket(Packet(0, 0.0, 450)); // the lap clock jumped back

        Assert.Equal(1, recorder.Status.CurrentCount);
        Assert.True(recorder.Status.CurrentIsComparable);
    }

    [Fact]
    public void A_restarted_race_forgets_the_best_trace()
    {
        var recorder = new LapTraceRecorder();
        DriveLap(recorder, 0, 20, 0);
        recorder.OnPacket(Packet(1, 0.0, 1000, lastLap: 20));
        DriveLap(recorder, 1, 21, 1000, startAtSeconds: 0.1, lastLap: 20);

        recorder.OnPacket(Packet(0, 0.0, 0));

        Assert.Equal(0, recorder.Status.BestCount);
        Assert.Null(recorder.Status.BestLapSeconds);
    }

    [Fact]
    public void Menu_packets_are_ignored()
    {
        var recorder = new LapTraceRecorder();

        recorder.OnPacket(Packet(0, 0.0, 0) with { IsRaceOn = false });

        Assert.Equal(0, recorder.Status.CurrentCount);
    }

    [Fact]
    public void Free_roam_stops_recording_when_the_array_is_full_and_is_never_a_reference()
    {
        var recorder = new LapTraceRecorder();
        for (var i = 0; i < LapTraceRecorder.MaxSamplesPerLap + 20; i++)
        {
            recorder.OnPacket(Packet(0, i * 0.1, i * 2.0));
        }

        var status = recorder.Status;
        Assert.Equal(LapTraceRecorder.MaxSamplesPerLap, status.CurrentCount);
        Assert.False(status.CurrentIsComparable);
    }

    [Fact]
    public void Reset_forgets_everything_and_bumps_both_versions()
    {
        var recorder = new LapTraceRecorder();
        DriveLap(recorder, 0, 20, 0);
        recorder.OnPacket(Packet(1, 0.0, 1000, lastLap: 20));
        var before = recorder.Status;

        recorder.Reset();

        var after = recorder.Status;
        Assert.Equal(0, after.CurrentCount);
        Assert.Equal(0, after.BestCount);
        Assert.Null(after.BestLapSeconds);
        Assert.NotEqual(before.CurrentVersion, after.CurrentVersion);
        Assert.NotEqual(before.BestVersion, after.BestVersion);
    }

    [Fact]
    public void Copying_into_a_small_buffer_gives_the_first_samples()
    {
        var recorder = new LapTraceRecorder();
        DriveLap(recorder, 0, 20, 0);

        var small = new LapTraceSample[3];
        var copied = recorder.CopyCurrentTo(small);

        Assert.Equal(3, copied);
        Assert.Equal(0, small[0].DistanceMeters);
    }

    [Fact]
    public void The_tail_copy_gives_only_the_samples_recorded_since_the_reader_last_copied()
    {
        var recorder = new LapTraceRecorder();
        DriveLap(recorder, 0, 20, 0, stopAtSeconds: 2.0); // 50 m/s: a sample every 100 ms
        var first = recorder.Status;
        var buffer = new LapTraceSample[LapTraceRecorder.MaxSamplesPerLap];
        var copied = recorder.CopyCurrentTailTo(first.CurrentLapId, 0, buffer);
        Assert.Equal(first.CurrentCount, copied);

        DriveLap(recorder, 0, 20, 0, startAtSeconds: 2.1, stopAtSeconds: 3.0);
        var tail = recorder.CopyCurrentTailTo(first.CurrentLapId, copied, buffer.AsSpan(copied));

        var all = Current(recorder);
        Assert.Equal(all.Length - copied, tail);
        Assert.Equal(all.Select(s => s.DistanceMeters), buffer[..(copied + tail)].Select(s => s.DistanceMeters));
    }

    [Fact]
    public void The_tail_copy_refuses_a_lap_that_is_no_longer_the_one_asked_for()
    {
        var recorder = new LapTraceRecorder();
        DriveLap(recorder, 0, 20, 0, stopAtSeconds: 2.0);
        var lapId = recorder.Status.CurrentLapId;
        var buffer = new LapTraceSample[LapTraceRecorder.MaxSamplesPerLap];

        recorder.OnPacket(Packet(1, 0.0, 1000, lastLap: 20)); // the lap ended

        Assert.NotEqual(lapId, recorder.Status.CurrentLapId);
        Assert.Equal(-1, recorder.CopyCurrentTailTo(lapId, 5, buffer));
        Assert.Equal(-1, recorder.CopyCurrentTailTo(recorder.Status.CurrentLapId, 99, buffer)); // beyond what was recorded
    }

    [Fact]
    public void A_restarted_lap_and_a_reset_are_new_laps_for_the_tail_copy()
    {
        var recorder = new LapTraceRecorder();
        DriveLap(recorder, 0, 20, 0, stopAtSeconds: 3.0);
        var original = recorder.Status.CurrentLapId;

        recorder.OnPacket(Packet(0, 0.0, 1500)); // the lap clock jumped back: the player restarted the lap
        var restarted = recorder.Status.CurrentLapId;
        recorder.Reset();

        Assert.NotEqual(original, restarted);
        Assert.NotEqual(restarted, recorder.Status.CurrentLapId);
    }

    [Fact]
    public void The_tracker_and_the_recorder_agree_on_which_lap_is_the_reference()
    {
        // Lap 1 (20 s) is a full lap, lap 2 is joined midway and is FASTER (15 s), lap 3 is the lap being compared. Neither may take
        // lap 2 as the reference, and both must compare lap 3 with lap 1 by the same numbers.
        var tracker = new LapTracker();
        var recorder = new LapTraceRecorder();
        void Feed(TelemetryPacket packet)
        {
            tracker.OnPacket(packet);
            recorder.OnPacket(packet);
        }

        for (var t = 0.0; t <= 20 + 1e-9; t += 0.1)
        {
            Feed(Packet(0, t, 50 * t));
        }

        for (var t = 5.0; t <= 15 + 1e-9; t += 0.1) // joined at 5 s: not seen from the start
        {
            Feed(Packet(1, t, 1000 + (66.6 * t), lastLap: t == 5.0 ? 20 : 0));
        }

        Feed(Packet(2, 0.0, 2000, lastLap: 15));
        var best = Best(recorder);
        for (var t = 0.1; t <= 12 + 1e-9; t += 0.1)
        {
            Feed(Packet(2, t, 2000 + (40 * t)));
            var expected = LapTraceMath.DeltaSeconds(best, 40 * t, t);
            var actual = tracker.Current.DeltaSeconds;
            Assert.Equal(expected is null, actual is null);
            if (expected is { } e)
            {
                Assert.Equal(e, actual!.Value, 3);
            }
        }

        Assert.Equal(20, recorder.Status.BestLapSeconds);
    }
}
