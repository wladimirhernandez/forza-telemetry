using System.Net.Sockets;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Udp;
using Microsoft.Extensions.Time.Testing;

namespace ForzaTelemetry.App.Tests;

public class MainViewModelTests
{
    [Fact]
    public async Task Start_then_a_received_packet_updates_Rpm_Speed_and_Gear_on_Refresh()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var vm = new MainViewModel(service, TimeProvider.System);

        await vm.StartCommand.ExecuteAsync(null);
        fake.Publish(TestPackets.Sample(currentEngineRpm: 5_200f, engineMaxRpm: 7_500f, speedMetersPerSecond: 40f, gear: 4));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));

        vm.Refresh();

        Assert.Equal(5_200f, vm.Rpm);
        Assert.Equal(7_500f, vm.MaxRpm);
        Assert.Equal(40f * 3.6f, vm.SpeedKmh, precision: 3);
        Assert.Equal("4", vm.GearLabel);
        Assert.True(vm.IsRunning);
        Assert.Equal("Receiving", vm.Status);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Refresh_maps_the_packet_to_the_four_tires()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var vm = new MainViewModel(service, TimeProvider.System);

        await vm.StartCommand.ExecuteAsync(null);
        fake.Publish(TestPackets.Sample(
            tempFrontLeft: 32f, tempFrontRight: 140f, tempRearLeft: 210f, tempRearRight: 240f,
            slipFrontLeft: 0.7f, suspensionFrontLeft: 0.3f, suspensionRearRight: 0.9f));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));

        vm.Refresh();

        Assert.Equal(TireTempBand.Cold, vm.FrontLeft.Band);
        Assert.Equal(TireTempBand.Ideal, vm.FrontRight.Band);
        Assert.Equal(TireTempBand.Warm, vm.RearLeft.Band);
        Assert.Equal(TireTempBand.Hot, vm.RearRight.Band);
        Assert.Equal(0.7f, vm.FrontLeft.Slip);
        Assert.Equal(0.3f, vm.FrontLeft.Suspension);
        Assert.Equal(0.9f, vm.RearRight.Suspension);
        Assert.Equal(0, vm.FrontLeft.TemperatureC, precision: 3);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Stop_then_Start_again_still_updates_the_dashboard()
    {
        var firstSource = new FakeTelemetrySource();
        var secondSource = new FakeTelemetrySource();
        var sourcesToHandOut = new Queue<ITelemetrySource>([firstSource, secondSource]);
        var service = new TelemetryService(() => sourcesToHandOut.Dequeue());
        var vm = new MainViewModel(service, TimeProvider.System);

        await vm.StartCommand.ExecuteAsync(null);
        firstSource.Publish(TestPackets.Sample(gear: 1));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        vm.Refresh();
        Assert.Equal("1", vm.GearLabel);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.False(vm.IsRunning);
        Assert.Equal("Stopped", vm.Status);

        await vm.StartCommand.ExecuteAsync(null);
        secondSource.Publish(TestPackets.Sample(gear: 2));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 2));
        vm.Refresh();

        Assert.Equal("2", vm.GearLabel);
        Assert.True(vm.IsRunning);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Factory_throwing_port_in_use_shows_a_friendly_error_and_IsRunning_false()
    {
        var service = new TelemetryService(
            () => throw new SocketException((int)SocketError.AddressAlreadyInUse));
        var vm = new MainViewModel(service, TimeProvider.System);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
        Assert.Contains("already in use", vm.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(UdpTelemetryListener.DefaultPort.ToString(), vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PacketsPerSecond_is_computed_from_the_injected_TimeProvider()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var timeProvider = new FakeTimeProvider();
        var vm = new MainViewModel(service, timeProvider);

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
        var vm = new MainViewModel(service, timeProvider);

        await vm.StartCommand.ExecuteAsync(null);
        fake.Publish(TestPackets.Sample());
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        vm.Refresh();
        Assert.Equal("Receiving", vm.Status);

        // No further packets published — advance well past the (2s) silence threshold.
        timeProvider.Advance(TimeSpan.FromSeconds(3));
        vm.Refresh();

        Assert.StartsWith("No data for", vm.Status);

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
        var vm = new MainViewModel(service, TimeProvider.System);
        var ticker = new ManualTicker();
        ticker.Start(TimeSpan.FromMilliseconds(50), vm.Refresh);

        await vm.StartCommand.ExecuteAsync(null);
        fake.Publish(TestPackets.Sample(gear: 7));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));

        ticker.Tick();

        Assert.Equal("7", vm.GearLabel);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task ConnectionState_follows_stopped_listening_receiving_and_no_data()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var timeProvider = new FakeTimeProvider();
        var vm = new MainViewModel(service, timeProvider);
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
        var vm = new MainViewModel(service, TimeProvider.System);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionState.Error, vm.ConnectionState);
    }

    [Fact]
    public async Task RpmReadout_uses_space_grouping_and_raises_change_notification()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var vm = new MainViewModel(service, TimeProvider.System);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.StartCommand.ExecuteAsync(null);
        fake.Publish(TestPackets.Sample(currentEngineRpm: 7_439f, engineMaxRpm: 9_000f));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        vm.Refresh();

        Assert.Equal("7 439 / 9 000", vm.RpmReadout);
        Assert.Contains(nameof(MainViewModel.RpmReadout), changed);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public void Design_time_constructor_populates_sample_values_for_the_previewer()
    {
        var vm = new MainViewModel();

        Assert.True(vm.IsRunning);
        Assert.False(string.IsNullOrWhiteSpace(vm.GearLabel));
        Assert.True(vm.MaxRpm > 0);
    }
}
