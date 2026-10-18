using ForzaTelemetry.Core.Laps;

namespace ForzaTelemetry.Core.Tests;

public class LapTrackerTests
{
    private const double Length = 1000; // meters per lap in the synthetic track

    private static uint _clock;

    private static TelemetryPacket Lap(
        int lapsCompleted, double lapTime, double distance, double lastLap = 0, double bestLap = 0) =>
        TestPackets.Blank with
        {
            IsRaceOn = true,
            TimestampMS = _clock += 20,
            LapNumber = (ushort)lapsCompleted,
            CurrentLap = (float)lapTime,
            DistanceTraveled = (float)distance,
            LastLap = (float)lastLap,
            BestLap = (float)bestLap,
        };

    /// <summary>
    /// Drives one lap at constant speed (length / lapSeconds), 100 ms per packet. The first packet of the lap
    /// carries the previous lap's LastLap/BestLap like the real game does after crossing the line.
    /// </summary>
    private static void DriveLap(
        LapTracker tracker, int lapsCompleted, double lapSeconds, double startDistance,
        double lastLap = 0, double bestLap = 0, double? stopAtSeconds = null, double startAtSeconds = 0)
    {
        var speed = Length / lapSeconds;
        var end = stopAtSeconds ?? lapSeconds;
        for (var t = startAtSeconds; t <= end + 1e-9; t += 0.1)
        {
            tracker.OnPacket(Lap(lapsCompleted, t, startDistance + (speed * t), lastLap, bestLap));
        }
    }

    [Fact]
    public void Starts_on_lap_one_with_nothing_recorded()
    {
        var tracker = new LapTracker();

        Assert.Equal(1, tracker.Current.CurrentLapNumber);
        Assert.Empty(tracker.Current.Laps);
        Assert.Null(tracker.Current.DeltaSeconds);
    }

    [Fact]
    public void Records_a_lap_when_the_lap_number_increases()
    {
        var tracker = new LapTracker();

        DriveLap(tracker, 0, 20, 0);
        tracker.OnPacket(Lap(1, 0.0, 1000, lastLap: 20, bestLap: 20));

        var snap = tracker.Current;
        Assert.Equal(2, snap.CurrentLapNumber);
        var lap = Assert.Single(snap.Laps);
        Assert.Equal(1, lap.Number);
        Assert.Equal(20, lap.TimeSeconds, 3);
        Assert.True(lap.IsBest);
        Assert.Equal(20, snap.LastLapSeconds);
        Assert.Equal(20, snap.BestLapSeconds);
    }

    [Fact]
    public void A_slower_second_lap_is_not_best()
    {
        var tracker = new LapTracker();
        DriveLap(tracker, 0, 20, 0);
        DriveLap(tracker, 1, 22, 1000, lastLap: 20, bestLap: 20);

        tracker.OnPacket(Lap(2, 0.0, 2000, lastLap: 22, bestLap: 20));

        var laps = tracker.Current.Laps;
        Assert.Equal(2, laps.Count);
        Assert.True(laps[0].IsBest);
        Assert.False(laps[1].IsBest);
        Assert.Equal(22, tracker.Current.LastLapSeconds);
        Assert.Equal(20, tracker.Current.BestLapSeconds);
    }

    [Fact]
    public void Falls_back_to_the_last_seen_current_lap_when_last_lap_is_zero()
    {
        var tracker = new LapTracker();
        DriveLap(tracker, 0, 20, 0);

        tracker.OnPacket(Lap(1, 0.0, 1000, lastLap: 0));

        Assert.Equal(20, Assert.Single(tracker.Current.Laps).TimeSeconds, 3);
    }

