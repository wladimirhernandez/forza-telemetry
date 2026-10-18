using System.Globalization;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;

namespace ForzaTelemetry.App.Tests;

public class SettingsViewModelTests
{
    private sealed class MemoryStore : ISettingsStore
    {
        public AppSettings? LastSaved { get; private set; }

        public AppSettings Load() => AppSettings.Default;

        public bool TrySave(AppSettings settings)
        {
            LastSaved = settings;
            return true;
        }
    }

    private sealed class FakeFolderOpener : IFolderOpener
    {
        public List<string> Opened { get; } = [];

        public bool TryOpen(string path)
        {
            Opened.Add(path);
            return true;
        }
    }

    private sealed class FakeRestarter : IAppRestarter
    {
        public int Restarts { get; private set; }

        public void Restart() => Restarts++;
    }

    private sealed class Fixture
    {
        public MemoryStore Store { get; } = new();

        public SettingsService Settings { get; }

        public DataOutViewModel DataOut { get; } = new(AppSettings.Default.UdpPort);

        public FakeFolderOpener Folder { get; } = new();

        public FakeRestarter Restarter { get; } = new();

        public SettingsViewModel Vm { get; }

        public Fixture(AppSettings? initial = null, bool canRestart = true)
        {
            Settings = new SettingsService(Store, initial ?? AppSettings.Default);
            // An English PC, whatever machine runs the tests: "System" must not depend on the host language.
            Vm = new SettingsViewModel(
                Settings, DataOut, @"C:\logs", Folder, canRestart ? Restarter : null, CultureInfo.GetCultureInfo("en-US"));
        }
    }

