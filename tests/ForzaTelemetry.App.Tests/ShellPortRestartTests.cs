using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App.Tests;

/// <summary>What the shell does when the UDP port changes: the message quotes the right port, a failed listener is retried, a parked restart is never lost.</summary>
public class ShellPortRestartTests
{
    private sealed class MemoryStore : ISettingsStore
    {
        public AppSettings Load() => AppSettings.Default;

        public bool TrySave(AppSettings settings) => true;
    }

    /// <summary>A listener factory like the app's: reads the port from the settings, and "fails" for the ports in <see cref="BusyPorts"/>.</summary>
    private sealed class Rig
    {
        public Rig(INetworkInfo? networkInfo = null, ILogger? dataOutLogger = null)
        {
            Settings = new SettingsService(new MemoryStore(), AppSettings.Default);
            DataOut = new DataOutViewModel(Settings.Current.UdpPort, networkInfo, logger: dataOutLogger);
            Service = new TelemetryService(() =>
            {
                PortsTried.Add(Settings.Current.UdpPort);
                if (BusyPorts.Contains(Settings.Current.UdpPort))
                {
                    throw new SocketException((int)SocketError.AddressAlreadyInUse);
                }

                return new FakeTelemetrySource();
            });
            Shell = new ShellViewModel(Service, TimeProvider.System, dataOut: DataOut, settings: Settings);
        }

        public HashSet<int> BusyPorts { get; } = [];

        public List<int> PortsTried { get; } = [];

        public SettingsService Settings { get; }

        public DataOutViewModel DataOut { get; }

        public TelemetryService Service { get; }

        public ShellViewModel Shell { get; }
    }

    [Fact]
    public async Task The_port_in_use_message_names_the_port_that_was_tried_not_the_displayed_one()
    {
        var rig = new Rig();
        rig.BusyPorts.Add(5300);

        await rig.Shell.StartCommand.ExecuteAsync(null);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.ErrorPortInUse, 5300), rig.Shell.Status);

        // The port shown elsewhere moves on (a settings change whose restart is still parked, say): the failed bind was
        // still on 5300, and that is what the error must say.
        rig.DataOut.Port = 9999;
        rig.Shell.Refresh();

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.ErrorPortInUse, 5300), rig.Shell.Status);
    }

    [Fact]
    public async Task Applying_a_new_port_while_the_listener_is_faulted_retries_the_start_on_the_new_port()
    {
        var rig = new Rig();
        rig.BusyPorts.Add(5300);
        await rig.Shell.StartCommand.ExecuteAsync(null);
        Assert.NotNull(rig.Service.Fault);
        Assert.False(rig.Service.IsRunning);

        rig.Settings.Update(s => s with { UdpPort = 5400 });

        Assert.True(await Wait.UntilAsync(() => rig.Service.IsRunning && !rig.Shell.IsBusy));
        Assert.Equal([5300, 5400], rig.PortsTried);
        Assert.Null(rig.Service.Fault);
        rig.Shell.Refresh();
        Assert.NotEqual(ConnectionState.Error, rig.Shell.ConnectionState);
        Assert.Contains("5400", rig.Shell.Status); // "Listening on UDP 5400 ..."

        await rig.Shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task A_retry_that_fails_again_reports_the_new_port()
    {
        var rig = new Rig();
        rig.BusyPorts.UnionWith([5300, 5400]);
        await rig.Shell.StartCommand.ExecuteAsync(null);

        rig.Settings.Update(s => s with { UdpPort = 5400 });

        Assert.True(await Wait.UntilAsync(() => rig.PortsTried.Count == 2 && !rig.Shell.IsBusy));
        rig.Shell.Refresh();
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.ErrorPortInUse, 5400), rig.Shell.Status);
    }

    [Fact]
    public async Task A_stopped_listener_that_never_failed_is_not_started_by_a_port_change()
    {
        var rig = new Rig();

        rig.Settings.Update(s => s with { UdpPort = 5400 });
        await Task.Delay(50);

        Assert.Empty(rig.PortsTried);
        Assert.False(rig.Service.IsRunning);
    }

    [Fact]
    public async Task A_parked_port_restart_still_runs_when_the_command_in_flight_throws()
    {
        var network = new GatedThrowingLogger();
        var rig = new Rig(new OneAddressNetworkInfo(), network); // a real lookup, so the Start awaits it on the thread pool
        await rig.Shell.StartCommand.ExecuteAsync(null); // running on 5300
        Assert.True(rig.Service.IsRunning);

        // A second Start whose address lookup blocks and then throws (ExecuteAsync does not consult CanExecute).
        network.ArmNextCall();
        var failing = rig.Shell.StartCommand.ExecuteAsync(null);
        Assert.True(network.Entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(rig.Shell.IsBusy);

        rig.Settings.Update(s => s with { UdpPort = 5400 }); // arrives mid-command: parked
        network.Release.Set();

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);

        // The failure must not swallow the parked restart: the listener ends up running on the new port.
        Assert.True(await Wait.UntilAsync(() => !rig.Shell.IsBusy && rig.Service.IsRunning && rig.PortsTried.Count == 2));
        Assert.Equal([5300, 5400], rig.PortsTried);

        await rig.Shell.StopCommand.ExecuteAsync(null);
    }

    private sealed class OneAddressNetworkInfo : INetworkInfo
    {
        public IReadOnlyList<LanAddress> GetLanAddresses() => [new LanAddress("Wi-Fi", IPAddress.Parse("192.168.0.10"))];
    }

    /// <summary>
    /// A logger that works, except that the next call after <see cref="ArmNextCall"/> waits for <see cref="Release"/> and then throws.
    /// It is how this test makes the Start command fail from the inside: the address lookup itself no longer can (an adapter-enumeration
    /// failure degrades to "not found"), but the Start still logs the addresses it read, on the same path.
    /// </summary>
    private sealed class GatedThrowingLogger : ILogger
    {
        private volatile bool _armed;

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public void ArmNextCall() => _armed = true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (_armed)
            {
                _armed = false;
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("The log sink failed.");
            }
        }
    }
}