    [Fact]
    public void Delta_is_positive_when_slower_than_the_best_lap_at_the_same_distance()
    {
        var tracker = new LapTracker();
        DriveLap(tracker, 0, 20, 0); // best: 50 m/s, so at 500 m the clock read 10.0 s
        // Second lap at 1000/22 m/s: after 11 s it is at 500 m, 1.0 s behind.
        DriveLap(tracker, 1, 22, 1000, lastLap: 20, bestLap: 20, stopAtSeconds: 11.0);

        Assert.Equal(1.0, tracker.Current.DeltaSeconds!.Value, 2);
    }

    [Fact]
    public void Delta_is_negative_when_faster_than_the_best_lap()
    {
        var tracker = new LapTracker();
        DriveLap(tracker, 0, 20, 0);
        // 1000/18 m/s: at 9 s it is at 500 m, where the best lap needed 10 s.
        DriveLap(tracker, 1, 18, 1000, lastLap: 20, bestLap: 20, stopAtSeconds: 9.0);

        Assert.Equal(-1.0, tracker.Current.DeltaSeconds!.Value, 2);
    }

    [Fact]
    public void Delta_interpolates_between_recorded_samples()
    {
        var tracker = new LapTracker();
        DriveLap(tracker, 0, 20, 0);
        DriveLap(tracker, 1, 20, 1000, lastLap: 20, bestLap: 20, stopAtSeconds: 0);

        // 123.4 m into the lap, off the 1 m grid of reference samples, same pace: delta must be ~0.
        tracker.OnPacket(Lap(1, 123.4 / 50, 1000 + 123.4, lastLap: 20, bestLap: 20));

        Assert.Equal(0.0, tracker.Current.DeltaSeconds!.Value, 2);
    }

    [Fact]
    public void A_new_best_lap_becomes_the_delta_reference()
    {
        var tracker = new LapTracker();
        DriveLap(tracker, 0, 20, 0);
        DriveLap(tracker, 1, 18, 1000, lastLap: 20, bestLap: 20); // faster: becomes the reference
        // Third lap at 20 s pace: at 10 s it is at 500 m; the 18 s lap was there at 9 s, so +1.0.
        DriveLap(tracker, 2, 20, 2000, lastLap: 18, bestLap: 18, stopAtSeconds: 10.0);

        Assert.True(tracker.Current.Laps[1].IsBest);
        Assert.Equal(1.0, tracker.Current.DeltaSeconds!.Value, 2);
    }

    [Fact]
    public void A_slower_lap_does_not_replace_the_delta_reference()
    {
        var tracker = new LapTracker();
        DriveLap(tracker, 0, 20, 0);
        DriveLap(tracker, 1, 25, 1000, lastLap: 20, bestLap: 20);
        // Reference stays the 20 s lap: at 20 s pace, halfway is 10 s, delta 0.
        DriveLap(tracker, 2, 20, 2000, lastLap: 25, bestLap: 20, stopAtSeconds: 10.0);

        Assert.Equal(0.0, tracker.Current.DeltaSeconds!.Value, 2);
    }

    [Fact]
    public void No_delta_until_a_full_reference_lap_exists()
    {
        var tracker = new LapTracker();

        DriveLap(tracker, 0, 20, 0, stopAtSeconds: 10);

        Assert.Null(tracker.Current.DeltaSeconds);
    }

    [Fact]
    public void A_lap_joined_midway_never_becomes_the_reference()
    {
        var tracker = new LapTracker();
        // Tracker starts listening 10 s into lap 1.
        DriveLap(tracker, 0, 20, -500, startAtSeconds: 10); // distances from -500 + ... irrelevant: lap joined late
        DriveLap(tracker, 1, 20, 1000, lastLap: 20, bestLap: 20, stopAtSeconds: 10);

        Assert.Single(tracker.Current.Laps); // the partial lap still counts as a finished lap time
        Assert.Null(tracker.Current.DeltaSeconds); // but gives no reference
    }

