using System.Globalization;
using System.Net.Sockets;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using System.Net;
using ForzaTelemetry.Udp;
using Microsoft.Extensions.Time.Testing;

namespace ForzaTelemetry.App.Tests;

public class ShellViewModelTests
{
    [Fact]
    public async Task Start_then_a_received_packet_updates_Rpm_Speed_and_Gear_on_Refresh()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var vm = new ShellViewModel(service, TimeProvider.System);

        await vm.StartCommand.ExecuteAsync(null);
        fake.Publish(TestPackets.Sample(currentEngineRpm: 5_200f, engineMaxRpm: 7_500f, speedMetersPerSecond: 40f, gear: 4));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));

        vm.Refresh();

        Assert.Equal(5_200f, vm.Dashboard.Rpm);
        Assert.Equal(7_500f, vm.Dashboard.MaxRpm);
        Assert.Equal(40f * 3.6f, vm.Dashboard.SpeedKmh, precision: 3);
        Assert.Equal("4", vm.Dashboard.GearLabel);
        Assert.True(vm.IsRunning);
        Assert.Equal(Strings.StatusReceiving, vm.Status);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Refresh_maps_the_packet_to_the_four_tires()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var vm = new ShellViewModel(service, TimeProvider.System);

        await vm.StartCommand.ExecuteAsync(null);
        fake.Publish(TestPackets.Sample(
            tempFrontLeft: 32f, tempFrontRight: 140f, tempRearLeft: 210f, tempRearRight: 240f,
            slipFrontLeft: 0.7f, suspensionFrontLeft: 0.3f, suspensionRearRight: 0.9f));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));

        vm.Refresh();

        Assert.Equal(TireTempBand.Cold, vm.Dashboard.FrontLeft.Band);
        Assert.Equal(TireTempBand.Ideal, vm.Dashboard.FrontRight.Band);
        Assert.Equal(TireTempBand.Warm, vm.Dashboard.RearLeft.Band);
        Assert.Equal(TireTempBand.Hot, vm.Dashboard.RearRight.Band);
        Assert.Equal(0.7f, vm.Dashboard.FrontLeft.Slip);
        Assert.Equal(0.3f, vm.Dashboard.FrontLeft.Suspension);
        Assert.Equal(0.9f, vm.Dashboard.RearRight.Suspension);
        Assert.Equal(0, vm.Dashboard.FrontLeft.TemperatureC, precision: 3);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Stop_then_Start_again_still_updates_the_dashboard()
    {
        var firstSource = new FakeTelemetrySource();
        var secondSource = new FakeTelemetrySource();
        var sourcesToHandOut = new Queue<ITelemetrySource>([firstSource, secondSource]);
        var service = new TelemetryService(() => sourcesToHandOut.Dequeue());
        var vm = new ShellViewModel(service, TimeProvider.System);

        await vm.StartCommand.ExecuteAsync(null);
        firstSource.Publish(TestPackets.Sample(gear: 1));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        vm.Refresh();
        Assert.Equal("1", vm.Dashboard.GearLabel);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.False(vm.IsRunning);
        Assert.Equal(Strings.StatusStopped, vm.Status);

        await vm.StartCommand.ExecuteAsync(null);
        secondSource.Publish(TestPackets.Sample(gear: 2));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 2));
        vm.Refresh();

        Assert.Equal("2", vm.Dashboard.GearLabel);
        Assert.True(vm.IsRunning);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Factory_throwing_port_in_use_shows_a_friendly_error_and_IsRunning_false()
    {
        var service = new TelemetryService(
            () => throw new SocketException((int)SocketError.AddressAlreadyInUse));
        var vm = new ShellViewModel(service, TimeProvider.System);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
        Assert.Equal(ConnectionState.Error, vm.ConnectionState);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, Strings.ErrorPortInUse, UdpTelemetryListener.DefaultPort),
            vm.Status);
    }

    [Fact]
    public async Task PacketsPerSecond_is_computed_from_the_injected_TimeProvider()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var timeProvider = new FakeTimeProvider();
        var vm = new ShellViewModel(service, timeProvider);

        // Anchors _lastRefreshAt at the fake clock's starting instant.
        await vm.StartCommand.ExecuteAsync(null);

        for (var i = 0; i < 10; i++)
        {
            fake.Publish(TestPackets.Sample());
        }

        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 10));

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        vm.Refresh();

        Assert.Equal(10.0, vm.PacketsPerSecond, precision: 3);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Status_shows_no_data_once_silence_exceeds_the_threshold()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var timeProvider = new FakeTimeProvider();
        var vm = new ShellViewModel(service, timeProvider);

        await vm.StartCommand.ExecuteAsync(null);
        fake.Publish(TestPackets.Sample());
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        vm.Refresh();
        Assert.Equal(Strings.StatusReceiving, vm.Status);

        // No further packets published — advance well past the (2s) silence threshold.
        timeProvider.Advance(TimeSpan.FromSeconds(3));
        vm.Refresh();

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.StatusNoData, 3.0), vm.Status);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task A_ManualTicker_wired_like_the_composition_root_drives_Refresh()
    {
        // Demonstrates the real wiring pattern from App.xaml.cs OnStartup (ticker.Start(interval,
        // vm.Refresh)) without needing WPF's real DispatcherTimer/UI thread — the test decides
        // exactly when a "tick" happens by calling ManualTicker.Tick() itself.
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var vm = new ShellViewModel(service, TimeProvider.System);
        var ticker = new ManualTicker();
        ticker.Start(TimeSpan.FromMilliseconds(50), vm.Refresh);

        await vm.StartCommand.ExecuteAsync(null);
        fake.Publish(TestPackets.Sample(gear: 7));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));

        ticker.Tick();

        Assert.Equal("7", vm.Dashboard.GearLabel);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task ConnectionState_follows_stopped_listening_receiving_and_no_data()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var timeProvider = new FakeTimeProvider();
        var vm = new ShellViewModel(service, timeProvider);
        Assert.Equal(ConnectionState.Stopped, vm.ConnectionState);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Listening, vm.ConnectionState);

        fake.Publish(TestPackets.Sample());
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        vm.Refresh();
        Assert.Equal(ConnectionState.Receiving, vm.ConnectionState);

        timeProvider.Advance(TimeSpan.FromSeconds(3));
        vm.Refresh();
        Assert.Equal(ConnectionState.NoData, vm.ConnectionState);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Stopped, vm.ConnectionState);
    }

    [Fact]
    public async Task ConnectionState_is_Error_when_the_port_is_already_in_use()
    {
        var service = new TelemetryService(
            () => throw new SocketException((int)SocketError.AddressAlreadyInUse));
        var vm = new ShellViewModel(service, TimeProvider.System);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionState.Error, vm.ConnectionState);
    }

    [Fact]
    public async Task RpmReadout_uses_space_grouping_and_raises_change_notification()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var vm = new ShellViewModel(service, TimeProvider.System);
        var changed = new List<string?>();
        vm.Dashboard.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.StartCommand.ExecuteAsync(null);
        fake.Publish(TestPackets.Sample(currentEngineRpm: 7_439f, engineMaxRpm: 9_000f));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        vm.Refresh();

        Assert.Equal("7 439 / 9 000", vm.Dashboard.RpmReadout);
        Assert.Contains(nameof(DashboardViewModel.RpmReadout), changed);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task ToggleListening_starts_when_stopped_and_stops_when_running()
    {
        var firstSource = new FakeTelemetrySource();
        var secondSource = new FakeTelemetrySource();
        var sourcesToHandOut = new Queue<ITelemetrySource>([firstSource, secondSource]);
        var service = new TelemetryService(() => sourcesToHandOut.Dequeue());
        var vm = new ShellViewModel(service, TimeProvider.System);

        await vm.ToggleListeningCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);

        await vm.ToggleListeningCommand.ExecuteAsync(null);
        Assert.False(vm.IsRunning);
        Assert.Equal(ConnectionState.Stopped, vm.ConnectionState);

        // The toggle can start again after a stop (it rebuilds the listener through the factory).
        await vm.ToggleListeningCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public void Design_time_sample_populates_values_for_the_previewer()
    {
        var vm = ShellViewModel.DesignSample;

        Assert.True(vm.IsRunning);
        Assert.False(string.IsNullOrWhiteSpace(vm.Dashboard.GearLabel));
        Assert.True(vm.Dashboard.MaxRpm > 0);
    }

    // ---- Per-session state ------------------------------------------------------------------------------

    [Fact]
    public async Task Start_after_Stop_does_not_show_the_previous_sessions_packet()
    {
        var firstSource = new FakeTelemetrySource();
        var secondSource = new FakeTelemetrySource();
        var sources = new Queue<ITelemetrySource>([firstSource, secondSource]);
        var service = new TelemetryService(() => sources.Dequeue());
        var timeProvider = new FakeTimeProvider();
        var vm = new ShellViewModel(service, timeProvider);

        await vm.StartCommand.ExecuteAsync(null);
        firstSource.Publish(TestPackets.Sample(currentEngineRpm: 6_000f, gear: 5, carOrdinal: 7, performanceIndex: 700));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        vm.Refresh();
        Assert.Equal(6_000f, vm.Dashboard.Rpm);
        await vm.StopCommand.ExecuteAsync(null);

        // Long pause between sessions: the old silence clock would read "No data for 60 s" right after Start.
        timeProvider.Advance(TimeSpan.FromSeconds(60));
        await vm.StartCommand.ExecuteAsync(null);
        vm.Refresh();

        Assert.Null(service.Latest);
        Assert.Equal(0f, vm.Dashboard.Rpm);
        Assert.Equal("-", vm.Dashboard.GearLabel);
        Assert.False(vm.Dashboard.HasCarInfo);
        Assert.Equal(TireTempBand.Unknown, vm.Dashboard.FrontLeft.Band);
        Assert.Equal(0.0, vm.PacketsPerSecond);
        Assert.Equal(ConnectionState.Listening, vm.ConnectionState);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, Strings.StatusListening, UdpTelemetryListener.DefaultPort),
            vm.Status);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Packets_per_second_after_a_restart_ignores_the_old_sessions_counter()
    {
        var firstSource = new FakeTelemetrySource();
        var secondSource = new FakeTelemetrySource();
        var sources = new Queue<ITelemetrySource>([firstSource, secondSource]);
        var service = new TelemetryService(() => sources.Dequeue());
        var timeProvider = new FakeTimeProvider();
        var vm = new ShellViewModel(service, timeProvider);

        await vm.StartCommand.ExecuteAsync(null);
        for (var i = 0; i < 50; i++)
        {
            firstSource.Publish(TestPackets.Sample());
        }

        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 50));
        await vm.StopCommand.ExecuteAsync(null);
        await vm.StartCommand.ExecuteAsync(null);

        secondSource.Publish(TestPackets.Sample());
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 51));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        vm.Refresh();

        // 1 new packet in 1 s, not 51.
        Assert.Equal(1.0, vm.PacketsPerSecond, precision: 3);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Stop_shows_an_idle_dashboard_and_later_ticks_do_not_bring_the_old_values_back()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var vm = new ShellViewModel(service, TimeProvider.System);

        await vm.StartCommand.ExecuteAsync(null);
        // 7_000 of 7_000 rpm: a redline packet with pedals and G-forces.
        fake.Publish(TestPackets.Sample(
            currentEngineRpm: 7_000f, engineMaxRpm: 7_000f, accel: 255, brake: 100, accelerationX: 9.81f,
            power: 200_000f, fuel: 0.5f));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        vm.Refresh();
        Assert.True(vm.Dashboard.IsRedline);
        Assert.True(vm.Dashboard.ThrottleInput > 0);

        await vm.StopCommand.ExecuteAsync(null);
        vm.Refresh();
        vm.Refresh();

        Assert.False(vm.Dashboard.IsRedline);
        Assert.Equal(0.0, vm.Dashboard.ThrottleInput);
        Assert.Equal(0.0, vm.Dashboard.BrakeInput);
        Assert.Equal(0.0, vm.Dashboard.LateralG);
        Assert.Equal(0.0, vm.Dashboard.PowerKw);
        Assert.Equal(0.0, vm.Dashboard.FuelPercent);
        Assert.Equal(0f, vm.Dashboard.Rpm);
        Assert.Equal(ConnectionState.Stopped, vm.ConnectionState);
    }

    [Fact]
    public async Task A_faulted_loop_zeroes_packets_per_second_and_the_live_values()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var timeProvider = new FakeTimeProvider();
        var vm = new ShellViewModel(service, timeProvider);

        await vm.StartCommand.ExecuteAsync(null);
        for (var i = 0; i < 5; i++)
        {
            fake.Publish(TestPackets.Sample(currentEngineRpm: 7_000f, engineMaxRpm: 7_000f, accel: 200));
        }

        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 5));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        vm.Refresh();
        Assert.True(vm.PacketsPerSecond > 0);
        Assert.True(vm.Dashboard.IsRedline);

        fake.FailNextReadWith(new InvalidOperationException("boom"));
        fake.Publish(TestPackets.Sample());
        Assert.True(await Wait.UntilAsync(() => service.Fault is not null));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        vm.Refresh();

        Assert.Equal(ConnectionState.Error, vm.ConnectionState);
        Assert.Equal(0.0, vm.PacketsPerSecond);
        Assert.False(vm.Dashboard.IsRedline);
        Assert.Equal(0.0, vm.Dashboard.ThrottleInput);
        Assert.Equal(0f, vm.Dashboard.Rpm);
    }

    [Fact]
    public async Task Packets_with_IsRaceOn_false_do_not_reset_the_per_car_gauge_scale()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var vm = new ShellViewModel(service, TimeProvider.System);
        await vm.StartCommand.ExecuteAsync(null);

        // A strong car: 500 kW sets the power gauge scale; a 250 kW reading is then half of it.
        fake.Publish(TestPackets.Sample(power: 500_000f, carOrdinal: 11));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        vm.Refresh();
        var tokenAfterCar = vm.Dashboard.TrailResetToken;

        // Pause menu: zeroed packet with another ordinal. Must not count as a car change.
        fake.Publish(TestPackets.Sample(isRaceOn: false, carOrdinal: 0, power: 0));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 2));
        vm.Refresh();
        Assert.Equal(tokenAfterCar, vm.Dashboard.TrailResetToken);
        Assert.Equal(ConnectionState.Receiving, vm.ConnectionState);

        fake.Publish(TestPackets.Sample(power: 250_000f, carOrdinal: 11));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 3));
        vm.Refresh();

        Assert.Equal(tokenAfterCar, vm.Dashboard.TrailResetToken);
        Assert.Equal(0.5, vm.Dashboard.PowerRatio, precision: 2);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task A_paused_game_shows_the_idle_dashboard()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var vm = new ShellViewModel(service, TimeProvider.System);
        await vm.StartCommand.ExecuteAsync(null);

        fake.Publish(TestPackets.Sample(currentEngineRpm: 7_000f, engineMaxRpm: 7_000f));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        vm.Refresh();
        Assert.True(vm.Dashboard.IsRedline);

        fake.Publish(TestPackets.Sample(currentEngineRpm: 7_000f, engineMaxRpm: 7_000f, isRaceOn: false));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 2));
        vm.Refresh();

        Assert.False(vm.Dashboard.IsRedline);
        Assert.Equal(0f, vm.Dashboard.Rpm);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task TrailResetToken_changes_on_Start_Stop_and_when_the_car_changes()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var vm = new ShellViewModel(service, TimeProvider.System);
        var token = vm.Dashboard.TrailResetToken;

        await vm.StartCommand.ExecuteAsync(null);
        Assert.NotEqual(token, vm.Dashboard.TrailResetToken);
        token = vm.Dashboard.TrailResetToken;

        fake.Publish(TestPackets.Sample(carOrdinal: 1));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        vm.Refresh();
        Assert.NotEqual(token, vm.Dashboard.TrailResetToken); // first car seen
        token = vm.Dashboard.TrailResetToken;

        fake.Publish(TestPackets.Sample(carOrdinal: 1));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 2));
        vm.Refresh();
        Assert.Equal(token, vm.Dashboard.TrailResetToken); // same car: trail keeps growing

        fake.Publish(TestPackets.Sample(carOrdinal: 2));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 3));
        vm.Refresh();
        Assert.NotEqual(token, vm.Dashboard.TrailResetToken);
        token = vm.Dashboard.TrailResetToken;

        await vm.StopCommand.ExecuteAsync(null);
        Assert.NotEqual(token, vm.Dashboard.TrailResetToken);
    }

    [Fact]
    public void Status_texts_exist_in_the_neutral_and_the_Spanish_resources()
    {
        var spanish = CultureInfo.GetCultureInfo("es");
        string[] keys =
        [
            nameof(Strings.StatusStopped), nameof(Strings.StatusListening), nameof(Strings.StatusNoData),
            nameof(Strings.StatusReceiving), nameof(Strings.ErrorPortInUse), nameof(Strings.ErrorSocket), nameof(Strings.ErrorGeneric)
        ];

        foreach (var key in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture)));
            Assert.False(string.IsNullOrWhiteSpace(Strings.ResourceManager.GetString(key, spanish)));
        }

        Assert.Equal("Detenido", Strings.ResourceManager.GetString(nameof(Strings.StatusStopped), spanish));
    }

    // ---- Data Out address -------------------------------------------------------------------------------

    private sealed class FakeNetworkInfo(params string[] ips) : INetworkInfo
    {
        public string[] Ips { get; set; } = ips;

        public int LastReadThreadId { get; private set; }

        public IReadOnlyList<LanAddress> GetLanAddresses()
        {
            LastReadThreadId = Environment.CurrentManagedThreadId;
            return Ips.Select(ip => new LanAddress("Wi-Fi", IPAddress.Parse(ip))).ToList();
        }
    }

    [Fact]
    public void DataOutAddress_lists_the_LAN_addresses_and_the_port_is_the_listener_default()
    {
        var service = new TelemetryService(() => new FakeTelemetrySource());
        var vm = new ShellViewModel(service, TimeProvider.System, new FakeNetworkInfo("192.168.0.204", "10.0.0.7"));

        Assert.Equal("192.168.0.204, 10.0.0.7", vm.DataOut.Address);
        Assert.Equal(UdpTelemetryListener.DefaultPort, vm.DataOut.Port);
    }

    [Fact]
    public void DataOutAddress_says_not_found_when_there_is_no_usable_adapter()
    {
        var service = new TelemetryService(() => new FakeTelemetrySource());

        Assert.Equal(Strings.DataOutIpNone, new ShellViewModel(service, TimeProvider.System, new FakeNetworkInfo()).DataOut.Address);
        Assert.Equal(Strings.DataOutIpNone, new ShellViewModel(service, TimeProvider.System).DataOut.Address);
    }

    [Fact]
    public async Task DataOutAddress_is_refreshed_on_Start()
    {
        var network = new FakeNetworkInfo("192.168.0.204");
        var service = new TelemetryService(() => new FakeTelemetrySource());
        var vm = new ShellViewModel(service, TimeProvider.System, network);

        network.Ips = ["172.16.0.9"];
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("172.16.0.9", vm.DataOut.Address);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Toggle_decides_from_the_live_service_state_not_the_tick_updated_IsRunning()
    {
        var service = new TelemetryService(() => new FakeTelemetrySource());
        var vm = new ShellViewModel(service, TimeProvider.System);

        await service.StartAsync(); // the service runs, but no Refresh() ran: vm.IsRunning is still the stale false
        Assert.False(vm.IsRunning);

        await vm.ToggleListeningCommand.ExecuteAsync(null); // reading the stale flag would Start again instead

        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task Commands_cannot_overlap_while_one_is_in_flight()
    {
        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeTelemetrySource { DisposeGate = disposeGate };
        var service = new TelemetryService(() => fake);
        var vm = new ShellViewModel(service, TimeProvider.System);
        await vm.StartCommand.ExecuteAsync(null);

        var stopping = vm.ToggleListeningCommand.ExecuteAsync(null); // blocks inside the source's dispose

        Assert.True(vm.IsBusy);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));
        Assert.False(vm.ToggleListeningCommand.CanExecute(null));

        // Even called from code (which skips CanExecute) a second command is dropped, not run concurrently.
        await vm.StopCommand.ExecuteAsync(null);
        await vm.ToggleListeningCommand.ExecuteAsync(null);
        Assert.False(stopping.IsCompleted);

        disposeGate.SetResult();
        await stopping;

        Assert.False(vm.IsBusy);
        Assert.False(service.IsRunning);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_paused_game_keeps_the_real_packets_per_second_and_the_redline()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var timeProvider = new FakeTimeProvider();
        var vm = new ShellViewModel(service, timeProvider);
        await vm.StartCommand.ExecuteAsync(null);

        fake.Publish(TestPackets.Sample(engineMaxRpm: 8_200f));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        vm.Refresh();
        Assert.Equal(8_200f, vm.Dashboard.MaxRpm);

        for (var i = 0; i < 4; i++)
        {
            fake.Publish(TestPackets.Sample(engineMaxRpm: 0f, isRaceOn: false));
        }

        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 5));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        vm.Refresh();

        Assert.Equal(0f, vm.Dashboard.Rpm); // live driving values are idle...
        Assert.Equal(4.0, vm.PacketsPerSecond, precision: 3); // ...but packets are still arriving
        Assert.Equal(8_200f, vm.Dashboard.MaxRpm); // and the car's redline survives the pause

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task DataOut_PrimaryAddress_is_the_first_address_and_HasAddress_follows_it()
    {
        var network = new FakeNetworkInfo("192.168.0.204", "10.0.0.7");
        var vm = new ShellViewModel(new TelemetryService(() => new FakeTelemetrySource()), TimeProvider.System, network);

        Assert.Equal("192.168.0.204", vm.DataOut.PrimaryAddress);
        Assert.True(vm.DataOut.HasAddress);

        network.Ips = [];
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Null(vm.DataOut.PrimaryAddress);
        Assert.False(vm.DataOut.HasAddress);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Every_address_read_is_logged_and_Start_reads_the_adapters_off_the_calling_thread()
    {
        var logger = new ListLogger<ShellViewModel>();
        var network = new FakeNetworkInfo("192.168.0.204");
        var vm = new ShellViewModel(new TelemetryService(() => new FakeTelemetrySource()), TimeProvider.System, network, logger);
        var callerThread = Environment.CurrentManagedThreadId;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(2, logger.Entries.Count(e => e.Message.Contains("192.168.0.204 (Wi-Fi)"))); // constructor + Start
        Assert.NotEqual(callerThread, network.LastReadThreadId);
        await vm.StopCommand.ExecuteAsync(null);
    }
}
