using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core.Recording;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App;

/// <summary>
/// What the Sessions page needs from the shell to start and stop a replay. The shell owns the one lock that serializes every
/// command that changes what the service is doing (Start, Stop, port moves), so a replay must go through it too instead of
/// calling the service behind its back. An interface so this view model never references the shell (which holds it).
/// </summary>
public interface ISessionsHost
{
    /// <summary>Plays the recording at <paramref name="path"/>, replacing live listening or another replay.</summary>
    /// <param name="path">The recording file.</param>
    /// <param name="label">What the top bar says it is replaying (the recording's start time).</param>
    Task PlayAsync(string path, string label);

    /// <summary>Ends the replay and returns to idle (the user presses Start to listen to the game again).</summary>
    Task StopReplayAsync();

    /// <summary>
    /// Plays the recording being replayed again from its first record, as a fresh session (empty engines and screens): a restart
    /// and "play again" are not a seek, because everything the engines accumulated would be counted twice.
    /// </summary>
    Task RestartReplayAsync();

    /// <summary>
    /// A command (start, stop, replay, port move) is running, so one that arrives now is dropped. Asked BEFORE acting on a user's
    /// request, so the page does not clear a message or forget a name for a request that was never going to be carried out.
    /// </summary>
    bool IsBusy { get; }
}

/// <summary>One recording in the library list: its display values and the commands on it (play, rename, delete).</summary>
/// <remarks>
/// The commands live on the row, not on the page, so a button binds simply to <c>{Binding PlayCommand}</c>: no
/// <c>RelativeSource</c> climbing to the page's view model, which does not work from a <c>KeyBinding</c> (the Enter/Esc keys of
/// the rename box) at all.
/// </remarks>
public sealed partial class RecordingRowViewModel : ObservableObject
{
    private readonly SessionsViewModel _owner;

    internal RecordingRowViewModel(RecordingItem item, SessionsViewModel owner, bool isPlaying)
    {
        Item = item;
        _owner = owner;
        _isPlaying = isPlaying;
        StartedText = SessionFormat.Started(item.StartedAt);
        DurationText = item.IsReadable ? SessionFormat.Duration(item.Duration) : "-";
        SizeText = SessionFormat.Size(item.SizeBytes);
    }

    /// <summary>The underlying recording.</summary>
    public RecordingItem Item { get; }

    /// <summary>Full path of the file.</summary>
    public string Path => Item.Path;

    /// <summary>File name without extension: what the user sees and renames.</summary>
    public string Name => Item.Name;

    /// <summary>When it was recorded, in the user's format.</summary>
    public string StartedText { get; }

    /// <summary>Length, <c>m:ss</c>.</summary>
    public string DurationText { get; }

    /// <summary>File size.</summary>
    public string SizeText { get; }

    /// <summary>Cars seen, for example <c>Car #1234</c> (or the player's nickname). Computed, so a rename shows without rebuilding the list.</summary>
    public string CarsText => Item.IsReadable ? SessionFormat.Cars(Item.CarOrdinals, _owner.CarNames) : "-";

