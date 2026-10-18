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
            CarChanges = new CarChangeDetector(TopSpeed, Acceleration, Braking);
            Vm = new PerformanceViewModel(Acceleration, TopSpeed, CarChanges, History, Units, Confirmation, braking: Braking);
        }

        public AccelerationTimer Acceleration { get; } = new();

        public BrakingTest Braking { get; } = new();

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
            Braking.Reset();
            Vm.BeginSession();
        }

        public int Car { get; set; } = 1234;

        public int Pi { get; set; } = 780;

        public int CarClass { get; set; } = 3;

        private TelemetryPacket Packet(double speed, double distance, byte gear, bool raceOn, byte throttle, byte brake) =>
            TestPackets.Sample(
                speedMetersPerSecond: (float)speed,
                gear: gear,
                carOrdinal: Car,
                carClass: CarClass,
                performanceIndex: Pi,
                drivetrain: 2,
                isRaceOn: raceOn,
                accel: throttle,
                brake: brake) with
            {
                TimestampMS = _ms,
                DistanceTraveled = (float)distance
            };

        /// <summary>One packet through the engines (as the receive loop does) and one UI refresh from it (as the tick does).</summary>
        public TelemetryPacket Feed(
            double speed,
            double distance = 0,
            byte gear = 1,
            bool raceOn = true,
            uint stepMs = 20,
            byte throttle = 0,
            byte brake = 0,
            bool refresh = true)
        {
            var packet = Packet(speed, distance, gear, raceOn, throttle, brake);
            CarChanges.OnPacket(packet); // first, like the app: it must see a new car before the engines do
            Acceleration.OnPacket(packet);
            Braking.OnPacket(packet);
            TopSpeed.OnPacket(packet);
            if (refresh)
            {
                Vm.Refresh(packet); // false: the engines move on between two UI ticks (a whole stop can fit in one)
            }

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

        /// <summary>
        /// A constant-acceleration launch from the line for <paramref name="seconds"/>, with the throttle floored: the engine ends a run
        /// when the driver lifts, so a launch without throttle is not a launch.
        /// </summary>
        public void Launch(double seconds, double accel = Accel)
        {
            for (var t = 0.0; t <= seconds + 1e-9; t += 0.02)
            {
                Feed(accel * t, 0.5 * accel * t * t, gear: 1, throttle: 255);
            }
        }

        /// <summary>
        /// Cruises, then brakes hard to a standstill at a constant <paramref name="decel"/>, brake floored, throttle off. With the defaults
        /// (40 m/s, 10 m/s^2) the closed forms are exact: 100 km/h to the 1 km/h standstill is 38.58 m in 2.75 s, and 60 mph is 35.97 m in
        /// 2.65 s. The distance grows with the speed, so the engines get a real stopping distance.
        /// </summary>
        public void BrakeHard(double fromSpeed = 40, double decel = 10, bool refresh = true)
        {
            double distance = 0;
            for (var i = 0; i < 5; i++)
            {
                Feed(fromSpeed, distance, gear: 5, refresh: refresh);
                distance += fromSpeed * 0.02;
            }

            for (var t = 0.02; fromSpeed - (decel * t) > 0; t += 0.02)
            {
                Feed(fromSpeed - (decel * t), distance + (fromSpeed * t) - (decel * t * t / 2), gear: 5, brake: 255, refresh: refresh);
            }

            Feed(0, distance + (fromSpeed * fromSpeed / (2 * decel)), gear: 5, brake: 255, refresh: refresh);
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
        Assert.Equal(AccelerationSnapshot.Empty, rig.Acceleration.Current with { CompletedRuns = 0 }); // the counter never goes down
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

    [Fact]
    public void Reset_run_asks_the_receive_loop_to_reset_the_engines_and_shows_them_empty_meanwhile()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(10.0);
        Assert.True(rig.TopSpeed.TopSpeedMs > 0);

        rig.Vm.ResetRunCommand.Execute(null);

        // The UI thread did not write the engines (the loop owns them): the reset is pending and the page already says empty,
        // and keeps saying so on later ticks even though the engines still hold the old numbers.
        Assert.True(rig.CarChanges.HasPendingEngineReset);
        Assert.True(rig.TopSpeed.TopSpeedMs > 0);
        rig.Vm.Refresh(null);
        Assert.Equal("--", rig.Vm.TopSpeedText);
        Assert.Equal("--", rig.Vm.ZeroTo100Text);

        rig.Feed(0); // the loop's next packet carries the reset out before the engines see it

        Assert.False(rig.CarChanges.HasPendingEngineReset);
        Assert.Equal(0, rig.TopSpeed.TopSpeedMs);
        Assert.Equal("--", rig.Vm.TopSpeedText);
    }

    [Fact]
    public void Reset_run_after_the_car_changed_saves_the_old_cars_top_speed_under_the_old_car_and_never_the_new_cars()
    {
        var rig = NewRig();
        rig.Feed(70, gear: 5); // 252 km/h in car 1234
        Assert.Empty(rig.History.Records);

        // The receive loop sees another car (and resets the engines) between two UI ticks; the UI has not looked yet.
        rig.Car = 77;
        rig.Pi = 450;
        rig.CarChanges.OnPacket(TestPackets.Sample(speedMetersPerSecond: 3, carOrdinal: 77, performanceIndex: 450, carClass: 1, drivetrain: 0));

        rig.Vm.ResetRunCommand.Execute(null);

        Assert.Equal(252, rig.History.Find(new CarKey(1234, 780))!.TopSpeedKmh!.Value, precision: 1);
        Assert.Null(rig.History.Find(new CarKey(77, 450))); // the new car has no top speed yet, and none is invented for it
    }

    // ---- Replays are not saved -------------------------------------------------------------------------------

    [Fact]
    public void A_replay_session_measures_and_shows_but_saves_nothing()
    {
        var rig = NewRig();
        rig.Vm.BeginSession(isReplay: true);
        rig.Stand();
        rig.Launch(10.0);
        rig.BrakeToStop(100, 800);

        rig.Vm.EndSession();
        rig.Vm.ResetRunCommand.Execute(null);

        Assert.True(rig.Vm.IsReplaySession);
        Assert.Empty(rig.History.Records); // not in memory...
        Assert.False(File.Exists(rig.HistoryPath)); // ...and not on disk
    }

    [Fact]
    public void A_replay_never_writes_the_history_file_even_when_something_is_unsaved_and_the_top_speed_settles()
    {
        var rig = NewRig();
        // Something unsaved in memory (a live Merge whose Flush has not happened): any Flush during the replay would write it.
        rig.History.Merge(new CarKey(9, 9), 2, 1, new PerformanceResult(null, null, null, null, 150));
        rig.Vm.BeginSession(isReplay: true);

        rig.Feed(70, gear: 5);
        rig.Feed(70, gear: 5);
        rig.Feed(40, gear: 5); // well below the peak: the "settled" path that saves a live session's top speed
        rig.Vm.Refresh(null);

        Assert.False(File.Exists(rig.HistoryPath));
        Assert.Null(rig.History.Find(new CarKey(1234, 780)));
    }

    [Fact]
    public void A_replay_still_shows_what_it_measured_while_it_plays()
    {
        var rig = NewRig();
        rig.Vm.BeginSession(isReplay: true);
        rig.Stand();

        rig.Launch(10.0);

        Assert.NotEqual("--", rig.Vm.TopSpeedText);
        Assert.NotEqual("--", rig.Vm.ZeroTo100Text);
    }

    [Fact]
    public void A_car_change_inside_a_replay_does_not_save_the_previous_cars_top_speed()
    {
        var rig = NewRig();
        rig.Vm.BeginSession(isReplay: true);
        rig.Feed(70, gear: 5);
        rig.Car = 77;
        rig.Pi = 450;

        rig.Feed(5);

        Assert.Empty(rig.History.Records);
    }

    [Fact]
    public void The_next_live_session_saves_again_after_a_replay()
    {
        var rig = NewRig();
        rig.Vm.BeginSession(isReplay: true);
        rig.Feed(70, gear: 5);
        rig.Vm.EndSession();
        Assert.Empty(rig.History.Records);

        rig.StartNewSession(); // BeginSession() defaults to a live session
        rig.Feed(60, gear: 5);
        rig.Vm.EndSession();

        Assert.False(rig.Vm.IsReplaySession);
        Assert.Equal(216, rig.History.Find(new CarKey(1234, 780))!.TopSpeedKmh!.Value, precision: 1);
    }

    // ---- Units ------------------------------------------------------------------------------------------------

    [Fact]
    public void Speeds_follow_the_unit_and_the_times_never_convert()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(10.0);
        Assert.Equal("km/h", rig.Vm.SpeedUnitLabel);
        var topKmh = Parse(rig.Vm.TopSpeedText);

        rig.Units.SpeedUnit = SpeedUnit.Mph;

        Assert.Equal("mph", rig.Vm.SpeedUnitLabel);
        Assert.Equal(topKmh / 1.609344, Parse(rig.Vm.TopSpeedText), precision: 0);
        Assert.Contains("mph", rig.Vm.QuarterMileTrapText);
        Assert.Contains("mph", Assert.Single(rig.Vm.Rows).TopSpeedText);
        Assert.Equal("2.75", rig.Vm.ZeroTo100Text.Replace(',', '.')); // times never convert
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
        Assert.Equal(4.0, Parse(row.WindowAText.Replace(" s", "")));
        Assert.Equal("--", row.WindowBText);
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

    // ---- The extra acceleration windows ---------------------------------------------------------------------

    [Fact]
    public void One_launch_measures_every_window_and_saves_them_all()
    {
        var rig = NewRig();
        rig.Stand();

        rig.Launch(10.0);

        // Closed forms for a = 10 m/s^2 (the timer starts at the 1 km/h standstill line, which is why 0.28 m/s is taken off).
        Assert.Equal(2.78, Parse(rig.Vm.HundredTo200Text), precision: 2); // (55.56 - 27.78) / 10
        Assert.Equal(2.65, Parse(rig.Vm.ZeroTo60MphText), precision: 2); // (26.82 - 0.28) / 10
        Assert.Equal(3.13, Parse(rig.Vm.SixtyTo130MphText), precision: 2); // (58.12 - 26.82) / 10
        var record = Assert.Single(rig.History.Records);
        Assert.Equal(2.78, record.HundredTo200Seconds!.Value, precision: 2);
        Assert.Equal(2.65, record.ZeroTo60MphSeconds!.Value, precision: 2);
        Assert.Equal(3.13, record.SixtyTo130MphSeconds!.Value, precision: 2);
    }

    [Fact]
    public void The_acceleration_slots_show_the_metric_windows_and_switch_to_mph_with_the_unit()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(10.0);

        Assert.Equal(Strings.PerfTitle100, rig.Vm.WindowATitle);
        Assert.Equal(rig.Vm.ZeroTo100Text, rig.Vm.WindowAText);
        Assert.Equal(Strings.PerfTitle200, rig.Vm.WindowBTitle);
        Assert.Equal(rig.Vm.ZeroTo200Text, rig.Vm.WindowBText);
        Assert.Equal(Strings.PerfTitle100To200, rig.Vm.ExtraWindowTitle);
        Assert.Equal(rig.Vm.HundredTo200Text, rig.Vm.ExtraWindowText);
        Assert.Equal(Strings.PerfColZeroTo100, rig.Vm.WindowAColumnTitle);
        Assert.Equal($"{Strings.PerfTitle100}: {rig.Vm.ZeroTo100Text}", rig.Vm.WindowAName);

        var changed = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        rig.Units.SpeedUnit = SpeedUnit.Mph;

        Assert.Equal(Strings.PerfTitle60Mph, rig.Vm.WindowATitle);
        Assert.Equal(rig.Vm.ZeroTo60MphText, rig.Vm.WindowAText);
        Assert.Equal(Strings.PerfTitle60To130Mph, rig.Vm.WindowBTitle);
        Assert.Equal(rig.Vm.SixtyTo130MphText, rig.Vm.WindowBText);
        Assert.Equal(Strings.PerfTitle100, rig.Vm.ExtraWindowTitle); // the metric reference stays on screen as the extra tile
        Assert.Equal(rig.Vm.ZeroTo100Text, rig.Vm.ExtraWindowText);
        Assert.Equal(Strings.PerfColZeroTo60Mph, rig.Vm.WindowAColumnTitle);
        Assert.Equal(Strings.PerfColSixtyTo130Mph, rig.Vm.WindowBColumnTitle);
        Assert.Contains(nameof(PerformanceViewModel.WindowAText), changed);
        Assert.Contains(nameof(PerformanceViewModel.WindowBTitle), changed);
        Assert.Contains(nameof(PerformanceViewModel.ExtraWindowName), changed);
        Assert.Contains(nameof(PerformanceViewModel.BrakingTitle), changed);
        Assert.Contains(nameof(PerformanceViewModel.DistanceUnitLabel), changed);
    }

    [Fact]
    public void A_temperature_change_does_not_touch_the_window_slots()
    {
        var rig = NewRig();
        var changed = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        rig.Units.TemperatureUnit = TemperatureUnit.Fahrenheit;

        Assert.DoesNotContain(nameof(PerformanceViewModel.WindowAText), changed);
        Assert.DoesNotContain(nameof(PerformanceViewModel.BrakingTitle), changed);
    }

    [Fact]
    public void A_quicker_run_flags_the_new_windows_as_new_bests_in_the_slots_of_the_current_unit()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(10.0);
        rig.BrakeToStop(100, 800);

        rig.Launch(10.0, accel: 11);

        Assert.True(rig.Vm.HundredTo200IsNewBest);
        Assert.True(rig.Vm.ZeroTo60MphIsNewBest);
        Assert.True(rig.Vm.SixtyTo130MphIsNewBest);
        Assert.True(rig.Vm.WindowAIsNewBest); // km/h: follows 0-100
        Assert.True(rig.Vm.ExtraWindowIsNewBest); // follows 100-200
        rig.Units.SpeedUnit = SpeedUnit.Mph;
        Assert.True(rig.Vm.WindowAIsNewBest); // mph: follows 0-60
        Assert.True(rig.Vm.WindowBIsNewBest); // and 60-130
        Assert.True(rig.Vm.ExtraWindowIsNewBest); // 0-100 improved too
    }

    [Fact]
    public void A_kept_partial_run_saves_only_what_it_measured_and_never_wipes_older_bests()
    {
        var rig = NewRig();
        rig.Stand();
        rig.Launch(10.0);
        rig.BrakeToStop(100, 800);
        var key = new CarKey(1234, 780);
        var before = rig.History.Find(key)!;
        Assert.NotNull(before.ZeroTo100Seconds);

        // A quicker launch that ends at 99 km/h: past 60 mph, short of 100 km/h, then the driver lifts. A mph player's 0-60 is real.
        rig.Launch(2.5, accel: 11);
        rig.Feed(15, 35, throttle: 255);

        var after = rig.History.Find(key)!;
        Assert.True(after.ZeroTo60MphSeconds < before.ZeroTo60MphSeconds);
        Assert.Equal(before.ZeroTo100Seconds, after.ZeroTo100Seconds); // not measured by this run: untouched
        Assert.Equal(before.QuarterMileSeconds, after.QuarterMileSeconds);
        Assert.True(rig.Vm.ZeroTo60MphIsNewBest);
        Assert.False(rig.Vm.ZeroTo100IsNewBest);
    }

    // ---- Braking ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_stop_that_finishes_between_two_ticks_is_saved_exactly_when_the_counter_moves_even_with_equal_values()
    {
        var rig = NewRig();
        rig.BrakeHard(decel: 10);
        var key = new CarKey(1234, 780);
        Assert.Equal(38.58, rig.History.Find(key)!.BrakeHundredMeters!.Value, precision: 2);

        // The whole second stop happens with no UI tick in between (Braking, Done and Armed all pass unseen), and it is better.
        rig.BrakeHard(decel: 11, refresh: false);
        rig.Feed(0, 0, gear: 5);

        Assert.Equal(35.07, rig.History.Find(key)!.BrakeHundredMeters!.Value, precision: 2);
        Assert.True(rig.Vm.BrakingIsNewBest);
    }

    [Fact]
    public void Before_any_data_the_braking_tile_is_empty_and_says_waiting()
    {
        var rig = NewRig();

        Assert.Equal("--", rig.Vm.BrakingDistanceText);
        Assert.Equal("--", rig.Vm.BrakingTimeText);
        Assert.Equal(PerformanceRunState.WaitingForData, rig.Vm.BrakingRunState);
        Assert.Equal(Strings.PerfStateWaiting, rig.Vm.BrakingStateText);
        Assert.Equal(Strings.PerfTitleBrake100, rig.Vm.BrakingTitle);
        Assert.Equal("m", rig.Vm.DistanceUnitLabel);
    }

    [Fact]
    public void The_braking_state_line_walks_idle_armed_braking_done_and_quotes_the_speeds_in_the_unit()
    {
        var rig = NewRig();

        rig.Feed(20, gear: 4); // 72 km/h: below the arming speed
        Assert.Equal(PerformanceRunState.Idle, rig.Vm.BrakingRunState);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.PerfBrakeStateIdle, "105", "km/h"), rig.Vm.BrakingStateText);

        rig.Feed(40, 0.8, gear: 4);
        Assert.Equal(PerformanceRunState.Armed, rig.Vm.BrakingRunState);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.PerfBrakeStateArmed, "100", "km/h"), rig.Vm.BrakingStateText);

        rig.Units.SpeedUnit = SpeedUnit.Mph;
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.PerfBrakeStateArmed, "60", "mph"), rig.Vm.BrakingStateText); // the 60-0 mph test's own lines
        rig.Feed(20, 1.2, gear: 4, stepMs: 400); // falls through the line coasting: the 0.3 s brake ramp starts...
        rig.Feed(20, 1.3, gear: 4); // ...and runs out with no brake, which disarms
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.PerfBrakeStateIdle, "63", "mph"), rig.Vm.BrakingStateText);
        rig.Units.SpeedUnit = SpeedUnit.Kmh;

        var rig2 = NewRig();
        rig2.BrakeHard();
        Assert.Equal(PerformanceRunState.Done, rig2.Vm.BrakingRunState);
        Assert.Equal(Strings.PerfBrakeStateDone, rig2.Vm.BrakingStateText);
    }

    [Fact]
    public void While_a_stop_is_being_measured_the_tile_says_braking_and_counts_the_time()
    {
        var rig = NewRig();
        for (var i = 0; i < 5; i++)
        {
            rig.Feed(40, i * 0.8, gear: 5);
        }

        // Just past the 100 km/h line (27.78 m/s), brake floored: measuring.
        for (var t = 0.02; t < 1.5; t += 0.02)
        {
            rig.Feed(40 - (10 * t), 4 + (40 * t) - (5 * t * t), gear: 5, brake: 255);
        }

        Assert.Equal(PerformanceRunState.Running, rig.Vm.BrakingRunState);
        Assert.Equal(Strings.PerfBrakeStateBraking, rig.Vm.BrakingStateText);
        Assert.Equal("--", rig.Vm.BrakingDistanceText); // no result until the stop is complete
        Assert.NotEqual("--", rig.Vm.BrakingTimeText);
    }

    [Fact]
    public void A_finished_stop_shows_the_distance_and_time_saves_them_under_the_car_and_fills_the_table()
    {
        var rig = NewRig();

        rig.BrakeHard();

        Assert.Equal(38.58, Parse(rig.Vm.BrakingDistanceText), precision: 1);
        Assert.Equal(2.75, Parse(rig.Vm.BrakingTimeText), precision: 2);
        var record = Assert.Single(rig.History.Records);
        Assert.Equal(new CarKey(1234, 780), record.Car);
        Assert.Equal(38.58, record.BrakeHundredMeters!.Value, precision: 2);
        Assert.Equal(35.97, record.BrakeSixtyMphMeters!.Value, precision: 2);
        Assert.True(File.Exists(rig.HistoryPath));
        var row = Assert.Single(rig.Vm.Rows);
        Assert.Equal($"{rig.Vm.BrakingDistanceText} m", row.BrakingText);
        Assert.False(rig.Vm.BrakingIsNewBest); // the first stop of a car is a record, not an improvement
    }

    [Fact]
    public void With_mph_the_tile_and_the_table_show_the_60_to_0_distance_in_feet()
    {
        var rig = NewRig();
        rig.Units.SpeedUnit = SpeedUnit.Mph;

        rig.BrakeHard();

        Assert.Equal(Strings.PerfTitleBrake60Mph, rig.Vm.BrakingTitle);
        Assert.Equal("ft", rig.Vm.DistanceUnitLabel);
        Assert.Equal(35.97 * 3.280839895, Parse(rig.Vm.BrakingDistanceText), precision: 0); // whole feet
        Assert.Equal(2.65, Parse(rig.Vm.BrakingTimeText), precision: 2);
        Assert.Equal($"{rig.Vm.BrakingDistanceText} ft", Assert.Single(rig.Vm.Rows).BrakingText);
    }

    [Fact]
    public void A_shorter_stop_is_flagged_as_a_new_best_and_the_flag_clears_when_the_next_stop_begins()
    {
        var rig = NewRig();
        rig.BrakeHard(decel: 10);
        rig.BrakeHard(decel: 11);

        Assert.True(rig.Vm.BrakingIsNewBest);
        Assert.Equal(35.07, rig.History.Find(new CarKey(1234, 780))!.BrakeHundredMeters!.Value, precision: 2);

        rig.BrakeHard(decel: 9); // longer than the best: nothing new

        Assert.False(rig.Vm.BrakingIsNewBest);
        Assert.Equal(35.07, rig.History.Find(new CarKey(1234, 780))!.BrakeHundredMeters!.Value, precision: 2);
    }

    [Fact]
    public void A_stop_the_engine_dropped_saves_nothing_and_keeps_the_previous_result_on_screen()
    {
        var rig = NewRig();
        rig.BrakeHard();
        var shown = rig.Vm.BrakingDistanceText;
        var best = rig.History.Find(new CarKey(1234, 780))!.BrakeHundredMeters;

        // A second stop in which the driver gets back on the throttle halfway: the engine discards it.
        for (var i = 0; i < 5; i++)
        {
            rig.Feed(40, i * 0.8, gear: 5);
        }

        for (var t = 0.02; t < 1.0; t += 0.02)
        {
            rig.Feed(40 - (10 * t), 4 + (40 * t) - (5 * t * t), gear: 5, brake: 255);
        }

        rig.Feed(15, 30, gear: 5, throttle: 255);

        Assert.Equal(shown, rig.Vm.BrakingDistanceText);
        Assert.Equal(best, rig.History.Find(new CarKey(1234, 780))!.BrakeHundredMeters);
    }

    [Fact]
    public void A_replay_measures_and_shows_a_stop_but_saves_nothing()
    {
        var rig = NewRig();
        rig.Vm.BeginSession(isReplay: true);

        rig.BrakeHard();
        rig.Vm.EndSession();

        Assert.NotEqual("--", rig.Vm.BrakingDistanceText); // measured and shown...
        Assert.Empty(rig.History.Records); // ...never saved
        Assert.False(File.Exists(rig.HistoryPath));
    }

    [Fact]
    public void A_change_of_car_clears_the_braking_tile_and_credits_nothing_to_the_new_car()
    {
        var rig = NewRig();
        rig.BrakeHard();
        Assert.NotEqual("--", rig.Vm.BrakingDistanceText);

        rig.Car = 77;
        rig.Pi = 450;
        rig.Feed(5, gear: 2);

        Assert.Equal("--", rig.Vm.BrakingDistanceText);
        Assert.Equal(38.58, rig.History.Find(new CarKey(1234, 780))!.BrakeHundredMeters!.Value, precision: 2); // the first car keeps it
        Assert.Null(rig.History.Find(new CarKey(77, 450))?.BrakeHundredMeters);
    }

    [Fact]
    public void A_fresh_session_and_the_reset_button_clear_the_braking_tile()
    {
        var rig = NewRig();
        rig.BrakeHard();

        rig.Vm.ResetRunCommand.Execute(null);
        Assert.Equal("--", rig.Vm.BrakingDistanceText);

        rig.BrakeHard();
        Assert.NotEqual("--", rig.Vm.BrakingDistanceText);

        rig.StartNewSession();
        Assert.Equal("--", rig.Vm.BrakingDistanceText);
        Assert.Equal(PerformanceRunState.WaitingForData, rig.Vm.BrakingRunState);
    }

    [Fact]
    public void Without_a_braking_engine_the_tile_simply_stays_empty()
    {
        var acceleration = new AccelerationTimer();
        var topSpeed = new TopSpeedTracker();
        var vm = new PerformanceViewModel(
            acceleration, topSpeed, new CarChangeDetector(topSpeed, acceleration), new PerformanceHistoryStore(Path.Combine(_directory, "p.json"), _time));

        vm.Refresh(TestPackets.Sample(speedMetersPerSecond: 40, gear: 4, carOrdinal: 1, performanceIndex: 500));

        Assert.Equal("--", vm.BrakingDistanceText);
    }

    // ---- Table: braking column, details ----------------------------------------------------------------------

    private static void SeedBraking(PerformanceHistoryStore store)
    {
        store.Merge(new CarKey(300, 600), 2, 0, new PerformanceResult(5.0, 15.0, 14.0, 160, 200, BrakeHundredMeters: 44.4, BrakeSixtyMphMeters: 40.1));
        store.Merge(new CarKey(100, 800), 4, 2, new PerformanceResult(3.0, 8.0, 11.0, 210, 300, 5.5, 2.6, 7.4, 36.2, 33.0));
        store.Merge(new CarKey(200, 700), 3, 2, new PerformanceResult(4.0, null, 12.0, 180, 260));
    }

    [Fact]
    public void The_braking_column_sorts_shortest_first_with_cars_that_never_stopped_last()
    {
        var rig = NewRig();
        SeedBraking(rig.History);

        rig.Vm.SortByCommand.Execute(PerformanceSortColumn.Braking);

        Assert.True(rig.Vm.IsSortedByBraking);
        Assert.Equal([CarLabel(100), CarLabel(300), CarLabel(200)], rig.Vm.Rows.Select(r => r.CarLabel).ToArray());
        Assert.Equal($"{36.2.ToString("F1", CultureInfo.CurrentCulture)} m", rig.Vm.Rows[0].BrakingText);
        Assert.Equal("--", rig.Vm.Rows[2].BrakingText);
    }

    [Fact]
    public void The_acceleration_columns_and_their_sort_follow_the_unit()
    {
        var rig = NewRig();
        SeedBraking(rig.History);
        rig.Units.SpeedUnit = SpeedUnit.Mph;

        var row = rig.Vm.Rows.Single(r => r.CarLabel == CarLabel(100));
        Assert.Equal(2.6, Parse(row.WindowAText.Replace(" s", "")), precision: 2);
        Assert.Equal(7.4, Parse(row.WindowBText.Replace(" s", "")), precision: 2);
        Assert.Equal("108 ft", row.BrakingText); // 33.0 m, whole feet
        Assert.Equal("--", rig.Vm.Rows.Single(r => r.CarLabel == CarLabel(200)).WindowAText); // never measured: a dash

        rig.Vm.SortByCommand.Execute(PerformanceSortColumn.ZeroTo100);

        Assert.Equal(CarLabel(100), rig.Vm.Rows[0].CarLabel); // the only car with a 0-60 mph time leads
    }

    [Fact]
    public void A_rows_details_list_every_stored_best_in_both_unit_families_for_the_tooltip_and_screen_readers()
    {
        var rig = NewRig();
        SeedBraking(rig.History);

        var row = rig.Vm.Rows.Single(r => r.CarLabel == CarLabel(100));

        Assert.Contains(row.DetailLines, l => l.StartsWith($"{Strings.PerfTitle100}: ", StringComparison.Ordinal));
        Assert.Contains(row.DetailLines, l => l.StartsWith($"{Strings.PerfTitle100To200}: ", StringComparison.Ordinal));
        Assert.Contains(row.DetailLines, l => l.StartsWith($"{Strings.PerfTitle60Mph}: ", StringComparison.Ordinal));
        Assert.Contains(row.DetailLines, l => l.StartsWith($"{Strings.PerfTitle60To130Mph}: ", StringComparison.Ordinal));
        Assert.Contains(row.DetailLines, l => l.StartsWith($"{Strings.PerfTitleBrake100}: ", StringComparison.Ordinal));
        Assert.Contains(row.DetailLines, l => l.StartsWith($"{Strings.PerfTitleBrake60Mph}: ", StringComparison.Ordinal));
        Assert.Equal(row.DetailLines.Count, row.DetailsTooltip.Split(Environment.NewLine).Length);
        Assert.Contains(Strings.PerfTitle100To200, row.Summary);
        Assert.DoesNotContain('·', row.Summary);

        var oldCar = rig.Vm.Rows.Single(r => r.CarLabel == CarLabel(200));
        Assert.DoesNotContain(oldCar.DetailLines, l => l.StartsWith(Strings.PerfTitleBrake100, StringComparison.Ordinal)); // nothing invented for a car without it
    }
}
