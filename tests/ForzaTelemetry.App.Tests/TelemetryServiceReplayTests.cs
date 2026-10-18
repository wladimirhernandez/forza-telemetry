using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Recording;
using Microsoft.Extensions.Time.Testing;

namespace ForzaTelemetry.App.Tests;

/// <summary>
/// The live/replay swap inside <see cref="TelemetryService"/>: one source at a time, through the same serialized start path, with
/// the same observers, and a fresh session every time.
/// </summary>
public sealed class TelemetryServiceReplayTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class CountingObserver : IPacketObserver
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void OnPacket(TelemetryPacket packet) => Interlocked.Increment(ref _count);
    }

    private static Func<string, ReplayTelemetrySource> InstantReplay(bool pausedAtStart = false) => path =>
    {
        var source = new ReplayTelemetrySource(path, new FakeTimeProvider()) { Speed = ReplaySpeed.Max };
        if (pausedAtStart)
        {
            source.Pause();
        }

        return source;
    };

    // ---- A replay behaves like live data ------------------------------------------------------------------

    [Fact]
    public async Task A_replay_feeds_the_observers_and_latest_exactly_like_live_data()
    {
        var path = await RecordingFiles.WriteAsync(_folder, "drive", packets: 12, car: 777);
        var observer = new CountingObserver();
        var service = new TelemetryService(() => new FakeTelemetrySource(), observers: [observer], replayFactory: InstantReplay());

        await service.StartReplayAsync(path);

        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 12));
        Assert.Equal(12, observer.Count); // every packet reached the engines, not only the sampled ones
        Assert.Equal(777, service.Latest!.CarOrdinal);
        Assert.Equal(TelemetrySourceMode.Replay, service.Mode);
        Assert.True(service.IsRunning);
        Assert.True(service.IsReplaying);
        Assert.NotNull(service.Replay);
        Assert.Equal(path, service.ReplayPath);
        await service.StopAsync();
    }

    [Fact]
    public async Task Stopping_ends_the_replay_and_clears_the_player_and_the_last_packet()
    {
        var path = await RecordingFiles.WriteAsync(_folder, "drive");
        var service = new TelemetryService(() => new FakeTelemetrySource(), replayFactory: InstantReplay());
        await service.StartReplayAsync(path);
        Assert.True(await Wait.UntilAsync(() => service.Latest is not null));

        await service.StopAsync();

        Assert.False(service.IsRunning);
        Assert.False(service.IsReplaying);
        Assert.Null(service.Replay);
        Assert.Null(service.ReplayPath);
        Assert.Null(service.Latest);
    }

    // ---- Mutual exclusion ---------------------------------------------------------------------------------

    [Fact]
    public async Task Starting_a_replay_while_listening_live_stops_the_live_source_first()
    {
        var path = await RecordingFiles.WriteAsync(_folder, "drive");
        var live = new FakeTelemetrySource();
        var service = new TelemetryService(() => live, replayFactory: InstantReplay(pausedAtStart: true));
        await service.StartAsync();
        Assert.Equal(TelemetrySourceMode.Live, service.Mode);

        await service.StartReplayAsync(path);

        Assert.True(live.IsDisposed); // the UDP port is released before the replay exists
        Assert.Equal(TelemetrySourceMode.Replay, service.Mode);
        Assert.True(service.IsReplaying);
        await service.StopAsync();
    }

    [Fact]
    public async Task Starting_live_while_replaying_stops_the_replay_first()
    {
        var path = await RecordingFiles.WriteAsync(_folder, "drive");
        var service = new TelemetryService(() => new FakeTelemetrySource(), replayFactory: InstantReplay(pausedAtStart: true));
        await service.StartReplayAsync(path);
        Assert.True(service.IsReplaying);

        await service.StartAsync();

        Assert.Equal(TelemetrySourceMode.Live, service.Mode);
        Assert.False(service.IsReplaying);
        Assert.Null(service.Replay);
        Assert.Null(service.ReplayPath);
        Assert.True(service.IsRunning);
        await service.StopAsync();
    }

    [Fact]
    public async Task Starting_live_when_already_live_is_still_a_no_op()
    {
        var builds = 0;
        var service = new TelemetryService(() =>
        {
            builds++;
            return new FakeTelemetrySource();
        });
        await service.StartAsync();

        await service.StartAsync();

        Assert.Equal(1, builds);
        await service.StopAsync();
    }

    [Fact]
    public async Task Replaying_the_same_file_again_is_a_fresh_replay_that_starts_from_the_beginning()
    {
        var path = await RecordingFiles.WriteAsync(_folder, "drive", packets: 6);
        var service = new TelemetryService(() => new FakeTelemetrySource(), replayFactory: InstantReplay());
        await service.StartReplayAsync(path);
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 6 && service.Replay!.IsFinished));
        var first = service.Replay;

        await service.StartReplayAsync(path);

        Assert.NotSame(first, service.Replay); // a new source: position and finished state start over
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 12)); // 6 + 6: the new replay ran from the beginning
        await service.StopAsync();
    }

    // ---- Failures -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_recording_becomes_a_fault_not_an_exception_and_names_the_replay_mode()
    {
        var service = new TelemetryService(() => new FakeTelemetrySource(), replayFactory: InstantReplay());

        await service.StartReplayAsync(Path.Combine(_folder, "gone.fh6rec"));

        Assert.True(await Wait.UntilAsync(() => service.Fault is not null));
        Assert.IsAssignableFrom<IOException>(service.Fault);
        Assert.Equal(TelemetrySourceMode.Replay, service.Mode); // the shell tells "recording failed" from "port busy" by this
        Assert.False(service.IsRunning);
        Assert.False(service.IsReplaying);
    }

    [Fact]
    public async Task A_file_that_is_not_a_recording_becomes_an_invalid_recording_fault()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "junk.fh6rec");
        await File.WriteAllTextAsync(path, "definitely not a recording");
        var service = new TelemetryService(() => new FakeTelemetrySource(), replayFactory: InstantReplay());

        await service.StartReplayAsync(path);

        Assert.True(await Wait.UntilAsync(() => service.Fault is not null));
        Assert.IsType<InvalidRecordingException>(service.Fault);
    }

    [Fact]
    public async Task A_later_live_start_clears_a_replay_fault()
    {
        var service = new TelemetryService(() => new FakeTelemetrySource(), replayFactory: InstantReplay());
        await service.StartReplayAsync(Path.Combine(_folder, "gone.fh6rec"));
        Assert.True(await Wait.UntilAsync(() => service.Fault is not null));

        await service.StartAsync();

        Assert.Null(service.Fault);
        Assert.Equal(TelemetrySourceMode.Live, service.Mode);
        Assert.True(service.IsRunning);
        await service.StopAsync();
    }

    // ---- Player wiring: hold at the end, restart ----------------------------------------------------------

    [Fact]
    public async Task At_the_end_the_replay_holds_as_finished_and_a_restart_plays_it_again_in_the_same_session()
    {
        var path = await RecordingFiles.WriteAsync(_folder, "drive", packets: 8);
        var service = new TelemetryService(() => new FakeTelemetrySource(), replayFactory: InstantReplay());
        await service.StartReplayAsync(path);

        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 8 && service.Replay!.IsFinished));
        Assert.True(service.IsRunning); // HoldAtEnd: finished is a state of the replay, not the end of the service
        Assert.True(service.IsReplaying);

        service.Replay!.SeekToStart();

        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 16));
        Assert.True(await Wait.UntilAsync(() => service.Replay!.IsFinished));
        await service.StopAsync();
    }

    [Fact]
    public async Task Pause_and_resume_gate_the_packets_the_service_receives()
    {
        var path = await RecordingFiles.WriteAsync(_folder, "drive", packets: 5);
        var service = new TelemetryService(() => new FakeTelemetrySource(), replayFactory: InstantReplay(pausedAtStart: true));
        await service.StartReplayAsync(path);

        await Task.Delay(100);
        Assert.Equal(0, service.ReceivedCount); // paused before the first record

        service.Replay!.Resume();

        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 5));
        await service.StopAsync();
    }

    [Fact]
    public async Task A_replay_started_without_a_path_is_refused()
    {
        var service = new TelemetryService(() => new FakeTelemetrySource());

        await Assert.ThrowsAsync<ArgumentException>(() => service.StartReplayAsync(""));
    }
}
