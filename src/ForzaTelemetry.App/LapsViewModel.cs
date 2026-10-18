using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Laps;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App;

/// <summary>How the live delta compares with the best lap; the view maps it to the pill colour.</summary>
public enum LapDeltaState
{
    /// <summary>No reference lap yet, or not racing: the pill is hidden.</summary>
    Hidden,

    /// <summary>Faster than the best lap at this point of the track (negative delta, green).</summary>
    Ahead,

    /// <summary>Slower than the best lap at this point of the track (positive delta, red).</summary>
    Behind,

    /// <summary>Level with the best lap to the millisecond (neutral).</summary>
    Even
}

/// <summary>One finished lap of the session as shown in the list.</summary>
/// <param name="NumberText">The lap number.</param>
/// <param name="TimeText">The lap time, <c>m:ss.fff</c>.</param>
/// <param name="DeltaText">The gap to the session's best lap (<c>+0.412</c>); empty for the best lap itself and for a lap with no time.</param>
/// <param name="IsBest">True for the fastest lap of the session; the view highlights it.</param>
public sealed record LapRow(string NumberText, string TimeText, string DeltaText, bool IsBest)
{
    /// <summary>The whole row as one sentence for screen readers.</summary>
    public string Summary => IsBest
        ? $"{Strings.LapsColLap} {NumberText}, {TimeText}, {Strings.LapsBestTag}"
        : string.IsNullOrEmpty(DeltaText)
            ? $"{Strings.LapsColLap} {NumberText}, {TimeText}"
            : $"{Strings.LapsColLap} {NumberText}, {TimeText}, {DeltaText}";
}

/// <summary>
/// The Laps screen: the running lap clock, the live delta against the best lap, last and best lap, and the session's lap list.
/// </summary>
/// <remarks>
/// All the lap logic (detecting a finished lap, remembering the best one, comparing against it by DISTANCE into the lap) is in
/// <see cref="LapTracker"/>, which sees every packet on the receive thread. This class only reads its snapshot once per UI tick
/// and turns it into text, so, like <see cref="PerformanceViewModel"/>, it adds no timing error of its own: the numbers are as
/// exact as the game's own clock, whatever the refresh rate.
/// <para>
/// <b>"Racing".</b> The hero clock and the delta only mean something while driving packets arrive (<c>IsRaceOn</c>) and a lap
/// clock is running. Outside that (not listening, a menu, a pause) they show placeholders instead of the last, frozen,
/// number: a frozen delta looks live and misleads. Last lap, best lap and the list DO stay, because after a session that is
/// what the user wants to read.
/// </para>
/// <para>
/// <b>Delta to best in the list.</b> Each row compares with the fastest lap of THIS session's list, so the numbers on screen
/// add up (a lap's gap is its time minus the highlighted time). The "Best lap" card prefers the game's own best, which may be
/// from an earlier session and is therefore not necessarily in the list.
/// </para>
/// </remarks>
public sealed partial class LapsViewModel : ObservableObject
{
    private readonly LapTracker _tracker;

    // The immutable lap array of the snapshot the rows were built from: the tracker replaces it only when a lap finishes,
    // so reference equality tells "nothing new" without comparing or rebuilding 20 times a second.
    private IReadOnlyList<LapRecord>? _rowsBuiltFrom;

    private readonly CsvExportRunner? _export;
    private readonly TimeProvider _time;

    /// <param name="tracker">The engine that follows laps (fed by the receive loop).</param>
    /// <param name="saveDialog">Asks where to save the CSV; optional (the Export button is then disabled).</param>
    /// <param name="logger">Receives export failures; optional.</param>
    /// <param name="time">Clock for the suggested file name; defaults to the system clock.</param>
    public LapsViewModel(LapTracker tracker, IFileSaveDialog? saveDialog = null, ILogger? logger = null, TimeProvider? time = null)
    {
        _tracker = tracker;
        _time = time ?? TimeProvider.System;
        _export = saveDialog is null ? null : new CsvExportRunner(saveDialog, ExportStatus, logger);
        Refresh(null);
    }

    /// <summary>The message next to the Export button (saved, or why not).</summary>
    public ExportFeedback ExportStatus { get; } = new();

