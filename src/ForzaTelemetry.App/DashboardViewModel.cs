using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;

namespace ForzaTelemetry.App;

/// <summary>
/// The values the Dashboard screen shows: RPM, speed, gear, the four tires, shift lights, G-force, inputs, the engine
/// gauges and the car pill.
/// </summary>
/// <remarks>
/// It does NOT read <c>TelemetryService</c> and has no clock: <see cref="ShellViewModel"/> does that once per tick and
/// hands over the packet through <see cref="Apply"/>, <see cref="ResetLive"/> and <see cref="ResetSession"/>. That keeps a
/// single place that knows about the network, the 20 Hz tick, faults and "no data" timing, while this class is a plain
/// packet-to-numbers mapper that is easy to test.
/// <para>
/// <b>Bindings are reflection-based:</b> unlike a compiled binding, WPF resolves <c>{Binding SpeedKmh}</c> by property
/// name at RUNTIME, so a typo shows up as a blank label (and a line in the Output window), not as a build error.
/// <c>DashboardView.xaml</c> declares <c>d:DataContext</c> so the editor can at least check names at design time.
/// </para>
/// </remarks>
public sealed partial class DashboardViewModel : ObservableObject
{
    private const float DefaultMaxRpm = 9000f;
    private const string NoGearLabel = "-";

    // The documented range of a real performance index; 0 (menus, loading) and anything outside it is not a car.
    private const int MinPerformanceIndex = 100;
    private const int MaxPerformanceIndex = 999;

    // Highest power/torque/boost seen for the current car, the scale of the engine gauges (see PowerRatio).
    private const double PeakPowerFloorKw = 100;
    private const double PeakTorqueFloorNm = 100;
    private const double PeakBoostFloorPsi = 10;
    private double _peakPowerKw = PeakPowerFloorKw;
    private double _peakTorqueNm = PeakTorqueFloorNm;
    private double _peakBoostPsi = PeakBoostFloorPsi;
    private int? _currentCarOrdinal;

    private readonly UnitsService _units;

    // Telemetry numbers read best with a space as thousands separator ("7 439"), regardless of the user's culture.
    private static readonly NumberFormatInfo SpacedNumbers = new() { NumberGroupSeparator = " ", NumberDecimalDigits = 0 };

    /// <summary>
    /// A populated instance for the XAML designer (<c>d:DataContext="{x:Static local:DashboardViewModel.DesignSample}"</c>)
    /// so the Visual Studio/Rider previewer shows a full dashboard instead of blank zeros. A static factory, not a
    /// special constructor: nothing at runtime ever touches it.
    /// </summary>
    public static DashboardViewModel DesignSample => new()
    {
        Rpm = 4_200f,
        MaxRpm = 7_800f,
        SpeedKmh = 128f,
        GearLabel = "4",

        // A mid-corner, mid-throttle snapshot so the previewer shows every control populated.
        LateralG = 0.8,
        LongitudinalG = 0.3,
        ThrottleInput = 0.7,
        SteerInput = 0.25,
        PowerKw = 280,
        PowerHp = DashboardMath.ToHorsepower(280),
        PowerRatio = 0.8,
        TorqueNm = 410,
        TorqueRatio = 0.75,
        BoostPsi = 14,
        BoostRatio = 0.7,
        FuelPercent = 62,
        FuelRatio = 0.62,
        HasCarInfo = true,
        CarClassLabel = "A",
        CarPerformanceIndex = 780,
        DrivetrainLabel = "AWD"
    };

    // [NotifyPropertyChangedFor]: RpmReadout is computed from Rpm and MaxRpm, so it must raise its own change
    // notification whenever either input changes, or the bound TextBlock would keep showing stale text.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RpmReadout))]
    private float _rpm;

    // 9000 is a placeholder redline shown before the first packet arrives (so the RPM bar has SOME
    // scale instead of Maximum=0); the real value is overwritten from EngineMaxRpm on first refresh,
    // since different cars in FH6 have very different actual redlines.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RpmReadout))]
    private float _maxRpm = DefaultMaxRpm;

    // The speed is kept in km/h (what the packet carries and what the tests assert); SpeedReadout is what the screen shows.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedReadout))]
    private float _speedKmh;

    [ObservableProperty]
    private string _gearLabel = NoGearLabel;

    // ---- Shift lights ------------------------------------------------------

    /// <summary>True from 97% of the redline RPM up: the shift-light strip blinks while this holds.</summary>
    [ObservableProperty]
    private bool _isRedline;

    // ---- G-force -------------------------------------------------------------

    /// <summary>Acceleration to the right in g (negative = left); the packet's car-local X axis.</summary>
    [ObservableProperty]
    private double _lateralG;

    /// <summary>Acceleration forward in g (negative = braking); the packet's car-local Z axis.</summary>
    [ObservableProperty]
    private double _longitudinalG;

