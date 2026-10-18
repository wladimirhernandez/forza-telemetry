using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core.Recording;

namespace ForzaTelemetry.App.Tests;

/// <summary>The Sessions page: recording, the library, rename and delete, and the player, on a temp folder with real files.</summary>
public sealed class SessionsViewModelTests
{
    private sealed class FakeHost : ISessionsHost
    {
        public List<(string Path, string Label)> Played { get; } = [];

        public int Stops { get; private set; }

        public int Restarts { get; private set; }

        public bool IsBusy { get; set; }

        public Task RestartReplayAsync()
        {
            Restarts++;
            return Task.CompletedTask;
        }

        public Task PlayAsync(string path, string label)
        {
            Played.Add((path, label));
            return Task.CompletedTask;
        }

        public Task StopReplayAsync()
        {
            Stops++;
            return Task.CompletedTask;
        }
    }

    /// <summary>A host whose restart is the real shell's, with a page tick landing right after the new source starts.</summary>
    private sealed class TickingHost(SessionsRig rig) : ISessionsHost
    {
        public bool IsBusy => false;

        public async Task RestartReplayAsync()
        {
            await rig.Shell.RestartReplayAsync();
            rig.Sessions.Refresh(); // the tick notices the new source before the page continues
        }

        public Task PlayAsync(string path, string label) => rig.Shell.PlayAsync(path, label);

        public Task StopReplayAsync() => rig.Shell.StopReplayAsync();
    }

    // ---- Record: when it is possible ----------------------------------------------------------------------

    [Fact]
    public void Recording_is_disabled_with_an_explanation_until_the_app_is_listening()
    {
        using var rig = new SessionsRig();
        rig.Sessions.Refresh();

        Assert.False(rig.Sessions.ToggleRecordingCommand.CanExecute(null));
        Assert.Equal(SessionsStrings.SessionsRecordNeedsLiveTooltip, rig.Sessions.RecordTooltip);
    }

    [Fact]
    public async Task Recording_is_offered_while_listening_live()
    {
        using var rig = new SessionsRig();

        await rig.StartLiveAsync();

        Assert.True(rig.Sessions.IsLive);
        Assert.True(rig.Sessions.ToggleRecordingCommand.CanExecute(null));
        Assert.Equal(SessionsStrings.SessionsRecordTooltip, rig.Sessions.RecordTooltip);
    }