    /// <summary>There is something to export and somewhere to ask where to put it.</summary>
    public bool CanExport => HasLaps && _export is not null;

    /// <summary>Explains the Export button; when it is disabled, also says why (a greyed-out button says nothing by itself).</summary>
    public string ExportTooltip => CanExport ? ExportStrings.ExportLapsTooltip : ExportStrings.ExportLapsDisabledTooltip;

    /// <summary>
    /// Saves the session's laps (the list as the game counted them, oldest first) as a CSV file the user picks the place of. The
    /// laps are read from the tracker's immutable snapshot BEFORE the dialog opens, so the file is what the list showed when the
    /// button was pressed even if a lap finishes while the dialog is up.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportCsvAsync()
    {
        var laps = _tracker.Current.Laps;
        await _export!.RunAsync(
            CsvExportRunner.SuggestedName(ExportStrings.ExportLapsFileName, _time.GetLocalNow()),
            stream => LapsCsv.Write(stream, laps));
    }

    // [NotifyPropertyChangedFor(HeroName)] on the four values the hero sentence is built from: the screen-reader name is
    // announced as changed exactly when one of them changed, instead of Refresh raising it on every 20 Hz tick.

    /// <summary>True while driving packets arrive and a lap clock is running (see the class remarks).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    [NotifyPropertyChangedFor(nameof(HasEmptyText))]
    [NotifyPropertyChangedFor(nameof(HeroName))]
    private bool _isRacing;

    /// <summary>
    /// The running lap time, <c>m:ss.fff</c>; while no lap is in progress it is the zero time (<c>0:00.000</c>), which the view
    /// shows small and muted next to a "no lap in progress" sentence (a huge row of dashes read as a broken clock).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroName))]
    private string _currentLapText = LapTimeFormatter.Zero;

    /// <summary>For example <c>Lap 3</c>; empty when not racing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroName))]
    private string _lapNumberText = "";

    /// <summary>The live gap to the best lap (<c>+0.412</c> / <c>-0.412</c>), empty when there is no reference.</summary>
    [ObservableProperty]
    private string _deltaText = "";

    /// <summary>Closed-set colour state of the delta pill.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDelta))]
    private LapDeltaState _deltaState = LapDeltaState.Hidden;

    /// <summary>True when the delta pill is shown.</summary>
    public bool HasDelta => DeltaState != LapDeltaState.Hidden;

    /// <summary>The delta as a sentence for screen readers ("Behind your best by 0.412 s"); empty when hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroName))]
    private string _deltaName = "";

    [ObservableProperty]
    private string _lastLapText = LapTimeFormatter.Placeholder;

    /// <summary>False while <see cref="LastLapText"/> is the placeholder; the view dims it.</summary>
    [ObservableProperty]
    private bool _hasLastLap;

    [ObservableProperty]
    private string _bestLapText = LapTimeFormatter.Placeholder;

    /// <summary>False while <see cref="BestLapText"/> is the placeholder; the view dims it.</summary>
    [ObservableProperty]
    private bool _hasBestLap;

    /// <summary>The session's finished laps, newest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLaps))]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    [NotifyPropertyChangedFor(nameof(HasEmptyText))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    [NotifyPropertyChangedFor(nameof(ExportTooltip))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    private IReadOnlyList<LapRow> _rows = [];

    public bool HasLaps => Rows.Count > 0;

    /// <summary>
    /// What the lap list says while it is empty: the "start a race" hint when nothing is happening, or "complete a lap" once a
    /// lap clock is running; empty when there are laps to list.
    /// </summary>
    public string EmptyText => HasLaps
        ? ""
        : IsRacing ? Strings.LapsEmptyNoLapsYet : Strings.LapsEmptyNotRacing;

    public bool HasEmptyText => EmptyText.Length > 0;

    /// <summary>The hero clock as one sentence for screen readers, for example "Lap 3, 1:02.345, behind your best by 0.412 s".</summary>
    public string HeroName => IsRacing
        ? string.Join(", ", new[] { LapNumberText, CurrentLapText, DeltaName }.Where(part => part.Length > 0))
        : Strings.LapsNoLapInProgress; // the same sentence the hero shows while idle (LapsView), so what is read is what is seen

