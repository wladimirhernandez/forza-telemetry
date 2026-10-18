using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App;

/// <summary>
/// The Tuning screen: the dyno curve of the current car build, the live understeer/oversteer balance, suspension
/// bottoming counters and tire temperature advice.
/// </summary>
/// <remarks>
/// <para>
/// <b>Smoothing the balance.</b> The balance index from <see cref="BalanceAnalyzer"/> is computed from instantaneous
/// slip angles, which flicker from packet to packet; a bar that twitches is unreadable. The view model blends each new
/// reading into a running value (an exponential moving average) so the bar glides. When the car is not sliding enough
/// to judge, or no new sample arrives, the last value is held for <see cref="BalanceHold"/> of wall-clock time and then hidden rather than
/// displayed as if it were still true. It is time-based (not "N refreshes") because refreshes stop while another tab is
/// open: coming back after a minute must not show a minute-old balance as current.
/// </para>
/// <para>
/// <b>Change detection and allocations.</b> Lists and sentences are only replaced when their content actually changed.
/// <see cref="DynoCapture"/> has no version counter and building a curve allocates, so the curve is polled at
/// <see cref="DynoPollInterval"/> (2 Hz) rather than every tick; the tire values reuse one array and their text is only
/// rebuilt when a temperature or the unit changed.
/// </para>
/// </remarks>
public sealed partial class TuningViewModel : ObservableObject, IRefreshablePage
{
    /// <summary>Weight of a new balance reading in the running value (0..1); about a quarter second at 20 Hz.</summary>
    public const double BalanceSmoothing = 0.2;

    /// <summary>How long the last balance reading stays on screen once readings stop (not enough slip, or no new data).</summary>
    public static readonly TimeSpan BalanceHold = TimeSpan.FromSeconds(2);

    /// <summary>How often the dyno curve is re-read (it only changes during full-throttle pulls).</summary>
    public static readonly TimeSpan DynoPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly DynoCapture _dyno;
    private readonly TelemetryHistory _history;
    private readonly SuspensionBottomingCounter _bottoming;
    private readonly UnitsService _units;
    private readonly TimeProvider _time;
    private readonly CarNames? _carNames;
    private readonly float[] _temps = new float[4];
    private readonly float[] _shownTemps = new float[4];
    private TemperatureUnit _shownTempUnit = (TemperatureUnit)(-1);
    private long _lastDynoPollStamp;
    private bool _hasPolledDyno;
    private int _shownCar = -1;
    private int _shownPi = -1;
    private long _lastBalanceStamp;
    private readonly TelemetrySample[] _latest = new TelemetrySample[1];

    private (int Count, double Power, double Torque, int Car, int Pi) _curveSignature;
    private TireAdvice _lastTireAdvice = (TireAdvice)(-1);
    private uint _lastTimeMs;
    private bool _hasBalanceReading;
    private BottomingCounts _lastCounts = new(-1, -1, -1, -1);

    /// <param name="dyno">Per-car, per-build power and torque capture.</param>
    /// <param name="history">Used for the newest sample (slip angles, tire temperatures).</param>
    /// <param name="bottoming">Suspension bottoming counters.</param>
    /// <param name="units">Temperature unit conversion and label.</param>
    /// <param name="timeProvider">Clock for the balance hold and the dyno poll; defaults to the system clock.</param>
    public TuningViewModel(
        DynoCapture dyno, TelemetryHistory history, SuspensionBottomingCounter bottoming, UnitsService units,
        TimeProvider? timeProvider = null,
        CarNames? carNames = null)
    {
        _carNames = carNames;
        if (carNames is not null)
        {
            // A rename while the page is showing: the header must not keep the old name until the next car change.
            carNames.Changed += (_, _) => _shownCar = -1;
        }

        _time = timeProvider ?? TimeProvider.System;
        _dyno = dyno;
        _history = history;
        _bottoming = bottoming;
        _units = units;

        // The power unit can change while the page is showing: the dyno chart redraws its axis, curve and peak chip in the new unit.
        units.PropertyChanged += (_, e) =>
        {
            if (UnitsService.AffectsPower(e.PropertyName))
            {
                OnPropertyChanged(nameof(PowerUnit));
            }
        };

        _carText = InsightsStrings.NoCarYet;
        _piText = string.Empty;
        _balanceAdvice = InsightText.BalanceAdvice(BalanceState.Unknown);
        _suspensionAdvice = InsightText.SuspensionAdvice(default);
        _tireLines = [InsightsStrings.TiresNoData];
    }

    // ---- Dyno ------------------------------------------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<DynoPoint> _curve = [];

    /// <summary>The unit of the dyno's power axis, from the Power setting.</summary>
    public PowerUnit PowerUnit => _units.PowerUnit;

