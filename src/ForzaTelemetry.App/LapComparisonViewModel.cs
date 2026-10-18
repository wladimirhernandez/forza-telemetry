using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core.Laps;

namespace ForzaTelemetry.App;

/// <summary>
/// The "Lap comparison" half of the Analysis page: the best complete lap and the lap in progress, laid over each other by distance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cheap refresh.</b> Same contract as <see cref="AnalysisViewModel"/>: the shell calls <see cref="Refresh"/> only while the page
/// is showing, the sample arrays are allocated once here and rewritten in place, and the chart is told to rebuild only through
/// <see cref="DataVersion"/>. The recorder's own versions decide what to copy, so the best lap (which changes a few times per
/// session) is not copied 20 times a second.
/// </para>
/// <para>
/// <b>What is compared.</b> The recorder keeps the lap in progress only when it was seen from the start line
/// (<see cref="LapTraceStatus.CurrentIsComparable"/>); a lap joined midway has no honest "distance into the lap", so it is not drawn
/// and the page says why, instead of overlaying a trace shifted by an unknown offset.
/// </para>
/// </remarks>
public sealed partial class LapComparisonViewModel : ObservableObject
{
    /// <summary>Below this length the distance axis reads in meters (km would need decimals for a short lap).</summary>
    private const double MetersBeforeKilometers = 2000;

    private const double FeetPerMile = 5280;
    private const double MetersPerMile = UnitsService.KilometersPerMile * 1000;

    private readonly LapTraceRecorder _recorder;
    private readonly UnitsService _units;

    private int _seenCurrentVersion = -1;
    private int _seenBestVersion = -1;
    private bool _seenComparable;
    private string _seenUnitKey = "";

    /// <param name="recorder">The engine that records both laps (fed by the receive loop).</param>
    /// <param name="units">Speed and distance units and labels.</param>
    public LapComparisonViewModel(LapTraceRecorder recorder, UnitsService units)
    {
        _recorder = recorder;
        _units = units;
        CurrentTrace = new LapTraceSample[LapTraceRecorder.MaxSamplesPerLap];
        BestTrace = new LapTraceSample[LapTraceRecorder.MaxSamplesPerLap];
        _speedUnitLabel = units.SpeedLabel;
        _speedScale = units.ConvertSpeed(3.6);
    }

    /// <summary>The lap in progress the chart draws; only the first <see cref="CurrentCount"/> samples are valid.</summary>
    public LapTraceSample[] CurrentTrace { get; }

    /// <summary>The best complete lap the chart draws; only the first <see cref="BestCount"/> samples are valid.</summary>
    public LapTraceSample[] BestTrace { get; }