    [Fact]
    public void Delta_is_unavailable_beyond_the_recorded_distance_of_the_reference()
    {
        var tracker = new LapTracker();
        DriveLap(tracker, 0, 20, 0);
        tracker.OnPacket(Lap(1, 0.0, 1000, lastLap: 20, bestLap: 20));

        // 1500 m into a lap whose reference only covers ~1000 m (e.g. a detour).
        tracker.OnPacket(Lap(1, 30.0, 2500, lastLap: 20, bestLap: 20));

        Assert.Null(tracker.Current.DeltaSeconds);
    }

    [Fact]
    public void A_decreasing_lap_number_is_a_race_restart_and_clears_history()
    {
        var tracker = new LapTracker();
        DriveLap(tracker, 0, 20, 0);
        DriveLap(tracker, 1, 20, 1000, lastLap: 20, bestLap: 20);
        tracker.OnPacket(Lap(2, 0.0, 2000, lastLap: 20, bestLap: 20));
        Assert.Equal(2, tracker.Current.Laps.Count);

        tracker.OnPacket(Lap(0, 0.0, 0));

        var snap = tracker.Current;
        Assert.Equal(1, snap.CurrentLapNumber);
        Assert.Empty(snap.Laps);
        Assert.Null(snap.BestLapSeconds);
        Assert.Null(snap.DeltaSeconds);
    }

    [Fact]
    public void A_lap_clock_reset_within_the_same_lap_discards_that_laps_samples()
    {
        var tracker = new LapTracker();
        DriveLap(tracker, 0, 20, 0);
        DriveLap(tracker, 1, 20, 1000, lastLap: 20, bestLap: 20, stopAtSeconds: 10); // half a lap...
        tracker.OnPacket(Lap(1, 0.0, 5000, lastLap: 20, bestLap: 20)); // ...then lap restart far away

        // Still on lap 2, no laps added by the restart, and the new lap measures from its own start.
        Assert.Equal(2, tracker.Current.CurrentLapNumber);
        Assert.Single(tracker.Current.Laps);
        tracker.OnPacket(Lap(1, 5.0, 5250, lastLap: 20, bestLap: 20));
        Assert.Equal(0.0, tracker.Current.DeltaSeconds!.Value, 2);
    }

    [Fact]
    public void Packets_with_race_off_are_ignored()
    {
        var tracker = new LapTracker();
        DriveLap(tracker, 0, 20, 0, stopAtSeconds: 5);
        var before = tracker.Current;

        tracker.OnPacket(Lap(7, 99, 12345) with { IsRaceOn = false });

        Assert.Equal(before, tracker.Current);
    }

    [Fact]
    public void Free_roam_never_grows_the_sample_buffers_and_never_becomes_a_reference()
    {
        var tracker = new LapTracker();

        // LapNumber never changes in free roam; drive far beyond the sample capacity (1 sample per metre).
        for (var i = 0; i <= LapTracker.MaxSamplesPerLap + 500; i++)
        {
            tracker.OnPacket(Lap(0, i / 50.0, i * 1.5));
        }

        // Still functional: no delta (no reference), lap clock keeps showing.
        Assert.Null(tracker.Current.DeltaSeconds);
        Assert.True(tracker.Current.CurrentLapSeconds > 100);
    }

    [Fact]
    public void A_lap_with_unknown_time_is_still_listed_so_numbering_stays_aligned()
    {
        var tracker = new LapTracker();
        tracker.OnPacket(Lap(0, 0.0, 0));
        // Lap ends with neither LastLap nor a positive CurrentLap on the previous packet.
        tracker.OnPacket(Lap(1, 0.0, 10, lastLap: 0));
        DriveLap(tracker, 1, 20, 1000, lastLap: 0);
        tracker.OnPacket(Lap(2, 0.0, 2000, lastLap: 20, bestLap: 20));

        var snap = tracker.Current;
        Assert.Equal(3, snap.CurrentLapNumber);
        Assert.Equal([1, 2], snap.Laps.Select(l => l.Number));
        Assert.Equal(0, snap.Laps[0].TimeSeconds); // unknown
        Assert.False(snap.Laps[0].IsBest);
        Assert.True(snap.Laps[1].IsBest);
    }

