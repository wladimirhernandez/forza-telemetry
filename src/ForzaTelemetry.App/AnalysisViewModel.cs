using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core.Insights;
using ForzaTelemetry.Core.Laps;

namespace ForzaTelemetry.App;

/// <summary>What the Analysis page is showing.</summary>
public enum AnalysisMode
{
    /// <summary>The last seconds of driving as stacked time traces, with the current numeric readouts.</summary>
    Live,

    /// <summary>The lap in progress laid over the best lap, by distance (see <see cref="LapComparisonViewModel"/>).</summary>
    LapComparison
}

/// <summary>
/// The Analysis screen: live traces over a selectable time window plus the current numeric readouts, or the lap comparison.
/// </summary>
/// <remarks>
/// <b>Two modes, one refresh.</b> Only the mode on screen is refreshed (the live copy of thousands of samples, or the lap traces),
/// so the hidden half costs nothing. Switching back brings the page up to date on the next tick, because both halves read the
/// engines' current state, not "what changed since last time".
/// <para>
/// <b>Cheap refresh.</b> The shell calls <see cref="Refresh"/> only while this page is showing. It copies the ring
/// buffer into ONE array allocated at construction (<see cref="Samples"/>) and bumps <see cref="DataVersion"/> only when a
/// new sample actually arrived, so a paused game costs nothing and the 20 Hz tick never allocates here. The chart reads the
/// same array; it is rewritten in place on the UI thread, which is also the only thread that draws it.
/// </para>
/// </remarks>
public sealed partial class AnalysisViewModel : ObservableObject, IRefreshablePage
{
    /// <summary>The window lengths offered by the selector, in seconds.</summary>
    public static readonly int[] WindowChoices = [10, 30, 60];

    private readonly TelemetryHistory _history;
    private readonly UnitsService _units;
    private const int AssumedMaxHz = 80;

    private readonly TelemetrySample[] _newest = new TelemetrySample[1];
    private uint _lastTimeMs;
    private int _lastCount;
    private int _lastWindow;

    /// <param name="history">The ring buffer fed per packet by the telemetry service.</param>
    /// <param name="units">Speed unit conversion and label.</param>
    /// <param name="lapTraces">The per-lap trace recorder behind the lap comparison; optional (the mode selector is then hidden).</param>
    public AnalysisViewModel(TelemetryHistory history, UnitsService units, LapTraceRecorder? lapTraces = null)
    {
        _history = history;
        _units = units;
        Samples = new TelemetrySample[history.Capacity];
        if (lapTraces is not null)
        {
            Comparison = new LapComparisonViewModel(lapTraces, units);
            ModeOptions =
            [
                new ChoiceOption<AnalysisMode>(AnalysisMode.Live, InsightsStrings.ModeLive),
                new ChoiceOption<AnalysisMode>(AnalysisMode.LapComparison, InsightsStrings.ModeCompare)
            ];
        }
        else
        {
            ModeOptions = [];
        }

        WindowOptions =
        [
            .. WindowChoices.Select(seconds => new ChoiceOption<int>(
                seconds, string.Format(CultureInfo.CurrentCulture, InsightsStrings.WindowSecondsFormat, seconds)))
        ];
        _speedUnitLabel = units.SpeedLabel;
        _speedScale = units.ConvertSpeed(3.6);
    }

    /// <summary>The segmented selector's items (10 / 30 / 60 s).</summary>
    public IReadOnlyList<ChoiceOption<int>> WindowOptions { get; }

    /// <summary>The lap comparison; null when the page was built without a lap trace recorder.</summary>
    public LapComparisonViewModel? Comparison { get; }

    /// <summary>The "Live traces | Lap comparison" selector's items; empty when there is no comparison.</summary>
    public IReadOnlyList<ChoiceOption<AnalysisMode>> ModeOptions { get; }

    /// <summary>The mode selector is offered (there is a lap trace recorder behind it).</summary>
    public bool HasModes => Comparison is not null;

    /// <summary>What the page shows. Always <see cref="AnalysisMode.Live"/> when there is no comparison.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiveMode))]
    [NotifyPropertyChangedFor(nameof(IsCompareMode))]
    [NotifyPropertyChangedFor(nameof(ShowLiveChart))]
    [NotifyPropertyChangedFor(nameof(ShowLiveEmpty))]
    private AnalysisMode _mode = AnalysisMode.Live;

    public bool IsLiveMode => Mode == AnalysisMode.Live;

    public bool IsCompareMode => Mode == AnalysisMode.LapComparison;

