using System.Globalization;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Laps;
using Microsoft.Extensions.Time.Testing;

namespace ForzaTelemetry.App.Tests;

/// <summary>The Laps screen driven by the REAL <see cref="LapTracker"/> with synthetic laps of a 1000 m track, as the app wires them.</summary>
public class LapsViewModelTests
{
    private const double TrackLength = 1000;

    private static string Sep => CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;

    private sealed class Rig
    {
        private uint _ms = 1_000;

        public Rig(LapTracker? tracker = null)
        {
            Tracker = tracker ?? new LapTracker();
            Vm = new LapsViewModel(Tracker);
        }

        public LapTracker Tracker { get; }

        public LapsViewModel Vm { get; }

        /// <summary>One packet through the tracker (as the receive loop does) and one UI refresh from it (as the tick does).</summary>
        public TelemetryPacket Feed(int lapsCompleted, double lapTime, double distance, double lastLap = 0, double bestLap = 0, bool raceOn = true)
        {
            var packet = TestPackets.Sample(isRaceOn: raceOn) with
            {
                TimestampMS = _ms += 20,
                LapNumber = (ushort)lapsCompleted,
                CurrentLap = (float)lapTime,
                DistanceTraveled = (float)distance,
                LastLap = (float)lastLap,
                BestLap = (float)bestLap
            };
            Tracker.OnPacket(packet);
            Vm.Refresh(packet);
            return packet;
        }

        /// <summary>Drives a lap at constant speed, 100 ms per packet, from its first packet to <paramref name="stopAtSeconds"/> (default: the whole lap).</summary>
        public void DriveLap(
            int lapsCompleted, double lapSeconds, double startDistance, double lastLap = 0, double bestLap = 0, double? stopAtSeconds = null)
        {
            var speed = TrackLength / lapSeconds;
            var end = stopAtSeconds ?? lapSeconds;
            for (var t = 0.0; t <= end + 1e-9; t += 0.1)
            {
                Feed(lapsCompleted, t, startDistance + (speed * t), lastLap, bestLap);
            }
        }

        /// <summary>Drives laps back to back (times in seconds) and then the first packet of the next lap, so the last one is recorded.</summary>
        public void DriveSession(params double[] lapTimes)
        {
            double last = 0, best = 0, distance = 0;
            for (var lap = 0; lap < lapTimes.Length; lap++)
            {
                DriveLap(lap, lapTimes[lap], distance, last, best);
                distance += TrackLength;
                last = lapTimes[lap];
                best = best == 0 ? last : Math.Min(best, last);
            }

            Feed(lapTimes.Length, 0.0, distance, last, best);
        }
    }

    private static string Time(double seconds) => LapTimeFormatter.Format(seconds);

    // ---- Empty and not racing --------------------------------------------------------------------------

    [Fact]
    public void Before_any_data_everything_is_a_placeholder_and_the_screen_says_how_to_start()
    {
        var vm = new Rig().Vm;

        Assert.False(vm.IsRacing);
        Assert.Equal(LapTimeFormatter.Zero, vm.CurrentLapText);
        Assert.Equal(LapTimeFormatter.Placeholder, vm.LastLapText);
        Assert.Equal(LapTimeFormatter.Placeholder, vm.BestLapText);
        Assert.False(vm.HasLastLap);
        Assert.False(vm.HasBestLap);
        Assert.Equal("", vm.LapNumberText);
        Assert.False(vm.HasDelta);
        Assert.Equal(LapDeltaState.Hidden, vm.DeltaState);
        Assert.False(vm.HasLaps);
        Assert.Empty(vm.Rows);
        Assert.Equal(Strings.LapsEmptyNotRacing, vm.EmptyText);
        Assert.True(vm.HasEmptyText);
        Assert.Equal(Strings.LapsNoLapInProgress, vm.HeroName); // the sentence the idle hero shows (LapsView), not the list's empty text
    }

    [Fact]
    public void The_empty_message_is_the_one_the_spec_asks_for()
    {
        Assert.Equal(
            "Lap times appear here once you start a race or time trial.",
            Strings.ResourceManager.GetString(nameof(Strings.LapsEmptyNotRacing), CultureInfo.GetCultureInfo("en")));
    }