    [Fact]
    public void Current_returns_equal_snapshots_when_nothing_changed()
    {
        var tracker = new LapTracker();
        tracker.OnPacket(Lap(0, 1.0, 10));

        Assert.Equal(tracker.Current, tracker.Current);
    }

    [Fact]
    public void Reset_forgets_the_laps_the_best_time_and_the_delta_reference()
    {
        var tracker = new LapTracker();
        DriveLap(tracker, 0, 20, 0);
        tracker.OnPacket(Lap(1, 0.0, 1000, lastLap: 20, bestLap: 20));
        DriveLap(tracker, 1, 21, 1000, lastLap: 20, bestLap: 20, stopAtSeconds: 10);
        Assert.NotNull(tracker.Current.DeltaSeconds);
        Assert.Single(tracker.Current.Laps);

        tracker.Reset();

        Assert.Equal(LapSnapshot.Empty, tracker.Current);
    }

    [Fact]
    public void After_a_reset_the_next_lap_is_lap_one_with_no_delta_until_a_lap_has_been_completed()
    {
        var tracker = new LapTracker();
        DriveLap(tracker, 0, 20, 0);
        tracker.OnPacket(Lap(1, 0.0, 1000, lastLap: 20, bestLap: 20));
        tracker.Reset();

        DriveLap(tracker, 0, 25, 5000, stopAtSeconds: 10);

        var snapshot = tracker.Current;
        Assert.Equal(1, snapshot.CurrentLapNumber);
        Assert.Null(snapshot.DeltaSeconds); // the old best lap is gone, so there is nothing to compare with
        Assert.Empty(snapshot.Laps);
        Assert.Null(snapshot.BestLapSeconds);
    }

    [Fact]
    public void Laps_are_numbered_by_the_games_counter_when_the_tracker_joins_a_race_already_in_progress()
    {
        var tracker = new LapTracker();

        // The tracker's first packet already says 3 laps are done (a Start or a Reset in the middle of a race).
        DriveLap(tracker, 3, 20, 3000);
        tracker.OnPacket(Lap(4, 0.0, 4000, lastLap: 20, bestLap: 20));
        DriveLap(tracker, 4, 21, 4000, lastLap: 20, bestLap: 20, startAtSeconds: 0.1);
        tracker.OnPacket(Lap(5, 0.0, 5000, lastLap: 21, bestLap: 20));

        var snap = tracker.Current;
        Assert.Equal(6, snap.CurrentLapNumber);
        // The hero says "Lap 6", so the list must call the laps that just finished 4 and 5, never 1 and 2.
        Assert.Equal([4, 5], snap.Laps.Select(l => l.Number));
    }

    [Fact]
    public void A_lap_with_an_unknown_time_keeps_the_games_number()
    {
        var tracker = new LapTracker();

        tracker.OnPacket(Lap(7, 0.0, 0));
        tracker.OnPacket(Lap(8, 0.0, 10)); // no LastLap and a zero CurrentLap before it: the time is unknown

        var lap = Assert.Single(tracker.Current.Laps);
        Assert.Equal(8, lap.Number);
        Assert.Equal(0, lap.TimeSeconds);
    }

    [Fact]
    public void A_jump_of_more_than_one_lap_labels_the_finished_lap_with_the_packets_number_and_invents_no_laps()
    {
        var tracker = new LapTracker();
        DriveLap(tracker, 3, 20, 3000);

        // Two packets of the counter were lost (or the game paused): 3 -> 5. The lap that just finished is lap 5.
        tracker.OnPacket(Lap(5, 0.0, 5000, lastLap: 21, bestLap: 20));

        var snap = tracker.Current;
        Assert.Equal(6, snap.CurrentLapNumber);
        var lap = Assert.Single(snap.Laps); // lap 4 was never seen: no made-up row for it
        Assert.Equal(5, lap.Number);
        Assert.Equal(21, lap.TimeSeconds, 3);
    }
}