    // ---- Port ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("5300", true, 5300)]
    [InlineData("1", true, 1)]
    [InlineData("65535", true, 65535)]
    [InlineData("0", false, 0)]
    [InlineData("65536", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(" 5300", false, 0)]
    [InlineData("-5", false, 0)]
    [InlineData("53.5", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData("99999999999", false, 0)]
    public void TryParsePort_accepts_only_whole_numbers_from_1_to_65535(string text, bool valid, int expected)
    {
        Assert.Equal(valid, SettingsViewModel.TryParsePort(text, out var port));
        Assert.Equal(expected, port);
    }

    [Fact]
    public void The_box_starts_with_the_stored_port_and_no_error()
    {
        var f = new Fixture(AppSettings.Default with { UdpPort = 6000 });

        Assert.Equal("6000", f.Vm.PortText);
        Assert.Null(f.Vm.PortError);
        Assert.False(f.Vm.HasPortError);
    }

    [Fact]
    public void An_invalid_port_shows_the_localized_message_and_blocks_Apply()
    {
        var f = new Fixture();
        var changed = new List<string?>();
        f.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        f.Vm.PortText = "70000";

        Assert.Equal(Strings.PortInvalid, f.Vm.PortError);
        Assert.True(f.Vm.HasPortError);
        Assert.False(f.Vm.ApplyPortCommand.CanExecute(null));
        Assert.Contains(nameof(SettingsViewModel.PortError), changed);
        Assert.Contains(nameof(SettingsViewModel.HasPortError), changed);

        f.Vm.PortText = "5301";
        Assert.Null(f.Vm.PortError);
        Assert.True(f.Vm.ApplyPortCommand.CanExecute(null));
    }

    [Fact]
    public void Apply_is_disabled_for_the_port_already_in_use()
    {
        var f = new Fixture();

        Assert.False(f.Vm.ApplyPortCommand.CanExecute(null));
        f.Vm.PortText = "5301";
        Assert.True(f.Vm.ApplyPortCommand.CanExecute(null));
        f.Vm.PortText = "5300";
        Assert.False(f.Vm.ApplyPortCommand.CanExecute(null));
    }

    [Fact]
    public void Apply_saves_the_new_port_and_disables_itself_again()
    {
        var f = new Fixture();
        f.Vm.PortText = "5301";

        f.Vm.ApplyPortCommand.Execute(null);

        Assert.Equal(5301, f.Settings.Current.UdpPort);
        Assert.Equal(5301, f.Store.LastSaved!.UdpPort);
        Assert.False(f.Vm.ApplyPortCommand.CanExecute(null)); // nothing left to apply
    }

    [Fact]
    public void Typing_does_not_apply_anything_until_Apply_is_pressed()
    {
        var f = new Fixture();

        f.Vm.PortText = "5";
        f.Vm.PortText = "53";
        f.Vm.PortText = "5301";

        Assert.Equal(5300, f.Settings.Current.UdpPort);
        Assert.Null(f.Store.LastSaved);
    }

    [Fact]
    public void Apply_with_an_invalid_port_changes_nothing()
    {
        var f = new Fixture();
        f.Vm.PortText = "0";

        f.Vm.ApplyPortCommand.Execute(null); // from code (Enter key) CanExecute is still consulted by RelayCommand

        Assert.Equal(5300, f.Settings.Current.UdpPort);
    }

    [Fact]
    public void The_box_follows_a_port_changed_elsewhere()
    {
        var f = new Fixture();

        f.Settings.Update(s => s with { UdpPort = 7000 });

        Assert.Equal("7000", f.Vm.PortText);
    }

    // ---- Units, startup ----------------------------------------------------------------------------

    [Fact]
    public void Choosing_units_updates_and_saves_the_settings()
    {
        var f = new Fixture();

        f.Vm.SpeedUnit = SpeedUnit.Mph;
        f.Vm.TemperatureUnit = TemperatureUnit.Fahrenheit;

        Assert.Equal(SpeedUnit.Mph, f.Settings.Current.SpeedUnit);
        Assert.Equal(TemperatureUnit.Fahrenheit, f.Settings.Current.TemperatureUnit);
        Assert.Equal(f.Settings.Current, f.Store.LastSaved);
    }

    [Fact]
    public void The_unit_choices_list_both_options_with_localized_labels()
    {
        var f = new Fixture();

        Assert.Equal([SpeedUnit.Kmh, SpeedUnit.Mph], f.Vm.SpeedUnitOptions.Select(o => o.Value));
        Assert.Equal(["km/h", "mph"], f.Vm.SpeedUnitOptions.Select(o => o.Label));
        Assert.Equal([TemperatureUnit.Celsius, TemperatureUnit.Fahrenheit], f.Vm.TemperatureUnitOptions.Select(o => o.Value));
        Assert.Equal(["°C", "°F"], f.Vm.TemperatureUnitOptions.Select(o => o.Label));
    }

    [Fact]
    public void The_toggles_start_from_the_stored_values()
    {
        var f = new Fixture(AppSettings.Default with
        {
            SpeedUnit = SpeedUnit.Mph, TemperatureUnit = TemperatureUnit.Fahrenheit, StartListeningOnLaunch = true
        });

        Assert.Equal(SpeedUnit.Mph, f.Vm.SpeedUnit);
        Assert.Equal(TemperatureUnit.Fahrenheit, f.Vm.TemperatureUnit);
        Assert.True(f.Vm.StartListeningOnLaunch);
    }

    [Fact]
    public void Start_listening_on_launch_is_saved_when_toggled()
    {
        var f = new Fixture();

        f.Vm.StartListeningOnLaunch = true;

        Assert.True(f.Settings.Current.StartListeningOnLaunch);
        Assert.True(f.Store.LastSaved!.StartListeningOnLaunch);
    }

    // ---- Language -----------------------------------------------------------------------------

    [Fact]
    public void Changing_the_language_is_saved_and_offers_a_restart()
    {
        var f = new Fixture();
        Assert.False(f.Vm.LanguageChangePending);
        Assert.False(f.Vm.CanRestart);

        f.Vm.Language = AppLanguage.Spanish;

        Assert.Equal(AppLanguage.Spanish, f.Settings.Current.Language);
        Assert.True(f.Vm.LanguageChangePending);
        Assert.True(f.Vm.CanRestart);

        f.Vm.RestartCommand.Execute(null);
        Assert.Equal(1, f.Restarter.Restarts);
    }

    [Fact]
    public void Going_back_to_the_language_on_screen_cancels_the_pending_note()
    {
        var f = new Fixture(AppSettings.Default with { Language = AppLanguage.English });

        f.Vm.Language = AppLanguage.Spanish;
        Assert.True(f.Vm.LanguageChangePending);

        f.Vm.Language = AppLanguage.English;
        Assert.False(f.Vm.LanguageChangePending);
    }

    [Fact]
    public void Without_a_restarter_the_note_shows_but_the_restart_button_does_not()
    {
        var f = new Fixture(canRestart: false);

        f.Vm.Language = AppLanguage.Spanish;

        Assert.True(f.Vm.LanguageChangePending);
        Assert.False(f.Vm.CanRestart);
    }

    [Fact]
    public void While_settings_cannot_be_saved_the_page_warns_with_the_path_and_offers_no_restart_for_a_language()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store, AppSettings.Default, persistenceSuspended: true);
        var vm = new SettingsViewModel(
            settings,
            new DataOutViewModel(5300),
            @"C:\logs",
            restarter: new FakeRestarter(),
            systemCulture: CultureInfo.GetCultureInfo("en-US"),
            settingsPath: @"C:\data\settings.json");

        vm.Language = AppLanguage.Spanish;

        Assert.True(vm.IsPersistenceSuspended);
        Assert.Contains(@"C:\data\settings.json", vm.PersistenceWarning);
        Assert.False(vm.LanguageChangePending); // the choice is lost at the restart, so "applies next time" would be false
        Assert.False(vm.CanRestart);
        Assert.Null(store.LastSaved); // and nothing was written
    }