    /// <summary>A car was renamed: the cars text, and the sentence that quotes it, are read again.</summary>
    internal void OnCarNamesChanged()
    {
        OnPropertyChanged(nameof(CarsText));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>The app closed while this was being recorded.</summary>
    public bool IsTruncated => Item.IsReadable && Item.IsTruncated;

    /// <summary>Not a recording this version can read: it cannot be played.</summary>
    public bool IsUnreadable => !Item.IsReadable;

    /// <summary>The whole row as one sentence for screen readers (a row of five values means little read cell by cell).</summary>
    public string Summary => string.Format(
        CultureInfo.CurrentCulture, SessionsStrings.SessionsRowName, Name, StartedText, DurationText, CarsText);

    public string PlayName => string.Format(CultureInfo.CurrentCulture, SessionsStrings.SessionsPlayNamed, Name);

    public string RenameName => string.Format(CultureInfo.CurrentCulture, SessionsStrings.SessionsRenameNamed, Name);

    public string DeleteName => string.Format(CultureInfo.CurrentCulture, SessionsStrings.SessionsDeleteNamed, Name);

    public string NameBoxName => string.Format(CultureInfo.CurrentCulture, SessionsStrings.SessionsNameBoxName, Name);

    /// <summary>This is the recording being replayed now (the list marks it, and refuses to rename or delete it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanModify))]
    [NotifyPropertyChangedFor(nameof(RenameTooltip))]
    [NotifyPropertyChangedFor(nameof(DeleteTooltip))]
    private bool _isPlaying;

    /// <summary>
    /// This recording is being converted to CSV right now. Asked of the page by path, not stored on the row: the list is rebuilt
    /// whenever the library is re-read, and a new row object for the same file must still know it is being read.
    /// </summary>
    public bool IsExporting => _owner.IsExportingFile(Path);

    /// <summary>
    /// Rename and delete are only offered while nothing has the file open. A replay or a CSV export streams the file from disk, and
    /// Windows refuses to rename or delete an open file (and a renamed file would pull the path from under the export), so the
    /// honest thing is to say so up front instead of failing afterwards.
    /// </summary>
    public bool CanModify => !IsPlaying && !IsExporting;

    public string RenameTooltip => InUseTooltip ?? SessionsStrings.SessionsRename;

    public string DeleteTooltip => InUseTooltip ?? SessionsStrings.SessionsDelete;

    private string? InUseTooltip =>
        IsPlaying ? SessionsStrings.SessionsInUseTooltip : IsExporting ? SessionsStrings.SessionsExportingTooltip : null;

    /// <summary>The name is being edited in place.</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>The text in the rename box. Every keystroke is validated, so the message appears while typing.</summary>
    [ObservableProperty]
    private string _editName = "";

    /// <summary>Why the typed name cannot be used; empty when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditError))]
    private string _editError = "";

    public bool HasEditError => EditError.Length > 0;

    partial void OnEditNameChanged(string value) =>
        EditError = SessionsViewModel.Describe(RecordingNameValidator.Validate(RecordingNameValidator.Normalize(value)));

    [RelayCommand(CanExecute = nameof(IsReadable))]
    private Task PlayAsync() => _owner.PlayAsync(this);

    private bool IsReadable() => Item.IsReadable;

    [RelayCommand]
    private void BeginRename() => _owner.BeginRename(this);

    [RelayCommand]
    private Task CommitRenameAsync() => _owner.CommitRenameAsync(this);

    [RelayCommand]
    private void CancelRename() => _owner.CancelRename(this);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(this);

    /// <summary>This recording can be converted to CSV now: it is readable, there is a dialog to ask with, and no other export is running.</summary>
    public bool CanExport => Item.IsReadable && _owner.CanExport;

    /// <summary>Says what the button does, or why it is disabled while another export runs.</summary>
    public string ExportTooltip => _owner.IsExporting ? ExportStrings.ExportBusyTooltip : ExportStrings.ExportRecordingTooltip;

    public string ExportName => string.Format(CultureInfo.CurrentCulture, ExportStrings.ExportRecordingNamed, Name);

    /// <summary>An export started or ended somewhere on the page: the button is enabled or disabled again.</summary>
    internal void OnExportStateChanged()
    {
        ExportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(ExportTooltip));
        OnPropertyChanged(nameof(IsExporting));
        OnPropertyChanged(nameof(CanModify));
        OnPropertyChanged(nameof(RenameTooltip));
        OnPropertyChanged(nameof(DeleteTooltip));
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportAsync() => _owner.ExportAsync(this);
}

/// <summary>
/// The Sessions screen: the record panel, the player bar while a recording is replaying, and the library of recordings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recording</b> is the <see cref="SessionRecorder"/> fed by the UDP listener's raw-datagram tap (see
/// <c>UdpTelemetryListener</c>), so it only has something to record while the app is listening LIVE. It is therefore offered
/// only then, and it ends by itself when listening ends (Stop, a fault, a port move): a recording that kept "running" with no
/// source would just grow a silent tail. The shell calls <see cref="StopRecordingAsync"/> BEFORE it stops the listener, so the
/// last packets are on disk; <see cref="Refresh"/> is the safety net for the endings the shell does not initiate (a fault).
/// </para>
/// <para>
/// <b>Replay</b> drives the whole app: <see cref="ISessionsHost.PlayAsync"/> swaps the live source for the recording inside
/// <see cref="TelemetryService"/>, so every other tab shows the replay. This class only shows the player bar and forwards its
/// buttons to the playback controls of the running <see cref="ReplayTelemetrySource"/>. Everything it displays is read from
/// the service in <see cref="Refresh"/> (called ~20 times a second by the shell), never pushed from the receive thread.
/// </para>
/// <para>
/// <b>The library</b> is read off the UI thread (<see cref="RecordingLibrary.ScanAsync"/>) and re-read whenever the page opens
/// and after anything that changes the folder. A load never replaces the list while a name is being edited, which would throw
/// the user's typing away.
/// </para>
/// </remarks>
public sealed partial class SessionsViewModel : ObservableObject
{
    // The bytes a recording file has beyond its payloads: one header, plus one record header per datagram. Added to the
    // recorder's payload counter so the live "size" is the size the file will have, not a payload-only underestimate.
    private static long FileSizeOf(long packets, long payloadBytes) =>
        RecordingFormat.HeaderSize + (packets * RecordingFormat.RecordHeaderSize) + payloadBytes;

