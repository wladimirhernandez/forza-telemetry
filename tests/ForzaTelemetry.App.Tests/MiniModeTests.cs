using System.ComponentModel;
using System.IO;
using System.Windows;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core.Laps;

namespace ForzaTelemetry.App.Tests;

/// <summary>The compact always-on-top window: where it goes (pure geometry), its state and persistence (view model), and how it is wired to the shell.</summary>
public sealed class MiniModeTests : IDisposable
{
    private sealed class MemoryStore : ISettingsStore
    {
        public List<AppSettings> Saved { get; } = [];

        public AppSettings Load() => AppSettings.Default;

        public bool TrySave(AppSettings settings)
        {
            Saved.Add(settings);
            return true;
        }
    }

    // Two 1920x1080 screens side by side; the second one is the primary (its taskbar is the one the user works with).
    private static readonly Rect Left = new(-1920, 0, 1920, 1040);
    private static readonly Rect Main = new(0, 0, 1920, 1040);
    private static readonly Size DefaultSize = new(360, 140);
    private static readonly Size Min = new(260, 110);
    private static readonly Size Max = new(640, 260);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static Rect Place(Rect? saved, params Rect[] areas) =>
        MiniModeGeometry.Place(saved, areas, primaryIndex: Array.IndexOf(areas, Main), DefaultSize, Min, Max);

    private static MiniModeViewModel NewViewModel(out SettingsService settings, out MemoryStore store, AppSettings? initial = null)
    {
        store = new MemoryStore();
        settings = new SettingsService(store, initial ?? AppSettings.Default);
        return new MiniModeViewModel(new DashboardViewModel(), settings: settings);
    }

    // ---- Geometry: where the window goes --------------------------------------------------------

    [Fact]
    public void With_nothing_remembered_the_window_is_centered_on_the_primary_screen_at_the_default_size()
    {
        var placed = Place(null, Left, Main);

        Assert.Equal(DefaultSize, placed.Size);
        Assert.Equal((Main.Width - 360) / 2, placed.Left);
        Assert.Equal((Main.Height - 140) / 2, placed.Top);
    }

    [Fact]
    public void A_remembered_position_that_is_on_a_screen_is_kept()
    {
        var placed = Place(new Rect(-1500, 200, 400, 150), Left, Main);

        Assert.Equal(new Rect(-1500, 200, 400, 150), placed); // on the left screen, untouched
    }

    [Fact]
    public void A_position_on_a_screen_that_is_gone_falls_back_to_the_middle_of_the_primary_screen_keeping_the_size()
    {
        // The laptop was undocked: the left screen no longer exists.
        var placed = Place(new Rect(-1500, 200, 400, 150), Main);

        Assert.Equal(new Size(400, 150), placed.Size);
        Assert.Equal(Main.Left + (Main.Width - 400) / 2, placed.Left);
        Assert.Equal(Main.Top + (Main.Height - 150) / 2, placed.Top);
    }

    [Fact]
    public void A_window_left_half_off_the_edge_is_pulled_fully_onto_its_screen()
    {
        // 60% visible: honored, but the part that hung off the right edge comes back.
        var placed = Place(new Rect(Main.Right - 240, 300, 400, 150), Main);

        Assert.Equal(Main.Right - 400, placed.Left);
        Assert.Equal(300, placed.Top);
    }

    [Fact]
    public void A_window_that_is_mostly_off_screen_counts_as_lost_and_is_recentered()
    {
        var placed = Place(new Rect(Main.Right - 100, 300, 400, 150), Main); // only 25% visible

        Assert.Equal(Main.Left + (Main.Width - 400) / 2, placed.Left);
    }

    [Fact]
    public void The_remembered_size_is_clamped_to_the_limits()
    {
        Assert.Equal(new Size(640, 260), Place(new Rect(100, 100, 5000, 5000), Main).Size);
        Assert.Equal(new Size(260, 110), Place(new Rect(100, 100, 10, 10), Main).Size);
    }

    [Fact]
    public void A_window_larger_than_a_tiny_screen_is_shrunk_to_fit_it()
    {
        var tiny = new Rect(0, 0, 300, 100);

        var placed = MiniModeGeometry.Place(null, [tiny], 0, DefaultSize, Min, Max);

        Assert.True(tiny.Contains(placed));
    }

    [Theory]
    [InlineData(double.NaN, 0, 400, 150)]
    [InlineData(0, double.PositiveInfinity, 400, 150)]
    [InlineData(0, 0, 0, 150)]
    [InlineData(0, 0, 400, 0)]
    public void A_damaged_remembered_rectangle_is_ignored(double left, double top, double width, double height)
    {
        var placed = Place(new Rect(left, top, width, height), Main);

        Assert.Equal(DefaultSize, placed.Size);
        Assert.Equal((Main.Width - 360) / 2, placed.Left);
    }