    [Fact]
    public async Task Recording_is_disabled_during_a_replay_and_says_why()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true);
        var path = await rig.AddRecordingAsync("drive");

        await rig.Shell.PlayAsync(path, "label");

        Assert.True(rig.Sessions.IsReplaying);
        Assert.False(rig.Sessions.ToggleRecordingCommand.CanExecute(null));
        Assert.Equal(SessionsStrings.SessionsRecordDuringReplayTooltip, rig.Sessions.RecordTooltip);
        await rig.Shell.StopReplayAsync();
    }

    // ---- Record: the whole cycle --------------------------------------------------------------------------

    [Fact]
    public async Task Record_then_stop_writes_a_valid_file_into_the_folder_and_lists_it_newest_first()
    {
        using var rig = new SessionsRig();
        await rig.AddRecordingAsync("older", startedAt: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await rig.StartLiveAsync();

        await rig.Sessions.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.True(rig.Sessions.IsRecording);
        for (var i = 0; i < 25; i++)
        {
            rig.Recorder.OnDatagram(RecordingFiles.Datagram(car: 4321, sequence: i)); // what the listener's tap does
        }

        rig.Sessions.Refresh();
        Assert.Equal("25", rig.Sessions.RecordingPacketsText);
        Assert.Equal(SessionFormat.Size(24 + (25 * 12) + (25 * 324)), rig.Sessions.RecordingSizeText); // header + records + payloads

        await rig.Sessions.ToggleRecordingCommand.ExecuteAsync(null);
        await rig.Sessions.PendingLibraryRefresh; // the list is rescanned in the background after the stop

        Assert.False(rig.Sessions.IsRecording);
        Assert.Equal(["2026-10-18_01-23-45", "older"], rig.Sessions.Rows.Select(r => r.Name)); // the new one is the newest
        var info = await RecordingFileInfo.ScanFileAsync(rig.Sessions.Rows[0].Path);
        Assert.Equal(25, info.PacketCount);
        Assert.Equal([4321], info.CarOrdinals);
        Assert.Contains("2026-10-18_01-23-45", rig.Sessions.Notice);
        Assert.False(rig.Sessions.NoticeIsError);
    }

    [Fact]
    public async Task The_live_numbers_follow_the_recorder_while_it_runs()
    {
        using var rig = new SessionsRig();
        await rig.StartLiveAsync();
        await rig.Sessions.ToggleRecordingCommand.ExecuteAsync(null);

        rig.Clock.Advance(TimeSpan.FromSeconds(75));
        rig.Sessions.Refresh();

        Assert.Equal("1:15", rig.Sessions.RecordingDurationText);
        Assert.Contains("1:15", rig.Sessions.PillText);
        await rig.Sessions.StopRecordingAsync();
    }

    [Fact]
    public async Task A_recording_that_cannot_start_shows_an_error_and_does_not_pretend_to_record()
    {
        using var rig = new SessionsRig();
        await rig.StartLiveAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.Folder)!);
        await File.WriteAllTextAsync(rig.Folder, "a FILE where the folder should be"); // CreateDirectory cannot succeed

        await rig.Sessions.ToggleRecordingCommand.ExecuteAsync(null);

        Assert.False(rig.Sessions.IsRecording);
        Assert.True(rig.Sessions.HasRecordingError);
        Assert.False(rig.Recorder.IsRecording);
    }

    [Fact]
    public async Task A_recording_ends_by_itself_when_listening_ends_without_the_shell()
    {
        using var rig = new SessionsRig();
        await rig.StartLiveAsync();
        await rig.Sessions.ToggleRecordingCommand.ExecuteAsync(null);
        rig.Recorder.OnDatagram(RecordingFiles.Datagram());

        await rig.Service.StopAsync(); // the loop is gone (a fault looks the same from here); the shell was not asked
        rig.Sessions.Refresh();
        await rig.Sessions.StopRecordingAsync(); // waits for the stop Refresh started
        await rig.Sessions.PendingLibraryRefresh;

        Assert.False(rig.Sessions.IsRecording);
        Assert.False(rig.Recorder.IsRecording);
        Assert.Single(rig.Sessions.Rows); // the file was finished and listed
        Assert.Equal(1, (await RecordingFileInfo.ScanFileAsync(rig.Sessions.Rows[0].Path)).PacketCount);
    }

    [Fact]
    public async Task The_shells_stop_finishes_the_recording_before_the_listener_goes_away()
    {
        using var rig = new SessionsRig();
        await rig.StartLiveAsync();
        await rig.Sessions.ToggleRecordingCommand.ExecuteAsync(null);
        rig.Recorder.OnDatagram(RecordingFiles.Datagram());

        await rig.Shell.StopCommand.ExecuteAsync(null);
        await rig.Sessions.PendingLibraryRefresh;

        Assert.False(rig.Recorder.IsRecording);
        Assert.False(rig.Service.IsRunning);
        Assert.Single(rig.Sessions.Rows);
    }

    [Fact]
    public async Task Stopping_a_recording_twice_at_once_is_one_stop()
    {
        using var rig = new SessionsRig();
        await rig.StartLiveAsync();
        await rig.Sessions.ToggleRecordingCommand.ExecuteAsync(null);

        var first = rig.Sessions.StopRecordingAsync();
        var second = rig.Sessions.StopRecordingAsync();
        await Task.WhenAll(first, second);

        Assert.False(rig.Sessions.IsRecording);
        Assert.Single(Directory.GetFiles(rig.Folder));
    }

    // ---- Library ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_empty_library_asks_for_a_first_recording_and_a_full_one_does_not()
    {
        using var rig = new SessionsRig();
        var rows = await rig.LoadLibraryAsync();
        Assert.Empty(rows);
        Assert.True(rig.Sessions.ShowEmpty);
        Assert.False(rig.Sessions.HasRows);

        await rig.AddRecordingAsync("one");
        await rig.LoadLibraryAsync();

        Assert.False(rig.Sessions.ShowEmpty);
        Assert.True(rig.Sessions.HasRows);
    }

    [Fact]
    public async Task A_row_carries_what_the_list_shows()
    {
        using var rig = new SessionsRig();
        await rig.AddRecordingAsync("monza", packets: 31, car: 2211, startedAt: new DateTimeOffset(2026, 10, 17, 18, 5, 0, TimeSpan.Zero));

        var row = Assert.Single(await rig.LoadLibraryAsync());

        Assert.Equal("monza", row.Name);
        Assert.Equal("0:03", row.DurationText); // 31 records 100 ms apart: 3.0 s
        Assert.Equal(SessionFormat.Cars([2211]), row.CarsText);
        Assert.Equal(SessionFormat.Size(new FileInfo(row.Path).Length), row.SizeText);
        Assert.False(row.IsTruncated);
        Assert.False(row.IsUnreadable);
        Assert.True(row.CanModify);
        Assert.Contains("monza", row.Summary);
    }

    [Fact]
    public async Task An_unreadable_file_cannot_be_played_but_can_be_deleted()
    {
        using var rig = new SessionsRig();
        Directory.CreateDirectory(rig.Folder);
        await File.WriteAllTextAsync(Path.Combine(rig.Folder, "junk.fh6rec"), "nope");
        var host = new FakeHost();
        rig.Sessions.Attach(host);

        var row = Assert.Single(await rig.LoadLibraryAsync());

        Assert.True(row.IsUnreadable);
        Assert.False(row.PlayCommand.CanExecute(null));
        Assert.True(row.CanModify);
    }

    [Fact]
    public async Task Open_folder_asks_the_opener_for_the_recordings_folder()
    {
        using var rig = new SessionsRig();

        rig.Sessions.OpenFolderCommand.Execute(null);

        Assert.Equal([rig.Folder], rig.Opener.Opened);
    }

    // ---- Rename -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rename_edits_in_place_validates_while_typing_and_saves_with_enter()
    {
        using var rig = new SessionsRig();
        await rig.AddRecordingAsync("old");
        var row = Assert.Single(await rig.LoadLibraryAsync());

        row.BeginRenameCommand.Execute(null);
        Assert.True(row.IsEditing);
        Assert.Equal("old", row.EditName);
        Assert.False(row.HasEditError);

        row.EditName = "bad/name";
        Assert.True(row.HasEditError);
        Assert.Equal(SessionsStrings.SessionsNameInvalid, row.EditError);
        await row.CommitRenameCommand.ExecuteAsync(null);
        Assert.True(row.IsEditing); // refused: still editing, nothing renamed
        Assert.True(File.Exists(Path.Combine(rig.Folder, "old.fh6rec")));

        row.EditName = "Monza qualifying";
        Assert.False(row.HasEditError);
        await row.CommitRenameCommand.ExecuteAsync(null);

        Assert.Equal(["Monza qualifying"], rig.Sessions.Rows.Select(r => r.Name));
        Assert.True(File.Exists(Path.Combine(rig.Folder, "Monza qualifying.fh6rec")));
        Assert.False(File.Exists(Path.Combine(rig.Folder, "old.fh6rec")));
    }

    [Fact]
    public async Task Rename_to_a_taken_name_says_so_and_changes_nothing()
    {
        using var rig = new SessionsRig();
        await rig.AddRecordingAsync("first");
        await rig.AddRecordingAsync("second");
        var rows = await rig.LoadLibraryAsync();
        var row = rows.Single(r => r.Name == "first");

        row.BeginRenameCommand.Execute(null);
        row.EditName = "second";
        await row.CommitRenameCommand.ExecuteAsync(null);

        Assert.True(row.IsEditing);
        Assert.Equal(SessionsStrings.SessionsNameExists, row.EditError);
        Assert.Equal(2, Directory.GetFiles(rig.Folder).Length);
    }

    [Fact]
    public async Task Escape_cancels_the_edit_and_leaves_the_name()
    {
        using var rig = new SessionsRig();
        await rig.AddRecordingAsync("keep");
        var row = Assert.Single(await rig.LoadLibraryAsync());

        row.BeginRenameCommand.Execute(null);
        row.EditName = "something else";
        row.CancelRenameCommand.Execute(null);

        Assert.False(row.IsEditing);
        Assert.True(File.Exists(Path.Combine(rig.Folder, "keep.fh6rec")));
    }

    [Fact]
    public async Task Saving_an_unchanged_name_just_ends_the_edit()
    {
        using var rig = new SessionsRig();
        await rig.AddRecordingAsync("same");
        var row = Assert.Single(await rig.LoadLibraryAsync());

        row.BeginRenameCommand.Execute(null);
        await row.CommitRenameCommand.ExecuteAsync(null);

        Assert.False(row.IsEditing);
        Assert.False(row.HasEditError);
    }

    [Fact]
    public async Task A_library_reload_does_not_rebuild_the_list_under_a_name_being_typed()
    {
        using var rig = new SessionsRig();
        await rig.AddRecordingAsync("a");
        var row = Assert.Single(await rig.LoadLibraryAsync());
        row.BeginRenameCommand.Execute(null);
        row.EditName = "half typed";
        await rig.AddRecordingAsync("b");

        await rig.Sessions.RefreshLibraryAsync();

        Assert.Same(row, Assert.Single(rig.Sessions.Rows));
        Assert.Equal("half typed", row.EditName);
    }

    // ---- Delete -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_asks_first_and_removes_the_file_when_confirmed()
    {
        using var rig = new SessionsRig(confirmDelete: true);
        var path = await rig.AddRecordingAsync("doomed");
        var row = Assert.Single(await rig.LoadLibraryAsync());

        await row.DeleteCommand.ExecuteAsync(null);

        var asked = Assert.Single(rig.Confirmation.Asked);
        Assert.Contains("doomed", asked.Message);
        Assert.Equal(SessionsStrings.SessionsDeleteConfirmButton, asked.Confirm);
        Assert.False(File.Exists(path));
        Assert.Empty(rig.Sessions.Rows);
        Assert.True(rig.Sessions.ShowEmpty);
    }

    [Fact]
    public async Task Delete_does_nothing_when_the_user_declines()
    {
        using var rig = new SessionsRig(confirmDelete: false);
        var path = await rig.AddRecordingAsync("kept");
        var row = Assert.Single(await rig.LoadLibraryAsync());

        await row.DeleteCommand.ExecuteAsync(null);

        Assert.Single(rig.Confirmation.Asked);
        Assert.True(File.Exists(path));
        Assert.Single(rig.Sessions.Rows);
    }

    [Fact]
    public async Task Delete_of_a_file_in_use_shows_an_error_notice_and_keeps_the_row()
    {
        using var rig = new SessionsRig();
        var path = await rig.AddRecordingAsync("busy");
        var row = Assert.Single(await rig.LoadLibraryAsync());
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        await row.DeleteCommand.ExecuteAsync(null);

        Assert.True(rig.Sessions.NoticeIsError);
        Assert.Contains("busy", rig.Sessions.Notice);
        Assert.True(File.Exists(path));
    }

    // ---- Play ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Play_asks_the_host_with_the_path_and_the_recordings_start_as_the_label()
    {
        using var rig = new SessionsRig();
        var path = await rig.AddRecordingAsync("drive", startedAt: new DateTimeOffset(2026, 10, 18, 1, 23, 45, TimeSpan.Zero));
        var host = new FakeHost();
        rig.Sessions.Attach(host);
        var row = Assert.Single(await rig.LoadLibraryAsync());

        await row.PlayCommand.ExecuteAsync(null);

        var played = Assert.Single(host.Played);
        Assert.Equal(path, played.Path);
        Assert.Equal(row.Item.StartedAt.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), played.Label);
    }

    // ---- Player -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_player_shows_a_paused_replay_and_resumes_it()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true);
        var path = await rig.AddRecordingAsync("drive", packets: 20);
        await rig.Shell.PlayAsync(path, "label");
        rig.Sessions.Refresh();

        Assert.True(rig.Sessions.IsReplaying);
        Assert.True(rig.Sessions.IsPaused);
        Assert.Equal(SessionsStrings.SessionsPaused, rig.Sessions.PlayerStateText);
        Assert.Equal(SessionsStrings.SessionsResume, rig.Sessions.PlayPauseLabel);
        Assert.False(rig.Sessions.IsShowingPause);
        Assert.Equal("drive", rig.Sessions.PlayerTitle);

        rig.Sessions.PlayPauseCommand.Execute(null);

        Assert.False(rig.Sessions.IsPaused);
        Assert.Equal(SessionsStrings.SessionsPause, rig.Sessions.PlayPauseLabel);
        Assert.True(rig.Sessions.IsShowingPause);
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task Pausing_a_running_replay_holds_it()
    {
        using var rig = new SessionsRig(replaySpeed: ReplaySpeed.Normal);
        var path = await rig.AddRecordingAsync("drive", packets: 20);
        await rig.Shell.PlayAsync(path, "label");
        rig.Sessions.Refresh();

        rig.Sessions.PlayPauseCommand.Execute(null);

        Assert.True(rig.Service.Replay!.IsPaused);
        Assert.Equal(SessionsStrings.SessionsPaused, rig.Sessions.PlayerStateText);
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task Choosing_a_speed_forwards_it_to_the_running_replay_and_a_new_replay_starts_at_one_times()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true, replaySpeed: ReplaySpeed.Normal);
        var path = await rig.AddRecordingAsync("drive", packets: 20);
        await rig.Shell.PlayAsync(path, "label");
        rig.Sessions.Refresh();
        Assert.Equal(["0.5×", "1×", "2×", "4×"], rig.Sessions.SpeedOptions.Select(o => o.Label).Select(l => l.Replace(',', '.')));

        rig.Sessions.SelectedSpeed = ReplaySpeed.Quadruple;

        Assert.Equal(ReplaySpeed.Quadruple, rig.Service.Replay!.Speed);

        await rig.Shell.PlayAsync(path, "label"); // a fresh replay: the source starts at 1x, and the selector follows
        rig.Sessions.Refresh();

        Assert.Equal(ReplaySpeed.Normal, rig.Service.Replay!.Speed);
        Assert.Equal(ReplaySpeed.Normal, rig.Sessions.SelectedSpeed);
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task A_finished_replay_says_finished_shows_full_progress_and_play_restarts_it()
    {
        using var rig = new SessionsRig();
        var path = await rig.AddRecordingAsync("drive", packets: 10);
        await rig.Shell.PlayAsync(path, "label");
        Assert.True(await Wait.UntilAsync(() => rig.Service.Replay!.IsFinished));
        rig.Sessions.Refresh();

        Assert.True(rig.Sessions.IsFinished);
        Assert.Equal(SessionsStrings.SessionsFinished, rig.Sessions.PlayerStateText);
        Assert.Equal(1, rig.Sessions.Progress);
        Assert.Equal(SessionsStrings.SessionsReplayAgain, rig.Sessions.PlayPauseLabel);
        var total = SessionFormat.Duration(rig.Service.Replay!.Duration);
        Assert.Equal($"{total} / {total}", rig.Sessions.PositionText); // finished: the position is the whole recording
        var before = rig.Service.ReceivedCount;

        rig.Sessions.PlayPauseCommand.Execute(null); // "play" on a finished recording means: again, from the start

        Assert.True(await Wait.UntilAsync(() => rig.Service.ReceivedCount == before + 10));
        Assert.True(await Wait.UntilAsync(() => rig.Service.Replay!.IsFinished));
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task Restart_goes_back_to_the_start_and_plays()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true);
        var path = await rig.AddRecordingAsync("drive", packets: 6);
        await rig.Shell.PlayAsync(path, "label");
        rig.Service.Replay!.Resume();
        Assert.True(await Wait.UntilAsync(() => rig.Service.Replay!.IsFinished));
        var before = rig.Service.ReceivedCount;

        await rig.Sessions.RestartCommand.ExecuteAsync(null);
        rig.Service.Replay!.Resume(); // this rig builds every replay paused; the app's own replays start playing

        Assert.True(await Wait.UntilAsync(() => rig.Service.ReceivedCount == before + 6));
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task The_progress_and_position_follow_the_recording()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true);
        var path = await rig.AddRecordingAsync("drive", packets: 101); // 10 s at 100 ms
        await rig.Shell.PlayAsync(path, "label");
        rig.Service.Replay!.Resume();
        Assert.True(await Wait.UntilAsync(() => rig.Service.Replay!.IsFinished));
        rig.Service.Replay.Pause();
        rig.Service.Replay.SeekToStart();
        Assert.True(await Wait.UntilAsync(() => rig.Service.Replay!.Position == TimeSpan.Zero));

        rig.Sessions.Refresh();

        Assert.Equal(0, rig.Sessions.Progress);
        Assert.Equal("0:00 / 0:10", rig.Sessions.PositionText);
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task The_replayed_recording_is_marked_and_cannot_be_renamed_or_deleted_meanwhile()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true);
        var path = await rig.AddRecordingAsync("playing");
        await rig.AddRecordingAsync("idle");
        await rig.LoadLibraryAsync();

        await rig.Shell.PlayAsync(path, "label");
        rig.Sessions.Refresh();

        var playing = rig.Sessions.Rows.Single(r => r.Name == "playing");
        var idle = rig.Sessions.Rows.Single(r => r.Name == "idle");
        Assert.True(playing.IsPlaying);
        Assert.False(playing.CanModify);
        Assert.Equal(SessionsStrings.SessionsInUseTooltip, playing.RenameTooltip);
        Assert.True(idle.CanModify);
        playing.BeginRenameCommand.Execute(null);
        Assert.False(playing.IsEditing); // refused, with the reason in the tooltip

        await rig.Shell.StopReplayAsync();
        rig.Sessions.Refresh();
        Assert.False(playing.IsPlaying);
        Assert.True(playing.CanModify);
    }

    [Fact]
    public async Task A_replay_that_fails_is_reported_on_the_page_once()
    {
        using var rig = new SessionsRig();
        var path = await rig.AddRecordingAsync("drive");
        var row = Assert.Single(await rig.LoadLibraryAsync());
        rig.Sessions.Attach(rig.Shell);
        File.Delete(path); // gone between the listing and the click

        await row.PlayCommand.ExecuteAsync(null);
        Assert.True(await Wait.UntilAsync(() => rig.Service.Fault is not null));
        rig.Sessions.Refresh();

        Assert.True(rig.Sessions.NoticeIsError);
        Assert.Contains("drive", rig.Sessions.Notice);
        var notice = rig.Sessions.Notice;
        rig.Sessions.Notice = "";
        rig.Sessions.Refresh();
        Assert.Equal("", rig.Sessions.Notice); // reported once, not on every tick
        Assert.NotEmpty(notice);
    }

    // ---- Requests the shell drops ------------------------------------------------------------------------

    [Fact]
    public async Task A_play_the_busy_shell_will_drop_leaves_the_notice_and_the_playing_name_alone()
    {
        using var rig = new SessionsRig();
        await rig.AddRecordingAsync("drive");
        var host = new FakeHost { IsBusy = true };
        rig.Sessions.Attach(host);
        var row = Assert.Single(await rig.LoadLibraryAsync());
        rig.Sessions.Notice = "Deleted something earlier";

        await row.PlayCommand.ExecuteAsync(null);

        Assert.Empty(host.Played); // not even asked: the shell would have dropped it
        Assert.Equal("Deleted something earlier", rig.Sessions.Notice); // a message about something else is not erased for nothing

        host.IsBusy = false;
        await row.PlayCommand.ExecuteAsync(null);

        Assert.Single(host.Played);
        Assert.Equal("", rig.Sessions.Notice); // accepted: the message is about the previous action, not this replay
    }

    [Fact]
    public async Task Restart_asks_the_host_for_a_fresh_replay_instead_of_seeking_the_running_one()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true);
        var path = await rig.AddRecordingAsync("drive", packets: 6);
        await rig.Shell.PlayAsync(path, "label");
        var host = new FakeHost();
        rig.Sessions.Attach(host);
        rig.Sessions.Refresh();

        await rig.Sessions.RestartCommand.ExecuteAsync(null);

        Assert.Equal(1, host.Restarts);
        Assert.True(rig.Service.Replay!.IsPaused); // untouched: the host decides what a restart does, not the page
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task A_restart_keeps_the_chosen_speed()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true, replaySpeed: ReplaySpeed.Normal);
        var path = await rig.AddRecordingAsync("drive", packets: 20);
        await rig.Shell.PlayAsync(path, "label");
        rig.Sessions.Refresh();
        rig.Sessions.SelectedSpeed = ReplaySpeed.Double;
        var before = rig.Service.Replay;

        await rig.Sessions.RestartCommand.ExecuteAsync(null);

        Assert.NotSame(before, rig.Service.Replay);
        Assert.Equal(ReplaySpeed.Double, rig.Service.Replay!.Speed);
        Assert.Equal(ReplaySpeed.Double, rig.Sessions.SelectedSpeed);
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task A_restart_applies_the_speed_to_the_new_source_even_when_the_tick_saw_it_first()
    {
        using var rig = new SessionsRig(pauseReplayAtStart: true, replaySpeed: ReplaySpeed.Normal);
        var path = await rig.AddRecordingAsync("drive", packets: 20);
        await rig.Shell.PlayAsync(path, "label");
        rig.Sessions.Refresh();
        rig.Sessions.SelectedSpeed = ReplaySpeed.Quadruple;
        var host = new TickingHost(rig);
        rig.Sessions.Attach(host);

        await rig.Sessions.RestartCommand.ExecuteAsync(null);

        // The tick ran between the host starting the new source and the page getting control back: it reset the selector to 1x.
        // The page puts the chosen speed back on the selector and on the source, right after the host returned.
        Assert.Equal(ReplaySpeed.Quadruple, rig.Service.Replay!.Speed);
        Assert.Equal(ReplaySpeed.Quadruple, rig.Sessions.SelectedSpeed);
        await rig.Shell.StopReplayAsync();
    }

    [Fact]
    public async Task Shutdown_makes_a_library_refresh_do_nothing_and_not_fail()
    {
        using var rig = new SessionsRig();
        await rig.AddRecordingAsync("drive", packets: 3);

        rig.Sessions.Shutdown();
        await rig.Sessions.RefreshLibraryAsync();

        Assert.Empty(rig.Sessions.Rows); // the scan never published into the page
        Assert.False(rig.Sessions.IsLoading);
        await rig.Sessions.PendingLibraryRefresh; // nothing left running, nothing faulted
    }

    [Fact]
    public async Task A_replay_that_dies_leaves_its_row_free_to_rename_and_delete()
    {
        using var rig = new SessionsRig();
        var path = await rig.AddRecordingAsync("drive");
        var row = Assert.Single(await rig.LoadLibraryAsync());
        File.Delete(path); // gone between the listing and the click: the replay's loop dies on its first read

        await rig.Shell.PlayAsync(path, "label");
        Assert.True(await Wait.UntilAsync(() => rig.Service.Fault is not null));
        rig.Sessions.Refresh();

        // Nobody is playing it any more, so the row must not stay marked as in use (it would refuse a rename or delete for
        // as long as the dead replay's state lingered).
        Assert.False(row.IsPlaying);
        Assert.True(row.CanModify);
        await rig.Shell.StopReplayAsync(); // still works, and is a harmless no-op for a replay that is not running
    }
}