    private readonly TelemetryService _service;
    private readonly SessionRecorder _recorder;
    private readonly RecordingLibrary _library;
    private readonly IConfirmationService _confirmation;
    private readonly IFolderOpener? _folderOpener;
    private readonly ILogger? _logger;
    private readonly IFileSaveDialog? _saveDialog;
    private CancellationTokenSource? _exportCts;

    private ISessionsHost? _host;
    private string? _recordingPath;
    private Task? _stopTask;
    private int _loadVersion;
    private ReplayTelemetrySource? _trackedReplay;
    private string? _markedPlayingPath;
    private string _lastPlayedName = "";
    private Exception? _reportedPlaybackFault;
    private bool _syncingSpeed;

    // Set by Shutdown: the app is closing. A library scan still in flight is cancelled and its continuation (which would write
    // properties on a dispatcher that is going away) does nothing.
    private readonly CancellationTokenSource _shutdown = new();
    private bool _shuttingDown;

    private Task _libraryRefresh = Task.CompletedTask;

    /// <summary>The player's car nicknames; null when the page was built without them.</summary>
    internal CarNameStore? CarNames { get; }

    /// <param name="service">The telemetry service (live or replay), read in <see cref="Refresh"/>.</param>
    /// <param name="recorder">The recorder the listener feeds with raw datagrams.</param>
    /// <param name="library">The recordings folder.</param>
    /// <param name="confirmation">Asks before a recording is deleted; optional (deleting then needs no confirmation, for tests).</param>
    /// <param name="folderOpener">Opens the recordings folder in Explorer; optional.</param>
    /// <param name="logger">Receives recording failures; optional.</param>
    /// <param name="carNames">The player's car nicknames, shown in the Cars column; optional (cars then show as "Car #1234").</param>
    /// <param name="saveDialog">Asks where to save a recording as CSV; optional (the Export buttons are then disabled).</param>
    public SessionsViewModel(
        TelemetryService service,
        SessionRecorder recorder,
        RecordingLibrary library,
        IConfirmationService? confirmation = null,
        IFolderOpener? folderOpener = null,
        ILogger? logger = null,
        CarNameStore? carNames = null,
        IFileSaveDialog? saveDialog = null)
    {
        _saveDialog = saveDialog;
        CarNames = carNames;
        if (carNames is not null)
        {
            carNames.Changed += (_, _) =>
            {
                foreach (var row in Rows)
                {
                    row.OnCarNamesChanged();
                }
            };
        }

        _service = service;
        _recorder = recorder;
        _library = library;
        _confirmation = confirmation ?? new AlwaysConfirm();
        _folderOpener = folderOpener;
        _logger = logger;

        SpeedOptions =
        [
            new(ReplaySpeed.Half, SessionsStrings.SessionsSpeedHalf),
            new(ReplaySpeed.Normal, SessionsStrings.SessionsSpeedNormal),
            new(ReplaySpeed.Double, SessionsStrings.SessionsSpeedDouble),
            new(ReplaySpeed.Quadruple, SessionsStrings.SessionsSpeedQuadruple)
        ];
    }