    [Fact]
    public void Without_any_monitor_information_the_size_is_still_valid()
    {
        var placed = MiniModeGeometry.Place(null, [], 0, DefaultSize, Min, Max);

        Assert.Equal(DefaultSize, placed.Size);
    }

    [Fact]
    public void A_bad_primary_index_falls_back_to_the_first_screen()
    {
        var placed = MiniModeGeometry.Place(null, [Main], 7, DefaultSize, Min, Max);

        Assert.Equal((Main.Width - 360) / 2, placed.Left);
    }

    [Fact]
    public void The_setup_key_is_the_same_for_the_same_desk_in_any_order_and_differs_when_a_screen_changes()
    {
        var both = MiniModeGeometry.SetupKey([Left, Main]);

        Assert.Equal(both, MiniModeGeometry.SetupKey([Main, Left]));
        Assert.NotEqual(both, MiniModeGeometry.SetupKey([Main]));
        Assert.NotEqual(both, MiniModeGeometry.SetupKey([Left, new Rect(0, 0, 2560, 1400)]));
    }

    [Theory]
    [InlineData(0.2, 0.6)]
    [InlineData(0.6, 0.6)]
    [InlineData(0.85, 0.85)]
    [InlineData(1, 1)]
    [InlineData(7, 1)]
    [InlineData(double.NaN, 1)]
    public void Opacity_is_clamped_to_60_to_100_percent_and_a_damaged_value_is_opaque(double input, double expected) =>
        Assert.Equal(expected, MiniModeGeometry.ClampOpacity(input));

    // ---- View model: state and persistence ------------------------------------------------------

    [Fact]
    public void It_starts_closed_pinned_and_opaque_with_the_main_window_hidden_while_open()
    {
        var vm = NewViewModel(out _, out _);

        Assert.False(vm.IsOpen);
        Assert.True(vm.IsTopmost);
        Assert.Equal(1.0, vm.Opacity);
        Assert.True(vm.HideMainWindow);
    }

    [Fact]
    public void Toggle_and_Close_change_only_the_open_state()
    {
        var vm = NewViewModel(out _, out var store);

        vm.ToggleCommand.Execute(null);
        Assert.True(vm.IsOpen);

        vm.ToggleCommand.Execute(null);
        Assert.False(vm.IsOpen);

        vm.ToggleCommand.Execute(null);
        vm.CloseCommand.Execute(null);
        vm.CloseCommand.Execute(null); // closing a closed window is not an error
        Assert.False(vm.IsOpen);
        Assert.Empty(store.Saved); // opening and closing is state, not a setting
    }

    [Fact]
    public void Opening_raises_one_IsOpen_notification_for_the_controller()
    {
        var vm = NewViewModel(out _, out _);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        vm.ToggleCommand.Execute(null);

        Assert.Equal([nameof(MiniModeViewModel.IsOpen)], changes);
    }

    [Fact]
    public void The_values_come_from_the_saved_settings_with_opacity_kept_inside_its_range()
    {
        var vm = NewViewModel(
            out _, out _, new AppSettings { MiniAlwaysOnTop = false, MiniOpacity = 0.75, HideMainWindowInMiniMode = false });

        Assert.False(vm.IsTopmost);
        Assert.Equal(0.75, vm.Opacity);
        Assert.False(vm.HideMainWindow);
    }

    [Fact]
    public void Pinning_and_unpinning_is_saved_at_once()
    {
        var vm = NewViewModel(out var settings, out var store);

        vm.ToggleTopmostCommand.Execute(null);

        Assert.False(vm.IsTopmost);
        Assert.False(settings.Current.MiniAlwaysOnTop);
        Assert.False(store.Saved.Single().MiniAlwaysOnTop);
    }

    [Fact]
    public void Dragging_the_opacity_slider_saves_nothing_until_the_window_closes()
    {
        var vm = NewViewModel(out var settings, out var store);

        for (var step = 0; step < 40; step++)
        {
            vm.Opacity = 1.0 - step * 0.01; // a drag fires on every pixel
        }

        Assert.Empty(store.Saved);
        Assert.Equal(1.0, settings.Current.MiniOpacity);

        vm.SaveWindowState("setup", new Rect(10, 20, 360, 140));

        Assert.Single(store.Saved); // one write for the whole drag
        Assert.Equal(vm.Opacity, settings.Current.MiniOpacity);
    }

