using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using Microsoft.Extensions.Time.Testing;

namespace ForzaTelemetry.App.Tests;

/// <summary>The guide's view model: sentences and states from the live service, first-run opening, and what gets persisted.</summary>
public class ConnectionGuideViewModelTests
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

    private sealed class FakeSender : IDataOutSenderInfo
    {
        public string? LastSenderAddress { get; set; }
    }

    private sealed class FakeProfile(NetworkCategory category) : INetworkProfileInfo
    {
        public Task<NetworkCategory> GetCategoryAsync(IPAddress localAddress, CancellationToken cancellationToken = default) =>
            Task.FromResult(category);
    }

    private sealed class OneAddress : INetworkInfo
    {
        public IReadOnlyList<LanAddress> GetLanAddresses() => [new LanAddress("Wi-Fi", IPAddress.Parse("192.168.0.30"))];
    }

    private sealed class FakeHost : IConnectionGuideHost
    {
        public int Starts { get; private set; }

        public int DashboardShown { get; private set; }

        public Task StartListeningAsync()
        {
            Starts++;
            return Task.CompletedTask;
        }

        public void ShowDashboard() => DashboardShown++;
    }

    private sealed class Rig
    {
        public Rig(NetworkCategory network = NetworkCategory.Private, bool withSettings = true)
        {
            Source = new FakeTelemetrySource();
            Service = new TelemetryService(() => Source);
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
            Sender = new FakeSender();
            Store = new MemoryStore();
            Settings = withSettings ? new SettingsService(Store, AppSettings.Default) : null;
            DataOut = new DataOutViewModel(5300, new OneAddress(), networkProfile: new FakeProfile(network));
            Guide = new ConnectionGuideViewModel(Service, DataOut, Sender, Time, Settings);
            Guide.Attach(Host);
        }

        public FakeTelemetrySource Source { get; }

        public TelemetryService Service { get; }

        public FakeTimeProvider Time { get; }

        public FakeSender Sender { get; }

        public MemoryStore Store { get; }

        public SettingsService? Settings { get; }

        public DataOutViewModel DataOut { get; }

        public ConnectionGuideViewModel Guide { get; }

        public FakeHost Host { get; } = new();

        public async Task PublishAsync(bool isRaceOn)
        {
            var before = Service.ReceivedCount;
            Source.Publish(TestPackets.Sample(isRaceOn: isRaceOn));
            Assert.True(await Wait.UntilAsync(() => Service.ReceivedCount == before + 1));
        }
    }

    [Fact]
    public void It_starts_closed_and_Refresh_does_nothing_while_closed()
    {
        var rig = new Rig();

        rig.Guide.Refresh();

        Assert.False(rig.Guide.IsOpen);
        Assert.All(rig.Guide.Steps, step => Assert.Equal(CheckState.Pending, step.State));
    }

    [Fact]
    public void The_first_line_names_the_port_and_follows_a_port_change()
    {
        var rig = new Rig();
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, SetupStrings.SetupCheckListening, 5300), rig.Guide.ListeningStep.Title);

        rig.DataOut.Port = 5301;

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, SetupStrings.SetupCheckListening, 5301), rig.Guide.ListeningStep.Title);
    }

    [Fact]
    public void Opening_with_nothing_running_offers_Start_and_shows_the_waiting_lines()
    {
        var rig = new Rig();

        rig.Guide.Open();

        Assert.True(rig.Guide.IsOpen);
        Assert.True(rig.Guide.CanStartListening);
        Assert.Equal(CheckState.Pending, rig.Guide.ListeningStep.State);
        Assert.Equal(SetupStrings.SetupListeningPending, rig.Guide.ListeningStep.Detail);
        Assert.Equal(SetupStrings.SetupPacketsPending, rig.Guide.PacketsStep.Detail);
    }

    [Fact]
    public async Task Start_listening_goes_through_the_host()
    {
        var rig = new Rig();

        await rig.Guide.StartListeningCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Host.Starts);
    }

    [Fact]
    public async Task The_checklist_walks_listening_then_packets_with_the_sender_then_driving_then_connected()
    {
        var rig = new Rig();
        rig.Guide.Open();
        await rig.Service.StartAsync();

        rig.Guide.Refresh();
        Assert.Equal(CheckState.Ok, rig.Guide.ListeningStep.State);
        Assert.False(rig.Guide.CanStartListening);
        Assert.Equal(CheckState.Pending, rig.Guide.PacketsStep.State);

        // A menu packet from the console: the link works, nobody is driving.
        rig.Sender.LastSenderAddress = "192.168.0.77";
        await rig.PublishAsync(isRaceOn: false);
        rig.Guide.Refresh();
        Assert.Equal(CheckState.Ok, rig.Guide.PacketsStep.State);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, SetupStrings.SetupPacketsOk, "192.168.0.77"), rig.Guide.PacketsStep.Detail);
        Assert.Equal(CheckState.Pending, rig.Guide.DrivingStep.State);
        Assert.False(rig.Guide.IsConnected);

        await rig.PublishAsync(isRaceOn: true);
        rig.Guide.Refresh();
        Assert.Equal(CheckState.Ok, rig.Guide.DrivingStep.State);
        Assert.True(rig.Guide.IsConnected);

        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Having_driven_is_remembered_when_a_pause_menu_packet_follows()
    {
        var rig = new Rig();
        rig.Guide.Open();
        await rig.Service.StartAsync();
        rig.Sender.LastSenderAddress = "192.168.0.77";

        await rig.PublishAsync(isRaceOn: true);
        rig.Guide.Refresh();
        await rig.PublishAsync(isRaceOn: false); // pause
        rig.Guide.Refresh();

        Assert.True(rig.Guide.IsConnected);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Stopping_listening_forgets_what_was_seen()
    {
        var rig = new Rig();
        rig.Guide.Open();
        await rig.Service.StartAsync();
        rig.Sender.LastSenderAddress = "192.168.0.77";
        await rig.PublishAsync(isRaceOn: true);
        rig.Guide.Refresh();
        Assert.True(rig.Guide.IsConnected);

        await rig.Service.StopAsync();
        rig.Sender.LastSenderAddress = null;
        rig.Guide.Refresh();

        Assert.False(rig.Guide.IsConnected);
        Assert.Equal(CheckState.Pending, rig.Guide.DrivingStep.State);
        Assert.True(rig.Guide.CanStartListening);
    }

    [Fact]
    public async Task Silence_for_twenty_seconds_shows_the_warning_and_the_troubleshooting_list()
    {
        var rig = new Rig();
        rig.Guide.Open();
        await rig.Service.StartAsync();
        rig.Guide.Refresh(); // anchors "listening since"

        rig.Time.Advance(TimeSpan.FromSeconds(19));
        rig.Guide.Refresh();
        Assert.False(rig.Guide.ShowTroubleshooting);

        rig.Time.Advance(TimeSpan.FromSeconds(2));
        rig.Guide.Refresh();

        Assert.True(rig.Guide.ShowTroubleshooting);
        Assert.Equal(CheckState.Warning, rig.Guide.PacketsStep.State);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, SetupStrings.SetupPacketsWarning, 21), rig.Guide.PacketsStep.Detail);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task A_public_network_shows_the_fix_steps_until_data_arrives()
    {
        var rig = new Rig(NetworkCategory.Public);
        Assert.True(await Wait.UntilAsync(() => rig.DataOut.Category == NetworkCategory.Public));
        rig.Guide.Open();
        await rig.Service.StartAsync();
        rig.Guide.Refresh();

        Assert.Equal(CheckState.Warning, rig.Guide.NetworkStep.State);
        Assert.True(rig.Guide.ShowNetworkFix);
        Assert.Equal(SetupStrings.SetupNetworkPublic, rig.Guide.NetworkStep.Detail);

        rig.Sender.LastSenderAddress = "192.168.0.77";
        rig.Guide.Refresh();

        Assert.Equal(CheckState.Ok, rig.Guide.NetworkStep.State);
        Assert.False(rig.Guide.ShowNetworkFix);
        Assert.Equal(SetupStrings.SetupNetworkPublicWorks, rig.Guide.NetworkStep.Detail);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task A_port_already_in_use_is_explained_on_the_first_line()
    {
        var rig = new Rig();
        var busy = new TelemetryService(() => throw new SocketException((int)SocketError.AddressAlreadyInUse));
        var guide = new ConnectionGuideViewModel(busy, rig.DataOut, rig.Sender, rig.Time, rig.Settings);
        guide.Open();

        await busy.StartAsync();
        guide.Refresh();

        Assert.Equal(CheckState.Warning, guide.ListeningStep.State);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.ErrorPortInUse, 5300), guide.ListeningStep.Detail);
        Assert.True(guide.CanStartListening); // retrying is the way out
    }

    // ---- First run and persistence -----------------------------------------------------------------

    [Fact]
    public void It_opens_by_itself_only_until_setup_was_completed()
    {
        var rig = new Rig();

        rig.Guide.OpenIfFirstRun();
        Assert.True(rig.Guide.IsOpen);

        var second = new Rig();
        second.Settings!.Update(s => s with { HasCompletedSetup = true });
        second.Guide.OpenIfFirstRun();
        Assert.False(second.Guide.IsOpen);
    }

    [Fact]
    public void Without_settings_it_never_opens_by_itself()
    {
        var rig = new Rig(withSettings: false);

        rig.Guide.OpenIfFirstRun();

        Assert.False(rig.Guide.IsOpen);
    }

    [Fact]
    public void Closing_the_guide_persists_that_setup_was_seen()
    {
        var rig = new Rig();
        rig.Guide.Open();
        Assert.False(rig.Settings!.Current.HasCompletedSetup); // opening alone is not completing

        rig.Guide.CloseCommand.Execute(null);

        Assert.False(rig.Guide.IsOpen);
        Assert.True(rig.Settings.Current.HasCompletedSetup);
        Assert.True(rig.Store.LastSaved!.HasCompletedSetup);
    }

    [Fact]
    public void Go_to_dashboard_closes_persists_and_navigates()
    {
        var rig = new Rig();
        rig.Guide.Open();

        rig.Guide.GoToDashboardCommand.Execute(null);

        Assert.False(rig.Guide.IsOpen);
        Assert.True(rig.Settings!.Current.HasCompletedSetup);
        Assert.Equal(1, rig.Host.DashboardShown);
    }

    [Fact]
    public async Task Getting_connected_persists_completion_even_if_the_guide_is_never_closed()
    {
        var rig = new Rig();
        rig.Guide.Open();
        await rig.Service.StartAsync();
        rig.Sender.LastSenderAddress = "192.168.0.77";
        await rig.PublishAsync(isRaceOn: true);

        rig.Guide.Refresh();

        Assert.True(rig.Guide.IsConnected);
        Assert.True(rig.Settings!.Current.HasCompletedSetup);
        await rig.Service.StopAsync();
    }

    [Fact]
    public void The_screen_reader_name_of_a_line_carries_its_state_as_a_word()
    {
        var rig = new Rig();
        rig.Guide.Open();

        var name = rig.Guide.ListeningStep.AutomationName;

        Assert.Contains(SetupStrings.SetupStatePending, name);
        Assert.Contains(rig.Guide.ListeningStep.Title, name);
        Assert.Contains(rig.Guide.ListeningStep.Detail, name);
    }

    // ---- Wiring --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_shell_attaches_itself_and_shows_the_connect_hint_only_while_the_game_is_not_feeding_the_dashboard()
    {
        var rig = new Rig();
        var shell = new ShellViewModel(rig.Service, TimeProvider.System, dataOut: rig.DataOut, guide: rig.Guide);
        Assert.Same(rig.Guide, shell.Dashboard.Guide);

        shell.Refresh();
        Assert.True(shell.Dashboard.ShowConnectHint); // stopped

        await shell.StartCommand.ExecuteAsync(null);
        shell.Refresh();
        Assert.True(shell.Dashboard.ShowConnectHint); // listening, nothing yet

        rig.Source.Publish(TestPackets.Sample(isRaceOn: true));
        Assert.True(await Wait.UntilAsync(() => rig.Service.ReceivedCount == 1));
        shell.Refresh();
        Assert.False(shell.Dashboard.ShowConnectHint); // receiving

        await shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task The_dashboard_link_opens_the_guide_and_its_Start_button_starts_the_shell()
    {
        var rig = new Rig();
        var shell = new ShellViewModel(rig.Service, TimeProvider.System, dataOut: rig.DataOut, guide: rig.Guide);

        shell.Dashboard.OpenGuideCommand.Execute(null);
        Assert.True(rig.Guide.IsOpen);

        await rig.Guide.StartListeningCommand.ExecuteAsync(null); // the shell replaced the fake host when it attached
        Assert.True(rig.Service.IsRunning);

        shell.Refresh();
        Assert.Equal(CheckState.Ok, rig.Guide.ListeningStep.State); // the tick refreshes the open guide

        shell.SelectedTab = shell.Tabs[1];
        rig.Guide.GoToDashboardCommand.Execute(null);
        Assert.Equal(PageId.Dashboard, shell.SelectedTab.Id);

        await shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public void Settings_offers_the_guide()
    {
        var rig = new Rig();
        var settingsPage = new SettingsViewModel(rig.Settings!, rig.DataOut, @"C:\logs", guide: rig.Guide);

        settingsPage.OpenSetupGuideCommand.Execute(null);

        Assert.True(rig.Guide.IsOpen);
    }
}
