using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Performance;

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
    ZeroTo100,
    ZeroTo200,
    QuarterMile,
    TopSpeed
}

/// <summary>One row of the per-car table, already formatted for display in the units that were selected when it was built.</summary>
public sealed record CarPerformanceRow(
    string CarLabel,
    string ClassLabel,
    string PerformanceIndexText,
    string DrivetrainLabel,
    string ZeroTo100Text,
    string ZeroTo200Text,
    string QuarterMileText,
    string TopSpeedText,
    string UpdatedText,
    bool IsCurrentCar)
{
    /// <summary>The whole row as one sentence for screen readers (a row of five numbers means little read cell by cell).</summary>
    public string Summary =>
        $"{CarLabel}, {ClassLabel} {PerformanceIndexText}, {DrivetrainLabel}. 0-100: {ZeroTo100Text}. 0-200: {ZeroTo200Text}. "
        + $"{QuarterMileText}. {TopSpeedText}. {UpdatedText}";
}

/// <summary>
/// The Performance screen: the live 0-100 / 0-200 / quarter-mile / top-speed cards, the run state, and the best results per car.
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
/// <b>Units.</b> The runs are defined in metric (0-100 and 0-200 km/h): that is what the engine measures and what the
/// car community quotes worldwide. The SPEEDS shown (line speed, top speed) follow the unit setting. In mph the cards keep
/// their km/h titles, and a note says the thresholds are about 62 and 124 mph, rather than silently changing what is timed
/// (a "0-60 mph" card would be a different, shorter run, and would need its own line in the engine).
/// </para>
/// </remarks>
public sealed partial class PerformanceViewModel : ObservableObject
{
    /// <summary>Once the car has slowed below this fraction of its top speed, the top-speed pass is over and worth saving.</summary>
    private const double TopSpeedSettledRatio = 0.85;

    private const string NoValue = "--";

    private readonly AccelerationTimer _acceleration;
    private readonly TopSpeedTracker _topSpeed;
    private readonly PerformanceHistoryStore _history;
    private readonly UnitsService _units;
    private readonly IConfirmationService _confirmation;

    // The car being driven (null until a packet carries a real one) and what the game says about it.
    private CarKey? _car;
    private int _carClass;
    private int _drivetrain;

    // Run edge detection (see the class remarks). The "results" are the four measured values of a snapshot.
    private bool _wasRunning;
    private Results _restingResults;
    private Results _resultsBeforeRun;
    private CarKey? _runCar;
    private bool _aborted;

    // The last top speed folded into the history for the current car.
    private double _mergedTopSpeedKmh;

    private AccelerationSnapshot _snapshot = AccelerationSnapshot.Empty;
    private double _topSpeedKmh;
    private bool _live;

    private readonly record struct Results(double? ZeroTo100, double? ZeroTo200, double? Quarter, double? Trap);