    /// <summary>
    /// Changes whenever the G-force trail must be wiped (new session, car change). The trail lives inside the
    /// control, so the view observes this counter (a plain "something happened" signal, its value is meaningless).
    /// </summary>
    [ObservableProperty]
    private int _trailResetToken;

    // ---- Inputs (all normalized: pedals 0..1, steering -1..1) -------------------

    [ObservableProperty]
    private double _throttleInput;

    [ObservableProperty]
    private double _brakeInput;

    [ObservableProperty]
    private double _clutchInput;

    [ObservableProperty]
    private double _handBrakeInput;

    [ObservableProperty]
    private double _steerInput;

    // ---- Engine card ---------------------------------------------------------------

    /// <summary>Engine power in kW, never negative (off-throttle engine braking reports negative watts).</summary>
    [ObservableProperty]
    private double _powerKw;

    /// <summary>The same power in mechanical horsepower.</summary>
    [ObservableProperty]
    private double _powerHp;

    /// <summary>Engine torque in Nm, never negative (see <see cref="PowerKw"/>).</summary>
    [ObservableProperty]
    private double _torqueNm;

    /// <summary>Turbo/supercharger boost in psi (the unit FH sends). Vacuum on a naturally aspirated engine shows as 0.</summary>
    [ObservableProperty]
    private double _boostPsi;

    /// <summary>Fuel left, 0..100.</summary>
    [ObservableProperty]
    private double _fuelPercent;

    // Gauge fill, 0..1. Power, torque and boost have no fixed maximum (it depends on the car), so each is measured
    // against the highest value seen for the CURRENT car (never below a floor, so a slow first second does not
    // pin the gauge at 100%). Fuel is already a 0..1 fraction.
    [ObservableProperty]
    private double _powerRatio;

    [ObservableProperty]
    private double _torqueRatio;

    [ObservableProperty]
    private double _boostRatio;

    [ObservableProperty]
    private double _fuelRatio;

    // ---- Car info pill -------------------------------------------------------------

    /// <summary>False until a packet carries a real car (performance index 100..999); the pill is hidden meanwhile.</summary>
    [ObservableProperty]
    private bool _hasCarInfo;

    [ObservableProperty]
    private string _carClassLabel = "";

    [ObservableProperty]
    private int _carPerformanceIndex;

    [ObservableProperty]
    private string _drivetrainLabel = "";

    /// <param name="units">Converts and labels speed and tire temperature; optional (metric) for tests and the designer.</param>
    public DashboardViewModel(UnitsService? units = null)
    {
        _units = units ?? new UnitsService();
        FrontLeft = new TireViewModel(_units);
        FrontRight = new TireViewModel(_units);
        RearLeft = new TireViewModel(_units);
        RearRight = new TireViewModel(_units);

        // The unit can change while the dashboard is on screen: re-announce the two properties that depend on it.
        _units.PropertyChanged += OnUnitsChanged;
    }