    [Fact]
    public void With_settings_saved_normally_there_is_no_warning()
    {
        var f = new Fixture();

        Assert.False(f.Vm.IsPersistenceSuspended);
        Assert.Equal("", f.Vm.PersistenceWarning);
    }

    [Fact]
    public void Choosing_the_language_the_PC_already_uses_needs_no_restart()
    {
        var englishPc = CultureInfo.GetCultureInfo("en-US");
        var settings = new SettingsService(new MemoryStore(), AppSettings.Default); // language: System
        var vm = new SettingsViewModel(
            settings, new DataOutViewModel(5300), @"C:\logs", restarter: new FakeRestarter(), systemCulture: englishPc);

        vm.Language = AppLanguage.English; // same text as System on an English PC

        Assert.False(vm.LanguageChangePending);
        Assert.False(vm.CanRestart);

        vm.Language = AppLanguage.Spanish;

        Assert.True(vm.LanguageChangePending);
        Assert.True(vm.CanRestart);
    }

    [Fact]
    public void On_a_Spanish_PC_switching_from_System_to_Spanish_needs_no_restart_and_to_English_does()
    {
        var settings = new SettingsService(new MemoryStore(), AppSettings.Default);
        var vm = new SettingsViewModel(
            settings, new DataOutViewModel(5300), @"C:\logs", systemCulture: CultureInfo.GetCultureInfo("es-ES"));

        vm.Language = AppLanguage.Spanish;
        Assert.False(vm.LanguageChangePending);

        vm.Language = AppLanguage.English;
        Assert.True(vm.LanguageChangePending);
    }

    [Fact]
    public void The_language_choices_use_each_languages_own_name()
    {
        var f = new Fixture();

        Assert.Equal([AppLanguage.System, AppLanguage.English, AppLanguage.Spanish], f.Vm.LanguageOptions.Select(o => o.Value));
        Assert.Equal("English", f.Vm.LanguageOptions[1].Label);
        Assert.Equal("Español", f.Vm.LanguageOptions[2].Label);
    }

    // ---- Diagnostics, guide, about -----------------------------------------------------------------

    [Fact]
    public void Open_logs_folder_opens_the_log_directory()
    {
        var f = new Fixture();

        f.Vm.OpenLogsFolderCommand.Execute(null);

        Assert.Equal([@"C:\logs"], f.Folder.Opened);
    }