    /// <param name="acceleration">The engine that times the runs (fed by the receive loop).</param>
    /// <param name="topSpeed">The engine that tracks the session top speed (fed by the receive loop).</param>
    /// <param name="history">The per-car bests.</param>
    /// <param name="units">Converts and labels the speeds; optional (metric) for tests and the designer.</param>
    /// <param name="confirmation">Asks before the history is cleared; optional (clearing then needs no confirmation, for tests).</param>
    public PerformanceViewModel(
        AccelerationTimer acceleration,
        TopSpeedTracker topSpeed,
        PerformanceHistoryStore history,
        UnitsService? units = null,
        IConfirmationService? confirmation = null)
    {
        _acceleration = acceleration;
        _topSpeed = topSpeed;
        _history = history;
        _units = units ?? new UnitsService();
        _confirmation = confirmation ?? new AlwaysConfirm();

        _history.Changed += (_, _) => RebuildRows();
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

    // ---- Result cards -------------------------------------------------------------------------------------

    [ObservableProperty]
    private string _zeroTo100Text = NoValue;

    [ObservableProperty]
    private string _zeroTo200Text = NoValue;

    [ObservableProperty]
    private string _quarterMileText = NoValue;

    /// <summary>The speed at the quarter-mile line with its unit (<c>187 km/h at the line</c>), or empty before one is measured.</summary>
    [ObservableProperty]
    private string _quarterMileTrapText = "";

    [ObservableProperty]
    private string _topSpeedText = NoValue;

    /// <summary>The unit label under the top speed: <c>km/h</c> or <c>mph</c>.</summary>
    public string SpeedUnitLabel => _units.SpeedLabel;

    // "New best": the value of the run that just ended beat an earlier best of this car. Cleared when the next run starts.
    [ObservableProperty]
    private bool _zeroTo100IsNewBest;

    [ObservableProperty]
    private bool _zeroTo200IsNewBest;

    [ObservableProperty]
    private bool _quarterMileIsNewBest;

    [ObservableProperty]
    private bool _topSpeedIsNewBest;

    /// <summary>True in mph: the note that the runs are still timed to 100 and 200 km/h is shown (see the class remarks).</summary>
    public bool ShowThresholdNote => _units.SpeedUnit == SpeedUnit.Mph;

    // Card names for screen readers, with the value spoken, e.g. "0-100 km/h: 2.84 seconds".
    public string ZeroTo100Name => $"{Strings.PerfTitle100}: {ZeroTo100Text}";

    public string ZeroTo200Name => $"{Strings.PerfTitle200}: {ZeroTo200Text}";

    public string QuarterMileName => $"{Strings.PerfTitleQuarter}: {QuarterMileText} {QuarterMileTrapText}".TrimEnd();

    public string TopSpeedName => $"{Strings.PerfTitleTopSpeed}: {TopSpeedText} {SpeedUnitLabel}";

    // ---- Current car --------------------------------------------------------------------------------------

    /// <summary>False until a packet carries a real car (performance index 100..999).</summary>
    [ObservableProperty]
    private bool _hasCar;

    /// <summary>For example <c>Car #1234 · A 780 · AWD</c>; a localized "no car yet" text before the first one.</summary>
    [ObservableProperty]
    private string _carSummary = Strings.PerfNoCar;

    // ---- History table ------------------------------------------------------------------------------------

    /// <summary>The per-car bests in the chosen order, formatted for display. Replaced as a whole when anything changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistory))]
    [NotifyCanExecuteChangedFor(nameof(ClearHistoryCommand))]
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
    private PerformanceSortColumn _sortColumn = PerformanceSortColumn.Recent;

    public bool IsSortedByRecent => SortColumn == PerformanceSortColumn.Recent;

    public bool IsSortedByCar => SortColumn == PerformanceSortColumn.Car;

    public bool IsSortedByZeroTo100 => SortColumn == PerformanceSortColumn.ZeroTo100;

    public bool IsSortedByZeroTo200 => SortColumn == PerformanceSortColumn.ZeroTo200;

    public bool IsSortedByQuarterMile => SortColumn == PerformanceSortColumn.QuarterMile;

    public bool IsSortedByTopSpeed => SortColumn == PerformanceSortColumn.TopSpeed;

    partial void OnSortColumnChanged(PerformanceSortColumn value) => RebuildRows();

    /// <summary>Sorts the table by a column (the header buttons pass the column as the command parameter).</summary>
    [RelayCommand]
    private void SortBy(PerformanceSortColumn column) => SortColumn = column;

    // ---- Commands -----------------------------------------------------------------------------------------

    /// <summary>Forgets the run results and the session top speed (the per-car history is kept; a new best is saved first).</summary>
    [RelayCommand]
    private void ResetRun()
    {
        SaveTopSpeedOfCurrentCar();
        _acceleration.Reset();
        _topSpeed.Reset();
        ForgetRun();
        _snapshot = AccelerationSnapshot.Empty;
        _topSpeedKmh = 0;
        Show();
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
    /// A new listening session starts (Start): everything the previous one measured leaves the screen, engines included.
    /// The saved history is untouched.
    /// </summary>
    public void BeginSession()
    {
        _acceleration.Reset();
        _topSpeed.Reset();
        _car = null;
        _live = false;
        ForgetRun();
        _snapshot = AccelerationSnapshot.Empty;
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
        _history.Flush();
    }

    /// <summary>
    /// Called once per UI tick with the latest packet (or null when none): follows the car, detects the end of a run, saves
    /// what is worth saving and refreshes the texts. Cheap by design: two snapshot reads and a few property writes.
    /// </summary>
    public void Refresh(TelemetryPacket? latest)
    {
        // Only a driving packet says anything about the car: menus and pauses send IsRaceOn = false with zeroed fields.
        _live = latest is { IsRaceOn: true };
        if (latest is { IsRaceOn: true })
        {
            ObserveCar(latest);
        }

        _snapshot = _acceleration.Current;
        _topSpeedKmh = _topSpeed.TopSpeedKmh;

        TrackRun(_snapshot);
        SaveTopSpeedWhenSettled(latest);
        Show();
    }

    // ---- Car ---------------------------------------------------------------------------------------------

    private void ObserveCar(TelemetryPacket packet)
    {
        var key = new CarKey(packet.CarOrdinal, packet.CarPerformanceIndex);
        if (!key.IsValid)
        {
            return; // not a car (loading, a menu): keep following the one we had
        }

        _carClass = packet.CarClass;
        _drivetrain = packet.DrivetrainType;

        if (key == _car)
        {
            return;
        }

        if (_car is not null)
        {
            // A different car (or the same one after an upgrade): save the old car's top speed, then clear the cards, because
            // the engines would otherwise keep showing the previous car's results as if they were this one's.
            SaveTopSpeedOfCurrentCar();
            _history.Flush();
            _acceleration.Reset();
            _topSpeed.Reset();
            ForgetRun();
        }

        SetCar(key);
    }

    private void SetCar(CarKey? key)
    {
        _car = key;
        _mergedTopSpeedKmh = 0;
        HasCar = key is not null;
        CarSummary = key is { } car
            ? string.Format(
                CultureInfo.CurrentCulture,
                "{0} · {1} {2} · {3}",
                CarLabel(car),
                CarInfoFormatter.ClassLabel(_carClass),
                car.PerformanceIndex,
                CarInfoFormatter.DrivetrainLabel(_drivetrain))
            : Strings.PerfNoCar;
        RebuildRows(); // the current car's row is highlighted
    }

    private static string CarLabel(CarKey car) =>
        string.Format(CultureInfo.CurrentCulture, Strings.PerfCarOrdinalFormat, car.CarOrdinal);

    // ---- Runs and saving --------------------------------------------------------------------------------

    private static Results ResultsOf(AccelerationSnapshot s) =>
        new(s.ZeroTo100KmhSeconds, s.ZeroTo200KmhSeconds, s.QuarterMileSeconds, s.QuarterMileTrapSpeedKmh);

    private void TrackRun(AccelerationSnapshot snapshot)
    {
        var running = snapshot.State == AccelerationState.Running;

        if (running && !_wasRunning)
        {
            // The run starts: remember who is driving and what the screen showed just before, so the end can tell
            // "this run measured something" from "this run was dropped and the old result is still showing".
            _runCar = _car;
            _resultsBeforeRun = _restingResults;
            ClearNewBest();
            _aborted = false;
        }
        else if (!running && _wasRunning)
        {
            FinishRun(snapshot);
        }

        if (!running)
        {
            _restingResults = ResultsOf(snapshot);
        }

        _wasRunning = running;
    }

    private void FinishRun(AccelerationSnapshot snapshot)
    {
        var results = ResultsOf(snapshot);
        if (results == _resultsBeforeRun)
        {
            // The engine dropped the run (nothing was measured): the old result is still showing. If the car is still
            // moving the state line says so, instead of silently going back to "ready".
            _aborted = snapshot.State == AccelerationState.Idle;
            return;
        }

        if (_runCar is { } car)
        {
            var improved = _history.Merge(
                car,
                _carClass,
                _drivetrain,
                new PerformanceResult(
                    results.ZeroTo100, results.ZeroTo200, results.Quarter, results.Trap, PositiveOrNull(_topSpeedKmh)));
            _mergedTopSpeedKmh = Math.Max(_mergedTopSpeedKmh, _topSpeedKmh);
            _history.Flush();

            ZeroTo100IsNewBest = improved.ZeroTo100;
            ZeroTo200IsNewBest = improved.ZeroTo200;
            QuarterMileIsNewBest = improved.QuarterMile;
            TopSpeedIsNewBest = improved.TopSpeed;
        }
    }

    /// <summary>
    /// Top speed is not tied to a run, so it is saved when the pass looks finished: the car has slowed well below its peak.
    /// </summary>
    private void SaveTopSpeedWhenSettled(TelemetryPacket? latest)
    {
        if (latest is { IsRaceOn: true } && _topSpeedKmh > _mergedTopSpeedKmh && latest.SpeedKmh < _topSpeedKmh * TopSpeedSettledRatio)
        {
            SaveTopSpeedOfCurrentCar();
            _history.Flush();
        }
    }

    private void SaveTopSpeedOfCurrentCar()
    {
        var top = _topSpeed.TopSpeedKmh;
        if (_car is not { } car || top <= 0)
        {
            return;
        }

        var improved = _history.Merge(car, _carClass, _drivetrain, new PerformanceResult(null, null, null, null, top));
        _mergedTopSpeedKmh = top;
        if (improved.TopSpeed)
        {
            TopSpeedIsNewBest = true;
        }
    }

    private static double? PositiveOrNull(double value) => value > 0 ? value : null;

    private void ForgetRun()
    {
        _wasRunning = false;
        _restingResults = default;
        _resultsBeforeRun = default;
        _runCar = null;
        _aborted = false;
        _mergedTopSpeedKmh = 0;
        ClearNewBest();
    }

    private void ClearNewBest()
    {
        ZeroTo100IsNewBest = false;
        ZeroTo200IsNewBest = false;
        QuarterMileIsNewBest = false;
        TopSpeedIsNewBest = false;
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
        QuarterMileText = FormatSeconds(snapshot.QuarterMileSeconds);
        QuarterMileTrapText = snapshot.QuarterMileTrapSpeedKmh is { } trap
            ? string.Format(CultureInfo.CurrentCulture, Strings.PerfTrapFormat, _units.FormatSpeed(trap), _units.SpeedLabel)
            : "";
        TopSpeedText = _topSpeedKmh > 0 ? _units.FormatSpeed(_topSpeedKmh) : NoValue;

        OnPropertyChanged(nameof(ZeroTo100Name));
        OnPropertyChanged(nameof(ZeroTo200Name));
        OnPropertyChanged(nameof(QuarterMileName));
        OnPropertyChanged(nameof(TopSpeedName));
    }

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
        OnPropertyChanged(nameof(ShowThresholdNote));
        RebuildRows();
        Show();
    }

    // ---- Table ------------------------------------------------------------------------------------------

    private void RebuildRows()
    {
        var records = _history.Records;
        IOrderedEnumerable<CarPerformanceRecord> ordered = SortColumn switch
        {
            PerformanceSortColumn.Car => records.OrderBy(r => r.Car.CarOrdinal).ThenBy(r => r.Car.PerformanceIndex),
            PerformanceSortColumn.ZeroTo100 => records.OrderBy(r => r.ZeroTo100Seconds ?? double.MaxValue),
            PerformanceSortColumn.ZeroTo200 => records.OrderBy(r => r.ZeroTo200Seconds ?? double.MaxValue),
            PerformanceSortColumn.QuarterMile => records.OrderBy(r => r.QuarterMileSeconds ?? double.MaxValue),
            PerformanceSortColumn.TopSpeed => records.OrderByDescending(r => r.TopSpeedKmh ?? 0),
            _ => records.OrderByDescending(r => r.UpdatedAt)
        };

        // Ties (and the Recent order) fall back to "most recently improved first".
        Rows = ordered.ThenByDescending(r => r.UpdatedAt).Select(ToRow).ToList();
    }

    private CarPerformanceRow ToRow(CarPerformanceRecord record)
    {
        var quarter = FormatSeconds(record.QuarterMileSeconds);
        if (record.QuarterMileSeconds is not null)
        {
            quarter += " s";
            if (record.QuarterMileTrapKmh is { } trap)
            {
                quarter += $"  ·  {_units.FormatSpeed(trap)} {_units.SpeedLabel}";
            }
        }

        return new CarPerformanceRow(
            CarLabel(record.Car),
            CarInfoFormatter.ClassLabel(record.CarClass),
            record.Car.PerformanceIndex.ToString(CultureInfo.CurrentCulture),
            CarInfoFormatter.DrivetrainLabel(record.DrivetrainType),
            record.ZeroTo100Seconds is { } t100 ? $"{FormatSeconds(t100)} s" : NoValue,
            record.ZeroTo200Seconds is { } t200 ? $"{FormatSeconds(t200)} s" : NoValue,
            quarter,
            record.TopSpeedKmh is { } top ? $"{_units.FormatSpeed(top)} {_units.SpeedLabel}" : NoValue,
            record.UpdatedAt == DateTimeOffset.UnixEpoch
                ? NoValue
                : record.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            record.Car == _car);
    }
}