    [Fact]
    public void Opacity_is_clamped_when_set_and_announces_its_derived_texts()
    {
        var vm = NewViewModel(out _, out _);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Opacity = 0.1;

        Assert.Equal(0.6, vm.Opacity);
        Assert.Equal(60, vm.OpacityPercent);
        Assert.Contains(nameof(MiniModeViewModel.OpacityPercent), changed);
        Assert.Contains(nameof(MiniModeViewModel.OpacityText), changed);

        changed.Clear();
        vm.Opacity = 0.05; // clamps to the same value: nothing to announce
        Assert.Empty(changed);
    }

    [Fact]
    public void The_position_is_remembered_per_monitor_setup_and_the_other_setup_is_untouched()
    {
        var vm = NewViewModel(out _, out _);

        vm.SaveWindowState("docked", new Rect(-1500, 200, 400, 150));
        vm.SaveWindowState("laptop", new Rect(40, 60, 360, 140));
        vm.SaveWindowState("docked", new Rect(-1400, 210, 420, 160)); // moved again on the docked desk

        Assert.Equal(new Rect(-1400, 210, 420, 160), vm.SavedPlacement("docked"));
        Assert.Equal(new Rect(40, 60, 360, 140), vm.SavedPlacement("laptop"));
        Assert.Null(vm.SavedPlacement("a desk never seen"));
    }

    [Fact]
    public void Only_the_most_recent_monitor_setups_are_remembered()
    {
        var vm = NewViewModel(out var settings, out _);

        for (var i = 0; i < AppSettings.MaxMiniPlacements + 3; i++)
        {
            vm.SaveWindowState($"setup-{i}", new Rect(i, i, 360, 140));
        }

        Assert.Equal(AppSettings.MaxMiniPlacements, settings.Current.MiniPlacements!.Count);
        Assert.Null(vm.SavedPlacement("setup-0")); // the oldest were forgotten
        Assert.NotNull(vm.SavedPlacement($"setup-{AppSettings.MaxMiniPlacements + 2}"));
    }

    [Fact]
    public void A_settings_change_from_elsewhere_moves_the_pin_and_announces_the_hide_option()
    {
        var vm = NewViewModel(out var settings, out _);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.Update(s => s with { MiniAlwaysOnTop = false, HideMainWindowInMiniMode = false });

        Assert.False(vm.IsTopmost);
        Assert.False(vm.HideMainWindow);
        Assert.Contains(nameof(MiniModeViewModel.HideMainWindow), changed);
    }

    [Fact]
    public void Without_settings_it_still_works_on_the_defaults()
    {
        var vm = new MiniModeViewModel(new DashboardViewModel());

        vm.ToggleTopmostCommand.Execute(null);
        vm.SaveWindowState("setup", new Rect(0, 0, 360, 140)); // nothing to save to, and no error

        Assert.False(vm.IsTopmost);
        Assert.Null(vm.SavedPlacement("setup"));
    }

    // ---- Wiring to the shell --------------------------------------------------------------------

    [Fact]
    public void The_shell_hands_the_mini_window_the_same_view_models_the_main_window_binds()
    {
        var laps = new LapsViewModel(new LapTracker());
        var shell = new ShellViewModel(
            new TelemetryService(() => new FakeTelemetrySource()), TimeProvider.System, lapsPage: laps);

        Assert.Same(shell.Dashboard, shell.Mini.Dashboard);
        Assert.Same(laps, shell.Mini.Laps);
    }

    [Fact]
    public void A_refresh_while_mini_mode_is_closed_does_no_work_for_it()
    {
        var shell = new ShellViewModel(new TelemetryService(() => new FakeTelemetrySource()), TimeProvider.System);
        var notifications = 0;
        ((INotifyPropertyChanged)shell.Mini).PropertyChanged += (_, _) => notifications++;

        for (var tick = 0; tick < 100; tick++)
        {
            shell.Refresh();
        }

        Assert.Equal(0, notifications); // the mini view model reacts to nothing per tick: it only exposes the live view models
    }

    // ---- Settings file --------------------------------------------------------------------------

    [Fact]
    public void The_mini_settings_survive_a_save_and_load()
    {
        var store = new JsonSettingsStore(Path.Combine(_directory, "settings.json"));
        var placements = new List<MiniWindowPlacement>
        {
            new("-1920,0,1920x1040;0,0,1920x1040", -1500, 200, 400, 150),
            new("0,0,1920x1040", 40, 60, 360, 140)
        };

        Assert.True(store.TrySave(new AppSettings
        {
            HideMainWindowInMiniMode = false, MiniAlwaysOnTop = false, MiniOpacity = 0.7, MiniPlacements = placements
        }));
        var loaded = new JsonSettingsStore(Path.Combine(_directory, "settings.json")).Load();

        Assert.False(loaded.HideMainWindowInMiniMode);
        Assert.False(loaded.MiniAlwaysOnTop);
        Assert.Equal(0.7, loaded.MiniOpacity);
        Assert.Equal(placements, loaded.MiniPlacements);
    }