    [Fact]
    public void The_guide_quotes_the_live_address_and_port_and_follows_changes()
    {
        var f = new Fixture();
        var changed = new List<string?>();
        f.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        f.DataOut.Port = 5555;

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.GuideStep3, f.DataOut.Address, 5555), f.Vm.GuideEndpointStep);
        Assert.Contains("5555", f.Vm.FirewallNote);
        Assert.Contains(nameof(SettingsViewModel.GuideEndpointStep), changed);
        Assert.Contains(nameof(SettingsViewModel.FirewallNote), changed);
    }

    [Fact]
    public void About_shows_the_name_version_and_license()
    {
        var f = new Fixture();

        Assert.Equal(AppInfo.Name, f.Vm.AppName);
        Assert.Contains(AppInfo.Version, f.Vm.VersionText);
        Assert.Contains("PolyForm Strict 1.0.0", f.Vm.LicenseText);
    }

    // ---- Shell integration ----------------------------------------------------------------------------

    [Fact]
    public void The_shell_shows_the_settings_screen_when_given_one()
    {
        var f = new Fixture();
        var shell = new ShellViewModel(
            new TelemetryService(() => new FakeTelemetrySource()),
            TimeProvider.System,
            dataOut: f.DataOut,
            settings: f.Settings,
            settingsPage: f.Vm);

        shell.NavigateCommand.Execute(PageId.Settings);

        Assert.Same(f.Vm, shell.CurrentPage);
        Assert.Same(f.DataOut, shell.DataOut);
    }

    [Fact]
    public async Task Changing_the_port_restarts_a_running_listener_on_the_new_port()
    {
        var f = new Fixture();
        var portsUsed = new List<int>();
        var firstSource = new FakeTelemetrySource();
        var service = new TelemetryService(() =>
        {
            portsUsed.Add(f.Settings.Current.UdpPort); // what the app's real factory does
            return portsUsed.Count == 1 ? firstSource : new FakeTelemetrySource();
        });
        var shell = new ShellViewModel(service, TimeProvider.System, dataOut: f.DataOut, settings: f.Settings, settingsPage: f.Vm);
        await shell.StartCommand.ExecuteAsync(null);

        f.Vm.PortText = "5400";
        f.Vm.ApplyPortCommand.Execute(null);

        Assert.True(await Wait.UntilAsync(() => portsUsed.Count == 2 && service.IsRunning && !shell.IsBusy));
        Assert.Equal([5300, 5400], portsUsed);
        Assert.True(firstSource.IsDisposed); // the old listener was released
        Assert.Equal(5400, shell.DataOut.Port);
        shell.Refresh();
        Assert.Contains("5400", shell.Status); // "Listening on UDP 5400, ..."

        await shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Changing_the_port_while_stopped_only_updates_the_pill_and_does_not_start_anything()
    {
        var f = new Fixture();
        var starts = 0;
        var service = new TelemetryService(() =>
        {
            starts++;
            return new FakeTelemetrySource();
        });
        var shell = new ShellViewModel(service, TimeProvider.System, dataOut: f.DataOut, settings: f.Settings, settingsPage: f.Vm);

        f.Settings.Update(s => s with { UdpPort = 5400 });
        await Task.Delay(50);

        Assert.Equal(0, starts);
        Assert.False(service.IsRunning);
        Assert.Equal(5400, shell.DataOut.Port);

        await shell.StartCommand.ExecuteAsync(null); // the next Start uses the new port
        Assert.Equal(1, starts);
        await shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task A_port_change_during_a_running_command_waits_for_it_and_still_applies()
    {
        var f = new Fixture();
        var portsUsed = new List<int>();
        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowSource = new FakeTelemetrySource { DisposeGate = disposeGate };
        var service = new TelemetryService(() =>
        {
            portsUsed.Add(f.Settings.Current.UdpPort);
            return portsUsed.Count == 1 ? slowSource : new FakeTelemetrySource();
        });
        var shell = new ShellViewModel(service, TimeProvider.System, dataOut: f.DataOut, settings: f.Settings, settingsPage: f.Vm);
        await shell.StartCommand.ExecuteAsync(null);

        var stopping = shell.StopCommand.ExecuteAsync(null); // blocks inside the source's dispose
        Assert.True(shell.IsBusy);
        f.Settings.Update(s => s with { UdpPort = 5400 }); // arrives mid-command: must not be lost nor run concurrently
        disposeGate.SetResult();
        await stopping;

        // The Stop wins (the user asked for it), so the listener ends up stopped, and the new port is remembered.
        Assert.True(await Wait.UntilAsync(() => !shell.IsBusy));
        Assert.False(service.IsRunning);
        Assert.Equal(5400, shell.DataOut.Port);
        await shell.StartCommand.ExecuteAsync(null);
        Assert.Equal([5300, 5400], portsUsed);
        await shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public void The_dashboard_follows_the_units_chosen_in_settings_end_to_end()
    {
        var f = new Fixture();
        var units = new UnitsService(f.Settings);
        var shell = new ShellViewModel(
            new TelemetryService(() => new FakeTelemetrySource()),
            TimeProvider.System,
            dashboard: new DashboardViewModel(units),
            dataOut: f.DataOut,
            settings: f.Settings,
            settingsPage: f.Vm);
        shell.Dashboard.Apply(TestPackets.Sample(speedMetersPerSecond: 50f)); // 180 km/h
        Assert.Equal("180", shell.Dashboard.SpeedReadout);

        f.Vm.SpeedUnit = SpeedUnit.Mph;

        Assert.Equal("112", shell.Dashboard.SpeedReadout);
        Assert.Equal("mph", shell.Dashboard.SpeedUnitLabel);
    }
}