    /// <summary>
    /// A new listening session starts (Start): the previous session's laps leave the screen. The tracker itself is reset by
    /// <see cref="TelemetryEngines.BeginSession"/>, before the receive loop starts.
    /// </summary>
    public void BeginSession()
    {
        ExportStatus.Clear(); // a message about the previous session's file is not about this one
        Refresh(null);
    }

    /// <summary>The session ends (Stop): the laps stay readable, the live clock and delta give way to placeholders.</summary>
    public void EndSession() => Refresh(null);

    /// <summary>
    /// Called once per UI tick with the latest packet (or null when none): reads the tracker's snapshot and refreshes the texts.
    /// </summary>
    public void Refresh(TelemetryPacket? latest)
    {
        var snapshot = _tracker.Current;
        var live = latest is { IsRaceOn: true };

        IsRacing = live && (snapshot.CurrentLapSeconds > 0 || snapshot.Laps.Count > 0);

        CurrentLapText = IsRacing ? LapTimeFormatter.Format(snapshot.CurrentLapSeconds) : LapTimeFormatter.Zero;
        LapNumberText = IsRacing ? string.Format(CultureInfo.CurrentCulture, Strings.LapsLapFormat, snapshot.CurrentLapNumber) : "";
        HasLastLap = snapshot.LastLapSeconds is { };
        LastLapText = snapshot.LastLapSeconds is { } last ? LapTimeFormatter.Format(last) : LapTimeFormatter.Placeholder;
        HasBestLap = snapshot.BestLapSeconds is { };
        BestLapText = snapshot.BestLapSeconds is { } best ? LapTimeFormatter.Format(best) : LapTimeFormatter.Placeholder;

        ShowDelta(IsRacing ? snapshot.DeltaSeconds : null);

        if (!ReferenceEquals(snapshot.Laps, _rowsBuiltFrom))
        {
            _rowsBuiltFrom = snapshot.Laps;
            Rows = BuildRows(snapshot.Laps);
        }
    }

    private void ShowDelta(double? delta)
    {
        if (delta is not { } value || !double.IsFinite(value))
        {
            DeltaState = LapDeltaState.Hidden;
            DeltaText = "";
            DeltaName = "";
            return;
        }

        DeltaText = LapTimeFormatter.FormatDelta(value);

        // "Level" is decided on the ROUNDED value, so the pill never shows "+0.000" in red or "-0.000" in green.
        var rounded = Math.Round(value, 3, MidpointRounding.AwayFromZero);
        DeltaState = rounded == 0 ? LapDeltaState.Even : rounded < 0 ? LapDeltaState.Ahead : LapDeltaState.Behind;
        DeltaName = DeltaState switch
        {
            LapDeltaState.Ahead => string.Format(CultureInfo.CurrentCulture, Strings.LapsDeltaAheadName, DeltaText.TrimStart('-')),
            LapDeltaState.Behind => string.Format(CultureInfo.CurrentCulture, Strings.LapsDeltaBehindName, DeltaText.TrimStart('+')),
            _ => Strings.LapsDeltaEvenName
        };
    }

    private static List<LapRow> BuildRows(IReadOnlyList<LapRecord> laps)
    {
        // The fastest lap with a real time. A lap whose time is unknown (0) can neither be best nor have a gap.
        var best = laps.Where(lap => lap.TimeSeconds > 0).MinBy(lap => lap.TimeSeconds); // ties: the earlier lap

        // Newest first: the lap that just finished is the one the user wants to see.
        return laps
            .OrderByDescending(lap => lap.Number)
            .Select(lap =>
            {
                var isBest = best is not null && lap.Number == best.Number;
                var hasTime = lap.TimeSeconds > 0;
                return new LapRow(
                    lap.Number.ToString(CultureInfo.CurrentCulture),
                    hasTime ? LapTimeFormatter.Format(lap.TimeSeconds) : LapTimeFormatter.Placeholder,
                    hasTime && !isBest ? LapTimeFormatter.FormatDelta(lap.TimeSeconds - best!.TimeSeconds) : "",
                    isBest);
            })
            .ToList();
    }
}
