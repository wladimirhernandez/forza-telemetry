using System.Globalization;
using System.IO;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Performance;
using Microsoft.Extensions.Time.Testing;

namespace ForzaTelemetry.App.Tests;

/// <summary>
/// The Performance screen driven by the REAL engines and a REAL history store (in a temp folder) with synthetic packets, the
/// way the app wires them: every packet goes to the engines, and the view model is refreshed from the latest one.
/// </summary>
public sealed class PerformanceViewModelTests : IDisposable
{
    private const double Accel = 10; // m/s^2: speed = a*t, distance = a*t^2/2, so every expected time has a closed form

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeConfirmation(bool answer) : IConfirmationService
    {
        public List<(string Title, string Message, string Confirm, string Cancel)> Asked { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
        {
            Asked.Add((title, message, confirmText, cancelText));
            return Task.FromResult(answer);
        }
    }

    private sealed class Rig
    {
        private uint _ms = 1_000;

        public Rig(string directory, TimeProvider time, bool confirm = true)
        {
            HistoryPath = Path.Combine(directory, "performance.json");
            History = new PerformanceHistoryStore(HistoryPath, time);
            Units = new UnitsService();
            Confirmation = new FakeConfirmation(confirm);
            CarChanges = new CarChangeDetector(TopSpeed, Acceleration);
            Vm = new PerformanceViewModel(Acceleration, TopSpeed, CarChanges, History, Units, Confirmation);
        }

        public AccelerationTimer Acceleration { get; } = new();

        public TopSpeedTracker TopSpeed { get; } = new();

        public CarChangeDetector CarChanges { get; }

        public string HistoryPath { get; }

        public PerformanceHistoryStore History { get; }

        public UnitsService Units { get; }

        public FakeConfirmation Confirmation { get; }

        public PerformanceViewModel Vm { get; }

        /// <summary>
        /// What the shell does for a fresh session, in its order: save the previous session, reset the engines (the detector
        /// included) while no loop runs, then let the screen reset what it displays.
        /// </summary>
        public void StartNewSession()
        {
            Vm.EndSession();
            CarChanges.Reset();
            TopSpeed.Reset();
            Acceleration.Reset();
            Vm.BeginSession();
        }

        public int Car { get; set; } = 1234;

        public int Pi { get; set; } = 780;

        public int CarClass { get; set; } = 3;

        private TelemetryPacket Packet(double speed, double distance, byte gear, bool raceOn) =>
            TestPackets.Sample(
                speedMetersPerSecond: (float)speed,
                gear: gear,
                carOrdinal: Car,
                carClass: CarClass,
                performanceIndex: Pi,
                drivetrain: 2,
                isRaceOn: raceOn) with
            {
                TimestampMS = _ms,
                DistanceTraveled = (float)distance
            };

        /// <summary>One packet through the engines (as the receive loop does) and one UI refresh from it (as the tick does).</summary>
        public TelemetryPacket Feed(double speed, double distance = 0, byte gear = 1, bool raceOn = true, uint stepMs = 20)
        {
            var packet = Packet(speed, distance, gear, raceOn);
            CarChanges.OnPacket(packet); // first, like the app: it must see a new car before the engines do
            Acceleration.OnPacket(packet);
            TopSpeed.OnPacket(packet);
            Vm.Refresh(packet);
            _ms += stepMs;
            return packet;
        }

        public void Stand(int packets = 10)
        {
            for (var i = 0; i < packets; i++)
            {
                Feed(0);
            }
        }

        /// <summary>A constant-acceleration launch from the line for <paramref name="seconds"/>.</summary>
        public void Launch(double seconds, double accel = Accel)
        {
            for (var t = 0.0; t <= seconds + 1e-9; t += 0.02)
            {
                Feed(accel * t, 0.5 * accel * t * t, gear: 1);
            }
        }

        /// <summary>Brakes from the given speed to a standstill (the timer arms again).</summary>
        public void BrakeToStop(double fromSpeed, double distance)
        {
            for (var i = 0; i <= 100; i++)
            {
                Feed(Math.Max(0, fromSpeed * (1 - (i / 100.0))), distance, gear: 2);
            }

            Stand(3);
        }
    }

    private Rig NewRig(bool confirm = true) => new(_directory, _time, confirm);

    private static string CarLabel(int ordinal) => string.Format(CultureInfo.CurrentCulture, Strings.PerfCarOrdinalFormat, ordinal);

    private static double Parse(string text) => double.Parse(text, CultureInfo.CurrentCulture);

    // ---- State line ---------------------------------------------------------------------------------------

    [Fact]
    public void Before_any_data_the_state_says_waiting_and_every_card_is_empty()
    {
        var rig = NewRig();

        Assert.Equal(PerformanceRunState.WaitingForData, rig.Vm.State);
        Assert.Equal(Strings.PerfStateWaiting, rig.Vm.StateText);
        Assert.Equal("--", rig.Vm.ZeroTo100Text);
        Assert.Equal("--", rig.Vm.ZeroTo200Text);
        Assert.Equal("--", rig.Vm.QuarterMileText);
        Assert.Equal("--", rig.Vm.TopSpeedText);
        Assert.Equal("", rig.Vm.QuarterMileTrapText);
        Assert.False(rig.Vm.HasCar);
        Assert.Equal(Strings.PerfNoCar, rig.Vm.CarSummary);
        Assert.False(rig.Vm.HasHistory);
    }

    [Fact]
    public void Driving_away_from_the_line_says_ready_and_standing_still_arms_the_timer()
    {
        var rig = NewRig();

        rig.Feed(30, gear: 3); // moving: not on the line
        Assert.Equal(PerformanceRunState.Idle, rig.Vm.State);
        Assert.Equal(Strings.PerfStateReady, rig.Vm.StateText);

        rig.Stand();
        Assert.Equal(PerformanceRunState.Armed, rig.Vm.State);
        Assert.Equal(Strings.PerfStateArmed, rig.Vm.StateText);
    }

    [Fact]
    public void A_launch_shows_running_with_the_elapsed_time_and_finishes_as_done_with_the_results()
    {
        var rig = NewRig();
        rig.Stand();

        rig.Launch(4.0);
        Assert.Equal(PerformanceRunState.Running, rig.Vm.State);
        Assert.Equal(Strings.PerfStateRunning, rig.Vm.StateText);
        Assert.Matches(@"^\d+[.,]\d s$", rig.Vm.ElapsedText);
        Assert.Equal(2.75, Parse(rig.Vm.ZeroTo100Text), precision: 2); // reached during the run, shown at once
        Assert.Equal("--", rig.Vm.QuarterMileText); // not reached yet

        rig.Launch(9.5);
        Assert.Equal(PerformanceRunState.Done, rig.Vm.State);
        Assert.Equal(Strings.PerfStateDone, rig.Vm.StateText);
        Assert.Equal("", rig.Vm.ElapsedText);
        Assert.Equal(2.75, Parse(rig.Vm.ZeroTo100Text), precision: 2);
        Assert.Equal(5.53, Parse(rig.Vm.ZeroTo200Text), precision: 2); // (200/3.6)/10 minus the 1 km/h start offset
        Assert.Equal(8.94, Parse(rig.Vm.QuarterMileText), precision: 2);
        Assert.Contains("km/h", rig.Vm.QuarterMileTrapText);
        var trapKmh = rig.History.Records[0].QuarterMileTrapKmh!.Value;
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, Strings.PerfTrapFormat, trapKmh.ToString("F0", CultureInfo.CurrentCulture), "km/h"),
            rig.Vm.QuarterMileTrapText);
    }

    [Fact]
    public void Without_driving_data_the_line_says_waiting_even_if_the_engine_still_remembers_being_armed()
    {
        var rig = NewRig();
        rig.Stand();
        Assert.Equal(PerformanceRunState.Armed, rig.Vm.State);

        rig.Vm.Refresh(null); // stopped, or the game closed

        Assert.Equal(PerformanceRunState.WaitingForData, rig.Vm.State);
    }

    [Fact]
    public void A_pause_while_stationary_says_waiting_and_resuming_arms_again()
    {
        var rig = NewRig();
        rig.Stand();

        rig.Feed(0, raceOn: false);
        Assert.Equal(PerformanceRunState.WaitingForData, rig.Vm.State);

        rig.Feed(0);
        Assert.Equal(PerformanceRunState.Armed, rig.Vm.State);
    }

    [Fact]
    public void A_run_cut_short_before_anything_was_measured_shows_aborted_until_the_car_is_ready_again()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(1.0); // running, 0-100 not reached
        Assert.Equal(PerformanceRunState.Running, rig.Vm.State);

        rig.Feed(10, 5, gear: 0); // reverse gear while moving: the engine drops the run

        Assert.Equal(PerformanceRunState.Aborted, rig.Vm.State);
        Assert.Equal(Strings.PerfStateAborted, rig.Vm.StateText);
        Assert.Equal("--", rig.Vm.ZeroTo100Text);

        rig.BrakeToStop(10, 5); // stopping on the line arms it again, which replaces the message
        Assert.Equal(PerformanceRunState.Armed, rig.Vm.State);
    }

    // ---- Saving -------------------------------------------------------------------------------------------

    [Fact]
    public void A_finished_run_is_saved_under_its_car_with_the_class_and_the_file_is_written()
    {
        var rig = NewRig();
        rig.Stand();

        rig.Launch(10.0);

        var record = Assert.Single(rig.History.Records);
        Assert.Equal(new CarKey(1234, 780), record.Car);
        Assert.Equal(3, record.CarClass);
        Assert.Equal(2.75, record.ZeroTo100Seconds!.Value, precision: 2);
        Assert.Equal(8.94, record.QuarterMileSeconds!.Value, precision: 2);
        Assert.True(record.TopSpeedKmh > 300);
        Assert.True(File.Exists(rig.HistoryPath));
        var row = Assert.Single(rig.Vm.Rows);
        Assert.Equal(CarLabel(1234), row.CarLabel);
        Assert.Equal("A", row.ClassLabel);
        Assert.Equal("780", row.PerformanceIndexText);
        Assert.Equal("AWD", row.DrivetrainLabel);
        Assert.True(row.IsCurrentCar);
    }

    [Fact]
    public void Nothing_is_saved_while_a_run_is_still_in_progress()
    {
        var rig = NewRig();
        rig.Stand();

        rig.Launch(4.0); // 0-100 already reached, run not over

        Assert.Empty(rig.History.Records);
        Assert.False(File.Exists(rig.HistoryPath));
    }

    [Fact]
    public void The_first_result_of_a_car_is_not_flagged_but_a_faster_run_is_flagged_as_a_new_best()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(10.0);
        Assert.False(rig.Vm.ZeroTo100IsNewBest);
        Assert.False(rig.Vm.QuarterMileIsNewBest);
        rig.BrakeToStop(100, 800);

        rig.Launch(10.0, accel: 11); // a quicker car on the same key: every time improves

        Assert.True(rig.Vm.ZeroTo100IsNewBest);
        Assert.True(rig.Vm.ZeroTo200IsNewBest);
        Assert.True(rig.Vm.QuarterMileIsNewBest);
        Assert.Equal(2.5, rig.History.Find(new CarKey(1234, 780))!.ZeroTo100Seconds!.Value, precision: 1);
    }

    [Fact]
    public void A_slower_run_keeps_the_stored_best_and_shows_no_new_best_tag()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(10.0, accel: 11);
        rig.BrakeToStop(100, 800);
        var best = rig.History.Find(new CarKey(1234, 780))!.ZeroTo100Seconds;

        rig.Launch(10.0, accel: 9);

        Assert.Equal(best, rig.History.Find(new CarKey(1234, 780))!.ZeroTo100Seconds);
        Assert.False(rig.Vm.ZeroTo100IsNewBest);
        Assert.Single(rig.History.Records);
    }

    [Fact]
    public void The_new_best_tags_clear_when_the_next_run_starts()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(10.0);
        rig.BrakeToStop(100, 800);
        rig.Launch(10.0, accel: 11);
        Assert.True(rig.Vm.ZeroTo100IsNewBest);
        rig.BrakeToStop(100, 1600);

        rig.Launch(1.0, accel: 9);

        Assert.False(rig.Vm.ZeroTo100IsNewBest);
        Assert.False(rig.Vm.QuarterMileIsNewBest);
    }

    [Fact]
    public void A_run_is_attributed_to_the_car_that_started_it()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(10.0);

        Assert.Equal(1234, Assert.Single(rig.History.Records).Car.CarOrdinal);
    }

    // ---- Car changes --------------------------------------------------------------------------------------

    [Fact]
    public void The_car_summary_shows_the_ordinal_class_pi_and_drivetrain()
    {
        var rig = NewRig();

        rig.Feed(0);

        Assert.True(rig.Vm.HasCar);

        // Separate values for the separate pills, and one sentence (with commas, no separator character) for screen readers.
        Assert.Equal(CarLabel(1234), rig.Vm.CarText);
        Assert.Equal("A", rig.Vm.CarClassLabel);
        Assert.Equal(780, rig.Vm.CarPerformanceIndex);
        Assert.Equal("AWD", rig.Vm.DrivetrainLabel);
        Assert.Equal($"{CarLabel(1234)}, A 780, AWD", rig.Vm.CarSummary);
        Assert.DoesNotContain('·', rig.Vm.CarSummary);
    }

    [Fact]
    public void A_menu_packet_without_a_real_car_does_not_clear_the_current_one()
    {
        var rig = NewRig();
        rig.Feed(0);

        rig.Pi = 0;
        rig.Feed(0, raceOn: true); // loading screen: PI 0

        Assert.True(rig.Vm.HasCar);
        Assert.Contains("780", rig.Vm.CarSummary);
    }

    [Fact]
    public void Switching_cars_saves_the_old_cars_top_speed_and_clears_the_cards_for_the_new_one()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(10.0);
        Assert.NotEqual("--", rig.Vm.ZeroTo100Text);

        rig.Car = 77;
        rig.Pi = 450;
        rig.CarClass = 1;
        rig.Feed(0);

        Assert.Equal("--", rig.Vm.ZeroTo100Text); // the previous car's result is not this car's
        Assert.Equal("--", rig.Vm.TopSpeedText);
        Assert.Contains(CarLabel(77), rig.Vm.CarSummary);
        var saved = Assert.Single(rig.History.Records);
        Assert.Equal(1234, saved.Car.CarOrdinal); // the old car's data was kept
        Assert.Equal(3, saved.CarClass); // ... under the OLD car's class (the new car is class 1), not the new one's
        Assert.True(File.Exists(rig.HistoryPath));
        Assert.Contains(rig.Vm.Rows, r => r.CarLabel == CarLabel(1234) && !r.IsCurrentCar);
    }

    [Fact]
    public void The_old_cars_top_speed_is_frozen_on_the_packet_where_the_car_changes_not_at_the_next_tick()
    {
        var rig = NewRig();
        rig.Feed(70, gear: 5); // 252 km/h in car 1234

        // Three packets of the NEW car arrive before the screen gets a tick (the game sends ~3 per 50 ms tick): the old
        // best must not absorb them, and the new car must not inherit it. Only the engines see these, no Vm.Refresh.
        rig.Car = 77;
        rig.Pi = 450;
        for (var i = 0; i < 3; i++)
        {
            var packet = TestPackets.Sample(speedMetersPerSecond: 20, gear: 3, carOrdinal: 77, performanceIndex: 450);
            rig.CarChanges.OnPacket(packet);
            rig.TopSpeed.OnPacket(packet);
        }

        Assert.Equal(20 * 3.6, rig.TopSpeed.TopSpeedKmh, precision: 3); // the new car starts its own maximum
        rig.Feed(20, gear: 3);

        Assert.Equal(252, rig.History.Find(new CarKey(1234, 780))!.TopSpeedKmh!.Value, precision: 1);
        Assert.Equal(CarLabel(77), rig.Vm.CarText);
    }

    [Fact]
    public void A_new_session_saves_the_previous_sessions_top_speed_before_the_engines_are_reset()
    {
        var rig = NewRig();
        rig.Feed(70, gear: 5);
        Assert.Empty(rig.History.Records); // not saved yet: nothing ended the session (a fault restart has no Stop)

        rig.StartNewSession();

        Assert.Equal(252, rig.History.Find(new CarKey(1234, 780))!.TopSpeedKmh!.Value, precision: 1);
        Assert.True(File.Exists(rig.HistoryPath)); // and written, not only merged in memory
        Assert.Equal("--", rig.Vm.TopSpeedText);
    }

    [Fact]
    public void The_same_car_is_picked_up_again_after_BeginSession()
    {
        var rig = NewRig();
        rig.Feed(0);
        rig.StartNewSession();
        Assert.False(rig.Vm.HasCar);

        rig.Feed(0); // the same car, first packet of the new session

        Assert.True(rig.Vm.HasCar);
        Assert.Equal(CarLabel(1234), rig.Vm.CarText);
    }

    [Fact]
    public void ResetRun_writes_the_saved_top_speed_to_disk_before_forgetting_it()
    {
        var rig = NewRig();
        rig.Feed(70, gear: 5);

        rig.Vm.ResetRunCommand.Execute(null);

        Assert.True(File.Exists(rig.HistoryPath));
        var reloaded = new PerformanceHistoryStore(rig.HistoryPath, _time);
        reloaded.Load();
        Assert.Equal(252, reloaded.Find(new CarKey(1234, 780))!.TopSpeedKmh!.Value, precision: 1);
        Assert.Equal("--", rig.Vm.TopSpeedText);
    }

    [Fact]
    public void A_refresh_that_changes_nothing_raises_no_screen_reader_name_notifications()
    {
        var rig = NewRig();
        rig.Feed(0);
        var raised = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        rig.Feed(0);
        rig.Feed(0);

        Assert.DoesNotContain(raised, name => name is not null && name.EndsWith("Name", StringComparison.Ordinal));
    }

    [Fact]
    public void The_screen_reader_name_of_a_card_changes_together_with_its_value()
    {
        var rig = NewRig();
        var raised = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        rig.Feed(70, gear: 5); // top speed goes from "--" to 252

        Assert.Contains(nameof(PerformanceViewModel.TopSpeedName), raised);
        Assert.Contains("252", rig.Vm.TopSpeedName);
    }

    [Fact]
    public void An_upgraded_car_with_a_new_performance_index_gets_its_own_row()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(10.0);

        rig.Pi = 820;
        rig.Stand();
        rig.Launch(10.0, accel: 11);

        Assert.Equal(2, rig.History.Records.Count);
        Assert.Equal([780, 820], rig.History.Records.Select(r => r.Car.PerformanceIndex).Order());
    }

    [Fact]
    public void Top_speed_is_saved_once_the_car_has_slowed_well_below_its_peak()
    {
        var rig = NewRig();
        rig.Feed(60, gear: 5);
        rig.Feed(70, gear: 5); // 252 km/h
        Assert.Empty(rig.History.Records); // still at the peak: the pass may not be over

        rig.Feed(55, gear: 4); // below 85 % of the peak

        var record = Assert.Single(rig.History.Records);
        Assert.Equal(252, record.TopSpeedKmh!.Value, precision: 1);
        Assert.True(File.Exists(rig.HistoryPath));
    }

    // ---- Session edges ----------------------------------------------------------------------------------

    [Fact]
    public void EndSession_saves_the_top_speed_and_keeps_the_numbers_on_screen()
    {
        var rig = NewRig();
        rig.Feed(70, gear: 5);

        rig.Vm.EndSession();

        Assert.Equal(252, rig.History.Find(new CarKey(1234, 780))!.TopSpeedKmh!.Value, precision: 1);
        Assert.Equal("252", rig.Vm.TopSpeedText);
        Assert.Equal(PerformanceRunState.WaitingForData, rig.Vm.State);
    }

    [Fact]
    public void BeginSession_clears_the_screen_and_the_engines_but_not_the_saved_history()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(10.0);
        rig.Vm.EndSession();

        rig.StartNewSession();

        Assert.Equal("--", rig.Vm.ZeroTo100Text);
        Assert.Equal("--", rig.Vm.TopSpeedText);
        Assert.False(rig.Vm.HasCar);
        Assert.Equal(AccelerationSnapshot.Empty, rig.Acceleration.Current);
        Assert.Equal(0, rig.TopSpeed.TopSpeedMs);
        Assert.Single(rig.Vm.Rows); // the best of the previous session is still there
    }

    // ---- Reset run ----------------------------------------------------------------------------------------

    [Fact]
    public void Reset_run_clears_the_cards_immediately_but_keeps_what_was_saved()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(10.0);

        rig.Vm.ResetRunCommand.Execute(null);

        Assert.Equal("--", rig.Vm.ZeroTo100Text);
        Assert.Equal("--", rig.Vm.QuarterMileText);
        Assert.Equal("--", rig.Vm.TopSpeedText);
        Assert.Single(rig.History.Records);
    }

    [Fact]
    public void Reset_run_saves_the_session_top_speed_first_so_it_is_not_lost()
    {
        var rig = NewRig();
        rig.Feed(70, gear: 5);

        rig.Vm.ResetRunCommand.Execute(null);

        Assert.Equal(252, rig.History.Find(new CarKey(1234, 780))!.TopSpeedKmh!.Value, precision: 1);
        Assert.Equal("--", rig.Vm.TopSpeedText);
    }

    // ---- Units ------------------------------------------------------------------------------------------------

    [Fact]
    public void Speeds_follow_the_unit_but_the_run_titles_stay_metric_and_a_note_explains_it()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(10.0);
        Assert.False(rig.Vm.ShowThresholdNote);
        Assert.Equal("km/h", rig.Vm.SpeedUnitLabel);
        var topKmh = Parse(rig.Vm.TopSpeedText);

        rig.Units.SpeedUnit = SpeedUnit.Mph;

        Assert.True(rig.Vm.ShowThresholdNote);
        Assert.Equal("mph", rig.Vm.SpeedUnitLabel);
        Assert.Equal(topKmh / 1.609344, Parse(rig.Vm.TopSpeedText), precision: 0);
        Assert.Contains("mph", rig.Vm.QuarterMileTrapText);
        Assert.Contains("mph", Assert.Single(rig.Vm.Rows).TopSpeedText);
        Assert.Equal("2.75", rig.Vm.ZeroTo100Text.Replace(',', '.')); // times never convert
        Assert.Contains("100", Strings.PerfThresholdNote);
    }

    [Fact]
    public void A_temperature_change_does_not_touch_the_speed_readouts()
    {
        var rig = NewRig();
        var changed = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        rig.Units.TemperatureUnit = TemperatureUnit.Fahrenheit;

        Assert.DoesNotContain(nameof(PerformanceViewModel.SpeedUnitLabel), changed);
        Assert.DoesNotContain(nameof(PerformanceViewModel.TopSpeedText), changed);
    }

    // ---- History table --------------------------------------------------------------------------------------

    private static void Seed(PerformanceHistoryStore store, TimeProvider time)
    {
        var clock = (FakeTimeProvider)time;
        store.Merge(new CarKey(300, 600), 2, 0, new PerformanceResult(5.0, 15.0, 14.0, 160, 200));
        clock.Advance(TimeSpan.FromHours(1));
        store.Merge(new CarKey(100, 800), 4, 2, new PerformanceResult(3.0, 8.0, 11.0, 210, 300));
        clock.Advance(TimeSpan.FromHours(1));
        store.Merge(new CarKey(200, 700), 3, 1, new PerformanceResult(4.0, null, 12.5, 180, 260));
    }

    [Fact]
    public void The_table_defaults_to_the_most_recently_improved_car_first()
    {
        var rig = NewRig();
        Seed(rig.History, _time);

        Assert.Equal([CarLabel(200), CarLabel(100), CarLabel(300)], rig.Vm.Rows.Select(r => r.CarLabel));
        Assert.True(rig.Vm.IsSortedByRecent);
    }

    [Theory]
    [InlineData(PerformanceSortColumn.Car, new[] { 100, 200, 300 })]
    [InlineData(PerformanceSortColumn.ZeroTo100, new[] { 100, 200, 300 })]
    [InlineData(PerformanceSortColumn.ZeroTo200, new[] { 100, 300, 200 })] // a car with no 0-200 sorts last
    [InlineData(PerformanceSortColumn.QuarterMile, new[] { 100, 200, 300 })]
    [InlineData(PerformanceSortColumn.TopSpeed, new[] { 100, 200, 300 })] // highest first
    public void Each_header_sorts_the_table_by_its_column(PerformanceSortColumn column, int[] expectedCars)
    {
        var rig = NewRig();
        Seed(rig.History, _time);

        rig.Vm.SortByCommand.Execute(column);

        Assert.Equal(expectedCars.Select(CarLabel), rig.Vm.Rows.Select(r => r.CarLabel));
        Assert.Equal(column, rig.Vm.SortColumn);
    }

    [Fact]
    public void The_sorted_column_announces_itself_for_the_header_marker()
    {
        var rig = NewRig();
        var changed = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        rig.Vm.SortByCommand.Execute(PerformanceSortColumn.QuarterMile);

        Assert.True(rig.Vm.IsSortedByQuarterMile);
        Assert.False(rig.Vm.IsSortedByRecent);
        Assert.Contains(nameof(PerformanceViewModel.IsSortedByQuarterMile), changed);
        Assert.Contains(nameof(PerformanceViewModel.IsSortedByRecent), changed);
    }

    [Fact]
    public void Rows_show_the_results_with_units_and_a_dash_for_what_a_car_has_not_reached()
    {
        var rig = NewRig();
        Seed(rig.History, _time);

        var row = rig.Vm.Rows.Single(r => r.CarLabel == CarLabel(200));

        Assert.Equal("A", row.ClassLabel); // CarClass 3
        Assert.Equal("700", row.PerformanceIndexText);
        Assert.Equal("RWD", row.DrivetrainLabel);
        Assert.Equal(4.0, Parse(row.ZeroTo100Text.Replace(" s", "")));
        Assert.Equal("--", row.ZeroTo200Text);
        Assert.Contains("12", row.QuarterMileText);
        Assert.DoesNotContain("km/h", row.QuarterMileText); // the time and the speed at the line are two lines, not one joined string
        Assert.Equal("180 km/h", row.QuarterMileTrapText); // the trap speed of THAT quarter mile
        Assert.Equal("260 km/h", row.TopSpeedText);
        Assert.False(string.IsNullOrWhiteSpace(row.Summary));
        Assert.DoesNotContain('·', row.Summary);
        Assert.Contains("180 km/h", row.Summary);
    }

    [Fact]
    public void The_table_updates_when_the_history_changes_and_reports_whether_there_is_any()
    {
        var rig = NewRig();
        Assert.False(rig.Vm.HasHistory);
        Assert.False(rig.Vm.ClearHistoryCommand.CanExecute(null));

        rig.History.Merge(new CarKey(1, 500), 1, 0, new PerformanceResult(6.0, null, null, null, null));

        Assert.True(rig.Vm.HasHistory);
        Assert.True(rig.Vm.ClearHistoryCommand.CanExecute(null));
    }

    [Fact]
    public void The_history_saved_by_an_earlier_session_is_shown_after_loading()
    {
        var first = NewRig();
        first.Stand();
        first.Launch(10.0);

        var second = new Rig(_directory, _time);
        second.History.Load();
        var reloaded = new PerformanceViewModel(second.Acceleration, second.TopSpeed, second.CarChanges, second.History);

        Assert.Single(reloaded.Rows);
        Assert.Equal(CarLabel(1234), reloaded.Rows[0].CarLabel);
    }

    // ---- Clear history ------------------------------------------------------------------------------------

    [Fact]
    public async Task Clear_history_asks_first_and_deletes_everything_when_confirmed()
    {
        var rig = NewRig(confirm: true);
        Seed(rig.History, _time);
        rig.History.Flush();

        await rig.Vm.ClearHistoryCommand.ExecuteAsync(null);

        var asked = Assert.Single(rig.Confirmation.Asked);
        Assert.Equal(Strings.PerfClearConfirmTitle, asked.Title);
        Assert.Equal(Strings.PerfClearConfirmMessage, asked.Message);
        Assert.Equal(Strings.PerfClearConfirmButton, asked.Confirm);
        Assert.Equal(Strings.CancelButton, asked.Cancel);
        Assert.Empty(rig.History.Records);
        Assert.False(rig.Vm.HasHistory);
        var reloaded = new PerformanceHistoryStore(rig.HistoryPath, _time);
        reloaded.Load();
        Assert.Empty(reloaded.Records);
    }

    [Fact]
    public async Task Clear_history_keeps_everything_when_the_user_cancels()
    {
        var rig = NewRig(confirm: false);
        Seed(rig.History, _time);

        await rig.Vm.ClearHistoryCommand.ExecuteAsync(null);

        Assert.Single(rig.Confirmation.Asked);
        Assert.Equal(3, rig.History.Records.Count);
        Assert.Equal(3, rig.Vm.Rows.Count);
    }

    // ---- Shell wiring -------------------------------------------------------------------------------------

    [Fact]
    public void The_shell_shows_the_performance_screen_when_given_one_and_refreshes_it_every_tick()
    {
        var rig = NewRig();
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake, observers: [rig.CarChanges, rig.Acceleration, rig.TopSpeed]);
        var shell = new ShellViewModel(service, TimeProvider.System, performancePage: rig.Vm);

        shell.NavigateCommand.Execute(PageId.Performance);

        Assert.Same(rig.Vm, shell.CurrentPage);
        Assert.Same(rig.Vm, shell.Performance);
    }

    [Fact]
    public async Task Packets_flow_from_the_service_through_the_engines_to_the_performance_screen()
    {
        var rig = NewRig();
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake, observers: [rig.CarChanges, rig.Acceleration, rig.TopSpeed]);
        var shell = new ShellViewModel(service, TimeProvider.System, performancePage: rig.Vm);
        await shell.StartCommand.ExecuteAsync(null);

        // Stationary packets arm the timer: the observers (not the 20 Hz sample) are what the engine listens to.
        for (uint ms = 1000; ms < 1200; ms += 20)
        {
            fake.Publish(TestPackets.Sample(speedMetersPerSecond: 0, carOrdinal: 1234, performanceIndex: 780) with { TimestampMS = ms });
        }

        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 10));
        shell.Refresh();

        Assert.Equal(PerformanceRunState.Armed, rig.Vm.State);
        Assert.True(rig.Vm.HasCar);

        await shell.StopCommand.ExecuteAsync(null);
        Assert.Equal(PerformanceRunState.WaitingForData, rig.Vm.State); // stopping ends the session
    }
}