    /// <summary>The live traces are drawn: live mode with at least one sample.</summary>
    public bool ShowLiveChart => IsLiveMode && HasData;

    /// <summary>The live empty state is shown: live mode with nothing to draw yet.</summary>
    public bool ShowLiveEmpty => IsLiveMode && IsEmpty;

    partial void OnModeChanged(AnalysisMode value)
    {
        if (value == AnalysisMode.Live)
        {
            _lastCount = 0; // the live copy may be stale (it was not refreshed while the comparison was showing)
        }
    }

    /// <summary>The samples the chart draws, oldest first; only the first <see cref="SampleCount"/> are valid.</summary>
    public TelemetrySample[] Samples { get; }

    [ObservableProperty]
    private int _windowSeconds = 30;

    [ObservableProperty]
    private int _sampleCount;

    /// <summary>Changes whenever <see cref="Samples"/> was refilled with new data (the chart's rebuild signal).</summary>
    [ObservableProperty]
    private int _dataVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowLiveChart))]
    [NotifyPropertyChangedFor(nameof(ShowLiveEmpty))]
    private bool _hasData;

    public bool IsEmpty => !HasData;

    /// <summary>Multiplier from m/s to the chosen speed unit (3.6 for km/h).</summary>
    [ObservableProperty]
    private double _speedScale = 3.6;

    [ObservableProperty]
    private string _speedUnitLabel;

    [ObservableProperty]
    private string _speedText = "0";

    [ObservableProperty]
    private string _rpmText = "0";

    [ObservableProperty]
    private string _gearText = "N";

    [ObservableProperty]
    private string _throttleText = "0%";

    [ObservableProperty]
    private string _brakeText = "0%";

    [ObservableProperty]
    private string _steerText = "0%";

    /// <summary>
    /// A new listening session starts: the traces of the previous one leave the screen. Only the display is reset: the shared
    /// history (which the Map and Tuning pages read too) is emptied by <see cref="TelemetryEngines.BeginSession"/>, before the
    /// receive loop starts, so no page owns a reset that others depend on.
    /// </summary>
    public void BeginSession()
    {
        _lastCount = 0;
        _lastTimeMs = 0;
        SampleCount = 0;
        HasData = false;
        DataVersion++; // the chart must redraw as empty even if it is the page on screen right now
        Comparison?.BeginSession();
    }

    /// <summary>Pulls the newest samples and readouts. Called by the shell on the UI thread while this page is showing.</summary>
    public void Refresh()
    {
        if (IsCompareMode && Comparison is not null)
        {
            Comparison.Refresh();
            return;
        }

        SpeedUnitLabel = _units.SpeedLabel;
        SpeedScale = _units.ConvertSpeed(3.6);

        // Peek at the newest sample first (a 1-element copy): it is enough to know whether anything changed, so an idle
        // or paused game costs one tiny copy per tick instead of thousands of samples.
        if (_history.CopyTo(_newest) == 0)
        {
            SampleCount = 0;
            HasData = false;
            _lastCount = 0;
            return;
        }

        var newest = _newest[0];
        var count = _history.Count;
        if (count != _lastCount || newest.TimeMs != _lastTimeMs || WindowSeconds != _lastWindow)
        {
            _lastCount = count;
            _lastTimeMs = newest.TimeMs;
            _lastWindow = WindowSeconds;
            CopyVisibleWindow();
            DataVersion++;
        }

        HasData = true;
        SpeedText = _units.FormatSpeed(newest.SpeedMs * 3.6);
        RpmText = newest.Rpm.ToString("0", CultureInfo.CurrentCulture);
        GearText = GearFormatter.Format(newest.Gear);
        ThrottleText = ReadoutFormat.Percent(newest.Throttle);
        BrakeText = ReadoutFormat.Percent(newest.Brake);
        SteerText = ReadoutFormat.Steer(newest.Steer);
    }

    /// <summary>
    /// Copies only what the chart can show. The packet rate is not fixed, so the first copy assumes at most
    /// <see cref="AssumedMaxHz"/> samples per second; if that did not reach back a whole window (a faster stream), it
    /// copies everything the buffer holds.
    /// </summary>
    private void CopyVisibleWindow()
    {
        var limit = Math.Min(Samples.Length, WindowSeconds * AssumedMaxHz);
        var copied = _history.CopyTo(Samples.AsSpan(0, limit));
        if (copied == limit && limit < Samples.Length
            && (long)Samples[copied - 1].TimeMs - Samples[0].TimeMs < WindowSeconds * 1000L)
        {
            copied = _history.CopyTo(Samples);
        }

        SampleCount = copied;
    }
}
