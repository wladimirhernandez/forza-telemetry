using System.Globalization;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    private readonly TelemetryService _telemetryService;
    private readonly TimeProvider _timeProvider;

    // Used by Refresh to turn "packets received since last time" into a per-second rate.
    private long _receivedCountAtLastRefresh;
    private DateTimeOffset _lastRefreshAt;

    // Updated only when ReceivedCount has gone UP since the previous Refresh — i.e. "the last time a
    // NEW packet actually arrived", not merely "the last time Refresh happened to run". Read from the
    // same injected _timeProvider as PacketsPerSecond (not TelemetryService, which has no clock of its
    // own — see that class's remarks) so a test can make time "pass" deterministically for both.
    private DateTimeOffset _lastPacketArrivalAt;

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
    private float _maxRpm = 9000f;

    [ObservableProperty]
    private float _speedKmh;

    [ObservableProperty]
    private string _gearLabel = "-";

    [ObservableProperty]
    private double _packetsPerSecond;

    // TODO(localization): the status sentences below are still English literals produced by the view model;
    // the view's own text already comes from Resources/Strings.resx.
    [ObservableProperty]
    private string _status = "Stopped";

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
        Status = "Receiving";
        IsRunning = true;
        ConnectionState = ConnectionState.Receiving;
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
    public MainViewModel(TelemetryService telemetryService, TimeProvider timeProvider)
    {
        _telemetryService = telemetryService;
        _timeProvider = timeProvider;
        _lastRefreshAt = timeProvider.GetUtcNow();
        _lastPacketArrivalAt = _lastRefreshAt;
    }

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
            IsRunning = false;
            ConnectionState = ConnectionState.Error;
            Status = DescribeFault(fault);
            return;
        }

        IsRunning = _telemetryService.IsRunning;
        UpdatePacketsPerSecond();

        var latest = _telemetryService.Latest;
        if (latest is not null)
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
        }

        var silenceSoFar = _timeProvider.GetUtcNow() - _lastPacketArrivalAt;
        ConnectionState = DetermineState(IsRunning, latest, silenceSoFar);
        Status = DescribeStatus(ConnectionState, silenceSoFar);
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
        ConnectionState.Listening => $"Listening on UDP {UdpTelemetryListener.DefaultPort} — waiting for data (drive in-game)",
        ConnectionState.NoData => $"No data for {silenceSoFar.TotalSeconds:F0}s",
        ConnectionState.Receiving => "Receiving",
        _ => "Stopped"
    };

    private static string DescribeFault(Exception fault) => fault switch
    {
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } =>
            $"Error: port {UdpTelemetryListener.DefaultPort} already in use — is another instance running?",
        SocketException socketEx => $"Error: {socketEx.SocketErrorCode} ({socketEx.Message})",
        _ => $"Error: {fault.Message}"
    };

    private bool CanStart() => !IsRunning;

    private bool CanStop() => IsRunning;

    // [RelayCommand]: CommunityToolkit.Mvvm's source generator turns this method into a public
    // `StartCommand` property (an IAsyncRelayCommand) that MainWindow.xaml binds a Button.Command to.
    // Two separate commands (Start/Stop), each with its own CanExecute, is the clearer pattern for a
    // first look at commands: the Button's enabled state follows directly from ONE boolean condition
    // per button, instead of one toggle button whose single Content/meaning has to flip depending on
    // hidden state — that flip is exactly the kind of thing that is easy to get backwards early on.
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        await _telemetryService.StartAsync();
        Refresh(); // Show the result (running, or a friendly fault) immediately, without waiting for the next tick.
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        await _telemetryService.StopAsync();
        Refresh();
    }
}
