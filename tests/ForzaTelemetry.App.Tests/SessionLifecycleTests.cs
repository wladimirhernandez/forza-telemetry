using System.Net.Sockets;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using Microsoft.Extensions.Time.Testing;

namespace ForzaTelemetry.App.Tests;

/// <summary>
/// When the shell wipes the engines' and screens' session data: on every Start the user asks for (before the receive loop exists,
/// so nothing races its first packets, even when that Start then fails), never when a port change merely moves a running listener.
/// </summary>
public sealed class SessionLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class MemoryStore : ISettingsStore
    {
        public AppSettings Load() => AppSettings.Default;

        public bool TrySave(AppSettings settings) => true;
    }

    private sealed class Rig
    {
        private uint _ms = 1_000;

        public Rig(string directory)
        {
            Engines = new TelemetryEngines();
            History = new PerformanceHistoryStore(Path.Combine(directory, "performance.json"), TimeProvider.System);
            Performance = new PerformanceViewModel(Engines.Acceleration, Engines.TopSpeed, Engines.CarChanges, History);
            Laps = new LapsViewModel(Engines.Laps);
            Settings = new SettingsService(new MemoryStore(), AppSettings.Default);
            Service = new TelemetryService(
                () =>
                {
                    if (BusyPorts.Contains(Settings.Current.UdpPort))
                    {
                        throw new SocketException((int)SocketError.AddressAlreadyInUse);
                    }

                    var source = new FakeTelemetrySource();
                    Sources.Add(source);
                    return source;
                },
                observers: Engines.Observers);
            Shell = new ShellViewModel(
                Service,
                TimeProvider.System,
                dataOut: new DataOutViewModel(Settings.Current.UdpPort, null),
                settings: Settings,
                performancePage: Performance,
                lapsPage: Laps,
                engines: Engines);
        }

        public TelemetryEngines Engines { get; }

        public PerformanceHistoryStore History { get; }

        public PerformanceViewModel Performance { get; }

        public LapsViewModel Laps { get; }

        public SettingsService Settings { get; }

        public TelemetryService Service { get; }

        public ShellViewModel Shell { get; }

        public HashSet<int> BusyPorts { get; } = [];

        /// <summary>Every listener the service built, in order (a test makes one of them fail mid-stream).</summary>
        public List<FakeTelemetrySource> Sources { get; } = [];

        /// <summary>One lap finished and the next one started, through the engines and the screens, as a session would produce.</summary>
        public void DriveOneLapAndTopSpeed()
        {
            for (var i = 0; i <= 10; i++)
            {
                Feed(lapsCompleted: 0, lapTime: i, distance: i * 100, speed: 70);
            }

            Feed(lapsCompleted: 1, lapTime: 0, distance: 1000, speed: 70, lastLap: 10);
        }

        private void Feed(int lapsCompleted, double lapTime, double distance, float speed, double lastLap = 0)
        {
            var packet = TestPackets.Sample(speedMetersPerSecond: speed, gear: 5, carOrdinal: 1234, carClass: 3, performanceIndex: 780) with
            {
                TimestampMS = _ms += 100,
                LapNumber = (ushort)lapsCompleted,
                CurrentLap = (float)lapTime,
                DistanceTraveled = (float)distance,
                LastLap = (float)lastLap
            };
            foreach (var observer in Engines.Observers)
            {
                observer.OnPacket(packet);
            }

            Performance.Refresh(packet);
            Laps.Refresh(packet);
        }
    }

    [Fact]
    public async Task A_start_that_fails_has_already_begun_the_new_session_and_clears_laps_and_results()
    {
        var rig = new Rig(_directory);
        rig.DriveOneLapAndTopSpeed();
        Assert.True(rig.Laps.HasLaps);
        rig.BusyPorts.Add(rig.Settings.Current.UdpPort);

        await rig.Shell.StartCommand.ExecuteAsync(null); // port in use

        Assert.NotNull(rig.Service.Fault);
        Assert.False(rig.Laps.HasLaps);
        Assert.Equal("--", rig.Performance.TopSpeedText);
        Assert.Equal(252, rig.History.Find(new CarKey(1234, 780))!.TopSpeedKmh!.Value, precision: 1); // saved, not lost
    }

    [Fact]
    public async Task A_start_that_succeeds_begins_a_new_session_and_clears_laps_and_results()
    {
        var rig = new Rig(_directory);
        rig.DriveOneLapAndTopSpeed();

        await rig.Shell.StartCommand.ExecuteAsync(null);

        Assert.False(rig.Laps.HasLaps);
        Assert.Equal("--", rig.Performance.TopSpeedText);
        await rig.Shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Moving_the_port_of_a_running_listener_continues_the_session_without_clearing_anything()
    {
        var rig = new Rig(_directory);
        await rig.Shell.StartCommand.ExecuteAsync(null);
        rig.DriveOneLapAndTopSpeed();
        var topSpeedText = rig.Performance.TopSpeedText;
        Assert.True(rig.Laps.HasLaps);

        rig.Settings.Update(s => s with { UdpPort = 5400 });
        Assert.True(await Wait.UntilAsync(() => !rig.Shell.IsBusy && rig.Service.IsRunning));

        Assert.True(rig.Laps.HasLaps);
        Assert.Equal(topSpeedText, rig.Performance.TopSpeedText);
        await rig.Shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Retrying_on_a_new_port_after_a_failed_start_begins_a_fresh_session()
    {
        var rig = new Rig(_directory);
        rig.BusyPorts.Add(5300);
        await rig.Shell.StartCommand.ExecuteAsync(null);
        Assert.NotNull(rig.Service.Fault);

        // While the listener is down something still fills the engines (a leftover from the failed attempt's session).
        rig.DriveOneLapAndTopSpeed();
        Assert.True(rig.Laps.HasLaps);

        rig.Settings.Update(s => s with { UdpPort = 5400 });
        Assert.True(await Wait.UntilAsync(() => rig.Service.IsRunning && !rig.Shell.IsBusy));

        Assert.False(rig.Laps.HasLaps); // the retry is the first real start, not the continuation of a session that never began
        await rig.Shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task A_fresh_start_saves_the_previous_top_speed_instead_of_discarding_it()
    {
        var rig = new Rig(_directory);
        rig.DriveOneLapAndTopSpeed();
        Assert.Empty(rig.History.Records); // the fault path has no Stop, so nothing saved the 252 km/h yet

        await rig.Shell.StartCommand.ExecuteAsync(null);

        Assert.Equal(252, rig.History.Find(new CarKey(1234, 780))!.TopSpeedKmh!.Value, precision: 1);
        await rig.Shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Moving_the_port_after_the_listener_died_mid_stream_keeps_the_session()
    {
        var rig = new Rig(_directory);
        await rig.Shell.StartCommand.ExecuteAsync(null);
        rig.DriveOneLapAndTopSpeed();
        var topSpeedText = rig.Performance.TopSpeedText;
        Assert.True(rig.Laps.HasLaps);

        // The listener ran and then broke (a socket error while receiving): not a failed Start.
        rig.Sources[0].FailNextReadWith(new SocketException((int)SocketError.ConnectionReset));
        rig.Sources[0].Publish(TestPackets.Sample());
        Assert.True(await Wait.UntilAsync(() => rig.Service.Fault is not null));
        Assert.False(rig.Service.StartFailed);

        rig.Settings.Update(s => s with { UdpPort = 5400 });
        Assert.True(await Wait.UntilAsync(() => rig.Service.IsRunning && !rig.Shell.IsBusy));

        // The laps and the bests are what the user was looking at: the network hiccuped, the session did not end.
        Assert.True(rig.Laps.HasLaps);
        Assert.Equal(topSpeedText, rig.Performance.TopSpeedText);
        await rig.Shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task A_session_saved_by_Stop_is_not_saved_a_second_time_by_the_next_Start()
    {
        var rig = new Rig(_directory);
        await rig.Shell.StartCommand.ExecuteAsync(null);
        rig.DriveOneLapAndTopSpeed();
        await rig.Shell.StopCommand.ExecuteAsync(null); // saves the 252 km/h
        Assert.Equal(252, rig.History.Find(new CarKey(1234, 780))!.TopSpeedKmh!.Value, precision: 1);

        // Something still reaches the engines after the Stop (a late packet). If the next Start saved "the previous session"
        // again it would read it and write it; the Stop already closed that session, so it must not.
        rig.Engines.TopSpeed.OnPacket(TestPackets.Sample(speedMetersPerSecond: 80, carOrdinal: 1234, carClass: 3, performanceIndex: 780));
        await rig.Shell.StartCommand.ExecuteAsync(null);

        Assert.Equal(252, rig.History.Find(new CarKey(1234, 780))!.TopSpeedKmh!.Value, precision: 1);
        await rig.Shell.StopCommand.ExecuteAsync(null);
    }
}
