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
/// to judge, or no new sample arrives, the last value is held for about two seconds and then hidden rather than
/// displayed as if it were still true.
/// </para>
/// <para>
/// <b>Change detection.</b> Lists and sentences are only replaced when their content actually changed, so the 20 Hz
/// refresh does not rebuild the advice lines or restart the chart's geometry for nothing.
/// </para>
/// </remarks>
public sealed partial class TuningViewModel : ObservableObject
{
    /// <summary>Weight of a new balance reading in the running value (0..1); about a quarter second at 20 Hz.</summary>
    public const double BalanceSmoothing = 0.2;

    /// <summary>Refreshes without a usable balance reading after which the bar is hidden (2 s at 20 Hz).</summary>
    public const int BalanceHoldTicks = 40;

    private readonly DynoCapture _dyno;
    private readonly TelemetryHistory _history;
    private readonly SuspensionBottomingCounter _bottoming;
    private readonly UnitsService _units;
    private readonly TelemetrySample[] _latest = new TelemetrySample[1];

    private (int Count, double Power, double Torque, int Car, int Pi) _curveSignature;
    private TireAdvice _lastTireAdvice = (TireAdvice)(-1);
    private uint _lastTimeMs;
    private int _balanceIdleTicks = BalanceHoldTicks;
    private bool _hasBalanceReading;
    private BottomingCounts _lastCounts = new(-1, -1, -1, -1);

    /// <param name="dyno">Per-car, per-build power and torque capture.</param>
    /// <param name="history">Used for the newest sample (slip angles, tire temperatures).</param>
    /// <param name="bottoming">Suspension bottoming counters.</param>
    /// <param name="units">Temperature unit conversion and label.</param>
    public TuningViewModel(
        DynoCapture dyno, TelemetryHistory history, SuspensionBottomingCounter bottoming, UnitsService units)
    {
        _dyno = dyno;
        _history = history;
        _bottoming = bottoming;
        _units = units;
        _carLabel = InsightsStrings.NoCarYet;
        _balanceAdvice = InsightText.BalanceAdvice(BalanceState.Unknown);
        _suspensionAdvice = InsightText.SuspensionAdvice(default);
        _tireLines = [InsightsStrings.TiresNoData];
    }

    // ---- Dyno ------------------------------------------------------------------------------------

    [ObservableProperty]
    private IReadOnlyList<DynoPoint> _curve = [];

    /// <summary>Changes when the curve content changed (the chart's rebuild signal).</summary>
    [ObservableProperty]
    private int _curveVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoCurve))]
    private bool _hasCurve;

    public bool HasNoCurve => !HasCurve;

    [ObservableProperty]
    private string _carLabel;

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
        RefreshDyno();
        RefreshSuspension();

        var hasSample = _history.CopyTo(_latest) == 1;
        HasData = hasSample;
        if (hasSample)
        {
            RefreshBalance(_latest[0]);
            RefreshTires(_latest[0]);
        }
    }

    [RelayCommand]
    private void ResetDyno()
    {
        _dyno.Reset(_dyno.CurrentCarOrdinal, _dyno.CurrentPerformanceIndex);
        _curveSignature = default;
        RefreshDyno();
    }

    [RelayCommand]
    private void ResetBottoming()
    {
        _bottoming.Reset();
        _lastCounts = new(-1, -1, -1, -1);
        RefreshSuspension();
    }

    private void RefreshDyno()
    {
        var car = _dyno.CurrentCarOrdinal;
        var pi = _dyno.CurrentPerformanceIndex;
        CarLabel = car == 0
            ? InsightsStrings.NoCarYet
            : string.Format(CultureInfo.CurrentCulture, InsightsStrings.CarLabelFormat, car, pi);

        var curve = _dyno.GetCurve(car, pi);
        var signature = (curve.Count, curve.Sum(p => (double)p.PowerWatts), curve.Sum(p => (double)p.TorqueNewtonMeters), car, pi);
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

        var reading = BalanceAnalyzer.AnalyzeBalance(sample);
        if (fresh && reading.State != BalanceState.Unknown)
        {
            // First reading after a gap snaps to it; later ones blend in.
            BalanceValue = _hasBalanceReading
                ? BalanceValue + ((reading.Index - BalanceValue) * BalanceSmoothing)
                : reading.Index;
            _hasBalanceReading = true;
            _balanceIdleTicks = 0;
        }
        else if (_balanceIdleTicks < BalanceHoldTicks)
        {
            _balanceIdleTicks++;
        }

        var active = _balanceIdleTicks < BalanceHoldTicks;
        if (!active)
        {
            _hasBalanceReading = false;
        }

        BalanceActive = active;
        BalanceState = active ? InsightText.ClassifyBalance(BalanceValue) : BalanceState.Unknown;
        BalanceLabel = InsightText.BalanceLabel(BalanceState);
        BalanceAdvice = InsightText.BalanceAdvice(BalanceState);
    }

    private void RefreshTires(in TelemetrySample sample)
    {
        var temps = new[] { sample.TireTempFrontLeft, sample.TireTempFrontRight, sample.TireTempRearLeft, sample.TireTempRearRight };
        var hasTemps = temps.Any(t => t > 0);
        var label = _units.TemperatureLabel;

        TempFrontLeft = hasTemps ? FormatTemp(temps[0], label) : "-";
        TempFrontRight = hasTemps ? FormatTemp(temps[1], label) : "-";
        TempRearLeft = hasTemps ? FormatTemp(temps[2], label) : "-";
        TempRearRight = hasTemps ? FormatTemp(temps[3], label) : "-";
        BandFrontLeft = hasTemps ? Band(temps[0]) : TireTempBand.Unknown;
        BandFrontRight = hasTemps ? Band(temps[1]) : TireTempBand.Unknown;
        BandRearLeft = hasTemps ? Band(temps[2]) : TireTempBand.Unknown;
        BandRearRight = hasTemps ? Band(temps[3]) : TireTempBand.Unknown;

        var advice = hasTemps
            ? BalanceAnalyzer.AnalyzeTireTemperatures(temps[0], temps[1], temps[2], temps[3])
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
