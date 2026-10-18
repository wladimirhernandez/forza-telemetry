using System.Globalization;
using System.IO;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Insights;
using ForzaTelemetry.Core.Performance;
using Microsoft.Extensions.Time.Testing;

namespace ForzaTelemetry.App.Tests;

/// <summary>Car nicknames end to end: the rename box, the display-name rule, and every screen that shows a car.</summary>
public sealed class CarNamesWiringTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private CarNameStore NewStore() => new(Path.Combine(_directory, "cars.json"));

    private static string Unnamed(int ordinal) => string.Format(CultureInfo.CurrentCulture, Strings.PerfCarOrdinalFormat, ordinal);

    // ---- Display name ---------------------------------------------------------------------------------------

    [Fact]
    public void An_unnamed_car_shows_the_localized_number_and_a_named_one_its_nickname()
    {
        var store = NewStore();

        Assert.Equal(Unnamed(2211), CarDisplayName.For(2211, store));
        Assert.Equal(Unnamed(2211), CarDisplayName.For(2211, null)); // no store at all (tests, designer)

        store.TrySet(2211, "Daily GT");

        Assert.Equal("Daily GT", CarDisplayName.For(2211, store));
        Assert.Equal(Unnamed(77), CarDisplayName.For(77, store));
    }

    // ---- The rename box ---------------------------------------------------------------------------------------

    [Fact]
    public void Begin_opens_the_box_for_the_car_with_its_current_name_ready_to_edit()
    {
        var store = NewStore();
        store.TrySet(2211, "Daily GT");
        var box = new CarRenameViewModel(store);

        box.Begin(2211);

        Assert.True(box.IsOpen);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, CarNameStrings.CarRenameTitle, 2211), box.Title);
        Assert.Equal("Daily GT", box.Text);
        Assert.True(box.HasNickname);
        Assert.True(box.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void An_unnamed_car_opens_with_an_empty_box_that_cannot_be_saved_yet_but_shows_no_error()
    {
        var box = new CarRenameViewModel(NewStore());

        box.Begin(2211);

        Assert.Equal("", box.Text);
        Assert.False(box.HasNickname);
        Assert.False(box.HasError); // the player has typed nothing yet: no scolding
        Assert.False(box.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Typing_validates_as_you_go()
    {
        var box = new CarRenameViewModel(NewStore());
        box.Begin(2211);

        box.Text = "Daily GT";
        Assert.False(box.HasError);
        Assert.True(box.SaveCommand.CanExecute(null));

        box.Text = "   "; // typed and then emptied: now it is worth saying
        Assert.Equal(CarNameStrings.CarNameEmpty, box.Error);
        Assert.False(box.SaveCommand.CanExecute(null));

        box.Text = new string('x', 41);
        Assert.Equal(CarNameStore.Describe(CarNameProblem.TooLong), box.Error);
        Assert.False(box.SaveCommand.CanExecute(null));

        box.Text = new string('x', 40);
        Assert.Null(box.Error);
        Assert.True(box.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Save_stores_the_trimmed_name_and_closes()
    {
        var store = NewStore();
        var box = new CarRenameViewModel(store);
        box.Begin(2211);
        box.Text = "  Daily GT  ";

        box.SaveCommand.Execute(null);

        Assert.Equal("Daily GT", store.Find(2211));
        Assert.False(box.IsOpen);
    }

    [Fact]
    public void Save_with_an_invalid_name_does_nothing_and_stays_open()
    {
        var store = NewStore();
        var box = new CarRenameViewModel(store);
        box.Begin(2211);
        box.Text = "   ";

        box.SaveCommand.Execute(null);

        Assert.True(box.IsOpen);
        Assert.Null(store.Find(2211));
    }

    [Fact]
    public void Cancel_closes_without_changing_anything()
    {
        var store = NewStore();
        store.TrySet(2211, "Daily GT");
        var box = new CarRenameViewModel(store);
        box.Begin(2211);
        box.Text = "Something else";

        box.CancelCommand.Execute(null);

        Assert.False(box.IsOpen);
        Assert.Equal("Daily GT", store.Find(2211));
    }

    [Fact]
    public void Use_default_forgets_the_nickname()
    {
        var store = NewStore();
        store.TrySet(2211, "Daily GT");
        var box = new CarRenameViewModel(store);
        box.Begin(2211);

        box.UseDefaultCommand.Execute(null);

        Assert.Null(store.Find(2211));
        Assert.False(box.IsOpen);
    }

    [Fact]
    public void There_is_no_box_for_no_car()
    {
        var box = new CarRenameViewModel(NewStore());

        box.Begin(0);

        Assert.False(box.IsOpen);
    }

    [Fact]
    public void Reopening_for_another_car_starts_clean()
    {
        var store = NewStore();
        store.TrySet(2211, "Daily GT");
        var box = new CarRenameViewModel(store);
        box.Begin(2211);
        box.Text = "";

        box.Begin(77);

        Assert.Equal("", box.Text);
        Assert.False(box.HasNickname);
        Assert.False(box.HasError);
        Assert.Equal(77, box.CarOrdinal);
    }

    // ---- Dashboard pill -----------------------------------------------------------------------------------------

    [Fact]
    public void The_car_pill_shows_the_name_follows_a_rename_and_offers_the_rename_box()
    {
        var store = NewStore();
        var box = new CarRenameViewModel(store);
        var dashboard = new DashboardViewModel(carNames: store) { CarRename = box };
        Assert.False(dashboard.RenameCarCommand.CanExecute(null)); // no car yet

        dashboard.Apply(TestPackets.Sample(carOrdinal: 2211, performanceIndex: 780));

        Assert.True(dashboard.HasCarInfo);
        Assert.Equal(Unnamed(2211), dashboard.CarName);
        Assert.Equal(2211, dashboard.CarOrdinal);
        Assert.True(dashboard.RenameCarCommand.CanExecute(null));

        dashboard.RenameCarCommand.Execute(null);
        Assert.True(box.IsOpen);
        Assert.Equal(2211, box.CarOrdinal);

        box.Text = "Daily GT";
        box.SaveCommand.Execute(null);
        Assert.Equal("Daily GT", dashboard.CarName); // no new packet needed
    }

    [Fact]
    public void The_pill_has_no_name_and_no_rename_while_idle()
    {
        var store = NewStore();
        var dashboard = new DashboardViewModel(carNames: store);
        dashboard.Apply(TestPackets.Sample(carOrdinal: 2211, performanceIndex: 780));

        dashboard.ResetLive();

        Assert.Equal("", dashboard.CarName);
        Assert.Equal(0, dashboard.CarOrdinal);
        Assert.False(dashboard.RenameCarCommand.CanExecute(null));
    }

    [Fact]
    public void A_menu_packet_without_a_real_car_cannot_be_named()
    {
        var dashboard = new DashboardViewModel(carNames: NewStore());

        dashboard.Apply(TestPackets.Sample(carOrdinal: 2211, performanceIndex: 0));

        Assert.False(dashboard.HasCarInfo);
        Assert.Equal(0, dashboard.CarOrdinal);
    }

    // ---- Performance ---------------------------------------------------------------------------------------------

    private sealed class PerformanceRig
    {
        public PerformanceRig(CarNameStore names, string directory)
        {
            History = new PerformanceHistoryStore(Path.Combine(directory, "performance.json"));
            CarChanges = new CarChangeDetector(TopSpeed, Acceleration);
            Box = new CarRenameViewModel(names);
            Vm = new PerformanceViewModel(Acceleration, TopSpeed, CarChanges, History, null, null, names, Box);
        }

        public AccelerationTimer Acceleration { get; } = new();

        public TopSpeedTracker TopSpeed { get; } = new();

        public CarChangeDetector CarChanges { get; }

        public PerformanceHistoryStore History { get; }

        public CarRenameViewModel Box { get; }

        public PerformanceViewModel Vm { get; }

        public void Drive(int car, int pi = 780)
        {
            var packet = TestPackets.Sample(carOrdinal: car, performanceIndex: pi, carClass: 3, drivetrain: 2, speedMetersPerSecond: 60f);
            CarChanges.OnPacket(packet);
            TopSpeed.OnPacket(packet);
            Vm.Refresh(packet);
        }
    }

    [Fact]
    public void Performance_names_the_current_car_and_its_table_row_and_follows_a_rename()
    {
        var store = NewStore();
        var rig = new PerformanceRig(store, _directory);
        rig.Drive(2211);
        rig.History.Merge(new CarKey(2211, 780), 3, 2, new PerformanceResult(5.1, null, null, null, 250));
        rig.History.Merge(new CarKey(77, 600), 2, 1, new PerformanceResult(7.0, null, null, null, 180));
        Assert.Equal(Unnamed(2211), rig.Vm.CarText);

        store.TrySet(2211, "Daily GT");

        Assert.Equal("Daily GT", rig.Vm.CarText);
        Assert.StartsWith("Daily GT,", rig.Vm.CarSummary);
        var named = Assert.Single(rig.Vm.Rows, row => row.CarOrdinal == 2211);
        Assert.Equal("Daily GT", named.CarLabel);
        Assert.Contains("Daily GT", named.RenameName);
        Assert.Equal(Unnamed(77), Assert.Single(rig.Vm.Rows, row => row.CarOrdinal == 77).CarLabel); // others untouched
    }

    [Fact]
    public void The_rename_buttons_open_the_box_for_the_right_car()
    {
        var store = NewStore();
        var rig = new PerformanceRig(store, _directory);
        rig.Drive(2211);

        rig.Vm.RenameCurrentCarCommand.Execute(null);
        Assert.True(rig.Box.IsOpen);
        Assert.Equal(2211, rig.Box.CarOrdinal);

        rig.Box.CancelCommand.Execute(null);
        rig.Vm.RenameCarCommand.Execute(77); // a row's button passes its own car
        Assert.Equal(77, rig.Box.CarOrdinal);
    }

    [Fact]
    public void Without_a_current_car_the_current_car_button_does_nothing()
    {
        var rig = new PerformanceRig(NewStore(), _directory);

        rig.Vm.RenameCurrentCarCommand.Execute(null);

        Assert.False(rig.Box.IsOpen);
    }

    // ---- Sessions ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_recordings_list_shows_the_nickname_and_updates_when_a_car_is_renamed()
    {
        var store = NewStore();
        using var rig = new SessionsRig(carNames: store);
        await rig.AddRecordingAsync("monza", car: 2211);
        var row = Assert.Single(await rig.LoadLibraryAsync());
        Assert.Equal(Unnamed(2211), row.CarsText);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        store.TrySet(2211, "Daily GT");

        Assert.Equal("Daily GT", row.CarsText);
        Assert.Contains("Daily GT", row.Summary);
        Assert.Contains(nameof(RecordingRowViewModel.CarsText), changed);
        Assert.Contains(nameof(RecordingRowViewModel.Summary), changed);
    }

    [Fact]
    public void Several_cars_show_the_first_ones_name_and_how_many_more()
    {
        var store = NewStore();
        store.TrySet(5, "Daily GT");

        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, SessionsStrings.SessionsCarsMoreFormat, "Daily GT", 2),
            SessionFormat.Cars([5, 77, 9], store));
    }

    // ---- Tuning --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_tuning_header_shows_the_nickname_and_follows_a_rename()
    {
        var store = NewStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        var dyno = new DynoCapture();
        var vm = new TuningViewModel(dyno, new TelemetryHistory(), new SuspensionBottomingCounter(), new UnitsService(), time, store);
        dyno.OnPacket(InsightPackets.Pull(4000, powerWatts: 160_000f, torque: 300, carOrdinal: 2211, performanceIndex: 612));
        vm.Refresh();
        Assert.Equal(Unnamed(2211), vm.CarText);

        store.TrySet(2211, "Daily GT");
        time.Advance(TimeSpan.FromSeconds(5)); // past the dyno poll interval
        vm.Refresh();

        Assert.Equal("Daily GT", vm.CarText);
        Assert.DoesNotContain("612", vm.CarText); // the PI stays its own pill
    }

    // ---- Shell -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_shell_hands_the_rename_box_to_the_dashboard_and_exposes_it_to_the_window()
    {
        var store = NewStore();
        var box = new CarRenameViewModel(store);
        var shell = new ShellViewModel(
            new TelemetryService(() => new FakeTelemetrySource()),
            TimeProvider.System,
            dashboard: new DashboardViewModel(carNames: store),
            carRename: box);

        Assert.Same(box, shell.CarRename);
        Assert.Same(box, shell.Dashboard.CarRename);
    }
}