    [Fact]
    public void A_file_written_before_mini_mode_existed_gets_the_defaults_for_it()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "udpPort": 5400, "speedUnit": "mph" }""");

        var loaded = new JsonSettingsStore(path).Load();

        Assert.Equal(5400, loaded.UdpPort);
        Assert.True(loaded.HideMainWindowInMiniMode);
        Assert.True(loaded.MiniAlwaysOnTop);
        Assert.Equal(1.0, loaded.MiniOpacity);
        Assert.Null(loaded.MiniPlacements);
    }

    [Fact]
    public void A_bad_mini_value_costs_only_that_value()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(
            path,
            """{ "miniOpacity": 0.2, "miniAlwaysOnTop": "yes", "hideMainWindowInMiniMode": false, "miniPlacements": "nowhere", "udpPort": 5400 }""");
        var logger = new ListLogger<JsonSettingsStore>();

        var loaded = new JsonSettingsStore(path, logger).Load();

        Assert.Equal(1.0, loaded.MiniOpacity); // below 60%: unusable, back to the default
        Assert.True(loaded.MiniAlwaysOnTop);
        Assert.False(loaded.HideMainWindowInMiniMode); // the good one survives
        Assert.Null(loaded.MiniPlacements);
        Assert.Equal(5400, loaded.UdpPort);
        Assert.Contains(logger.Entries, e => e.Message.Contains("miniOpacity"));
    }

    [Fact]
    public void A_damaged_placement_entry_is_dropped_and_the_good_ones_are_kept()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(
            path,
            """
            { "miniPlacements": [
                { "setup": "a", "left": 10, "top": 20, "width": 360, "height": 140 },
                { "setup": "b", "left": "far left", "top": 20, "width": 360, "height": 140 },
                { "setup": "c", "left": 10, "top": 20, "width": 0, "height": 140 },
                { "left": 10, "top": 20, "width": 360, "height": 140 },
                42,
                { "setup": "d", "left": -50, "top": 5, "width": 400, "height": 150 }
            ] }
            """);

        var loaded = new JsonSettingsStore(path).Load();

        Assert.Equal(["a", "d"], loaded.MiniPlacements!.Select(p => p.Setup));
    }

    [Fact]
    public void Sanitizing_drops_unusable_placements_keeps_the_last_per_setup_and_caps_the_list()
    {
        var many = Enumerable.Range(0, AppSettings.MaxMiniPlacements + 4)
            .Select(i => new MiniWindowPlacement($"s{i}", i, i, 360, 140))
            .Append(new MiniWindowPlacement("s11", 5, 5, 400, 150)) // a later entry for an existing setup replaces it
            .Append(new MiniWindowPlacement("bad", double.NaN, 0, 360, 140))
            .ToList();

        var clean = (AppSettings.Default with { MiniPlacements = many }).Sanitized();

        Assert.Equal(AppSettings.MaxMiniPlacements, clean.MiniPlacements!.Count);
        Assert.DoesNotContain(clean.MiniPlacements, p => p.Setup == "bad");
        Assert.Single(clean.MiniPlacements, p => p.Setup == "s11");
        Assert.Equal(400, clean.MiniPlacements.Single(p => p.Setup == "s11").Width);
    }

    [Fact]
    public void Sanitizing_clean_settings_returns_equal_settings_so_a_no_op_update_does_not_save()
    {
        var settings = AppSettings.Default with { MiniPlacements = [new MiniWindowPlacement("s", 1, 2, 360, 140)] };

        Assert.Equal(settings, settings.Sanitized());
    }

    [Fact]
    public void Sanitizing_puts_a_wild_opacity_back_to_the_default()
    {
        Assert.Equal(1.0, (AppSettings.Default with { MiniOpacity = 5 }).Sanitized().MiniOpacity);
        Assert.Equal(1.0, (AppSettings.Default with { MiniOpacity = double.NaN }).Sanitized().MiniOpacity);
        Assert.Equal(0.8, (AppSettings.Default with { MiniOpacity = 0.8 }).Sanitized().MiniOpacity);
    }

    // ---- Settings screen ------------------------------------------------------------------------

    [Fact]
    public void The_settings_screen_edits_the_hide_main_window_option()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store, AppSettings.Default);
        var vm = new SettingsViewModel(settings, new DataOutViewModel(AppSettings.Default.UdpPort), @"C:\logs");

        Assert.True(vm.HideMainWindowInMiniMode);

        vm.HideMainWindowInMiniMode = false;

        Assert.False(settings.Current.HideMainWindowInMiniMode);
        Assert.False(store.Saved.Single().HideMainWindowInMiniMode);
    }
}
