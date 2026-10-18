using System.Globalization;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App.Tests;

/// <summary>
/// The shell's state machine across live, replay and idle: what the top bar says, which commands exist, how the two modes exclude
/// each other and what a replay does to the engines.
/// </summary>
public sealed class ShellReplayTests
{
    private const string Label = "2026-10-18 01:23";

    // ---- Idle -> replay -> idle ---------------------------------------------------------------------------

    [Fact]
    public async Task Playing_from_idle_shows_replay_mode_in_the_top_bar()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true);
        var path = await rig.AddRecordingAsync("drive");

        await rig.Shell.PlayAsync(path, Label);

        Assert.True(rig.Shell.IsReplaying);
        Assert.True(rig.Shell.IsRunning);
        Assert.Equal(ConnectionState.Replaying, rig.Shell.ConnectionState);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.StatusReplaying, Label), rig.Shell.Status);
        Assert.Contains(Label, rig.Shell.Status);
    }

    [Fact]
    public async Task Stopping_a_replay_returns_to_idle_and_live_listening_must_be_started_again()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true);
        var path = await rig.AddRecordingAsync("drive");
        await rig.Shell.PlayAsync(path, Label);

        await rig.Shell.ToggleListeningCommand.ExecuteAsync(null); // the same top-bar button: it says "Stop replay" now

        Assert.False(rig.Shell.IsReplaying);
        Assert.False(rig.Shell.IsRunning);
        Assert.False(rig.Service.IsRunning);
        Assert.Equal(ConnectionState.Stopped, rig.Shell.ConnectionState);
        Assert.Equal(Strings.StatusStopped, rig.Shell.Status);
        Assert.Null(rig.Source); // nothing started listening behind the user's back

        await rig.Shell.StartCommand.ExecuteAsync(null);
        Assert.True(rig.Service.IsRunning);
        Assert.False(rig.Shell.IsReplaying);
        Assert.Equal(TelemetrySourceMode.Live, rig.Service.Mode);
        await rig.Shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task While_replaying_Start_is_not_available_only_Stop()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true);
        var path = await rig.AddRecordingAsync("drive");

        await rig.Shell.PlayAsync(path, Label);

        Assert.False(rig.Shell.StartCommand.CanExecute(null));
        Assert.True(rig.Shell.StopCommand.CanExecute(null));
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task Stopping_a_replay_through_the_page_is_a_no_op_when_nothing_is_replaying()
    {
        using var rig = new SessionsRig();
        await rig.StartLiveAsync();

        await rig.Shell.StopReplayAsync(); // the live listener is not a replay: it must be left alone

        Assert.True(rig.Service.IsRunning);
        Assert.Equal(TelemetrySourceMode.Live, rig.Service.Mode);
        await rig.Shell.StopCommand.ExecuteAsync(null);
    }

    // ---- Live <-> replay ----------------------------------------------------------------------------------

    [Fact]
    public async Task Playing_while_listening_live_stops_the_live_listener_cleanly_first()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true);
        var path = await rig.AddRecordingAsync("drive");
        await rig.StartLiveAsync();
        var live = rig.Source!;

        await rig.Shell.PlayAsync(path, Label);

        Assert.True(live.IsDisposed); // its socket would be released
        Assert.True(rig.Shell.IsReplaying);
        Assert.Equal(TelemetrySourceMode.Replay, rig.Service.Mode);
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task Playing_while_recording_ends_the_recording_first_and_keeps_the_file()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true);
        var path = await rig.AddRecordingAsync("drive");
        await rig.StartLiveAsync();
        await rig.Sessions.ToggleRecordingCommand.ExecuteAsync(null);
        rig.Recorder.OnDatagram(RecordingFiles.Datagram());

        await rig.Shell.PlayAsync(path, Label);

        Assert.False(rig.Recorder.IsRecording); // recording is not possible during a replay
        Assert.False(rig.Sessions.IsRecording);
        Assert.Equal(2, Directory.GetFiles(rig.Folder).Length); // the drive, and what had been recorded so far
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task Playing_one_recording_while_another_plays_replaces_it()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true);
        var first = await rig.AddRecordingAsync("first");
        var second = await rig.AddRecordingAsync("second");
        await rig.Shell.PlayAsync(first, Label);
        var firstReplay = rig.Service.Replay;

        await rig.Shell.PlayAsync(second, "other label");

        Assert.NotSame(firstReplay, rig.Service.Replay);
        Assert.Equal(second, rig.Service.ReplayPath);
        Assert.Contains("other label", rig.Shell.Status);
        await rig.Shell.StopReplayAsync();
    }

    // ---- A replay is a fresh session ----------------------------------------------------------------------

    [Fact]
    public async Task A_replay_starts_with_the_engines_reset_like_a_fresh_live_start()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true);
        var path = await rig.AddRecordingAsync("drive");
        for (var i = 0; i < 5; i++)
        {
            foreach (var observer in rig.Engines.Observers)
            {
                observer.OnPacket(TestPackets.Sample(speedMetersPerSecond: 40, carOrdinal: 9, performanceIndex: 500) with { TimestampMS = (uint)(1000 + i) });
            }
        }

        Assert.True(rig.Engines.History.Count > 0);

        await rig.Shell.PlayAsync(path, Label);

        Assert.Equal(0, rig.Engines.History.Count); // the previous session's samples are gone before the first replayed packet
        Assert.Equal(0, rig.Engines.TopSpeed.TopSpeedMs);
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task A_replay_drives_the_dashboard_through_the_normal_refresh()
    {
        using var rig = new SessionsRig(replaySpeed: ForzaTelemetry.Core.Recording.ReplaySpeed.Max);
        var path = await rig.AddRecordingAsync("drive", packets: 10, car: 4321);

        await rig.Shell.PlayAsync(path, Label);
        Assert.True(await Wait.UntilAsync(() => rig.Service.ReceivedCount == 10));
        rig.Shell.Refresh();

        Assert.Equal(30 * 3.6f, rig.Shell.Dashboard.SpeedKmh, precision: 2);
        Assert.Equal(4321, rig.Service.Latest!.CarOrdinal);
        Assert.Equal(ConnectionState.Replaying, rig.Shell.ConnectionState);
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task A_paused_or_finished_replay_never_turns_into_no_data()
    {
        using var rig = new SessionsRig(replaySpeed: ForzaTelemetry.Core.Recording.ReplaySpeed.Max);
        var path = await rig.AddRecordingAsync("drive", packets: 10);
        await rig.Shell.PlayAsync(path, Label);
        Assert.True(await Wait.UntilAsync(() => rig.Service.Replay!.IsFinished));
        rig.Shell.Refresh();

        rig.Clock.Advance(TimeSpan.FromSeconds(30)); // silence that would mean "the game went quiet" when live
        rig.Shell.Refresh();

        Assert.Equal(ConnectionState.Replaying, rig.Shell.ConnectionState);
        Assert.NotNull(rig.Service.Latest);
        Assert.NotEqual(0, rig.Shell.Dashboard.SpeedKmh); // the last packet stays on screen
        await rig.Shell.StopReplayAsync();
    }

    // ---- Failures and settings ----------------------------------------------------------------------------

    [Fact]
    public async Task A_recording_that_cannot_be_played_is_reported_as_such_not_as_a_network_problem()
    {
        using var rig = new SessionsRig();
        var path = Path.Combine(rig.Folder, "gone.fh6rec");

        await rig.Shell.PlayAsync(path, Label);
        Assert.True(await Wait.UntilAsync(() => rig.Service.Fault is not null));
        rig.Shell.Refresh();

        Assert.Equal(ConnectionState.Error, rig.Shell.ConnectionState);
        Assert.StartsWith(Strings.ErrorReplayFailed.Split('{')[0], rig.Shell.Status);
        Assert.False(rig.Shell.IsReplaying);
        Assert.False(rig.Shell.IsRunning);
    }

    [Fact]
    public async Task A_port_change_during_a_replay_leaves_the_replay_alone_and_does_not_start_listening()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true);
        var path = await rig.AddRecordingAsync("drive");
        await rig.Shell.PlayAsync(path, Label);

        rig.Settings.Update(s => s with { UdpPort = 5400 });
        await Task.Delay(200);

        Assert.True(rig.Shell.IsReplaying);
        Assert.Equal(TelemetrySourceMode.Replay, rig.Service.Mode);
        Assert.Null(rig.Source); // no live listener was built
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task A_port_change_after_a_failed_replay_does_not_start_live_listening()
    {
        using var rig = new SessionsRig();
        await rig.Shell.PlayAsync(Path.Combine(rig.Folder, "gone.fh6rec"), Label);
        Assert.True(await Wait.UntilAsync(() => rig.Service.Fault is not null));

        rig.Settings.Update(s => s with { UdpPort = 5401 });
        await Task.Delay(200);

        Assert.False(rig.Service.IsRunning);
        Assert.Null(rig.Source);
    }

    [Fact]
    public void Without_a_sessions_page_that_tab_is_the_placeholder_and_nothing_breaks()
    {
        var service = new TelemetryService(() => new FakeTelemetrySource());
        var shell = new ShellViewModel(service, TimeProvider.System);

        Assert.IsType<PlaceholderPageViewModel>(shell.Tabs.Single(t => t.Id == PageId.Sessions).Page);
        Assert.Null(shell.Sessions);
        shell.Refresh();
    }

    [Fact]
    public void With_a_sessions_page_that_tab_shows_it_and_the_shell_is_its_host()
    {
        using var rig = new SessionsRig();

        Assert.Same(rig.Sessions, rig.Shell.Tabs.Single(t => t.Id == PageId.Sessions).Page);
        Assert.Same(rig.Sessions, rig.Shell.Sessions);
    }
}
