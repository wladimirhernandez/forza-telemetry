using System.Globalization;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Udp;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App;

/// <summary>
/// The window's view model: which screen is showing (tab navigation), the connection status pills, the Data Out
/// address, the Start/Stop toggle, and the one refresh that pulls telemetry out of the service every tick.
/// </summary>
/// <remarks>
/// <b>Why a shell view model.</b> Before the tabs, one view model did everything because there was one screen. With
/// several screens, the things that are true no matter which screen is open (are we listening? what is the status? what
/// do I type into the game?) must not live inside any one screen's view model. This class owns them and the screens
/// own only their own values (<see cref="DashboardViewModel"/>, later the Laps/Performance/... view models). The window
/// binds its top bar to the shell and its content area to <see cref="CurrentPage"/>.
/// <para>
/// This class NEVER talks to <c>TelemetryService</c> per-packet. It only ever reads out of it inside
/// <see cref="Refresh"/>, which something else (the composition root's <c>IUiTicker</c>) calls on a
/// fixed schedule, on the UI thread — see <c>TelemetryService</c>'s and <c>DispatcherTimerTicker</c>'s
/// remarks for the full reasoning. That is also why this class holds no reference to <see cref="IUiTicker"/>
/// itself: it does not need to know HOW OFTEN it gets refreshed, only what to do when it is.
/// </para>
/// <para>
/// <b>One ticker, many pages.</b> There is a single 20 Hz tick for the whole app. Each tick the shell reads the service
/// once and updates the status and the <see cref="Dashboard"/> values. That is cheap on purpose: a property that is set to
/// the value it already has raises no change notification, and a screen that is not showing has no bindings listening
/// (the <c>ContentControl</c> removes the old page's view from the visual tree on navigation), so updating the dashboard
/// while another tab is open costs a few field writes. The dashboard is always fed because the top bar's car pill reads
/// it. A screen that is EXPENSIVE to update (a live chart, the track map) does not copy that approach: it implements
/// <see cref="IRefreshablePage"/> and the shell calls it only while <see cref="CurrentPage"/> is that screen.
/// </para>
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>How stale the latest packet must be before the status line switches to "No data for Ns".</summary>
    private static readonly TimeSpan NoDataThreshold = TimeSpan.FromSeconds(2);

    private readonly TelemetryService _telemetryService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;
    private readonly SettingsService? _settings;
    private readonly TelemetryEngines? _engines;

    // The port the LAST Start tried to bind (read from the settings at that moment, not when an error is shown). A port
    // change while the listener is running or has failed updates the settings at once, so the settings port can differ
    // from the one the current listener, or the failed bind, used. Null until the first Start; messages then fall back to
    // the displayed port.
    private int? _attemptedPort;

    // Used by Refresh to turn "packets received since last time" into a per-second rate.
    private long _receivedCountAtLastRefresh;
    private DateTimeOffset _lastRefreshAt;

    // Updated only when ReceivedCount has gone UP since the previous Refresh — i.e. "the last time a
    // NEW packet actually arrived", not merely "the last time Refresh happened to run". Read from the
    // same injected _timeProvider as PacketsPerSecond (not TelemetryService, which has no clock of its
    // own — see that class's remarks) so a test can make time "pass" deterministically for both.
    private DateTimeOffset _lastPacketArrivalAt;

    // Human-readable status sentence. Its text comes from Resources/Strings*.resx (same as the view's own text);
    // ConnectionState below is the language-neutral version the view and the tests can rely on.
    [ObservableProperty]
    private string _status = Strings.StatusStopped;

    /// <summary>Closed-set version of <see cref="Status"/> that the view maps to the status dot colour.</summary>
    [ObservableProperty]
    private ConnectionState _connectionState = ConnectionState.Stopped;

    [ObservableProperty]
    private double _packetsPerSecond;

    // [NotifyCanExecuteChangedFor]: whenever IsRunning changes, CommunityToolkit.Mvvm's source
    // generator re-checks StartCommand/StopCommand's CanExecute automatically (calling
    // Button.Command's CanExecute again is what actually enables/disables the bound Button in the
    // view) — without this attribute, the buttons would only re-evaluate the next time WPF
    // happened to ask, which is not guaranteed to be promptly after a Start/Stop.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    /// <param name="telemetryService">The already-constructed telemetry service this view model reads from in <see cref="Refresh"/>.</param>
    /// <param name="timeProvider">
    /// Injected instead of reading <see cref="DateTimeOffset.UtcNow"/> directly, so a test can pass a
    /// fake clock (<c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c>) and get a deterministic,
    /// exact <see cref="PacketsPerSecond"/> instead of one that depends on real wall-clock timing.
    /// </param>
    /// <param name="networkInfo">Source of the LAN address shown in the top bar; optional so tests that do not care can omit it.</param>
    /// <param name="logger">Receives the address list on every read (so the log shows what the user was told); optional.</param>
    /// <param name="clipboard">Target of the "copy IP" command; optional.</param>
    /// <param name="dashboard">The dashboard screen's view model; optional so only a test or the designer has to pass one.</param>
    /// <param name="dataOut">
    /// The address/port object shared with the Settings screen. Optional: when omitted the shell builds its own from
    /// <paramref name="networkInfo"/>, <paramref name="clipboard"/> and <paramref name="logger"/>.
    /// </param>
    /// <param name="settings">
    /// The live settings. When given, the shell restarts the listener if the UDP port changes; without it the port stays
    /// the default.
    /// </param>
    /// <param name="settingsPage">The Settings screen's view model; when omitted that tab shows the "coming soon" placeholder.</param>
    /// <param name="performancePage">The Performance screen's view model; when omitted that tab shows the placeholder.</param>
    /// <param name="lapsPage">The Laps screen's view model; when omitted that tab shows the placeholder.</param>
    /// <param name="analysisPage">The Analysis screen's view model; when omitted that tab shows the placeholder.</param>
    /// <param name="mapPage">The Map screen's view model; when omitted that tab shows the placeholder.</param>
    /// <param name="tuningPage">The Tuning screen's view model; when omitted that tab shows the placeholder.</param>
    /// <param name="engines">
    /// The per-packet engines the receive loop feeds. The shell resets them at the start of every fresh session, BEFORE the loop
    /// starts (see <see cref="StartCoreAsync"/>); when omitted (tests of other things) there is nothing to reset.
    /// </param>
    public ShellViewModel(
        TelemetryService telemetryService,
        TimeProvider timeProvider,
        INetworkInfo? networkInfo = null,
        ILogger? logger = null,
        IClipboardService? clipboard = null,
        DashboardViewModel? dashboard = null,
        DataOutViewModel? dataOut = null,
        SettingsService? settings = null,
        SettingsViewModel? settingsPage = null,
        PerformanceViewModel? performancePage = null,
        LapsViewModel? lapsPage = null,
        AnalysisViewModel? analysisPage = null,
        MapViewModel? mapPage = null,
        TuningViewModel? tuningPage = null,
        TelemetryEngines? engines = null)
    {
        _telemetryService = telemetryService;
        _engines = engines;
        _timeProvider = timeProvider;
        _logger = logger;
        _settings = settings;
        Dashboard = dashboard ?? new DashboardViewModel();
        DataOut = dataOut
            ?? new DataOutViewModel(settings?.Current.UdpPort ?? UdpTelemetryListener.DefaultPort, networkInfo, clipboard, logger);

        Performance = performancePage;
        Laps = lapsPage;
        Analysis = analysisPage;
        Map = mapPage;
        Tuning = tuningPage;
        Tabs = BuildTabs(Dashboard, performancePage, lapsPage, analysisPage, mapPage, tuningPage, settingsPage);
        _selectedTab = Tabs[0];

        if (settings is not null)
        {
            settings.Changed += OnSettingsChanged;
        }

        _lastRefreshAt = timeProvider.GetUtcNow();
        _lastPacketArrivalAt = _lastRefreshAt;
    }

    /// <summary>
    /// A populated instance for the XAML designer (<c>d:DataContext="{x:Static local:ShellViewModel.DesignSample}"</c>):
    /// it builds a harmless <see cref="TelemetryService"/> (whose factory is never invoked — nothing in design mode
    /// calls Start) and fills the status values with representative samples.
    /// </summary>
    public static ShellViewModel DesignSample => new(
        new TelemetryService(() => throw new InvalidOperationException("Design-time view model: Start is never called.")),
        TimeProvider.System,
        dashboard: DashboardViewModel.DesignSample)
    {
        PacketsPerSecond = 29.7,
        Status = Strings.StatusReceiving,
        IsRunning = true,
        ConnectionState = ConnectionState.Receiving
    };

    /// <summary>The Dashboard screen's values.</summary>
    public DashboardViewModel Dashboard { get; }

    /// <summary>The Performance screen's values; null when the shell was built without one (tests).</summary>
    public PerformanceViewModel? Performance { get; }

    /// <summary>The Laps screen's values; null when the shell was built without one (tests).</summary>
    public LapsViewModel? Laps { get; }

    /// <summary>The Analysis screen's values; null when the shell was built without one (tests).</summary>
    public AnalysisViewModel? Analysis { get; }

    /// <summary>The Map screen's values; null when the shell was built without one (tests).</summary>
    public MapViewModel? Map { get; }

    /// <summary>The Tuning screen's values; null when the shell was built without one (tests).</summary>
    public TuningViewModel? Tuning { get; }

    /// <summary>This PC's address and the port the game must send to (top bar pill; later also the Settings guide).</summary>
    public DataOutViewModel DataOut { get; }

    // ---- Navigation ---------------------------------------------------------------------------

    /// <summary>The tabs, left to right. The index in this list is also the Ctrl+N shortcut (Ctrl+1 is the first).</summary>
    public IReadOnlyList<NavigationItem> Tabs { get; }

    /// <summary>
    /// The selected tab. The tab strip binds this two-way, so a click, an arrow key or a Ctrl+N shortcut all end up here
    /// and nothing else has to keep them in sync. [NotifyPropertyChangedFor]: <see cref="CurrentPage"/> is derived from it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    private NavigationItem _selectedTab;

    /// <summary>
    /// The view model of the screen being shown. The window's <c>ContentControl</c> binds this as its Content; WPF then
    /// finds the <c>DataTemplate</c> whose DataType matches this object's runtime TYPE and builds that view for it.
    /// </summary>
    public object CurrentPage => SelectedTab.Page;

    /// <summary>Switches to the given screen (the Ctrl+1..8 key bindings pass the screen's <see cref="PageId"/>).</summary>
    [RelayCommand]
    private void Navigate(PageId page)
    {
        var target = Tabs.FirstOrDefault(tab => tab.Id == page);
        if (target is not null)
        {
            SelectedTab = target;
        }
    }

    private static List<NavigationItem> BuildTabs(
        DashboardViewModel dashboard,
        PerformanceViewModel? performancePage,
        LapsViewModel? lapsPage,
        AnalysisViewModel? analysisPage,
        MapViewModel? mapPage,
        TuningViewModel? tuningPage,
        SettingsViewModel? settingsPage)
    {
        // The order here is the order on screen and the Ctrl+N numbering. Placeholders are replaced by the real
        // screen's view model as each screen ships; nothing else (template, strip, shortcuts) needs to change.
        (PageId Id, string Title, object Page)[] pages =
        [
            (PageId.Dashboard, Strings.TabDashboard, dashboard),
            performancePage is not null
                ? (PageId.Performance, Strings.TabPerformance, performancePage)
                : Placeholder(PageId.Performance, Strings.TabPerformance, Strings.PerformancePlaceholder, PageIcons.Performance),
            lapsPage is not null
                ? (PageId.Laps, Strings.TabLaps, lapsPage)
                : Placeholder(PageId.Laps, Strings.TabLaps, Strings.LapsPlaceholder, PageIcons.Laps),
            Placeholder(PageId.Sessions, Strings.TabSessions, Strings.SessionsPlaceholder, PageIcons.Sessions),
            analysisPage is not null
                ? (PageId.Analysis, Strings.TabAnalysis, analysisPage)
                : Placeholder(PageId.Analysis, Strings.TabAnalysis, Strings.AnalysisPlaceholder, PageIcons.Analysis),
            mapPage is not null
                ? (PageId.Map, Strings.TabMap, mapPage)
                : Placeholder(PageId.Map, Strings.TabMap, Strings.MapPlaceholder, PageIcons.Map),
            tuningPage is not null
                ? (PageId.Tuning, Strings.TabTuning, tuningPage)
                : Placeholder(PageId.Tuning, Strings.TabTuning, Strings.TuningPlaceholder, PageIcons.Tuning),
            settingsPage is not null
                ? (PageId.Settings, Strings.TabSettings, settingsPage)
                : Placeholder(PageId.Settings, Strings.TabSettings, Strings.SettingsPlaceholder, PageIcons.Settings)
        ];

        return pages
            .Select((page, index) => new NavigationItem(page.Id, page.Title, $"Ctrl+{index + 1}", page.Page))
            .ToList();
    }

    private static (PageId Id, string Title, object Page) Placeholder(PageId id, string title, string description, string icon) =>
        (id, title, new PlaceholderPageViewModel(id, title, description, icon));

    // ---- Refresh --------------------------------------------------------------------------------

    /// <summary>
    /// Pulls the CURRENT state out of <see cref="TelemetryService"/> and pushes it into the status values and the
    /// dashboard. Called ~20 times/sec, on the UI thread, by whatever <c>IUiTicker</c>
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
            Dashboard.ResetLive();
            Performance?.Refresh(null); // no driving data: the state line says so (the measured numbers stay readable)
            Laps?.Refresh(null);
            PacketsPerSecond = 0; // the loop is dead: nothing is arriving, whatever the last sample said
            ConnectionState = ConnectionState.Error;
            Status = DescribeFault(fault);
            RefreshVisiblePage(); // what the engines still hold (the session before the fault) stays readable
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
            Dashboard.ResetLive();
        }
        else if (latest is not null)
        {
            Dashboard.Apply(latest);
        }

        var silenceSoFar = _timeProvider.GetUtcNow() - _lastPacketArrivalAt;

        // Every tick, whether or not the Performance or Laps tab is showing: they detect the end of a run or a lap and save
        // what is worth saving, which must not depend on which screen the user happens to be looking at. (A snapshot read
        // and a few property writes each.) After the stream has gone silent the last packet is no longer "live" (the game
        // was closed, the console went to sleep): they get null, so a frozen delta or an armed timer does not pose as current.
        var driving = silenceSoFar > NoDataThreshold ? null : latest;
        Performance?.Refresh(driving);
        Laps?.Refresh(driving);

        ConnectionState = DetermineState(IsRunning, latest, silenceSoFar);
        Status = DescribeStatus(ConnectionState, silenceSoFar);
        RefreshVisiblePage();
    }

    /// <summary>
    /// Refreshes the page on screen if it is one of the expensive ones (<see cref="IRefreshablePage"/>: the live traces, the map,
    /// the dyno). Only that one: the others are hidden, nobody sees their work, and returning to a tab brings its page up to date
    /// on the next tick because it reads the engines' current state.
    /// </summary>
    private void RefreshVisiblePage()
    {
        if (CurrentPage is IRefreshablePage page)
        {
            page.Refresh();
        }
    }

    /// <summary>
    /// Forgets everything the previous session produced (Start, Stop): the idle dashboard plus the packets/s and
    /// silence clocks re-anchored to "now" and to the service's current counter (which Stop/Start never resets).
    /// </summary>
    private void ResetSessionState()
    {
        Dashboard.ResetSession();
        PacketsPerSecond = 0;

        // Re-anchor the clocks. Without this the silence timer would still count from the previous session's last
        // packet (a fresh Start would instantly read "No data for 40 s"), and the next packets/s would divide the
        // whole backlog of the old counter by a stale interval.
        var now = _timeProvider.GetUtcNow();
        _lastRefreshAt = now;
        _lastPacketArrivalAt = now;
        _receivedCountAtLastRefresh = _telemetryService.ReceivedCount;
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

    private string DescribeStatus(ConnectionState state, TimeSpan silenceSoFar) => state switch
    {
        ConnectionState.Listening => string.Format(CultureInfo.CurrentCulture, Strings.StatusListening, _attemptedPort ?? DataOut.Port),
        ConnectionState.NoData => string.Format(CultureInfo.CurrentCulture, Strings.StatusNoData, silenceSoFar.TotalSeconds),
        ConnectionState.Receiving => Strings.StatusReceiving,
        _ => Strings.StatusStopped
    };

    private string DescribeFault(Exception fault) => fault switch
    {
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } =>
            string.Format(CultureInfo.CurrentCulture, Strings.ErrorPortInUse, _attemptedPort ?? DataOut.Port),
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

            // A port change that arrived while this command was in flight was parked (see RestartListenerAsync). It runs
            // in `finally`, not after it: if `action` threw, code after the try/finally would be skipped and the user's
            // port change would never be applied. RestartListenerAsync never throws, so it cannot hide the original error.
            if (_portRestartPending)
            {
                _portRestartPending = false;
                await RestartListenerAsync();
            }
        }
    }

    // ---- Settings changes ---------------------------------------------------------------------------

    // True when the port changed while another command was running, so the restart waits its turn.
    private bool _portRestartPending;

    /// <summary>
    /// Reacts to a settings change. Only the UDP port matters here (units are applied by <see cref="UnitsService"/>, language
    /// at startup): the pills must quote the new port, and a listener that is running must move to it.
    /// </summary>
    // async void is normally a smell (nobody can await it or observe its exception), but an event handler has to return
    // void. RestartListenerAsync catches and logs everything itself, so nothing can escape unobserved.
    private async void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.UdpPort == e.Current.UdpPort)
        {
            return;
        }

        if (_logger is not null)
        {
            AppLog.PortChanged(_logger, e.Previous.UdpPort, e.Current.UdpPort);
        }

        DataOut.Port = e.Current.UdpPort;
        await RestartListenerAsync();
    }

    /// <summary>
    /// Moves the listener to the port now in the settings by stopping and starting it through the same serialized path as
    /// the Start/Stop buttons (so it cannot interleave with them). The listener factory reads the port when it builds a
    /// new listener, so a plain Stop then Start is all it takes. If a command is already in flight, the restart is parked
    /// and runs right after it. A listener the user left STOPPED stays stopped: the next Start uses the new port.
    /// <para>
    /// A listener that FAILED (for example "port in use" on the old port) counts as one the user wants running: they are
    /// looking at the error and just typed another port to get out of it, so applying it retries the Start.
    /// </para>
    /// Never throws: failures are logged (it is awaited from an event handler and from a <c>finally</c> block).
    /// </summary>
    private async Task RestartListenerAsync()
    {
        if (IsBusy)
        {
            _portRestartPending = true;
            return;
        }

        try
        {
            await RunExclusiveAsync(async () =>
            {
                // continuesSession: moving the port of a RUNNING listener is the SAME listening session on another port. The laps,
                // the bests and the charts are what the user is looking at; wiping them because a setting changed would be a
                // data loss.
                if (_telemetryService.IsRunning)
                {
                    await StopCoreAsync();
                    await StartCoreAsync(continuesSession: true);
                }
                else if (_telemetryService.Fault is not null)
                {
                    // Nothing to stop: a failed start left no listener behind. And nothing to continue either: that failed Start
                    // already began a session (it reset the screens and the engines), so this retry is the first real start of it.
                    await StartCoreAsync(continuesSession: false);
                }
            });
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                AppLog.PortRestartFailed(_logger, ex);
            }
        }
    }

    // [RelayCommand]: CommunityToolkit.Mvvm's source generator turns this method into a public
    // `StartCommand` property (an IAsyncRelayCommand) that can be bound to a Button.Command.
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunExclusiveAsync(() => StartCoreAsync());

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => RunExclusiveAsync(StopCoreAsync);

    // The single Start/Stop toggle button: it does whatever the service's CURRENT state allows, decided INSIDE the
    // lock (after any in-flight command finished), so it can never pick Start from a stale answer. The button's
    // label/icon/look are switched by the view from IsRunning, so no text lives here.
    [RelayCommand(CanExecute = nameof(CanToggle))]
    private Task ToggleListeningAsync() =>
        RunExclusiveAsync(() => _telemetryService.IsRunning ? StopCoreAsync() : StartCoreAsync());

    /// <param name="continuesSession">
    /// True when this Start only moves a RUNNING listener (a port change): the session the screens show goes on, so nothing is
    /// reset. False for the user's own Start, which begins a new one.
    /// </param>
    private async Task StartCoreAsync(bool continuesSession = false)
    {
        await DataOut.RefreshAsync(); // The user may have changed network since the last Start.

        // A fresh Start while already listening is not a new session, it is nothing: wiping the engines under a running loop
        // is exactly what the ordering below exists to avoid. (Checked after the await: the state may have changed meanwhile.)
        if (!continuesSession && _telemetryService.IsRunning)
        {
            return;
        }

        // Remember what this Start is about to bind: the listener factory reads the same setting a moment from now.
        _attemptedPort = _settings?.Current.UdpPort ?? DataOut.Port;

        // A NEW session begins with the Start ATTEMPT, before the receive loop exists. Why before and not "after it worked":
        // the engines are written by the receive loop, so any reset that runs after the loop started races with its first
        // packets. Before it starts nothing else touches them and the reset is exact. The price is that a Start that then
        // fails (port in use) leaves the screens empty, which is honest: the previous session is over once the user asked
        // for another one. (A port move continues the session and skips this.)
        if (!continuesSession)
        {
            BeginFreshSession();
        }

        await _telemetryService.StartAsync();

        // After the service started (it dropped its own stale Latest) and on the UI thread (this continuation
        // resumes there, so no tick can interleave): wipe the previous session's LIVE values from the dashboard.
        ResetSessionState();

        Refresh(); // Show the result (running, or a friendly fault) immediately, without waiting for the next tick.
    }

    /// <summary>
    /// Ends the previous session's bookkeeping and gives every engine and screen a clean slate. Must run while NO receive
    /// loop is running (see <see cref="StartCoreAsync"/>). The order is the point:
    /// <list type="number">
    /// <item>The previous session's top speed is saved first: the engines are about to forget it, and a session that ended in a
    /// fault never went through Stop. The loop is not running, so this read cannot race a change of car.</item>
    /// <item>The ENGINES are reset (one call, <see cref="TelemetryEngines.BeginSession"/>), including the car-change detector.</item>
    /// <item>The screens reset only what they DISPLAY, after their source is empty.</item>
    /// </list>
    /// </summary>
    private void BeginFreshSession()
    {
        Performance?.EndSession();
        _engines?.BeginSession();

        Performance?.BeginSession();
        Laps?.BeginSession();
        Analysis?.BeginSession();
        Map?.BeginSession();
        Tuning?.BeginSession();
    }

    private async Task StopCoreAsync()
    {
        await _telemetryService.StopAsync();

        // A stopped dashboard must look idle: no frozen RPM, no shift light blinking at a redline that is long gone.
        ResetSessionState();

        // Unlike the dashboard, the Performance numbers stay on screen (the user stops to READ the result); only unsaved
        // bests are written out and the state line stops claiming the timer is armed.
        Performance?.EndSession();
        Laps?.EndSession();
        Refresh();
    }
}