    private sealed class AlwaysConfirm : IConfirmationService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText) => Task.FromResult(true);
    }

    /// <summary>Connects the shell that starts and stops replays (done by the shell's constructor).</summary>
    public void Attach(ISessionsHost host) => _host = host;

    // ---- Record panel -------------------------------------------------------------------------------------

    /// <summary>The app is listening to the game (not replaying): the only state in which there is anything to record.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    [NotifyPropertyChangedFor(nameof(RecordTooltip))]
    private bool _isLive;

    /// <summary>A recording is being written.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    [NotifyPropertyChangedFor(nameof(RecordTooltip))]
    [NotifyPropertyChangedFor(nameof(PillText))]
    private bool _isRecording;

    // A start or stop is in flight: the button is disabled so a double click cannot start two.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    private bool _isRecordingBusy;

    /// <summary>Live length of the recording, <c>m:ss</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PillText))]
    private string _recordingDurationText = SessionFormat.Duration(TimeSpan.Zero);

    /// <summary>Live file size.</summary>
    [ObservableProperty]
    private string _recordingSizeText = SessionFormat.Size(0);

    /// <summary>Datagrams stored so far.</summary>
    [ObservableProperty]
    private string _recordingPacketsText = "0";

    /// <summary>The warning that datagrams were dropped (the disk could not keep up); empty when none were.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDroppedWarning))]
    private string _droppedWarning = "";

    public bool HasDroppedWarning => DroppedWarning.Length > 0;

    /// <summary>A recording problem (it could not start, or the writer failed); empty when fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecordingError))]
    private string _recordingError = "";

    public bool HasRecordingError => RecordingError.Length > 0;

    /// <summary>A short message about what just happened (saved, deleted, could not delete); <see cref="NoticeIsError"/> says how to show it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = "";

    [ObservableProperty]
    private bool _noticeIsError;

    public bool HasNotice => Notice.Length > 0;

    /// <summary>The top bar's recording pill, for example <c>Recording 0:42</c>.</summary>
    public string PillText => string.Format(CultureInfo.CurrentCulture, SessionsStrings.SessionsRecordingPill, RecordingDurationText);

    /// <summary>
    /// What the Record button's tooltip says. Besides explaining the button, it explains WHY a disabled one is disabled, which is
    /// the only place a user can find out (a greyed-out button says nothing).
    /// </summary>
    public string RecordTooltip =>
        IsRecording ? SessionsStrings.SessionsStopRecordingTooltip
        : IsReplaying ? SessionsStrings.SessionsRecordDuringReplayTooltip
        : IsLive ? SessionsStrings.SessionsRecordTooltip
        : SessionsStrings.SessionsRecordNeedsLiveTooltip;

    private bool CanToggleRecording() => !IsRecordingBusy && (IsRecording || (IsLive && !IsReplaying));

    [RelayCommand(CanExecute = nameof(CanToggleRecording))]
    private async Task ToggleRecordingAsync()
    {
        IsRecordingBusy = true;
        try
        {
            if (IsRecording)
            {
                await StopRecordingAsync();
            }
            else
            {
                await StartRecordingAsync();
            }
        }
        finally
        {
            IsRecordingBusy = false;
        }
    }

    private async Task StartRecordingAsync()
    {
        string path;
        try
        {
            path = _library.NextRecordingPath();
            await _recorder.StartAsync(path); // CreateNew: an existing file is never touched
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordingError = string.Format(CultureInfo.CurrentCulture, SessionsStrings.SessionsRecordStartFailed, ex.Message);
            Log(l => AppLog.RecordingStartFailed(l, ex, _library.Folder));
            return;
        }

        _recordingPath = path;
        RecordingError = "";
        DroppedWarning = "";
        Notice = "";
        IsRecording = true;
        Log(l => AppLog.RecordingStarted(l, path));
        UpdateRecordingStats();
    }

    /// <summary>
    /// Stops the recording if one is running and waits until everything queued is on disk (a no-op otherwise). The shell calls
    /// this before it stops the listener. Concurrent callers share one stop.
    /// </summary>
    public Task StopRecordingAsync()
    {
        if (_stopTask is { IsCompleted: false } running)
        {
            return running; // a stop is already in flight: wait for that one
        }

        return _recorder.IsRecording ? _stopTask = StopRecordingCoreAsync(listeningEnded: false) : Task.CompletedTask;
    }

    private async Task StopRecordingCoreAsync(bool listeningEnded)
    {
        var path = _recordingPath;
        Exception? error = null;
        try
        {
            await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            // The recorder rethrows the I/O error that ended its writer. What was written before it is still a file.
            error = ex;
        }

        UpdateRecordingStats();
        IsRecording = false;
        _recordingPath = null;

        var name = path is null ? "" : Path.GetFileNameWithoutExtension(path);
        Log(l => AppLog.RecordingStopped(l, path ?? "", _recorder.DatagramCount, _recorder.BytesRecorded, _recorder.DroppedCount));
        if (error is not null)
        {
            RecordingError = string.Format(CultureInfo.CurrentCulture, SessionsStrings.SessionsRecordWriteError, error.Message);
            Log(l => AppLog.RecordingWriteFailed(l, error, path ?? ""));
        }
        else
        {
            SetNotice(
                string.Format(
                    CultureInfo.CurrentCulture,
                    listeningEnded ? SessionsStrings.SessionsRecordingEndedNotice : SessionsStrings.SessionsSavedNotice,
                    name),
                isError: false);
        }

        // NOT awaited. The stop itself ends here: the file is on disk and the recording state is cleared. Reading the folder
        // again is only about showing the new file in the list, and on a big library it takes a while; awaiting it made every
        // caller of the stop (the shell's Stop, a replay, a port move) wait for a rescan nobody asked for. The list updates when
        // the scan is done.
        _libraryRefresh = RefreshLibraryQuietlyAsync();
    }

    /// <summary>
    /// The rescan the last recording stop started in the background (completed when none is running). The page never waits for
    /// it; it exists so a test, or a shutdown, can wait for the scan to finish instead of racing the files it is reading.
    /// </summary>
    internal Task PendingLibraryRefresh => _libraryRefresh;

    /// <summary>
    /// The app is closing: cancels the library scan the last recording stop left running, and makes its continuation inert, so it
    /// never touches this page (or a closing dispatcher) afterwards. Cancelling, not waiting: the continuation would need the
    /// UI thread that is blocked in the exit path, so a wait could only ever run into its timeout.
    /// </summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        _shutdown.Cancel();
    }

    // The fire-and-forget wrapper: nobody awaits it, so it must not let anything escape (an unobserved exception would be lost
    // or, worse, tear the process down) and must leave a trace.
    private async Task RefreshLibraryQuietlyAsync()
    {
        try
        {
            await RefreshLibraryAsync();
        }
        catch (Exception ex)
        {
            Log(l => AppLog.LibraryRefreshFailed(l, ex, _library.Folder));
        }
    }

    private void UpdateRecordingStats()
    {
        RecordingDurationText = SessionFormat.Duration(_recorder.Duration);
        RecordingSizeText = SessionFormat.Size(FileSizeOf(_recorder.DatagramCount, _recorder.BytesRecorded));
        RecordingPacketsText = _recorder.DatagramCount.ToString("N0", CultureInfo.CurrentCulture);

        var dropped = _recorder.DroppedCount;
        DroppedWarning = dropped > 0
            ? string.Format(CultureInfo.CurrentCulture, SessionsStrings.SessionsDroppedWarning, dropped.ToString("N0", CultureInfo.CurrentCulture))
            : "";

        if (_recorder.Fault is { } fault)
        {
            RecordingError = string.Format(CultureInfo.CurrentCulture, SessionsStrings.SessionsRecordWriteError, fault.Message);
        }
    }

    // ---- Player -------------------------------------------------------------------------------------------

    /// <summary>A recording is being replayed: the player bar shows and every tab shows the replay.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    [NotifyPropertyChangedFor(nameof(RecordTooltip))]
    private bool _isReplaying;

    /// <summary>The name of the recording being replayed.</summary>
    [ObservableProperty]
    private string _playerTitle = "";

    /// <summary>The state in words: Playing, Paused or Finished (the player's state line, read by screen readers as it changes).</summary>
    [ObservableProperty]
    private string _playerStateText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    [NotifyPropertyChangedFor(nameof(IsShowingPause))]
    private bool _isPaused;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    [NotifyPropertyChangedFor(nameof(IsShowingPause))]
    private bool _isFinished;

    /// <summary>The play/pause button shows the pause icon (true only while it is really playing).</summary>
    public bool IsShowingPause => !IsPaused && !IsFinished;

    /// <summary>What the play/pause button does next, for its tooltip and its screen-reader name.</summary>
    public string PlayPauseLabel =>
        IsFinished ? SessionsStrings.SessionsReplayAgain
        : IsPaused ? SessionsStrings.SessionsResume
        : SessionsStrings.SessionsPause;

    /// <summary>0..1 position in the recording.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary><c>0:12 / 1:30</c>.</summary>
    [ObservableProperty]
    private string _positionText = "";

    /// <summary>The four playback speeds the segmented control offers.</summary>
    public IReadOnlyList<ChoiceOption<double>> SpeedOptions { get; }

    /// <summary>The playback speed; setting it (the segmented control) forwards it to the running replay.</summary>
    [ObservableProperty]
    private double _selectedSpeed = ReplaySpeed.Normal;

    partial void OnSelectedSpeedChanged(double value)
    {
        if (!_syncingSpeed && _service.Replay is { } replay && value > 0)
        {
            replay.Speed = value;
        }
    }

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (_service.Replay is not { } replay)
        {
            return;
        }

        if (replay.IsFinished)
        {
            // The recording ended and is being held: "play" means play it again, from the start.
            await RestartReplayAsync();
            return;
        }

        if (replay.IsPaused)
        {
            replay.Resume();
        }
        else
        {
            replay.Pause();
        }

        Refresh();
    }

    [RelayCommand]
    private Task RestartAsync() => RestartReplayAsync();

    // Restart means play from the start, even if it was paused, and as a FRESH session: the shell stops the replay and plays it
    // again (see ISessionsHost.RestartReplayAsync). Rewinding the running source instead would leave the engines holding the
    // first pass, so the second one would double the laps, the path and the top speed.
    private async Task RestartReplayAsync()
    {
        if (_host is null || _host.IsBusy || _service.Replay is null)
        {
            return;
        }

        // The new source starts at 1x, but a user who chose 2x and pressed restart did not ask for 1x: the chosen speed is applied
        // to the new source right here, once the host has started it, instead of being smuggled to RefreshPlayer through a field.
        var speed = SelectedSpeed;
        await _host.RestartReplayAsync();

        if (_service.Replay is { } replay)
        {
            // The tick may already have noticed the new source and reset the selector to 1x while the host was working: claim the
            // source as tracked and put the selector back, without writing it to the source a second time.
            _trackedReplay = replay;
            _syncingSpeed = true;
            SelectedSpeed = speed;
            _syncingSpeed = false;
            if (speed != ReplaySpeed.Normal)
            {
                replay.Speed = speed;
            }
        }
    }

    [RelayCommand]
    private Task StopReplayAsync() => _host?.StopReplayAsync() ?? Task.CompletedTask;

    // ---- Library ------------------------------------------------------------------------------------------

    /// <summary>The recordings, newest first. Replaced as a whole when the folder is read again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private IReadOnlyList<RecordingRowViewModel> _rows = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>The folder is being read for the first time and nothing is listed yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isLoading;

    /// <summary>Show the "record a session" invitation: the folder was read and is empty.</summary>
    public bool ShowEmpty => !HasRows && !IsLoading;

    /// <summary>
    /// Reads the recordings folder off the UI thread and replaces the list. Skipped while a name is being edited (the list would
    /// be rebuilt under the user's typing); a read that was overtaken by a newer one is discarded.
    /// </summary>
    [RelayCommand]
    public async Task RefreshLibraryAsync()
    {
        if (_shuttingDown || Rows.Any(row => row.IsEditing))
        {
            return;
        }

        var version = ++_loadVersion;
        IsLoading = Rows.Count == 0;
        try
        {
            var items = await _library.ScanAsync(excludePath: _recordingPath, ct: _shutdown.Token);
            if (_shuttingDown || version != _loadVersion || Rows.Any(row => row.IsEditing))
            {
                return;
            }

            Rows = items.Select(item => new RecordingRowViewModel(item, this, IsPlayingFile(item.Path))).ToList();
        }
        catch (OperationCanceledException) when (_shuttingDown)
        {
            // The app is closing and cancelled the scan: nothing to show, nothing to report.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The library logs what it could not read; a folder that cannot be listed at all just shows as empty.
            Log(l => AppLog.LibraryUnreadable(l, ex, _library.Folder));
        }
        finally
        {
            if (!_shuttingDown && version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private void OpenFolder() => _folderOpener?.TryOpen(_library.Folder);

    private bool IsPlayingFile(string path) =>
        _service.ReplayPath is { } playing && string.Equals(playing, path, StringComparison.OrdinalIgnoreCase);

    internal Task PlayAsync(RecordingRowViewModel row)
    {
        // A busy shell drops the request, so nothing about the page may change for it: the notice stays and the name of what is
        // playing stays. Both are decided here, BEFORE the call, because the shell's own refresh at the end of a failed replay
        // reports the failure using them; clearing afterwards would erase that report.
        if (_host is null || !row.Item.IsReadable || _host.IsBusy)
        {
            return Task.CompletedTask;
        }

        _lastPlayedName = row.Name;
        Notice = ""; // a message about the last recording or deletion is not about this replay
        return _host.PlayAsync(row.Path, SessionFormat.ReplayLabel(row.Item.StartedAt));
    }

    internal void BeginRename(RecordingRowViewModel row)
    {
        if (!row.CanModify)
        {
            return;
        }

        foreach (var other in Rows.Where(r => r.IsEditing && r != row))
        {
            other.IsEditing = false; // one name at a time
        }

        row.EditName = row.Name;
        row.IsEditing = true;
    }

    internal void CancelRename(RecordingRowViewModel row)
    {
        row.IsEditing = false;
        row.EditError = "";
    }

    internal async Task CommitRenameAsync(RecordingRowViewModel row)
    {
        if (!row.IsEditing)
        {
            return;
        }

        if (!row.CanModify)
        {
            CancelRename(row); // an export started while the name was being typed: the file is open, a rename would fail
            return;
        }

        var name = RecordingNameValidator.Normalize(row.EditName);
        var invalid = RecordingNameValidator.Validate(name);
        if (invalid != RecordingNameError.None)
        {
            row.EditError = Describe(invalid);
            return;
        }

        if (name == row.Name)
        {
            CancelRename(row); // nothing changed
            return;
        }

        switch (_library.TryRename(row.Path, name, out _))
        {
            case RecordingRenameResult.Renamed:
                row.IsEditing = false;
                await RefreshLibraryAsync();
                break;
            case RecordingRenameResult.AlreadyExists:
                row.EditError = SessionsStrings.SessionsNameExists;
                break;
            case RecordingRenameResult.InvalidName:
                row.EditError = Describe(RecordingNameError.InvalidCharacters);
                break;
            default:
                row.EditError = SessionsStrings.SessionsRenameFailed;
                break;
        }
    }

    internal async Task DeleteAsync(RecordingRowViewModel row)
    {
        if (!row.CanModify)
        {
            return;
        }

        var confirmed = await _confirmation.ConfirmAsync(
            SessionsStrings.SessionsDeleteConfirmTitle,
            string.Format(CultureInfo.CurrentCulture, SessionsStrings.SessionsDeleteConfirmMessage, row.Name),
            SessionsStrings.SessionsDeleteConfirmButton,
            Strings.CancelButton);
        if (!confirmed)
        {
            return;
        }

        if (!_library.TryDelete(row.Path))
        {
            SetNotice(string.Format(CultureInfo.CurrentCulture, SessionsStrings.SessionsDeleteFailed, row.Name), isError: true);
            return;
        }

        await RefreshLibraryAsync();
    }

    // ---- CSV export of a recording ---------------------------------------------------------------------------

    /// <summary>A recording is being converted to CSV right now (the page shows its progress and a Cancel button).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private bool _isExporting;

    /// <summary>How much of the recording has been converted, 0..1.</summary>
    [ObservableProperty]
    private double _exportProgress;

    /// <summary>For example <c>Exporting session.fh6rec... 37%</c>.</summary>
    [ObservableProperty]
    private string _exportText = "";

    /// <summary>The per-recording Export buttons are usable: there is a dialog to ask with and no export is running.</summary>
    public bool CanExport => _saveDialog is not null && !IsExporting;

    // The file being converted, so its row (and any row rebuilt for it) can refuse rename and delete while it is read.
    private string? _exportingPath;

    internal bool IsExportingFile(string path) =>
        _exportingPath is { } exporting && string.Equals(exporting, path, StringComparison.OrdinalIgnoreCase);

    partial void OnIsExportingChanged(bool value)
    {
        foreach (var row in Rows)
        {
            row.OnExportStateChanged();
        }
    }

    /// <summary>
    /// Converts one recording to a CSV file the user picks the place of. The conversion streams the file off the UI thread (a long
    /// session is hundreds of thousands of rows), shows its progress, and can be cancelled (<see cref="CancelExportCommand"/>); a
    /// cancelled or failed export leaves no partial file (see <see cref="ExportFile"/>). Only one runs at a time.
    /// </summary>
    internal async Task ExportAsync(RecordingRowViewModel row)
    {
        if (!row.CanExport || _saveDialog is null || _shuttingDown)
        {
            return;
        }

        var path = _saveDialog.PickSavePath(
            ExportStrings.ExportDialogTitle, row.Name + ".csv", CsvExportRunner.Filter);
        if (path is null)
        {
            return; // cancelled: not an error
        }

        // Linked to the shutdown token: closing the app cancels a running export like the user's Cancel does.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _exportCts = cts;
        Notice = "";
        ExportProgress = 0;
        ExportText = ProgressText(row.Name, 0);
        _exportingPath = row.Path; // before the flag: its change handler makes every row ask
        IsExporting = true;

        // Progress<T> posts to the context it was created on (the UI thread), so the bar moves without any marshaling here. A
        // report that arrives after the export ended is ignored: it must not bring the bar back.
        var progress = new Progress<double>(fraction =>
        {
            if (IsExporting && ReferenceEquals(_exportCts, cts))
            {
                ExportProgress = fraction;
                ExportText = ProgressText(row.Name, fraction);
            }
        });

        try
        {
            var result = await Task.Run(() => RecordingCsv.ExportFileAsync(row.Path, path, progress, cts.Token), cts.Token);
            Log(logger => AppLog.ExportSaved(logger, path));

            // A recording cut off by a crash exports every complete packet; the notice says so, or the file would look whole.
            SetNotice(
                string.Format(
                    CultureInfo.CurrentCulture,
                    result.IsTruncated ? ExportStrings.ExportSavedPacketsTruncated : ExportStrings.ExportSavedPackets,
                    System.IO.Path.GetFileName(path), result.Rows.ToString("N0", CultureInfo.CurrentCulture)),
                isError: false);
        }
        catch (OperationCanceledException)
        {
            Log(logger => AppLog.ExportCancelled(logger, path));
            if (!_shuttingDown)
            {
                SetNotice(ExportStrings.ExportCancelled, isError: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // A damaged or newer-format recording (InvalidRecordingException is an IOException) and a full or locked disk both land here.
            Log(logger => AppLog.ExportFailed(logger, ex, path));
            SetNotice(
                string.Format(CultureInfo.CurrentCulture, ExportStrings.ExportFailed, System.IO.Path.GetFileName(path), ex.Message),
                isError: true);
        }
        finally
        {
            _exportCts = null;
            _exportingPath = null;
            IsExporting = false;
            ExportProgress = 0;
        }
    }

    private static string ProgressText(string name, double fraction) =>
        string.Format(CultureInfo.CurrentCulture, ExportStrings.ExportProgressFormat, name, (int)Math.Round(fraction * 100));

    /// <summary>Stops the running export; its partial file is discarded.</summary>
    [RelayCommand]
    private void CancelExport() => _exportCts?.Cancel();

    /// <summary>The localized sentence for a name problem; empty for <see cref="RecordingNameError.None"/>.</summary>
    internal static string Describe(RecordingNameError error) => error switch
    {
        RecordingNameError.Empty => SessionsStrings.SessionsNameEmpty,
        RecordingNameError.InvalidCharacters => SessionsStrings.SessionsNameInvalid,
        RecordingNameError.ReservedName => SessionsStrings.SessionsNameReserved,
        RecordingNameError.TrailingDotOrSpace => SessionsStrings.SessionsNameTrailing,
        RecordingNameError.TooLong => SessionsStrings.SessionsNameTooLong,
        _ => ""
    };

    // ---- Driven by the shell ------------------------------------------------------------------------------

    /// <summary>
    /// Pulls the current state out of the recorder and the service into the bound values. Called ~20 times a second, on the UI
    /// thread, by the shell (every tick, whichever tab is showing: the recording pill in the top bar needs it, and so does the
    /// rule that ends a recording whose listener is gone).
    /// </summary>
    public void Refresh()
    {
        var replaying = _service.IsReplaying;
        IsReplaying = replaying;
        IsLive = _service.IsRunning && _service.Mode == TelemetrySourceMode.Live;

        RefreshRecording();
        RefreshPlayer(replaying);
        MarkPlayingRow();
    }

    private void RefreshRecording()
    {
        if (!_recorder.IsRecording)
        {
            return;
        }

        if (!IsLive && _stopTask is not { IsCompleted: false } && !IsRecordingBusy)
        {
            // Listening ended without the shell asking us first (a fault, the loop died): there is nothing left to record.
            // Stop now so the file is finished and listed, instead of showing a recording whose clock keeps running.
            _stopTask = StopRecordingCoreAsync(listeningEnded: true);
            return;
        }

        UpdateRecordingStats();
    }

    private void RefreshPlayer(bool replaying)
    {
        var replay = _service.Replay;
        if (!ReferenceEquals(replay, _trackedReplay))
        {
            // A different replay began (or ended): a new source always starts at 1x, so the selector follows without writing back.
            // (a restart is the exception: RestartReplayAsync puts the speed the user had chosen back right after it).
            _trackedReplay = replay;
            _syncingSpeed = true;
            SelectedSpeed = ReplaySpeed.Normal;
            _syncingSpeed = false;

            _reportedPlaybackFault = null;
        }

        if (!replaying || replay is null)
        {
            ReportPlaybackFault();
            return;
        }

        PlayerTitle = _service.ReplayPath is { } path ? Path.GetFileNameWithoutExtension(path) : _lastPlayedName;
        IsPaused = replay.IsPaused;
        IsFinished = replay.IsFinished;
        PlayerStateText = IsFinished ? SessionsStrings.SessionsFinished
            : IsPaused ? SessionsStrings.SessionsPaused
            : SessionsStrings.SessionsPlaying;

        var position = replay.Position;
        var duration = replay.Duration;
        Progress = IsFinished ? 1 : duration > TimeSpan.Zero ? Math.Clamp(position / duration, 0, 1) : 0;
        PositionText = string.Format(
            CultureInfo.CurrentCulture, SessionsStrings.SessionsPositionFormat, SessionFormat.Duration(IsFinished ? duration : position),
            SessionFormat.Duration(duration));
    }

    // A replay that died (a file that is gone or not a recording) leaves a fault in the service. The top bar says so briefly; the
    // page says it where the user is looking, once per failure.
    private void ReportPlaybackFault()
    {
        if (_service.Mode == TelemetrySourceMode.Replay && _service.Fault is { } fault && !ReferenceEquals(fault, _reportedPlaybackFault))
        {
            _reportedPlaybackFault = fault;
            SetNotice(
                string.Format(CultureInfo.CurrentCulture, SessionsStrings.SessionsPlaybackFailed, _lastPlayedName, fault.Message),
                isError: true);
        }
    }

    private void MarkPlayingRow()
    {
        var playing = _service.ReplayPath;
        if (string.Equals(playing, _markedPlayingPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _markedPlayingPath = playing;
        foreach (var row in Rows)
        {
            row.IsPlaying = IsPlayingFile(row.Path);
        }
    }

    private void SetNotice(string text, bool isError)
    {
        Notice = text;
        NoticeIsError = isError;
    }

    private void Log(Action<ILogger> write)
    {
        if (_logger is not null)
        {
            write(_logger);
        }
    }
}
