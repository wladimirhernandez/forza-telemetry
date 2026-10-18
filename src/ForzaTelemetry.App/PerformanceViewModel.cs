using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Performance;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App;

/// <summary>What the run-state line on the Performance page says; the view maps it to a dot colour.</summary>
public enum PerformanceRunState
{
    /// <summary>No driving data right now (not listening, in a menu, paused).</summary>
    WaitingForData,

    /// <summary>Driving but not on the line: stop the car to arm the timer.</summary>
    Idle,

    /// <summary>Stationary with the race on: the next launch is timed.</summary>
    Armed,

    /// <summary>A run is being timed.</summary>
    Running,

    /// <summary>A run finished and its results are shown.</summary>
    Done,

    /// <summary>A run was cut short (reverse, a pause, a stop) before anything was measured.</summary>
    Aborted
}

/// <summary>The columns of the per-car table, which the user can sort by.</summary>
public enum PerformanceSortColumn
{
    /// <summary>Most recently improved first (the default).</summary>
    Recent,
    Car,

    /// <summary>The first acceleration column: 0-100 km/h, or 0-60 mph while the speed unit is mph.</summary>
    ZeroTo100,

    /// <summary>The second acceleration column: 0-200 km/h, or 60-130 mph while the speed unit is mph.</summary>
    ZeroTo200,
    QuarterMile,
    TopSpeed,

    /// <summary>Shortest braking distance first (100-0 km/h, or 60-0 mph while the speed unit is mph).</summary>
    Braking
}

/// <summary>
/// One row of the per-car table, already formatted for display in the units that were selected when it was built. The
/// quarter mile is two values (the time and the speed at the line), kept apart so the table can stack them in two lines
/// instead of joining them with a separator character.
/// </summary>
/// <remarks>
/// The table shows the acceleration windows of the SELECTED unit (<see cref="WindowAText"/>, <see cref="WindowBText"/>) and one
/// braking column; every other stored best (the rolling 100-200 km/h, the other unit's windows, the second braking distance) is in
/// <see cref="Details"/>, the row's tooltip and its screen-reader sentence, so the table stays readable without hiding anything.
/// </remarks>
public sealed record CarPerformanceRow(
    string CarLabel,
    string ClassLabel,
    string PerformanceIndexText,
    string DrivetrainLabel,
    string WindowAText,
    string WindowBText,
    string QuarterMileText,
    string QuarterMileTrapText,
    string TopSpeedText,
    string BrakingText,
    string UpdatedText,
    bool IsCurrentCar,
    int CarOrdinal = 0,
    IReadOnlyList<string>? Details = null)
{
    /// <summary>Every stored best of this car, one "title: value" line each (empty when nothing was measured).</summary>
    public IReadOnlyList<string> DetailLines { get; } = Details ?? [];

    /// <summary>The detail lines as a multi-line tooltip.</summary>
    public string DetailsTooltip => string.Join(Environment.NewLine, DetailLines);

    /// <summary>What a screen reader announces for the row's rename button: "Rename Daily GT".</summary>
    public string RenameName => string.Format(System.Globalization.CultureInfo.CurrentCulture, CarNameStrings.CarRenameRowName, CarLabel);

    /// <summary>The whole row as one sentence for screen readers (a row of numbers means little read cell by cell): every stored best, with its name.</summary>
    public string Summary =>
        $"{CarLabel}, {ClassLabel} {PerformanceIndexText}, {DrivetrainLabel}. "
        + (DetailLines.Count > 0 ? string.Join(". ", DetailLines) + ". " : "")
        + UpdatedText;
}

/// <summary>
/// The Performance screen: the live acceleration, quarter-mile, top-speed and braking cards, the run state, and the best results per car.
/// </summary>
/// <remarks>
/// <b>Where the numbers come from.</b> The timing is done by <see cref="AccelerationTimer"/> and
/// <see cref="TopSpeedTracker"/>, which see EVERY packet on the receive thread (see <c>TelemetryService</c>). This class
/// only READS their snapshots once per UI tick (<see cref="Refresh"/>) and turns them into text, so a 20 Hz screen can never
/// lose precision: the engines interpolate between real packets, the screen merely displays their latest verdict.
/// <para>
/// <b>When a result is saved.</b> A run is saved to the history when it ENDS (the engine leaves the Running state), under the
/// car that was driven when it STARTED, never mid-run and never from a stale snapshot. Detecting the end by the state edge is
/// what makes a result belong to one car: the engine itself does not know cars exist. Top speed has no such edge (it
/// is a session maximum), so it is folded in when the car has clearly slowed after its peak, when the car changes, and when
/// the session ends; <see cref="PerformanceHistoryStore.Flush"/> then writes the file.
/// </para>
/// <para>
/// <b>Units.</b> One run measures every window at once (0-100, 0-200, 100-200 km/h; 0-60, 60-130 mph; see
/// <see cref="AccelerationTimer"/>), so the unit setting only chooses WHICH ones the page leads with: in km/h the cards are 0-100 and
/// 0-200 km/h and the extra tile is the rolling 100-200 km/h; in mph they are 0-60 and 60-130 mph and the extra tile keeps the
/// 0-100 km/h reference. Speeds and the braking distance (meters, or feet with mph) follow the unit as well, and nothing is
/// re-measured when it changes. The table and the saved history hold every window whichever unit is showing.
/// </para>
/// <para>
/// <b>Braking</b> is a second, independent test (<see cref="BrakingTest"/>: 100-0 km/h and 60-0 mph from one state machine). Like
/// the acceleration one it is saved when a run ENDS, recognised by the engine's completion counter, under the car that started it.
/// </para>
/// </remarks>
public sealed partial class PerformanceViewModel : ObservableObject
{
    /// <summary>Once the car has slowed below this fraction of its top speed, the top-speed pass is over and worth saving.</summary>
    private const double TopSpeedSettledRatio = 0.85;

    private const string NoValue = "--";

    private readonly AccelerationTimer _acceleration;
    private readonly BrakingTest? _braking;
    private readonly TopSpeedTracker _topSpeed;
    private readonly CarChangeDetector _carChanges;
    private readonly PerformanceHistoryStore _history;
    private readonly UnitsService _units;
    private readonly IConfirmationService _confirmation;

    // The car being driven (null until a packet carries a real one) and what the game says about it.
    private CarKey? _car;
    private int _carClass;
    private int _drivetrain;