    [ObservableProperty]
    private int _currentCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBestLap))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _bestCount;

    /// <summary>Changes whenever either trace array was refilled or a unit changed (the chart's rebuild signal).</summary>
    [ObservableProperty]
    private int _dataVersion;

    /// <summary>Changes whenever <see cref="BestTrace"/> was refilled: the chart rebuilds the best lap's lines only then.</summary>
    [ObservableProperty]
    private int _bestVersion;

    /// <summary>There is a best complete lap to compare against: the chart shows. Otherwise the empty state does.</summary>
    public bool HasBestLap => BestCount >= 2;

    public bool IsEmpty => !HasBestLap;

    /// <summary>Multiplier from m/s to the chosen speed unit (3.6 for km/h).</summary>
    [ObservableProperty]
    private double _speedScale = 3.6;

    [ObservableProperty]
    private string _speedUnitLabel;

    /// <summary>Multiplier from meters to the distance axis unit (1 for m, 0.001 for km, about 3.28 for ft, about 0.000621 for mi).</summary>
    [ObservableProperty]
    private double _distanceScale = 1;

    /// <summary>The distance axis unit: <c>m</c>, <c>km</c>, <c>ft</c> or <c>mi</c>.</summary>
    [ObservableProperty]
    private string _distanceUnit = Strings.DistanceUnit;

    /// <summary>The best lap's time, <c>m:ss.fff</c>, or the placeholder when there is none.</summary>
    [ObservableProperty]
    private string _bestLapText = LapTimeFormatter.Placeholder;

    /// <summary>Which lap that was (for example <c>Lap 3</c>); empty when there is none.</summary>
    [ObservableProperty]
    private string _bestLapNote = "";

    /// <summary>The lap clock of the lap in progress, at the newest recorded point.</summary>
    [ObservableProperty]
    private string _currentLapText = LapTimeFormatter.Placeholder;

    /// <summary>Which lap is in progress (for example <c>Lap 4</c>); empty when it is not drawn.</summary>
    [ObservableProperty]
    private string _currentLapNote = "";

    /// <summary>The gap to the best lap at the newest point (<c>+0.412</c>), or the placeholder.</summary>
    [ObservableProperty]
    private string _gapText = LapTimeFormatter.Placeholder;

    /// <summary>Closed-set colour state of the gap tile (the same one the Laps page uses).</summary>
    [ObservableProperty]
    private LapDeltaState _gapState = LapDeltaState.Hidden;

    /// <summary>How far into the lap the newest point is, in the axis unit (for example <c>1.42 km</c>).</summary>
    [ObservableProperty]
    private string _distanceText = "-";

    /// <summary>Explains why the lap in progress is not drawn; empty when it is, or when there is nothing to explain.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _note = "";

    public bool HasNote => Note.Length > 0;

    /// <summary>A new listening session starts: both laps leave the screen (the recorder itself is reset by the shell's engine reset).</summary>
    public void BeginSession()
    {
        _seenCurrentVersion = -1;
        _seenBestVersion = -1;
        _seenComparable = false;
        CurrentCount = 0;
        BestCount = 0;
        BestVersion++;
        ShowReadouts(default, 0, false);
        DataVersion++; // the chart must redraw as empty even if it is the page on screen right now
    }

    /// <summary>Pulls the newest traces and readouts. Called by the page, on the UI thread, while the comparison is showing.</summary>
    public void Refresh()
    {
        var status = _recorder.Status;
        var changed = false;

        var unitKey = _units.SpeedLabel;
        if (unitKey != _seenUnitKey)
        {
            _seenUnitKey = unitKey;
            SpeedUnitLabel = _units.SpeedLabel;
            SpeedScale = _units.ConvertSpeed(3.6);
            changed = true;
        }

        if (status.BestVersion != _seenBestVersion)
        {
            // The version read BEFORE the copy: a lap promoted in between makes the next tick copy again, never skip.
            _seenBestVersion = status.BestVersion;
            BestCount = _recorder.CopyBestTo(BestTrace);
            BestVersion++;
            changed = true;
        }

        // "Comparable" can flip without a new sample (the array filled up in free roam), hence its own check.
        if (status.CurrentVersion != _seenCurrentVersion || status.CurrentIsComparable != _seenComparable)
        {
            _seenCurrentVersion = status.CurrentVersion;
            _seenComparable = status.CurrentIsComparable;
            var copied = _recorder.CopyCurrentTo(CurrentTrace);

            // A lap joined midway has no honest "distance into the lap": it is not drawn (see the class remarks).
            CurrentCount = status.CurrentIsComparable ? copied : 0;
            changed = true;
        }

        if (changed)
        {
            UpdateAxisUnit();
            ShowReadouts(status, status.CurrentLapNumber, status.CurrentIsComparable);
            DataVersion++;
        }
    }

    /// <summary>
    /// Picks the distance axis unit for a lap of the given length: meters, or kilometers from <see cref="MetersBeforeKilometers"/>
    /// on; feet, or miles from half a mile on, when speeds are shown in mph (the same pairing as <see cref="UnitsService.DistanceLabel"/>).
    /// </summary>
    internal static (double Scale, string Label) ChooseDistanceUnit(double lengthMeters, SpeedUnit speedUnit) =>
        speedUnit == SpeedUnit.Mph
            ? lengthMeters >= MetersPerMile / 2 ? (1 / MetersPerMile, InsightsStrings.DistanceMi) : (UnitsService.FeetPerMeter, Strings.DistanceUnitFeet)
            : lengthMeters >= MetersBeforeKilometers ? (0.001, InsightsStrings.DistanceKm) : (1, Strings.DistanceUnit);

    private void UpdateAxisUnit()
    {
        // The axis spans the longer of the two laps, so it does not flip units while the lap in progress grows past the best one.
        var length = Math.Max(
            BestCount > 0 ? BestTrace[BestCount - 1].DistanceMeters : 0,
            CurrentCount > 0 ? CurrentTrace[CurrentCount - 1].DistanceMeters : 0);
        (DistanceScale, DistanceUnit) = ChooseDistanceUnit(length, _units.SpeedUnit);
    }

    private void ShowReadouts(LapTraceStatus status, int currentLapNumber, bool comparable)
    {
        BestLapText = status.BestLapSeconds is { } best ? LapTimeFormatter.Format(best) : LapTimeFormatter.Placeholder;
        BestLapNote = status.BestLapSeconds is null
            ? ""
            : string.Format(CultureInfo.CurrentCulture, Strings.LapsLapFormat, status.BestLapNumber);

        if (CurrentCount > 0)
        {
            var newest = CurrentTrace[CurrentCount - 1];
            CurrentLapText = LapTimeFormatter.Format(newest.TimeSeconds);
            CurrentLapNote = string.Format(CultureInfo.CurrentCulture, Strings.LapsLapFormat, currentLapNumber);
            DistanceText = (newest.DistanceMeters * DistanceScale).ToString(DistanceScale < 0.01 ? "0.00" : "0", CultureInfo.CurrentCulture)
                + " " + DistanceUnit;
            ShowGap(LapTraceMath.DeltaSeconds(BestTrace.AsSpan(0, BestCount), newest.DistanceMeters, newest.TimeSeconds));
        }
        else
        {
            CurrentLapText = LapTimeFormatter.Placeholder;
            CurrentLapNote = "";
            DistanceText = "-";
            ShowGap(null);
        }

        Note = !comparable && status.CurrentCount > 0 ? InsightsStrings.CompareNotComparable : "";
    }

    private void ShowGap(double? gap)
    {
        if (gap is not { } value || !double.IsFinite(value))
        {
            GapState = LapDeltaState.Hidden;
            GapText = LapTimeFormatter.Placeholder;
            return;
        }

        GapText = LapTimeFormatter.FormatDelta(value);

        // "Level" is decided on the ROUNDED value, like the Laps page, so the tile never shows "+0.000" in red.
        var rounded = Math.Round(value, 3, MidpointRounding.AwayFromZero);
        GapState = rounded == 0 ? LapDeltaState.Even : rounded < 0 ? LapDeltaState.Ahead : LapDeltaState.Behind;
    }
}