    private void OnUnitsChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SpeedReadout));
        OnPropertyChanged(nameof(SpeedUnitLabel));
    }

    public TireViewModel FrontLeft { get; }

    public TireViewModel FrontRight { get; }

    public TireViewModel RearLeft { get; }

    public TireViewModel RearRight { get; }

    /// <summary>The speed as whole-number text in the chosen unit (km/h or mph).</summary>
    public string SpeedReadout => _units.FormatSpeed(SpeedKmh);

    /// <summary>The unit label under the speed: <c>km/h</c> or <c>mph</c>.</summary>
    public string SpeedUnitLabel => _units.SpeedLabel;

    /// <summary>Current and maximum RPM as one readout, for example <c>7 439 / 9 000</c>.</summary>
    public string RpmReadout => string.Create(SpacedNumbers, $"{Rpm:N0} / {MaxRpm:N0}");

    /// <summary>Shows one driving packet (the shell only calls this for packets with <c>IsRaceOn</c>).</summary>
    public void Apply(TelemetryPacket latest)
    {
        Rpm = latest.CurrentEngineRpm;

        // A car between races/loading can briefly report EngineMaxRpm as 0 — keep the previous
        // (or the initial placeholder) redline rather than collapsing the RPM bar's scale to 0.
        if (latest.EngineMaxRpm > 0)
        {
            MaxRpm = latest.EngineMaxRpm;
        }

        SpeedKmh = latest.SpeedKmh;
        GearLabel = GearFormatter.Format(latest.Gear);
        UpdateTires(latest);
        UpdateDashboard(latest);
    }

    /// <summary>
    /// Idle dashboard: zero/placeholder LIVE readouts, no tire colors, no car pill, shift light off. Keeps the per-car
    /// gauge scales, so it is safe to call repeatedly (every tick while faulted or paused).
    /// </summary>
    public void ResetLive()
    {
        // Deliberately NOT reset here: MaxRpm (the redline belongs to the car, not to the pause; wiping it on every
        // paused tick would defeat the "keep the previous redline" guard in Apply).
        Rpm = 0;
        SpeedKmh = 0;
        GearLabel = NoGearLabel;

        IsRedline = false;
        LateralG = 0;
        LongitudinalG = 0;
        ThrottleInput = 0;
        BrakeInput = 0;
        ClutchInput = 0;
        HandBrakeInput = 0;
        SteerInput = 0;

        PowerKw = 0;
        PowerHp = 0;
        TorqueNm = 0;
        BoostPsi = 0;
        FuelPercent = 0;
        PowerRatio = 0;
        TorqueRatio = 0;
        BoostRatio = 0;
        FuelRatio = 0;

        HasCarInfo = false;
        CarClassLabel = "";
        CarPerformanceIndex = 0;
        DrivetrainLabel = "";

        FrontLeft.Reset();
        FrontRight.Reset();
        RearLeft.Reset();
        RearRight.Reset();
    }

    /// <summary>
    /// Forgets everything the previous session produced (Start, Stop): the idle dashboard plus fresh gauge scales, the
    /// placeholder redline and an empty G-force trail.
    /// </summary>
    public void ResetSession()
    {
        ResetLive();
        MaxRpm = DefaultMaxRpm;

        _currentCarOrdinal = null;
        _peakPowerKw = PeakPowerFloorKw;
        _peakTorqueNm = PeakTorqueFloorNm;
        _peakBoostPsi = PeakBoostFloorPsi;
        TrailResetToken++;
    }

    private void UpdateTires(TelemetryPacket p)
    {
        FrontLeft.Update(p.TireTempFrontLeft, p.TireCombinedSlipFrontLeft, p.NormalizedSuspensionTravelFrontLeft);
        FrontRight.Update(p.TireTempFrontRight, p.TireCombinedSlipFrontRight, p.NormalizedSuspensionTravelFrontRight);
        RearLeft.Update(p.TireTempRearLeft, p.TireCombinedSlipRearLeft, p.NormalizedSuspensionTravelRearLeft);
        RearRight.Update(p.TireTempRearRight, p.TireCombinedSlipRearRight, p.NormalizedSuspensionTravelRearRight);
    }

    /// <summary>Maps one packet to the shift-light, G-force, input, engine-gauge and car-info values.</summary>
    private void UpdateDashboard(TelemetryPacket p)
    {
        IsRedline = DashboardMath.IsRedline(p.CurrentEngineRpm, p.EngineMaxRpm);

        LateralG = DashboardMath.ToG(p.AccelerationX);
        LongitudinalG = DashboardMath.ToG(p.AccelerationZ);

        ThrottleInput = DashboardMath.NormalizePedal(p.Accel);
        BrakeInput = DashboardMath.NormalizePedal(p.Brake);
        ClutchInput = DashboardMath.NormalizePedal(p.Clutch);
        HandBrakeInput = DashboardMath.NormalizePedal(p.HandBrake);
        SteerInput = DashboardMath.NormalizeSteer(p.Steer);

        // A different car (CarOrdinal) has a different power band: start its gauge scale over.
        if (_currentCarOrdinal != p.CarOrdinal)
        {
            _currentCarOrdinal = p.CarOrdinal;
            TrailResetToken++; // the old car's G history means nothing for the new one
            if (p.EngineMaxRpm <= 0)
            {
                MaxRpm = DefaultMaxRpm; // no redline reported for the new car: do not inherit the old car's
            }
            _peakPowerKw = PeakPowerFloorKw;
            _peakTorqueNm = PeakTorqueFloorNm;
            _peakBoostPsi = PeakBoostFloorPsi;
        }

        PowerKw = DashboardMath.ToKilowatts(p.Power);
        PowerHp = DashboardMath.ToHorsepower(PowerKw);
        TorqueNm = Math.Max(0, p.Torque);
        BoostPsi = Math.Max(0, p.Boost);
        FuelRatio = DashboardMath.Clamp01(p.Fuel);
        FuelPercent = FuelRatio * 100;

        _peakPowerKw = Math.Max(_peakPowerKw, PowerKw);
        _peakTorqueNm = Math.Max(_peakTorqueNm, TorqueNm);
        _peakBoostPsi = Math.Max(_peakBoostPsi, BoostPsi);
        PowerRatio = DashboardMath.Normalize(PowerKw, 0, _peakPowerKw);
        TorqueRatio = DashboardMath.Normalize(TorqueNm, 0, _peakTorqueNm);
        BoostRatio = DashboardMath.Normalize(BoostPsi, 0, _peakBoostPsi);

        HasCarInfo = p.CarPerformanceIndex is >= MinPerformanceIndex and <= MaxPerformanceIndex;
        CarClassLabel = CarInfoFormatter.ClassLabel(p.CarClass);
        CarPerformanceIndex = p.CarPerformanceIndex;
        DrivetrainLabel = CarInfoFormatter.DrivetrainLabel(p.DrivetrainType);
    }
}