    [Fact]
    public void A_running_lap_shows_its_clock_and_number_and_asks_for_a_first_lap()
    {
        var rig = new Rig();

        rig.DriveLap(0, 20, 0, stopAtSeconds: 5.0);

        Assert.True(rig.Vm.IsRacing);
        Assert.Equal(Time(5.0), rig.Vm.CurrentLapText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.LapsLapFormat, 1), rig.Vm.LapNumberText);
        Assert.Equal(Strings.LapsEmptyNoLapsYet, rig.Vm.EmptyText); // racing, but no finished lap to list yet
        Assert.False(rig.Vm.HasDelta); // the first lap has nothing to compare with
        Assert.Contains(rig.Vm.CurrentLapText, rig.Vm.HeroName);
    }

    [Fact]
    public void A_menu_or_pause_packet_replaces_the_live_clock_and_delta_with_placeholders_but_keeps_the_laps()
    {
        var rig = new Rig();
        rig.DriveSession(21.4, 20.3);
        rig.DriveLap(2, 20.9, 2000, stopAtSeconds: 4.0);
        Assert.True(rig.Vm.IsRacing);

        rig.Feed(2, 4.0, 2190, raceOn: false);

        Assert.False(rig.Vm.IsRacing);
        Assert.Equal(LapTimeFormatter.Zero, rig.Vm.CurrentLapText);
        Assert.False(rig.Vm.HasDelta);
        Assert.Equal(2, rig.Vm.Rows.Count); // what was driven stays readable
        Assert.True(rig.Vm.HasLastLap);
    }

    [Fact]
    public void Without_a_packet_the_screen_is_not_racing()
    {
        var rig = new Rig();
        rig.DriveLap(0, 20, 0, stopAtSeconds: 5.0);

        rig.Vm.Refresh(null);

        Assert.False(rig.Vm.IsRacing);
        Assert.Equal(LapTimeFormatter.Zero, rig.Vm.CurrentLapText);
    }

    // ---- Last, best, list --------------------------------------------------------------------------------

    [Fact]
    public void Finished_laps_fill_last_best_and_the_list_newest_first_with_the_fastest_marked()
    {
        var rig = new Rig();

        rig.DriveSession(21.4, 20.3, 20.9);

        Assert.Equal(Time(20.9), rig.Vm.LastLapText);
        Assert.Equal(Time(20.3), rig.Vm.BestLapText);
        Assert.True(rig.Vm.HasLastLap);
        Assert.True(rig.Vm.HasBestLap);
        Assert.Equal(["3", "2", "1"], rig.Vm.Rows.Select(r => r.NumberText));
        Assert.Equal([Time(20.9), Time(20.3), Time(21.4)], rig.Vm.Rows.Select(r => r.TimeText));
        Assert.Equal([false, true, false], rig.Vm.Rows.Select(r => r.IsBest));
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.LapsLapFormat, 4), rig.Vm.LapNumberText);
        Assert.False(rig.Vm.HasEmptyText);
    }

    [Fact]
    public void Each_row_shows_its_gap_to_the_best_lap_and_the_best_lap_shows_none()
    {
        var rig = new Rig();

        rig.DriveSession(21.4, 20.3, 20.9);

        var lap3 = rig.Vm.Rows[0];
        var lap2 = rig.Vm.Rows[1];
        var lap1 = rig.Vm.Rows[2];
        Assert.Equal("+0" + Sep + "600", lap3.DeltaText);
        Assert.Equal("", lap2.DeltaText);
        Assert.Equal("+1" + Sep + "100", lap1.DeltaText);
    }

    [Fact]
    public void A_tie_for_best_marks_the_earlier_lap()
    {
        var rig = new Rig();

        rig.DriveSession(20.0, 20.0);

        Assert.Equal([false, true], rig.Vm.Rows.Select(r => r.IsBest)); // list is newest first: lap 1 is last
        Assert.Equal("1", rig.Vm.Rows.Single(r => r.IsBest).NumberText);
    }

    [Fact]
    public void A_lap_without_a_time_is_listed_without_a_gap_and_is_never_the_best()
    {
        var rig = new Rig();
        rig.Feed(0, 0.0, 0.0);
        rig.Feed(1, 0.0, 400, lastLap: 0); // the game reported no time for the lap that just ended

        var row = Assert.Single(rig.Vm.Rows);
        Assert.Equal("1", row.NumberText);
        Assert.Equal(LapTimeFormatter.Placeholder, row.TimeText);
        Assert.Equal("", row.DeltaText);
        Assert.False(row.IsBest);
    }

    [Fact]
    public void The_list_is_rebuilt_only_when_a_lap_finishes()
    {
        var rig = new Rig();
        rig.DriveSession(21.4);
        var rows = rig.Vm.Rows;

        rig.Feed(1, 1.0, 1050);
        rig.Feed(1, 1.1, 1055);

        Assert.Same(rows, rig.Vm.Rows); // same list instance: 20 refreshes a second must not rebuild it
    }

    [Fact]
    public void A_row_reads_as_one_sentence_for_screen_readers()
    {
        var rig = new Rig();
        rig.DriveSession(21.4, 20.3);

        var best = rig.Vm.Rows.Single(r => r.IsBest);
        var other = rig.Vm.Rows.Single(r => !r.IsBest);

        Assert.Contains(Strings.LapsBestTag, best.Summary);
        Assert.Contains(best.TimeText, best.Summary);
        Assert.Contains(other.DeltaText, other.Summary);
        Assert.Contains(other.TimeText, other.Summary);
    }

    // ---- Live delta ------------------------------------------------------------------------------------

    [Fact]
    public void Driving_faster_than_the_best_lap_shows_a_negative_delta_as_ahead()
    {
        var rig = new Rig();
        rig.DriveSession(21.0); // best lap 21.0 s at constant speed

        rig.DriveLap(1, 20.0, 1000, lastLap: 21.0, bestLap: 21.0, stopAtSeconds: 10.0); // same distance in less time

        Assert.Equal(LapDeltaState.Ahead, rig.Vm.DeltaState);
        Assert.True(rig.Vm.HasDelta);
        Assert.StartsWith("-", rig.Vm.DeltaText);
        Assert.Contains(rig.Vm.DeltaText.TrimStart('-'), rig.Vm.DeltaName);
        Assert.Contains(rig.Vm.DeltaName, rig.Vm.HeroName);
    }

    [Fact]
    public void Driving_slower_than_the_best_lap_shows_a_positive_delta_as_behind()
    {
        var rig = new Rig();
        rig.DriveSession(20.0);

        rig.DriveLap(1, 22.0, 1000, lastLap: 20.0, bestLap: 20.0, stopAtSeconds: 10.0);

        Assert.Equal(LapDeltaState.Behind, rig.Vm.DeltaState);
        Assert.StartsWith("+", rig.Vm.DeltaText);
        Assert.Contains(rig.Vm.DeltaText.TrimStart('+'), rig.Vm.DeltaName);
    }

    [Fact]
    public void Matching_the_best_lap_exactly_shows_level_with_no_sign()
    {
        var rig = new Rig();
        rig.DriveSession(20.0);

        rig.DriveLap(1, 20.0, 1000, lastLap: 20.0, bestLap: 20.0, stopAtSeconds: 10.0);

        Assert.Equal(LapDeltaState.Even, rig.Vm.DeltaState);
        Assert.Equal("0" + Sep + "000", rig.Vm.DeltaText);
        Assert.Equal(Strings.LapsDeltaEvenName, rig.Vm.DeltaName);
    }

    [Fact]
    public void The_delta_is_hidden_again_when_the_screen_stops_racing()
    {
        var rig = new Rig();
        rig.DriveSession(21.0);
        rig.DriveLap(1, 20.0, 1000, lastLap: 21.0, bestLap: 21.0, stopAtSeconds: 10.0);
        Assert.True(rig.Vm.HasDelta);

        rig.Vm.Refresh(null);

        Assert.False(rig.Vm.HasDelta);
        Assert.Equal("", rig.Vm.DeltaText);
        Assert.Equal("", rig.Vm.DeltaName);
    }

    [Fact]
    public void The_best_lap_card_prefers_the_games_own_best_lap()
    {
        var rig = new Rig();
        rig.DriveLap(0, 22.0, 0, bestLap: 19.5, stopAtSeconds: 5.0); // an earlier session's best, reported by the game

        Assert.Equal(Time(19.5), rig.Vm.BestLapText);
    }

    // ---- Session edges ---------------------------------------------------------------------------------

    [Fact]
    public void BeginSession_forgets_the_laps_and_the_reference_lap()
    {
        var rig = new Rig();
        rig.DriveSession(21.4, 20.3);
        Assert.True(rig.Vm.HasLaps);

        rig.Tracker.Reset(); // the engine reset, which the shell does before the loop starts
        rig.Vm.BeginSession();

        Assert.False(rig.Vm.HasLaps);
        Assert.Equal(LapTimeFormatter.Placeholder, rig.Vm.LastLapText);
        Assert.Equal(LapTimeFormatter.Placeholder, rig.Vm.BestLapText);
        Assert.Equal(Strings.LapsEmptyNotRacing, rig.Vm.EmptyText);
        Assert.Equal(LapSnapshot.Empty, rig.Tracker.Current);
    }

    [Fact]
    public void EndSession_keeps_the_laps_readable_and_stops_the_live_clock()
    {
        var rig = new Rig();
        rig.DriveSession(21.4, 20.3);

        rig.Vm.EndSession();

        Assert.Equal(2, rig.Vm.Rows.Count);
        Assert.Equal(Time(20.3), rig.Vm.BestLapText);
        Assert.False(rig.Vm.IsRacing);
        Assert.Equal(LapTimeFormatter.Zero, rig.Vm.CurrentLapText);
    }

    // ---- Shell wiring ------------------------------------------------------------------------------------

    [Fact]
    public void The_shell_shows_the_laps_screen_when_given_one()
    {
        var rig = new Rig();
        var shell = new ShellViewModel(
            new TelemetryService(() => new FakeTelemetrySource(), observers: [rig.Tracker]), TimeProvider.System, lapsPage: rig.Vm);

        shell.NavigateCommand.Execute(PageId.Laps);

        Assert.Same(rig.Vm, shell.CurrentPage);
        Assert.Same(rig.Vm, shell.Laps);
    }

    [Fact]
    public async Task Packets_flow_from_the_service_through_the_tracker_to_the_laps_screen_and_go_stale_after_silence()
    {
        var rig = new Rig();
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake, observers: [rig.Tracker]);
        var clock = new FakeTimeProvider();
        var shell = new ShellViewModel(service, clock, lapsPage: rig.Vm);
        await shell.StartCommand.ExecuteAsync(null);

        fake.Publish(TestPackets.Sample() with { TimestampMS = 1000, CurrentLap = 12.5f, DistanceTraveled = 300f });
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        shell.Refresh();

        Assert.True(rig.Vm.IsRacing);
        Assert.Equal(Time(12.5), rig.Vm.CurrentLapText);

        clock.Advance(TimeSpan.FromSeconds(5)); // the stream goes quiet: the last packet is no longer "now"
        shell.Refresh();

        Assert.False(rig.Vm.IsRacing);
        Assert.Equal(LapTimeFormatter.Zero, rig.Vm.CurrentLapText);

        await shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Starting_the_listener_clears_the_previous_sessions_laps()
    {
        var engines = new TelemetryEngines();
        var rig = new Rig(engines.Laps);
        var service = new TelemetryService(() => new FakeTelemetrySource(), observers: engines.Observers);
        var shell = new ShellViewModel(service, TimeProvider.System, lapsPage: rig.Vm, engines: engines);
        rig.DriveSession(21.4);
        Assert.True(rig.Vm.HasLaps);

        await shell.StartCommand.ExecuteAsync(null);

        Assert.False(rig.Vm.HasLaps);
        await shell.StopCommand.ExecuteAsync(null);
    }

    // ---- Idle clock and notifications ---------------------------------------------------------------------

    [Fact]
    public void When_no_lap_is_in_progress_the_clock_is_the_zero_time_not_a_row_of_dashes()
    {
        var rig = new Rig();

        Assert.False(rig.Vm.IsRacing);
        Assert.Equal("0:00" + Sep + "000", rig.Vm.CurrentLapText);
        Assert.NotEqual(LapTimeFormatter.Placeholder, rig.Vm.CurrentLapText);
    }

    [Fact]
    public void The_no_lap_in_progress_sentence_exists_in_both_languages()
    {
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("es") })
        {
            Assert.False(string.IsNullOrWhiteSpace(Strings.ResourceManager.GetString(nameof(Strings.LapsNoLapInProgress), culture)));
        }
    }

    [Fact]
    public void A_refresh_that_changes_nothing_raises_no_hero_name_notification()
    {
        var rig = new Rig();
        rig.Feed(0, 5.0, 100);
        var raised = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        rig.Feed(0, 5.0, 100); // identical values

        Assert.DoesNotContain(nameof(LapsViewModel.HeroName), raised);
    }

    [Fact]
    public void The_hero_name_is_announced_as_changed_when_the_clock_moves()
    {
        var rig = new Rig();
        rig.Feed(0, 5.0, 100);
        var raised = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        rig.Feed(0, 6.0, 200);

        Assert.Contains(nameof(LapsViewModel.HeroName), raised);
        Assert.Contains(Time(6.0), rig.Vm.HeroName);
    }

    [Fact]
    public void A_tracker_that_joins_a_race_on_lap_four_lists_the_games_lap_numbers()
    {
        var rig = new Rig();
        rig.DriveLap(3, 20, 3000);
        rig.Feed(4, 0.0, 4000, lastLap: 20, bestLap: 20);

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.LapsLapFormat, 5), rig.Vm.LapNumberText);
        Assert.Equal("4", Assert.Single(rig.Vm.Rows).NumberText); // the lap that just ended is lap 4, as the game counts it
    }

    [Fact]
    public void The_idle_hero_name_is_the_sentence_the_idle_hero_shows()
    {
        var rig = new Rig();
        rig.DriveLap(0, 20, 0, stopAtSeconds: 5.0);
        Assert.True(rig.Vm.IsRacing);

        rig.Vm.Refresh(null); // the stream is gone

        Assert.False(rig.Vm.IsRacing);
        Assert.Equal(Strings.LapsNoLapInProgress, rig.Vm.HeroName);
    }
}