    // Run edge detection (see the class remarks). A kept run is recognised by the engine's CompletedRuns counter growing, never by
    // comparing result values: values repeat (two identical runs) and a dropped run leaves the old values showing.
    private bool _wasRunning;
    private CarKey? _runCar;
    private bool _aborted;

    // The same for the braking test: a stop that finished with a distance is saved under the car that began it.
    private bool _wasBraking;
    private CarKey? _brakeCar;

    // The counters last seen. They only ever grow in the engines (a reset does not lower them), so after anything that empties
    // the engines or changes who is driving, the next consistent read ADOPTS the engines' values as the new baseline
    // (_baselinePending) instead of treating a run finished before the change as a new one.
    private long _seenRuns;
    private long _seenBrakes;
    private bool _baselinePending = true;

    // The last top speed folded into the history for the current car.
    private double _mergedTopSpeedKmh;

    private readonly CarNameStore? _carNames;
    private readonly CarRenameViewModel? _carRename;
    private readonly CsvExportRunner? _export;
    private readonly TimeProvider _time;

    private AccelerationSnapshot _snapshot = AccelerationSnapshot.Empty;
    private BrakingSnapshot _brakingSnapshot = BrakingSnapshot.Empty;
    private double _topSpeedKmh;
    private bool _live;

    private readonly record struct Results(
        double? ZeroTo100,
        double? ZeroTo200,
        double? Quarter,
        double? Trap,
        double? HundredTo200,
        double? ZeroTo60Mph,
        double? SixtyTo130Mph);

    /// <param name="acceleration">The engine that times the runs (fed by the receive loop).</param>
    /// <param name="topSpeed">The engine that tracks the session top speed (fed by the receive loop).</param>
    /// <param name="carChanges">Reports every change of car, noticed per packet by the receive loop (see <see cref="CarChangeDetector"/>).</param>
    /// <param name="history">The per-car bests.</param>
    /// <param name="units">Converts and labels the speeds; optional (metric) for tests and the designer.</param>
    /// <param name="confirmation">Asks before the history is cleared; optional (clearing then needs no confirmation, for tests).</param>
    /// <param name="carNames">The player's car nicknames, shown wherever a car is named; optional (cars then show as "Car #1234").</param>
    /// <param name="carRename">The shared rename box the rename buttons open; optional (the buttons then do nothing).</param>
    /// <param name="braking">The engine that measures the braking distance (fed by the receive loop); optional (the braking tile then stays empty).</param>
    /// <param name="saveDialog">Asks where to save the CSV; optional (the Export button is then disabled).</param>
    /// <param name="logger">Receives export failures; optional.</param>
    /// <param name="time">Clock for the suggested file name; defaults to the system clock.</param>
    public PerformanceViewModel(
        AccelerationTimer acceleration,
        TopSpeedTracker topSpeed,
        CarChangeDetector carChanges,
        PerformanceHistoryStore history,
        UnitsService? units = null,
        IConfirmationService? confirmation = null,
        CarNameStore? carNames = null,
        CarRenameViewModel? carRename = null,
        BrakingTest? braking = null,
        IFileSaveDialog? saveDialog = null,
        ILogger? logger = null,
        TimeProvider? time = null)
    {
        _braking = braking;
        _time = time ?? TimeProvider.System;
        _export = saveDialog is null ? null : new CsvExportRunner(saveDialog, ExportStatus, logger);
        _carNames = carNames;
        _carRename = carRename;
        _acceleration = acceleration;
        _topSpeed = topSpeed;
        _carChanges = carChanges;
        _history = history;
        _units = units ?? new UnitsService();
        _confirmation = confirmation ?? new AlwaysConfirm();

        _history.Changed += (_, _) => RebuildRows();
        if (carNames is not null)
        {
            carNames.Changed += (_, _) => OnCarNamesChanged();
        }

        _units.PropertyChanged += OnUnitsChanged;

        RebuildRows();
        Show();
    }

