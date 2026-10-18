using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App;

/// <summary>
/// The Analysis screen: live traces over a selectable time window plus the current numeric readouts.
/// </summary>
/// <remarks>
/// <b>Cheap refresh.</b> The shell calls <see cref="Refresh"/> only while this page is showing. It copies the ring
/// buffer into ONE array allocated at construction (<see cref="Samples"/>) and bumps <see cref="DataVersion"/> only when a
/// new sample actually arrived, so a paused game costs nothing and the 20 Hz tick never allocates here. The chart reads the
/// same array; it is rewritten in place on the UI thread, which is also the only thread that draws it.
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
    public AnalysisViewModel(TelemetryHistory history, UnitsService units)
    {
        _history = history;
        _units = units;
        Samples = new TelemetrySample[history.Capacity];
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
    /// A new listening session starts: the traces of the previous one leave the screen. The shared history is emptied here (the
    /// Map reads the car position from it too, so it restarts empty as well, which is what a new session means).
    /// </summary>
    public void BeginSession()
    {
        _history.Clear();
        _lastCount = 0;
        _lastTimeMs = 0;
        SampleCount = 0;
        HasData = false;
        DataVersion++; // the chart must redraw as empty even if it is the page on screen right now
    }

    /// <summary>Pulls the newest samples and readouts. Called by the shell on the UI thread while this page is showing.</summary>
    public void Refresh()
    {
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