    /// <summary>Changes when the curve content changed (the chart's rebuild signal).</summary>
    [ObservableProperty]
    private int _curveVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoCurve))]
    private bool _hasCurve;

    public bool HasNoCurve => !HasCurve;

    /// <summary>"Car 2211", or "No car detected yet"; shown as its own pill.</summary>
    [ObservableProperty]
    private string _carText;

    /// <summary>"PI 612", empty until a car is detected; shown as a second pill.</summary>
    [ObservableProperty]
    private string _piText;

    [ObservableProperty]
    private bool _hasCar;

    // ---- Balance ---------------------------------------------------------------------------------

    /// <summary>Smoothed balance index: -1 oversteer .. +1 understeer.</summary>
    [ObservableProperty]
    private double _balanceValue;

    [ObservableProperty]
    private bool _balanceActive;

    [ObservableProperty]
    private BalanceState _balanceState = BalanceState.Unknown;

    [ObservableProperty]
    private string _balanceLabel = string.Empty;

    [ObservableProperty]
    private string _balanceAdvice;

    // ---- Suspension ------------------------------------------------------------------------------

    [ObservableProperty]
    private int _bottomFrontLeft;

    [ObservableProperty]
    private int _bottomFrontRight;

    [ObservableProperty]
    private int _bottomRearLeft;

    [ObservableProperty]
    private int _bottomRearRight;

    [ObservableProperty]
    private int _severityFrontLeft;

    [ObservableProperty]
    private int _severityFrontRight;

    [ObservableProperty]
    private int _severityRearLeft;

    [ObservableProperty]
    private int _severityRearRight;

    [ObservableProperty]
    private string _suspensionAdvice;

    // ---- Tires -----------------------------------------------------------------------------------

    [ObservableProperty]
    private string _tempFrontLeft = "-";

    [ObservableProperty]
    private string _tempFrontRight = "-";

    [ObservableProperty]
    private string _tempRearLeft = "-";

    [ObservableProperty]
    private string _tempRearRight = "-";

    [ObservableProperty]
    private TireTempBand _bandFrontLeft;

    [ObservableProperty]
    private TireTempBand _bandFrontRight;

    [ObservableProperty]
    private TireTempBand _bandRearLeft;

    [ObservableProperty]
    private TireTempBand _bandRearRight;

    [ObservableProperty]
    private IReadOnlyList<string> _tireLines;

    // ---- Shared ----------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasData;

    public bool IsEmpty => !HasData;

    /// <summary>Pulls the current state of every engine. Called by the shell on the UI thread while this page is showing.</summary>
    public void Refresh()
    {
        RefreshDyno(force: false);
        RefreshSuspension();

        var hasSample = _history.CopyTo(_latest) == 1;
        HasData = hasSample;
        if (hasSample)
        {
            RefreshBalance(_latest[0]);
            RefreshTires(_latest[0]);
        }
    }

    /// <summary>
    /// A new listening session starts: the balance reading, the tire values and the bottoming display of the previous session
    /// leave the screen. The dyno curves stay on purpose: they describe the car, not the session (see <see cref="TelemetryEngines.Dyno"/>).
    /// The bottoming tallies and the history are emptied by <see cref="TelemetryEngines.BeginSession"/> before the receive loop starts.
    /// </summary>
    public void BeginSession()
    {
        _lastCounts = new(-1, -1, -1, -1);
        _hasBalanceReading = false;
        _lastTimeMs = 0;
        BalanceValue = 0;
        BalanceActive = false;
        BalanceState = BalanceState.Unknown;
        BalanceLabel = InsightText.BalanceLabel(BalanceState.Unknown);
        BalanceAdvice = InsightText.BalanceAdvice(BalanceState.Unknown);

        // The tire card is only rewritten when a temperature or the unit CHANGES, so forget what it showed, and blank it.
        Array.Clear(_shownTemps);
        _shownTempUnit = (TemperatureUnit)(-1);
        _lastTireAdvice = (TireAdvice)(-1);
        TempFrontLeft = TempFrontRight = TempRearLeft = TempRearRight = "-";
        BandFrontLeft = BandFrontRight = BandRearLeft = BandRearRight = TireTempBand.Unknown;
        TireLines = [InsightsStrings.TiresNoData];

        HasData = false;
        RefreshSuspension();
    }

    [RelayCommand]
    private void ResetDyno()
    {
        _dyno.Reset(_dyno.CurrentCarOrdinal, _dyno.CurrentPerformanceIndex);
        _curveSignature = default;
        RefreshDyno(force: true);
    }

    [RelayCommand]
    private void ResetBottoming()
    {
        _bottoming.Reset();
        _lastCounts = new(-1, -1, -1, -1);
        RefreshSuspension();
    }

    private void RefreshDyno(bool force)
    {
        if (!force && _hasPolledDyno && _time.GetElapsedTime(_lastDynoPollStamp) < DynoPollInterval)
        {
            return;
        }

        _hasPolledDyno = true;
        _lastDynoPollStamp = _time.GetTimestamp();

        var car = _dyno.CurrentCarOrdinal;
        var pi = _dyno.CurrentPerformanceIndex;
        if (car != _shownCar || pi != _shownPi)
        {
            _shownCar = car;
            _shownPi = pi;
            HasCar = car != 0;
            CarText = car == 0 ? InsightsStrings.NoCarYet : CarDisplayName.For(car, _carNames);
            PiText = car == 0 ? string.Empty : string.Format(CultureInfo.CurrentCulture, InsightsStrings.PiFormat, pi);
        }

        var curve = _dyno.GetCurve(car, pi);
        double power = 0, torque = 0;
        for (var i = 0; i < curve.Count; i++)
        {
            power += curve[i].PowerWatts;
            torque += curve[i].TorqueNewtonMeters;
        }

        var signature = (curve.Count, power, torque, car, pi);
        if (signature == _curveSignature)
        {
            return;
        }

        _curveSignature = signature;
        Curve = curve;
        HasCurve = curve.Count > 0;
        CurveVersion++;
    }

    private void RefreshSuspension()
    {
        var counts = _bottoming.Counts;
        if (counts == _lastCounts)
        {
            return;
        }

        _lastCounts = counts;
        BottomFrontLeft = counts.FrontLeft;
        BottomFrontRight = counts.FrontRight;
        BottomRearLeft = counts.RearLeft;
        BottomRearRight = counts.RearRight;
        SeverityFrontLeft = InsightText.BottomingSeverity(counts.FrontLeft);
        SeverityFrontRight = InsightText.BottomingSeverity(counts.FrontRight);
        SeverityRearLeft = InsightText.BottomingSeverity(counts.RearLeft);
        SeverityRearRight = InsightText.BottomingSeverity(counts.RearRight);
        SuspensionAdvice = InsightText.SuspensionAdvice(counts);
    }

    private void RefreshBalance(in TelemetrySample sample)
    {
        var fresh = sample.TimeMs != _lastTimeMs;
        _lastTimeMs = sample.TimeMs;

        // Held readings expire by the clock: after the hold, the next reading starts over instead of blending into a
        // value that is no longer true (for example after returning to this tab).
        if (_hasBalanceReading && _time.GetElapsedTime(_lastBalanceStamp) >= BalanceHold)
        {
            _hasBalanceReading = false;
        }

        var reading = BalanceAnalyzer.AnalyzeBalance(sample);
        if (fresh && reading.State != BalanceState.Unknown)
        {
            BalanceValue = _hasBalanceReading
                ? BalanceValue + ((reading.Index - BalanceValue) * BalanceSmoothing)
                : reading.Index;
            _hasBalanceReading = true;
            _lastBalanceStamp = _time.GetTimestamp();
        }

        BalanceActive = _hasBalanceReading;
        BalanceState = _hasBalanceReading ? InsightText.ClassifyBalance(BalanceValue) : BalanceState.Unknown;
        BalanceLabel = InsightText.BalanceLabel(BalanceState);
        BalanceAdvice = InsightText.BalanceAdvice(BalanceState);
    }

    private void RefreshTires(in TelemetrySample sample)
    {
        _temps[0] = sample.TireTempFrontLeft;
        _temps[1] = sample.TireTempFrontRight;
        _temps[2] = sample.TireTempRearLeft;
        _temps[3] = sample.TireTempRearRight;

        // Text and bands only change when a temperature or the unit did: no string building on every tick.
        if (_temps.AsSpan().SequenceEqual(_shownTemps) && _shownTempUnit == _units.TemperatureUnit)
        {
            return;
        }

        _temps.CopyTo(_shownTemps, 0);
        _shownTempUnit = _units.TemperatureUnit;
        var hasTemps = _temps[0] > 0 || _temps[1] > 0 || _temps[2] > 0 || _temps[3] > 0;
        var label = _units.TemperatureLabel;

        TempFrontLeft = hasTemps ? FormatTemp(_temps[0], label) : "-";
        TempFrontRight = hasTemps ? FormatTemp(_temps[1], label) : "-";
        TempRearLeft = hasTemps ? FormatTemp(_temps[2], label) : "-";
        TempRearRight = hasTemps ? FormatTemp(_temps[3], label) : "-";
        BandFrontLeft = hasTemps ? Band(_temps[0]) : TireTempBand.Unknown;
        BandFrontRight = hasTemps ? Band(_temps[1]) : TireTempBand.Unknown;
        BandRearLeft = hasTemps ? Band(_temps[2]) : TireTempBand.Unknown;
        BandRearRight = hasTemps ? Band(_temps[3]) : TireTempBand.Unknown;

        var advice = hasTemps
            ? BalanceAnalyzer.AnalyzeTireTemperatures(_temps[0], _temps[1], _temps[2], _temps[3])
            : (TireAdvice)(-1);
        if (advice != _lastTireAdvice)
        {
            _lastTireAdvice = advice;
            TireLines = hasTemps ? InsightText.TireLines(advice) : [InsightsStrings.TiresNoData];
        }
    }

    private string FormatTemp(float wire, string label) =>
        _units.FormatTemperature(TireTemperatureScale.ToCelsius(wire)) + " " + label;

    private static TireTempBand Band(float wire) => TireTemperatureScale.Classify(TireTemperatureScale.ToCelsius(wire));
}
