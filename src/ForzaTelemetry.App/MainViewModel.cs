using System.Globalization;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Udp;

namespace ForzaTelemetry.App;

/// <summary>
/// The dashboard's single view model: current RPM/speed/gear, a packets-per-second figure, a
/// human-readable status line, and Start/Stop commands.
/// </summary>
/// <remarks>
/// This class NEVER talks to <c>TelemetryService</c> per-packet. It only ever reads out of it inside
/// <see cref="Refresh"/>, which something else (the composition root's <c>IUiTicker</c>) calls on a
/// fixed schedule, on the UI thread — see <c>TelemetryService</c>'s and <c>DispatcherTimerTicker</c>'s
/// remarks for the full reasoning. That is also why this class holds no reference to <see cref="IUiTicker"/>
/// itself: it does not need to know HOW OFTEN it gets refreshed, only what to do when it is.
/// <para>
/// <b>Bindings are reflection-based:</b> unlike a compiled binding, WPF resolves <c>{Binding SpeedKmh}</c>
/// by property name at RUNTIME, so a typo shows up as a blank label (and a line in the Output window),
/// not as a build error. <c>MainWindow.xaml</c> declares <c>d:DataContext</c> so the editor can at
/// least check names at design time.
/// </para>
/// </remarks>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>How stale the latest packet must be before the status line switches to "No data for Ns".</summary>
    private static readonly TimeSpan NoDataThreshold = TimeSpan.FromSeconds(2);

    private const float DefaultMaxRpm = 9000f;
    private const string NoGearLabel = "-";

    // The documented range of a real performance index; 0 (menus, loading) and anything outside it is not a car.
    private const int MinPerformanceIndex = 100;
    private const int MaxPerformanceIndex = 999;

    private readonly TelemetryService _telemetryService;
    private readonly TimeProvider _timeProvider;
    private readonly INetworkInfo? _networkInfo;

    // Used by Refresh to turn "packets received since last time" into a per-second rate.
    private long _receivedCountAtLastRefresh;
    private DateTimeOffset _lastRefreshAt;

    // Updated only when ReceivedCount has gone UP since the previous Refresh — i.e. "the last time a
    // NEW packet actually arrived", not merely "the last time Refresh happened to run". Read from the
    // same injected _timeProvider as PacketsPerSecond (not TelemetryService, which has no clock of its
    // own — see that class's remarks) so a test can make time "pass" deterministically for both.
    private DateTimeOffset _lastPacketArrivalAt;

    // Highest power/torque/boost seen for the current car, the scale of the engine gauges (see PowerRatio).
    private const double PeakPowerFloorKw = 100;
    private const double PeakTorqueFloorNm = 100;
    private const double PeakBoostFloorPsi = 10;
    private double _peakPowerKw = PeakPowerFloorKw;
    private double _peakTorqueNm = PeakTorqueFloorNm;
    private double _peakBoostPsi = PeakBoostFloorPsi;
    private int? _currentCarOrdinal;

    // Telemetry numbers read best with a space as thousands separator ("7 439"), regardless of the user's culture.
    private static readonly NumberFormatInfo SpacedNumbers = new() { NumberGroupSeparator = " ", NumberDecimalDigits = 0 };

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

    [ObservableProperty]
    private float _speedKmh;

    [ObservableProperty]
    private string _gearLabel = NoGearLabel;

    [ObservableProperty]
    private double _packetsPerSecond;

    // Human-readable status sentence. Its text comes from Resources/Strings*.resx (same as the view's own text);
    // ConnectionState below is the language-neutral version the view and the tests can rely on.
    [ObservableProperty]
    private string _status = Strings.StatusStopped;

    /// <summary>Closed-set version of <see cref="Status"/> that the view maps to the status dot colour.</summary>
    [ObservableProperty]
    private ConnectionState _connectionState = ConnectionState.Stopped;

    // [NotifyCanExecuteChangedFor]: whenever IsRunning changes, CommunityToolkit.Mvvm's source
    // generator re-checks StartCommand/StopCommand's CanExecute automatically (calling
    // Button.Command's CanExecute again is what actually enables/disables the bound Button in the
    // view) — without this attribute, the buttons would only re-evaluate the next time WPF
    // happened to ask, which is not guaranteed to be promptly after a Start/Stop.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    // ---- Data Out address ---------------------------------------------------------------

    /// <summary>
    /// What to type into the game's Data Out IP field: this PC's likely LAN address(es), comma-separated, or a
    /// localized "not found" text. Refreshed on every Start, since the network can change while the app is open.
    /// </summary>
    [ObservableProperty]
    private string _dataOutAddress = Strings.DataOutIpNone;

    /// <summary>The UDP port the game must send to (fixed: <see cref="UdpTelemetryListener.DefaultPort"/>).</summary>
    public int DataOutPort => UdpTelemetryListener.DefaultPort;

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

    /// <summary>
    /// Design-time-only constructor: used by <c>MainWindow.xaml</c>'s <c>d:DataContext</c>
    /// (<c>IsDesignTimeCreatable=True</c>) so the Visual Studio/Rider designer shows a populated dashboard
    /// instead of blank zeros. It builds a harmless <see cref="TelemetryService"/> (whose factory is never
    /// invoked — nothing in design mode calls Start) and then overwrites the display properties with
    /// representative sample values.
    /// </summary>
    public MainViewModel()
        : this(
            new TelemetryService(() => throw new InvalidOperationException("Design-time view model: Start is never called.")),
            TimeProvider.System)
    {
        Rpm = 4_200f;
        MaxRpm = 7_800f;
        SpeedKmh = 128f;
        GearLabel = "4";
        PacketsPerSecond = 29.7;
        Status = Strings.StatusReceiving;
        IsRunning = true;
        ConnectionState = ConnectionState.Receiving;

        // Dashboard widgets: a mid-corner, mid-throttle snapshot so the previewer shows every control populated.
        LateralG = 0.8;
        LongitudinalG = 0.3;
        ThrottleInput = 0.7;
        SteerInput = 0.25;
        PowerKw = 280;
        PowerHp = DashboardMath.ToHorsepower(280);
        PowerRatio = 0.8;
        TorqueNm = 410;
        TorqueRatio = 0.75;
        BoostPsi = 14;
        BoostRatio = 0.7;
        FuelPercent = 62;
        FuelRatio = 0.62;
        HasCarInfo = true;
        CarClassLabel = "A";
        CarPerformanceIndex = 780;
        DrivetrainLabel = "AWD";
        DataOutAddress = "192.168.1.20";
    }

    /// <summary>
    /// Real constructor, used by the app's composition root (<c>App.xaml.cs</c>'s <c>OnStartup</c>).
    /// </summary>
    /// <param name="telemetryService">The already-constructed telemetry service this view model reads from in <see cref="Refresh"/>.</param>
    /// <param name="timeProvider">
    /// Injected instead of reading <see cref="DateTimeOffset.UtcNow"/> directly, so a test can pass a
    /// fake clock (<c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c>) and get a deterministic,
    /// exact <see cref="PacketsPerSecond"/> instead of one that depends on real wall-clock timing.
    /// </param>
    /// <param name="networkInfo">Source of the LAN address shown in the top bar; optional so tests that do not care can omit it.</param>
    public MainViewModel(TelemetryService telemetryService, TimeProvider timeProvider, INetworkInfo? networkInfo = null)
    {
        _telemetryService = telemetryService;
        _timeProvider = timeProvider;
        _networkInfo = networkInfo;
        RefreshDataOutAddress();
        _lastRefreshAt = timeProvider.GetUtcNow();
        _lastPacketArrivalAt = _lastRefreshAt;
    }

    public TireViewModel FrontLeft { get; } = new();

    public TireViewModel FrontRight { get; } = new();

    public TireViewModel RearLeft { get; } = new();

    public TireViewModel RearRight { get; } = new();

    /// <summary>Current and maximum RPM as one readout, for example <c>7 439 / 9 000</c>.</summary>
    public string RpmReadout => string.Create(SpacedNumbers, $"{Rpm:N0} / {MaxRpm:N0}");

    /// <summary>
    /// Pulls the CURRENT state out of <see cref="TelemetryService"/> and pushes it into this view
    /// model's bound properties. Called ~20 times/sec, on the UI thread, by whatever <c>IUiTicker</c>
    /// the composition root wired up — see the class remarks for why this is the ONLY place this
    /// view model reads the service, instead of reacting to every packet.
    /// </summary>
    public void Refresh()
    {
        var fault = _telemetryService.Fault;
        if (fault is not null)
        {
            // Early return: do not sample packets/s or the latest packet while faulted. But the early return must
            // not leave the last live-looking numbers on screen either (a frozen "29 packets/s" next to an error,
            // a shift light still blinking), so show the idle dashboard.
            IsRunning = false;
            ResetLiveValues();
            ConnectionState = ConnectionState.Error;
            Status = DescribeFault(fault);
            return;
        }

        IsRunning = _telemetryService.IsRunning;
        UpdatePacketsPerSecond();

        var latest = _telemetryService.Latest;
        if (latest is { IsRaceOn: false })
        {
            // Menus, pause and rewind send packets with IsRaceOn = false and (mostly) zeroed fields. They prove the
            // game is still talking (the arrival clock above already counted them), but their values are not driving
            // data: showing them would flash zeros, and their car ordinal would wrongly reset the per-car gauge
            // scale. So show the idle dashboard and leave the per-car state (peaks, ordinal) alone.
            ResetLiveValues();
        }
        else if (latest is not null)
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

        var silenceSoFar = _timeProvider.GetUtcNow() - _lastPacketArrivalAt;
        ConnectionState = DetermineState(IsRunning, latest, silenceSoFar);
        Status = DescribeStatus(ConnectionState, silenceSoFar);
    }

    /// <summary>
    /// Idle dashboard: zero/placeholder readouts, no tire colors, no car pill, shift light off. Keeps the per-car
    /// gauge scales and the clocks, so it is safe to call repeatedly (every tick while faulted or paused).
    /// </summary>
    private void ResetLiveValues()
    {
        Rpm = 0;
        MaxRpm = DefaultMaxRpm;
        SpeedKmh = 0;
        GearLabel = NoGearLabel;
        PacketsPerSecond = 0;

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
    /// Forgets everything the previous session produced (Start, Stop): the idle dashboard plus fresh gauge scales,
    /// an empty G-force trail, and the packets/s and silence clocks re-anchored to "now" and to the service's
    /// current counter (which Stop/Start never resets).
    /// </summary>
    private void ResetSessionState()
    {
        ResetLiveValues();

        _currentCarOrdinal = null;
        _peakPowerKw = PeakPowerFloorKw;
        _peakTorqueNm = PeakTorqueFloorNm;
        _peakBoostPsi = PeakBoostFloorPsi;
        TrailResetToken++;

        // Re-anchor the clocks. Without this the silence timer would still count from the previous session's last
        // packet (a fresh Start would instantly read "No data for 40 s"), and the next packets/s would divide the
        // whole backlog of the old counter by a stale interval.
        var now = _timeProvider.GetUtcNow();
        _lastRefreshAt = now;
        _lastPacketArrivalAt = now;
        _receivedCountAtLastRefresh = _telemetryService.ReceivedCount;
    }

    /// <summary>Re-reads this PC's LAN addresses into <see cref="DataOutAddress"/>.</summary>
    public void RefreshDataOutAddress()
    {
        var addresses = _networkInfo?.GetLanAddresses() ?? [];
        DataOutAddress = addresses.Count == 0
            ? Strings.DataOutIpNone
            : string.Join(", ", addresses.Select(a => a.Address.ToString()));
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

    private void UpdatePacketsPerSecond()
    {
        var now = _timeProvider.GetUtcNow();
        var elapsed = now - _lastRefreshAt;
        var receivedCount = _telemetryService.ReceivedCount;

        // Guard against a zero/negative elapsed time (e.g. two refreshes on the exact same tick in a
        // test) — dividing by it would produce NaN/Infinity instead of just "no change yet".
        if (elapsed > TimeSpan.Zero)
        {
            PacketsPerSecond = (receivedCount - _receivedCountAtLastRefresh) / elapsed.TotalSeconds;
        }

        if (receivedCount > _receivedCountAtLastRefresh)
        {
            // At least one NEW packet arrived since the previous Refresh — reset the silence clock.
            _lastPacketArrivalAt = now;
        }

        _receivedCountAtLastRefresh = receivedCount;
        _lastRefreshAt = now;
    }

    private static ConnectionState DetermineState(bool isRunning, TelemetryPacket? latest, TimeSpan silenceSoFar)
    {
        if (!isRunning)
        {
            return ConnectionState.Stopped;
        }

        if (latest is null)
        {
            return ConnectionState.Listening;
        }

        return silenceSoFar > NoDataThreshold ? ConnectionState.NoData : ConnectionState.Receiving;
    }

    private static string DescribeStatus(ConnectionState state, TimeSpan silenceSoFar) => state switch
    {
        ConnectionState.Listening => string.Format(CultureInfo.CurrentCulture, Strings.StatusListening, UdpTelemetryListener.DefaultPort),
        ConnectionState.NoData => string.Format(CultureInfo.CurrentCulture, Strings.StatusNoData, silenceSoFar.TotalSeconds),
        ConnectionState.Receiving => Strings.StatusReceiving,
        _ => Strings.StatusStopped
    };

    private static string DescribeFault(Exception fault) => fault switch
    {
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } =>
            string.Format(CultureInfo.CurrentCulture, Strings.ErrorPortInUse, UdpTelemetryListener.DefaultPort),
        SocketException socketEx =>
            string.Format(CultureInfo.CurrentCulture, Strings.ErrorSocket, socketEx.SocketErrorCode, socketEx.Message),
        _ => string.Format(CultureInfo.CurrentCulture, Strings.ErrorGeneric, fault.Message)
    };

    // ---- Start / Stop / Toggle ------------------------------------------------------------
    //
    // Three commands, one lock. They all end up in TelemetryService, so two of them overlapping (a double click on
    // the toggle, Stop pressed while a slow Start is still awaiting) must be impossible. IsBusy is the shared
    // "a command is in flight" flag: it disables all three buttons AND makes the guard in RunExclusiveAsync drop a
    // call that arrives anyway (ExecuteAsync from code does not consult CanExecute). It is only touched on the UI
    // thread, so a plain bool is enough.
    // CanStart/CanStop read the service's LIVE state, not IsRunning: that property is only refreshed by the 20 Hz
    // tick, so right after a Start it can still say "stopped" for up to 50 ms.

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleListeningCommand))]
    private bool _isBusy;

    private bool CanStart() => !IsBusy && !_telemetryService.IsRunning;

    private bool CanStop() => !IsBusy && _telemetryService.IsRunning;

    private bool CanToggle() => !IsBusy;

    private async Task RunExclusiveAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action();
        }
        finally
        {
            IsBusy = false;
            // The service's state changed while IsRunning (the tick copy) did not notify: re-evaluate the buttons now.
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
        }
    }

    // [RelayCommand]: CommunityToolkit.Mvvm's source generator turns this method into a public
    // `StartCommand` property (an IAsyncRelayCommand) that can be bound to a Button.Command.
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunExclusiveAsync(StartCoreAsync);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => RunExclusiveAsync(StopCoreAsync);

    // The single Start/Stop toggle button: it does whatever the service's CURRENT state allows, decided INSIDE the
    // lock (after any in-flight command finished), so it can never pick Start from a stale answer. The button's
    // label/icon/look are switched by the view from IsRunning, so no text lives here.
    [RelayCommand(CanExecute = nameof(CanToggle))]
    private Task ToggleListeningAsync() =>
        RunExclusiveAsync(() => _telemetryService.IsRunning ? StopCoreAsync() : StartCoreAsync());

    private async Task StartCoreAsync()
    {
        RefreshDataOutAddress(); // The user may have changed network since the last Start.
        await _telemetryService.StartAsync();

        // After the service started (it dropped its own stale Latest) and on the UI thread (this continuation
        // resumes there, so no tick can interleave): wipe the previous session from the screen.
        ResetSessionState();
        Refresh(); // Show the result (running, or a friendly fault) immediately, without waiting for the next tick.
    }

    private async Task StopCoreAsync()
    {
        await _telemetryService.StopAsync();

        // A stopped dashboard must look idle: no frozen RPM, no shift light blinking at a redline that is long gone.
        ResetSessionState();
        Refresh();
    }
}
