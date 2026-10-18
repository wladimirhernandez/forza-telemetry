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
public sealed partial class AnalysisViewModel : ObservableObject
{
    /// <summary>The window lengths offered by the selector, in seconds.</summary>
    public static readonly int[] WindowChoices = [10, 30, 60];

    private readonly TelemetryHistory _history;
    private readonly UnitsService _units;
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

    /// <summary>Pulls the newest samples and readouts. Called by the shell on the UI thread while this page is showing.</summary>
    public void Refresh()
    {
        SpeedUnitLabel = _units.SpeedLabel;
        SpeedScale = _units.ConvertSpeed(3.6);

        var count = _history.CopyTo(Samples);
        SampleCount = count;
        HasData = count > 0;
        if (count == 0)
        {
            _lastCount = 0;
            return;
        }

        var newest = Samples[count - 1];
        if (count != _lastCount || newest.TimeMs != _lastTimeMs || WindowSeconds != _lastWindow)
        {
            _lastCount = count;
            _lastTimeMs = newest.TimeMs;
            _lastWindow = WindowSeconds;
            DataVersion++;
        }

        SpeedText = _units.FormatSpeed(newest.SpeedMs * 3.6);
        RpmText = newest.Rpm.ToString("0", CultureInfo.CurrentCulture);
        GearText = GearFormatter.Format(newest.Gear);
        ThrottleText = Percent(newest.Throttle);
        BrakeText = Percent(newest.Brake);
        SteerText = FormatSteer(newest.Steer);
    }

    private static string Percent(float fraction) =>
        (fraction * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

    /// <summary>"R 35%", "L 12%" or "0%": direction letter plus magnitude, centered on zero like the chart lane.</summary>
    public static string FormatSteer(float steer) => Math.Abs(steer) < 0.005f
        ? "0%"
        : (steer > 0 ? InsightsStrings.SteerRightShort : InsightsStrings.SteerLeftShort) + " " + Percent(Math.Abs(steer));
}