    private sealed class AlwaysConfirm : IConfirmationService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText) => Task.FromResult(true);
    }

    // ---- Live state ---------------------------------------------------------------------------------------

    /// <summary>Closed-set run state the view maps to the dot colour.</summary>
    [ObservableProperty]
    private PerformanceRunState _state = PerformanceRunState.WaitingForData;

    /// <summary>The state as a localized sentence, for example "Armed: launch when ready".</summary>
    [ObservableProperty]
    private string _stateText = Strings.PerfStateWaiting;

    /// <summary>Seconds into the run while one is being timed (<c>3.4 s</c>), empty otherwise.</summary>
    [ObservableProperty]
    private string _elapsedText = "";

    /// <summary>
    /// The session on screen is a recording being replayed, so nothing it measures is saved (the page says so).
    /// </summary>
    /// <remarks>
    /// <b>Decision: replays never write to the history.</b> A replay is for REVIEWING a drive, and the drive's results were
    /// already saved when it happened live. Saving them again would add nothing, would stamp every best with today's date instead
    /// of the day it was set, and, since a recording can be played any number of times, would be a side effect of looking. The
    /// numbers are still measured and shown, so a replay is a faithful read-through; they just go nowhere. The flag belongs to
    /// the session (set by <see cref="BeginSession"/>) and stays until the next one begins, so it also covers the end of the
    /// replay, which would otherwise save the top speed of the recording.
    /// </remarks>
    [ObservableProperty]
    private bool _isReplaySession;

    // ---- Result cards -------------------------------------------------------------------------------------

    // The page shows its acceleration windows through three SLOTS (window A, window B, the extra tile) whose text, title, name and
    // "new best" flag come from whichever measured value the unit setting selects. [NotifyPropertyChangedFor] on each measured value
    // announces exactly the slot properties derived from it, so the 20 Hz refresh raises a notification only when something moved
    // (and a name is announced as changed exactly when its value changed).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowAText))]
    [NotifyPropertyChangedFor(nameof(WindowAName))]
    [NotifyPropertyChangedFor(nameof(ExtraWindowText))]
    [NotifyPropertyChangedFor(nameof(ExtraWindowName))]
    private string _zeroTo100Text = NoValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowBText))]
    [NotifyPropertyChangedFor(nameof(WindowBName))]
    private string _zeroTo200Text = NoValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExtraWindowText))]
    [NotifyPropertyChangedFor(nameof(ExtraWindowName))]
    private string _hundredTo200Text = NoValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowAText))]
    [NotifyPropertyChangedFor(nameof(WindowAName))]
    private string _zeroTo60MphText = NoValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowBText))]
    [NotifyPropertyChangedFor(nameof(WindowBName))]
    private string _sixtyTo130MphText = NoValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuarterMileName))]
    private string _quarterMileText = NoValue;

    /// <summary>The speed at the quarter-mile line with its unit (<c>187 km/h at the line</c>), or empty before one is measured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuarterMileName))]
    private string _quarterMileTrapText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopSpeedName))]
    private string _topSpeedText = NoValue;

    /// <summary>The unit label under the top speed: <c>km/h</c> or <c>mph</c>.</summary>
    public string SpeedUnitLabel => _units.SpeedLabel;

    // "New best": the value of the run that just ended beat an earlier best of this car. Cleared when the next run starts.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowAIsNewBest))]
    [NotifyPropertyChangedFor(nameof(ExtraWindowIsNewBest))]
    private bool _zeroTo100IsNewBest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowBIsNewBest))]
    private bool _zeroTo200IsNewBest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExtraWindowIsNewBest))]
    private bool _hundredTo200IsNewBest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowAIsNewBest))]
    private bool _zeroTo60MphIsNewBest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowBIsNewBest))]
    private bool _sixtyTo130MphIsNewBest;

    [ObservableProperty]
    private bool _quarterMileIsNewBest;

    [ObservableProperty]
    private bool _topSpeedIsNewBest;

    private bool IsMph => _units.SpeedUnit == SpeedUnit.Mph;

    // ---- The three acceleration slots (see the class remarks, "Units") ------------------------------------

    /// <summary>First card: 0-100 km/h, or 0-60 mph with mph.</summary>
    public string WindowATitle => IsMph ? Strings.PerfTitle60Mph : Strings.PerfTitle100;

    public string WindowAText => IsMph ? ZeroTo60MphText : ZeroTo100Text;

    public bool WindowAIsNewBest => IsMph ? ZeroTo60MphIsNewBest : ZeroTo100IsNewBest;

    // Card names for screen readers, with the value spoken, e.g. "0-100 km/h: 2.84".
    public string WindowAName => $"{WindowATitle}: {WindowAText}";

    /// <summary>Second card: 0-200 km/h, or 60-130 mph with mph.</summary>
    public string WindowBTitle => IsMph ? Strings.PerfTitle60To130Mph : Strings.PerfTitle200;

    public string WindowBText => IsMph ? SixtyTo130MphText : ZeroTo200Text;

    public bool WindowBIsNewBest => IsMph ? SixtyTo130MphIsNewBest : ZeroTo200IsNewBest;

    public string WindowBName => $"{WindowBTitle}: {WindowBText}";

    /// <summary>The compact tile: the rolling 100-200 km/h, or with mph the 0-100 km/h reference the cards no longer lead with.</summary>
    public string ExtraWindowTitle => IsMph ? Strings.PerfTitle100 : Strings.PerfTitle100To200;

    public string ExtraWindowText => IsMph ? ZeroTo100Text : HundredTo200Text;

    public bool ExtraWindowIsNewBest => IsMph ? ZeroTo100IsNewBest : HundredTo200IsNewBest;

    public string ExtraWindowName => $"{ExtraWindowTitle}: {ExtraWindowText}";

    // ---- Braking tile ---------------------------------------------------------------------------------------------

    /// <summary>The braking distance without its unit (<c>38.4</c>, or whole feet), <c>--</c> until a stop was measured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrakingName))]
    private string _brakingDistanceText = NoValue;

    /// <summary>Seconds the stop took (the live elapsed time while braking), <c>--</c> until measured; the view adds the unit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrakingName))]
    private string _brakingTimeText = NoValue;

    /// <summary>The braking test's own state line: how to arm it, that it is armed, braking, or done.</summary>
    [ObservableProperty]
    private string _brakingStateText = Strings.PerfStateWaiting;

    /// <summary>The dot colour of the braking state line (the same closed set as the run state: Braking reads as <see cref="PerformanceRunState.Running"/>).</summary>
    [ObservableProperty]
    private PerformanceRunState _brakingRunState = PerformanceRunState.WaitingForData;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrakingIsNewBest))]
    private bool _brakeHundredIsNewBest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrakingIsNewBest))]
    private bool _brakeSixtyMphIsNewBest;

    /// <summary>Title of the tile: <c>Braking 100-0 km/h</c>, or <c>Braking 60-0 mph</c> with mph.</summary>
    public string BrakingTitle => IsMph ? Strings.PerfTitleBrake60Mph : Strings.PerfTitleBrake100;

    /// <summary>The unit under the distance: <c>m</c>, or <c>ft</c> with mph.</summary>
    public string DistanceUnitLabel => _units.DistanceLabel;

    public bool BrakingIsNewBest => IsMph ? BrakeSixtyMphIsNewBest : BrakeHundredIsNewBest;

    public string BrakingName => $"{BrakingTitle}: {BrakingDistanceText} {DistanceUnitLabel}, {BrakingTimeText}";

    // ---- Table column titles (they follow the unit like the cards) -----------------------------------------------

    public string WindowAColumnTitle => IsMph ? Strings.PerfColZeroTo60Mph : Strings.PerfColZeroTo100;

    public string WindowBColumnTitle => IsMph ? Strings.PerfColSixtyTo130Mph : Strings.PerfColZeroTo200;

    public string QuarterMileName => $"{Strings.PerfTitleQuarter}: {QuarterMileText} {QuarterMileTrapText}".TrimEnd();

    public string TopSpeedName => $"{Strings.PerfTitleTopSpeed}: {TopSpeedText} {SpeedUnitLabel}";

    // ---- Current car --------------------------------------------------------------------------------------

    /// <summary>False until a packet carries a real car (performance index 100..999).</summary>
    [ObservableProperty]
    private bool _hasCar;

    /// <summary>The first pill: <c>Car #1234</c>, or the localized "no car detected yet" before the first real car.</summary>
    [ObservableProperty]
    private string _carText = Strings.PerfNoCar;

    /// <summary>Class letter(s) of the car (<c>A</c>); shown in the second pill, which is hidden until there is a car.</summary>
    [ObservableProperty]
    private string _carClassLabel = "";

    /// <summary>Performance index of the car (<c>780</c>); the view adds the "PI" label from a resource.</summary>
    [ObservableProperty]
    private int _carPerformanceIndex;

    /// <summary><c>FWD</c>, <c>RWD</c> or <c>AWD</c>; empty before the first car.</summary>
    [ObservableProperty]
    private string _drivetrainLabel = "";

    /// <summary>
    /// The whole car as one sentence for screen readers (<c>Car #1234, A 780, AWD</c>). The pills show it as separate pieces;
    /// a screen reader would read three unrelated fragments, so the container carries this instead.
    /// </summary>
    [ObservableProperty]
    private string _carSummary = Strings.PerfNoCar;

    // ---- History table ------------------------------------------------------------------------------------

    /// <summary>The per-car bests in the chosen order, formatted for display. Replaced as a whole when anything changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistory))]
    [NotifyCanExecuteChangedFor(nameof(ClearHistoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    [NotifyPropertyChangedFor(nameof(ExportTooltip))]
    private IReadOnlyList<CarPerformanceRow> _rows = [];

    public bool HasHistory => Rows.Count > 0;

    /// <summary>Which column the table is sorted by. [NotifyPropertyChangedFor]: each header shows a marker when it is the sorted one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortedByRecent))]
    [NotifyPropertyChangedFor(nameof(IsSortedByCar))]
    [NotifyPropertyChangedFor(nameof(IsSortedByZeroTo100))]
    [NotifyPropertyChangedFor(nameof(IsSortedByZeroTo200))]
    [NotifyPropertyChangedFor(nameof(IsSortedByQuarterMile))]
    [NotifyPropertyChangedFor(nameof(IsSortedByTopSpeed))]
    [NotifyPropertyChangedFor(nameof(IsSortedByBraking))]
    private PerformanceSortColumn _sortColumn = PerformanceSortColumn.Recent;

    public bool IsSortedByRecent => SortColumn == PerformanceSortColumn.Recent;

    public bool IsSortedByCar => SortColumn == PerformanceSortColumn.Car;

    public bool IsSortedByZeroTo100 => SortColumn == PerformanceSortColumn.ZeroTo100;

    public bool IsSortedByZeroTo200 => SortColumn == PerformanceSortColumn.ZeroTo200;

    public bool IsSortedByQuarterMile => SortColumn == PerformanceSortColumn.QuarterMile;

    public bool IsSortedByTopSpeed => SortColumn == PerformanceSortColumn.TopSpeed;

    public bool IsSortedByBraking => SortColumn == PerformanceSortColumn.Braking;

    partial void OnSortColumnChanged(PerformanceSortColumn value) => RebuildRows();

    /// <summary>Sorts the table by a column (the header buttons pass the column as the command parameter).</summary>
    [RelayCommand]
    private void SortBy(PerformanceSortColumn column) => SortColumn = column;

    // ---- Commands -----------------------------------------------------------------------------------------

    /// <summary>Forgets the run results and the session top speed (the per-car history is kept; a new best is saved first).</summary>
    [RelayCommand]
    private void ResetRun()
    {
        // Save, then WRITE: the engines are about to forget this top speed, so a crash right after would otherwise lose a
        // best that only ever lived in memory (Merge changes memory; only Flush persists). The engines are read first, and
        // that read applies a pending change of car before it credits any number to a car (see ReadEngines).
        //
        // A read that comes back false (a change of car landed mid-read) is retried, because the retry drains that change and
        // reads cleanly. What is NEVER done is saving after a failed read: the numbers held then are not known to belong to
        // the car being credited. If every attempt fails the cars are changing faster than a button can be pressed, and the
        // change itself already carried the old car's top speed into the history, so nothing worth keeping is lost.
        var consistent = false;
        for (var attempt = 0; attempt < 3 && !consistent; attempt++)
        {
            consistent = ReadEngines();
        }

        if (consistent)
        {
            SaveTopSpeedOfCurrentCar();
            FlushIfLive();
        }

        // The engines are written by the receive loop, so they are not reset from this (UI) thread: the loop is asked to, and
        // does it on its next packet. Until then the detector reports the reset as pending and ReadEngines shows them empty.
        _carChanges.RequestEngineReset();
        _baselinePending = true;
        ForgetRun();
        _snapshot = AccelerationSnapshot.Empty;
        _brakingSnapshot = BrakingSnapshot.Empty;
        _topSpeedKmh = 0;
        Show();
    }

    /// <summary>The message next to the Export button (saved, or why not).</summary>
    public ExportFeedback ExportStatus { get; } = new();

    /// <summary>There is a history to export and somewhere to ask where to put it.</summary>
    public bool CanExport => HasHistory && _export is not null;

    /// <summary>Explains the Export button; when it is disabled, also says why (a greyed-out button says nothing by itself).</summary>
    public string ExportTooltip => CanExport ? ExportStrings.ExportPerformanceTooltip : ExportStrings.ExportPerformanceDisabledTooltip;

    /// <summary>
    /// Saves every car's stored bests as a CSV file the user picks the place of. The export is of the STORED history (all cars, every
    /// value, in fixed units), not of what the table currently shows, so the sort order and the km/h or mph setting do not change the
    /// file (see <see cref="PerformanceCsv"/>). The records are copied before the dialog opens: the store is UI-thread-only, and the
    /// write runs on another thread.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportCsvAsync()
    {
        var records = _history.Records;
        // A snapshot: the store is UI-thread-only and the write runs on another thread. One lookup per car.
        var names = new Dictionary<int, string>();
        foreach (var ordinal in records.Select(r => r.Car.CarOrdinal).Distinct())
        {
            if (_carNames?.Find(ordinal) is { } name)
            {
                names[ordinal] = name;
            }
        }
        await _export!.RunAsync(
            CsvExportRunner.SuggestedName(ExportStrings.ExportPerformanceFileName, _time.GetLocalNow()),
            stream => PerformanceCsv.Write(stream, records, names.GetValueOrDefault));
    }

    /// <summary>Deletes every car's bests after the user confirmed.</summary>
    [RelayCommand(CanExecute = nameof(HasHistory))]
    private async Task ClearHistoryAsync()
    {
        var confirmed = await _confirmation.ConfirmAsync(
            Strings.PerfClearConfirmTitle,
            Strings.PerfClearConfirmMessage,
            Strings.PerfClearConfirmButton,
            Strings.CancelButton);

        if (confirmed)
        {
            _history.Clear();
        }
    }

    // ---- Driven by the shell ------------------------------------------------------------------------------

    /// <summary>
    /// A new listening session starts: everything the previous one measured leaves the SCREEN. The saved history is untouched.
    /// </summary>
    /// <remarks>
    /// This resets only what this page displays. The engines (and the car-change detector) are reset by
    /// <see cref="TelemetryEngines.BeginSession"/>, which the shell calls BEFORE the receive loop starts, after
    /// <see cref="EndSession"/> saved the previous session's top speed. Doing the engine reset here, from the UI thread after the
    /// loop was already running, raced with the first packets: a car change of packet one could be queued and then dropped, and
    /// a top speed read after the loop restarted could be saved under the wrong car.
    /// </remarks>
    /// <param name="isReplay">The session is a recording being played back (see <see cref="IsReplaySession"/>).</param>
    public void BeginSession(bool isReplay = false)
    {
        IsReplaySession = isReplay;
        _car = null;
        _live = false;
        _baselinePending = true;
        ForgetRun();
        _snapshot = AccelerationSnapshot.Empty;
        _brakingSnapshot = BrakingSnapshot.Empty;
        _topSpeedKmh = 0;
        SetCar(null);
        Show();
    }

    /// <summary>
    /// The session ends (Stop, or the app closing): the car's top speed and any finished run are saved. The numbers stay on
    /// screen so the user can read them after stopping; only the state line says the data is no longer live.
    /// </summary>
    public void EndSession()
    {
        Refresh(null);
        SaveTopSpeedOfCurrentCar();
        FlushIfLive();
    }

    /// <summary>
    /// Called once per UI tick with the latest packet (or null when none): follows the car, detects the end of a run, saves
    /// what is worth saving and refreshes the texts. Cheap by design: two snapshot reads and a few property writes.
    /// </summary>
    public void Refresh(TelemetryPacket? latest)
    {
        // Only a driving packet says anything about the car: menus and pauses send IsRaceOn = false with zeroed fields.
        _live = latest is { IsRaceOn: true };

        // When a change of car lands between the steps, the engines already hold the new car's numbers: this tick keeps
        // the previous values and the next tick (which drains the change first) reads them under the right car.
        if (ReadEngines())
        {
            TrackRun(_snapshot);
            TrackBraking(_brakingSnapshot);
            SaveTopSpeedWhenSettled(latest);
        }

        Show();
    }

    // ---- Car ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Takes the engines' numbers for the car this view model currently knows, or nothing when that cannot be guaranteed.
    /// </summary>
    /// <remarks>
    /// The receive loop can switch cars at any moment (it resets the engines when it does), while this runs on the UI thread, so
    /// "who is driving" and "what the engines say" cannot be read in one step. Without a guard, a change landing between the
    /// two reads would credit the NEW car's numbers (a fresh, tiny top speed, an empty timer) to the OLD car, and a top speed
    /// saved that way is wrong forever. The guard: apply every queued change first, read the detector's generation, check
    /// nothing is pending, read the engines, read the generation again. If anything moved, the values are discarded
    /// (<c>false</c>) and the next tick tries again. See <see cref="CarChangeDetector.Generation"/>.
    /// </remarks>
    private bool ReadEngines()
    {
        ApplyCarChanges();

        var generation = _carChanges.Generation;
        if (_carChanges.HasPendingChanges)
        {
            return false;
        }

        // A reset the receive loop has not carried out yet (see ResetRun) means the engines are, for the user, already empty.
        var resetPending = _carChanges.HasPendingEngineReset;
        var snapshot = resetPending ? AccelerationSnapshot.Empty : _acceleration.Current;
        var braking = resetPending || _braking is null ? BrakingSnapshot.Empty : _braking.Current;
        var topSpeedKmh = resetPending ? 0 : _topSpeed.TopSpeedKmh;
        if (_carChanges.Generation != generation)
        {
            return false;
        }

        if (_baselinePending && !resetPending)
        {
            // Nothing that finished before this point belongs to the car or session on screen now.
            _seenRuns = snapshot.CompletedRuns;
            _seenBrakes = braking.CompletedRuns;
            _baselinePending = false;
        }

        _snapshot = snapshot;
        _brakingSnapshot = braking;
        _topSpeedKmh = topSpeedKmh;
        return true;
    }

    /// <summary>
    /// Handles every change of car the receive loop has noticed since the last tick (normally none). The detector already
    /// froze the old car's top speed and reset the engines on the packet where the car changed; what is left for the UI
    /// thread is the bookkeeping that touches the history and the screen.
    /// </summary>
    private void ApplyCarChanges()
    {
        while (_carChanges.TryTake(out var change))
        {
            if (change!.PreviousCar is { } previous)
            {
                // The old car's best is saved under the OLD car's class and drivetrain (they travel in the change, because
                // the fields below describe the new car by now), then the cards are cleared: the engines were reset, and the
                // screen would otherwise keep showing the previous car's results as if they were this one's.
                if (change.PreviousTopSpeedKmh > 0)
                {
                    MergeIfLive(
                        previous,
                        change.PreviousClass,
                        change.PreviousDrivetrain,
                        new PerformanceResult(null, null, null, null, change.PreviousTopSpeedKmh));
                }

                FlushIfLive();
                ForgetRun();
            }

            _carClass = change.CarClass;
            _drivetrain = change.Drivetrain;

            // What the screen last read belongs to the previous car; the engines were reset for this one.
            _baselinePending = true;
            _snapshot = AccelerationSnapshot.Empty;
            _brakingSnapshot = BrakingSnapshot.Empty;
            _topSpeedKmh = 0;
            SetCar(change.Car);
        }
    }

    private void SetCar(CarKey? key)
    {
        _car = key;
        _mergedTopSpeedKmh = 0;
        HasCar = key is not null;

        if (key is { } car)
        {
            CarClassLabel = CarInfoFormatter.ClassLabel(_carClass);
            CarPerformanceIndex = car.PerformanceIndex;
            DrivetrainLabel = CarInfoFormatter.DrivetrainLabel(_drivetrain);
            ShowCarName(car);
        }
        else
        {
            CarText = Strings.PerfNoCar;
            CarClassLabel = "";
            CarPerformanceIndex = 0;
            DrivetrainLabel = "";
            CarSummary = Strings.PerfNoCar;
        }

        RebuildRows(); // the current car's row is highlighted
    }

    /// <summary>The first pill and the sentence a screen reader gets: the car's name (nickname or "Car #1234"), then its class, index and drivetrain.</summary>
    private void ShowCarName(CarKey car)
    {
        CarText = CarLabel(car);
        CarSummary = string.Format(
            CultureInfo.CurrentCulture, "{0}, {1} {2}, {3}", CarText, CarClassLabel, CarPerformanceIndex, DrivetrainLabel);
    }

    private string CarLabel(CarKey car) => CarDisplayName.For(car.CarOrdinal, _carNames);

    /// <summary>A car was renamed: the current car's pill and every table row quote the new name.</summary>
    private void OnCarNamesChanged()
    {
        if (_car is { } car)
        {
            ShowCarName(car);
        }

        RebuildRows();
    }

    /// <summary>Opens the rename box for the car being driven (the pencil next to the current-car pill).</summary>
    [RelayCommand]
    private void RenameCurrentCar()
    {
        if (_car is { } car)
        {
            _carRename?.Begin(car.CarOrdinal);
        }
    }

    /// <summary>Opens the rename box for one car of the table ("Rename" on its row).</summary>
    [RelayCommand]
    private void RenameCar(int carOrdinal) => _carRename?.Begin(carOrdinal);

    // ---- Runs and saving --------------------------------------------------------------------------------

    private static Results ResultsOf(AccelerationSnapshot s) =>
        new(
            s.ZeroTo100KmhSeconds,
            s.ZeroTo200KmhSeconds,
            s.QuarterMileSeconds,
            s.QuarterMileTrapSpeedKmh,
            s.HundredTo200KmhSeconds,
            s.ZeroTo60MphSeconds,
            s.SixtyTo130MphSeconds);

    private void TrackRun(AccelerationSnapshot snapshot)
    {
        var running = snapshot.State == AccelerationState.Running;

        if (running && !_wasRunning)
        {
            // The run starts: remember who is driving, so the result is saved under that car.
            _runCar = _car;
            ClearNewBest();
            _aborted = false;
        }
        else if (!running && _wasRunning)
        {
            FinishRun(snapshot);
        }

        _wasRunning = running;
    }

    private void FinishRun(AccelerationSnapshot snapshot)
    {
        if (snapshot.CompletedRuns <= _seenRuns)
        {
            // The engine dropped the run (nothing it shows was measured): the old result is still showing. If the car is still
            // moving the state line says so, instead of silently going back to "ready".
            _aborted = snapshot.State == AccelerationState.Idle;
            return;
        }

        _seenRuns = snapshot.CompletedRuns;

        // A kept run may be partial (only 0-60 mph, only the quarter mile): its unmeasured windows are null here, and the history
        // merges field by field, so a null never touches a stored best.
        var results = ResultsOf(snapshot);
        if (_runCar is { } car)
        {
            var improved = MergeIfLive(
                car,
                _carClass,
                _drivetrain,
                new PerformanceResult(
                    results.ZeroTo100,
                    results.ZeroTo200,
                    results.Quarter,
                    results.Trap,
                    PositiveOrNull(_topSpeedKmh),
                    results.HundredTo200,
                    results.ZeroTo60Mph,
                    results.SixtyTo130Mph));
            _mergedTopSpeedKmh = Math.Max(_mergedTopSpeedKmh, _topSpeedKmh);
            FlushIfLive();

            ZeroTo100IsNewBest = improved.ZeroTo100;
            ZeroTo200IsNewBest = improved.ZeroTo200;
            QuarterMileIsNewBest = improved.QuarterMile;
            TopSpeedIsNewBest = improved.TopSpeed;
            HundredTo200IsNewBest = improved.HundredTo200;
            ZeroTo60MphIsNewBest = improved.ZeroTo60Mph;
            SixtyTo130MphIsNewBest = improved.SixtyTo130Mph;
        }
    }

    /// <summary>
    /// The braking counterpart of <see cref="TrackRun"/>: a stop that is measured becomes a saved result when it FINISHES, under the car
    /// that was driven when it began. The test's <see cref="BrakingSnapshot.CompletedRuns"/> counter says that a stop finished since the
    /// last tick, so it is saved exactly once even when the state went Braking, Done, Armed (or Braking again) between two ticks, or the
    /// numbers equal the previous stop's. A stop the engine dropped (the brake was released, the throttle came back) never moves the
    /// counter: the previous result stays on screen and nothing is saved.
    /// </summary>
    private void TrackBraking(BrakingSnapshot snapshot)
    {
        var braking = snapshot.State == BrakingState.Braking;

        if (braking && !_wasBraking)
        {
            _brakeCar = _car;
            BrakeHundredIsNewBest = false;
            BrakeSixtyMphIsNewBest = false;
        }

        if (snapshot.CompletedRuns > _seenBrakes)
        {
            _seenBrakes = snapshot.CompletedRuns;
            SaveBraking(snapshot.GetCompletedResult());
            _brakeCar = braking ? _car : null; // a stop already under way again belongs to whoever drives now
        }

        _wasBraking = braking;
    }

    private void SaveBraking(BrakingSnapshot result)
    {
        // A whole stop can start and end between two ticks (then no edge named its car): it belongs to the current one.
        if ((_brakeCar ?? _car) is not { } car)
        {
            return;
        }

        var improved = MergeIfLive(
            car,
            _carClass,
            _drivetrain,
            new PerformanceResult(
                null,
                null,
                null,
                null,
                null,
                BrakeHundredMeters: result.HundredToZeroMeters,
                BrakeSixtyMphMeters: result.SixtyToZeroMphMeters));
        FlushIfLive();

        BrakeHundredIsNewBest = improved.BrakeHundred;
        BrakeSixtyMphIsNewBest = improved.BrakeSixtyMph;
    }

    /// <summary>
    /// Top speed is not tied to a run, so it is saved when the pass looks finished: the car has slowed well below its peak.
    /// </summary>
    private void SaveTopSpeedWhenSettled(TelemetryPacket? latest)
    {
        if (latest is { IsRaceOn: true } && _topSpeedKmh > _mergedTopSpeedKmh && latest.SpeedKmh < _topSpeedKmh * TopSpeedSettledRatio)
        {
            SaveTopSpeedOfCurrentCar();
            FlushIfLive();
        }
    }

    private void SaveTopSpeedOfCurrentCar()
    {
        // The value read by the last consistent ReadEngines, which always belongs to _car (see there).
        var top = _topSpeedKmh;
        if (_car is not { } car || top <= 0)
        {
            return;
        }

        var improved = MergeIfLive(car, _carClass, _drivetrain, new PerformanceResult(null, null, null, null, top));
        _mergedTopSpeedKmh = top;
        if (improved.TopSpeed)
        {
            TopSpeedIsNewBest = true;
        }
    }

    // The ONE place that decides "a replay never saves": every write to the per-car history goes through these two, so no call
    // site can forget the check (and the per-tick top-speed path cannot flush during a replay). A replay measures and shows; it
    // changes nothing, in memory or on disk.
    private PerformanceImprovements MergeIfLive(CarKey car, int carClass, int drivetrain, PerformanceResult result) =>
        IsReplaySession ? default : _history.Merge(car, carClass, drivetrain, result);

    private void FlushIfLive()
    {
        if (!IsReplaySession)
        {
            _history.Flush();
        }
    }

    private static double? PositiveOrNull(double value) => value > 0 ? value : null;

    private void ForgetRun()
    {
        _wasRunning = false;
        _runCar = null;
        _aborted = false;
        _mergedTopSpeedKmh = 0;
        _wasBraking = false;
        _brakeCar = null;
        BrakeHundredIsNewBest = false;
        BrakeSixtyMphIsNewBest = false;
        ClearNewBest();
    }

    private void ClearNewBest()
    {
        ZeroTo100IsNewBest = false;
        ZeroTo200IsNewBest = false;
        QuarterMileIsNewBest = false;
        TopSpeedIsNewBest = false;
        HundredTo200IsNewBest = false;
        ZeroTo60MphIsNewBest = false;
        SixtyTo130MphIsNewBest = false;
    }

    // ---- Display ----------------------------------------------------------------------------------------

    /// <summary>Turns the latest snapshots into the bound texts.</summary>
    private void Show()
    {
        var snapshot = _snapshot;

        State = DescribeState(snapshot.State);
        StateText = State switch
        {
            PerformanceRunState.Idle => Strings.PerfStateReady,
            PerformanceRunState.Armed => Strings.PerfStateArmed,
            PerformanceRunState.Running => Strings.PerfStateRunning,
            PerformanceRunState.Done => Strings.PerfStateDone,
            PerformanceRunState.Aborted => Strings.PerfStateAborted,
            _ => Strings.PerfStateWaiting
        };
        ElapsedText = State == PerformanceRunState.Running
            ? string.Format(CultureInfo.CurrentCulture, "{0:F1} s", snapshot.ElapsedSeconds)
            : "";

        ZeroTo100Text = FormatSeconds(snapshot.ZeroTo100KmhSeconds);
        ZeroTo200Text = FormatSeconds(snapshot.ZeroTo200KmhSeconds);
        HundredTo200Text = FormatSeconds(snapshot.HundredTo200KmhSeconds);
        ZeroTo60MphText = FormatSeconds(snapshot.ZeroTo60MphSeconds);
        SixtyTo130MphText = FormatSeconds(snapshot.SixtyTo130MphSeconds);
        QuarterMileText = FormatSeconds(snapshot.QuarterMileSeconds);
        QuarterMileTrapText = snapshot.QuarterMileTrapSpeedKmh is { } trap
            ? string.Format(CultureInfo.CurrentCulture, Strings.PerfTrapFormat, _units.FormatSpeed(trap), _units.SpeedLabel)
            : "";
        TopSpeedText = _topSpeedKmh > 0 ? _units.FormatSpeed(_topSpeedKmh) : NoValue;

        ShowBraking(_brakingSnapshot);
    }

    /// <summary>The braking tile: the distance and time of the unit's variant, and the state line that says what to do next.</summary>
    private void ShowBraking(BrakingSnapshot snapshot)
    {
        var meters = IsMph ? snapshot.SixtyToZeroMphMeters : snapshot.HundredToZeroMeters;
        var seconds = IsMph ? snapshot.SixtyToZeroMphSeconds : snapshot.HundredToZeroSeconds;
        BrakingDistanceText = meters is { } m ? _units.FormatDistance(m) : NoValue;
        BrakingTimeText = snapshot.State == BrakingState.Braking
            ? snapshot.ElapsedSeconds.ToString("F1", CultureInfo.CurrentCulture) // the live stop, until it has a result
            : FormatSeconds(seconds);

        // Like the run line: braking is shown whatever else is true, everything else needs live data (after Stop the engine may still
        // say "Armed", but nothing is armed any more).
        if (snapshot.State != BrakingState.Braking && !_live)
        {
            BrakingRunState = PerformanceRunState.WaitingForData;
            BrakingStateText = Strings.PerfStateWaiting;
            return;
        }

        (BrakingRunState, BrakingStateText) = snapshot.State switch
        {
            BrakingState.Armed => (PerformanceRunState.Armed, ArmingText(Strings.PerfBrakeStateArmed, BrakeStartKmh)),
            BrakingState.Braking => (PerformanceRunState.Running, Strings.PerfBrakeStateBraking),
            BrakingState.Done => (PerformanceRunState.Done, Strings.PerfBrakeStateDone),
            _ => (PerformanceRunState.Idle, ArmingText(Strings.PerfBrakeStateIdle, BrakeArmKmh))
        };
    }

    // The braking engine runs two tests (see BrakingTest): 100-0 km/h arms at 105 km/h and starts at the 100 km/h line, 60-0 mph arms
    // at 63 mph and starts at the 60 mph line. The sentences quote the lines of the test the tile shows, in the player's unit.
    private double BrakeArmKmh => IsMph ? 63 * SpeedCrossing.KmhPerMph : 105;

    private double BrakeStartKmh => IsMph ? 60 * SpeedCrossing.KmhPerMph : 100;

    private string ArmingText(string format, double kmh) =>
        string.Format(CultureInfo.CurrentCulture, format, _units.FormatSpeed(kmh), _units.SpeedLabel);

    private PerformanceRunState DescribeState(AccelerationState engineState)
    {
        // Running is shown whatever else is true (it can only be true with live packets). Everything else needs live data:
        // after Stop the engine may still say "Armed", but nothing is armed any more.
        if (engineState == AccelerationState.Running)
        {
            return PerformanceRunState.Running;
        }

        if (!_live)
        {
            return PerformanceRunState.WaitingForData;
        }

        return engineState switch
        {
            AccelerationState.Armed => PerformanceRunState.Armed,
            AccelerationState.Done => PerformanceRunState.Done,
            _ => _aborted ? PerformanceRunState.Aborted : PerformanceRunState.Idle
        };
    }

    private static string FormatSeconds(double? seconds) =>
        seconds is { } value ? value.ToString("F2", CultureInfo.CurrentCulture) : NoValue;

    private void OnUnitsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!UnitsService.AffectsSpeed(e.PropertyName))
        {
            return;
        }

        OnPropertyChanged(nameof(SpeedUnitLabel));
        OnPropertyChanged(nameof(TopSpeedName)); // the name speaks the unit, which is not a stored property
        RaiseUnitDependentProperties();
        RebuildRows();
        Show();
    }

    /// <summary>Everything the unit setting chooses between: which windows lead, the braking variant, and the table's column titles.</summary>
    private void RaiseUnitDependentProperties()
    {
        foreach (var name in (ReadOnlySpan<string>)
        [
            nameof(WindowATitle), nameof(WindowAText), nameof(WindowAIsNewBest), nameof(WindowAName),
            nameof(WindowBTitle), nameof(WindowBText), nameof(WindowBIsNewBest), nameof(WindowBName),
            nameof(ExtraWindowTitle), nameof(ExtraWindowText), nameof(ExtraWindowIsNewBest), nameof(ExtraWindowName),
            nameof(BrakingTitle), nameof(DistanceUnitLabel), nameof(BrakingIsNewBest), nameof(BrakingName),
            nameof(WindowAColumnTitle), nameof(WindowBColumnTitle)
        ])
        {
            OnPropertyChanged(name);
        }
    }

    // ---- Table ------------------------------------------------------------------------------------------

    private void RebuildRows()
    {
        var records = _history.Records;
        IOrderedEnumerable<CarPerformanceRecord> ordered = SortColumn switch
        {
            PerformanceSortColumn.Car => records.OrderBy(r => r.Car.CarOrdinal).ThenBy(r => r.Car.PerformanceIndex),
            PerformanceSortColumn.ZeroTo100 => records.OrderBy(r => (IsMph ? r.ZeroTo60MphSeconds : r.ZeroTo100Seconds) ?? double.MaxValue),
            PerformanceSortColumn.ZeroTo200 => records.OrderBy(r => (IsMph ? r.SixtyTo130MphSeconds : r.ZeroTo200Seconds) ?? double.MaxValue),
            PerformanceSortColumn.QuarterMile => records.OrderBy(r => r.QuarterMileSeconds ?? double.MaxValue),
            PerformanceSortColumn.TopSpeed => records.OrderByDescending(r => r.TopSpeedKmh ?? 0),
            PerformanceSortColumn.Braking => records.OrderBy(r => (IsMph ? r.BrakeSixtyMphMeters : r.BrakeHundredMeters) ?? double.MaxValue),
            _ => records.OrderByDescending(r => r.UpdatedAt)
        };

        // Ties (and the Recent order) fall back to "most recently improved first".
        Rows = ordered.ThenByDescending(r => r.UpdatedAt).Select(ToRow).ToList();
    }

    private CarPerformanceRow ToRow(CarPerformanceRecord record)
    {
        var quarter = record.QuarterMileSeconds is { } seconds ? $"{FormatSeconds(seconds)} s" : NoValue;
        var trapSpeed = record.QuarterMileSeconds is not null && record.QuarterMileTrapKmh is { } trap
            ? $"{_units.FormatSpeed(trap)} {_units.SpeedLabel}"
            : "";

        var windowA = IsMph ? record.ZeroTo60MphSeconds : record.ZeroTo100Seconds;
        var windowB = IsMph ? record.SixtyTo130MphSeconds : record.ZeroTo200Seconds;
        var brakeMeters = IsMph ? record.BrakeSixtyMphMeters : record.BrakeHundredMeters;

        return new CarPerformanceRow(
            CarLabel(record.Car),
            CarInfoFormatter.ClassLabel(record.CarClass),
            record.Car.PerformanceIndex.ToString(CultureInfo.CurrentCulture),
            CarInfoFormatter.DrivetrainLabel(record.DrivetrainType),
            windowA is { } a ? $"{FormatSeconds(a)} s" : NoValue,
            windowB is { } b ? $"{FormatSeconds(b)} s" : NoValue,
            quarter,
            trapSpeed,
            record.TopSpeedKmh is { } top ? $"{_units.FormatSpeed(top)} {_units.SpeedLabel}" : NoValue,
            brakeMeters is { } meters ? $"{_units.FormatDistance(meters)} {_units.DistanceLabel}" : NoValue,
            record.UpdatedAt == DateTimeOffset.UnixEpoch
                ? NoValue
                : record.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            record.Car == _car,
            record.Car.CarOrdinal,
            DetailsOf(record, quarter, trapSpeed));
    }

    /// <summary>One "title: value" line for every best the car has, in a fixed order, whichever unit the table is showing.</summary>
    private List<string> DetailsOf(CarPerformanceRecord record, string quarter, string trapSpeed)
    {
        var lines = new List<string>();

        void AddSeconds(string title, double? seconds)
        {
            if (seconds is { } value)
            {
                lines.Add($"{title}: {FormatSeconds(value)} s");
            }
        }

        AddSeconds(Strings.PerfTitle100, record.ZeroTo100Seconds);
        AddSeconds(Strings.PerfTitle200, record.ZeroTo200Seconds);
        AddSeconds(Strings.PerfTitle100To200, record.HundredTo200Seconds);
        AddSeconds(Strings.PerfTitle60Mph, record.ZeroTo60MphSeconds);
        AddSeconds(Strings.PerfTitle60To130Mph, record.SixtyTo130MphSeconds);

        if (record.QuarterMileSeconds is not null)
        {
            lines.Add(trapSpeed.Length > 0
                ? $"{Strings.PerfTitleQuarter}: {quarter}, {trapSpeed}"
                : $"{Strings.PerfTitleQuarter}: {quarter}");
        }

        if (record.TopSpeedKmh is { } top)
        {
            lines.Add($"{Strings.PerfTitleTopSpeed}: {_units.FormatSpeed(top)} {_units.SpeedLabel}");
        }

        if (record.BrakeHundredMeters is { } brake100)
        {
            lines.Add($"{Strings.PerfTitleBrake100}: {_units.FormatDistance(brake100)} {_units.DistanceLabel}");
        }

        if (record.BrakeSixtyMphMeters is { } brake60)
        {
            lines.Add($"{Strings.PerfTitleBrake60Mph}: {_units.FormatDistance(brake60)} {_units.DistanceLabel}");
        }

        return lines;
    }
}
