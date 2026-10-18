using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Recording;
using ForzaTelemetry.Udp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForzaTelemetry.App;

/// <summary>Composition root: builds the services by hand (no DI container), then the shell view model and the window.</summary>
public partial class App : Application, IAppRestarter
{
    // How often the UI samples the latest packet (~20 Hz); see DispatcherTimerTicker for why sampling.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(50);

    // Created first in OnStartup so that even a composition failure is logged; flushed and closed in OnExit.
    private AppLogging? _logging;
    private ILogger<App> _logger = NullLogger<App>.Instance;

    private TelemetryService? _telemetryService;
    private SessionRecorder? _recorder;
    private SessionsViewModel? _sessions;
    private SettingsService? _settings;
    private PerformanceViewModel? _performance;
    private DispatcherTimerTicker? _ticker;
    private Action? _refresh;

    // Set by Restart(): OnExit relaunches the app AFTER the listener is stopped, so the new instance finds the UDP port free.
    private bool _restartRequested;

    // True once the main window is on screen. Before that a dispatcher exception cannot be "shown and survived":
    // there is no window to keep running for.
    private bool _mainWindowShown;

    // True while the error dialog is open; see OnDispatcherUnhandledException.
    private bool _showingErrorDialog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _logging = AppLogging.Create(AppLogging.DefaultLogDirectory);
        _logger = _logging.Factory.CreateLogger<App>();
        AppLog.Starting(
            _logger,
            AppInfo.Name,
            AppInfo.Version,
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription);

        RegisterGlobalExceptionHandlers();

        // Exceptions thrown here (composition root) would otherwise leave a process with no window: the default
        // ShutdownMode only ends the app when the LAST WINDOW closes, and none was ever shown. So report and exit.
        try
        {
            ComposeAndShowMainWindow();
        }
        catch (Exception ex)
        {
            AppLog.StartupFailed(_logger, ex);
            // Without culture data WPF itself cannot lay out text (its font cache constructs cultures), so no fallback can
            // save the start: say what is wrong and how to fix it instead of showing a type-initializer stack message.
            var message = LanguageService.IsInvariantGlobalization ? Strings.StartupInvariantGlobalization : ex.Message;
            MessageBox.Show(message, "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ComposeAndShowMainWindow()
    {
        var loggerFactory = _logging!.Factory;

        // Settings first: the language must be applied BEFORE anything reads a localized string (see LanguageService).
        // A missing or corrupt file is not an error here: the store logs it and hands back the defaults. The guard covers
        // what the store does not (an unusable path, a blocked folder): factory settings, and the app still opens. A file that
        // EXISTS but could not be read is different: the defaults are then a stand-in, so the service never writes them over it.
        // Before anything can change the culture: remember what Windows gave us, so "System" keeps meaning that after Apply.
        LanguageService.CaptureSystemCulture();

        var store = new JsonSettingsStore(JsonSettingsStore.DefaultPath, loggerFactory.CreateLogger<JsonSettingsStore>());
        _settings = SettingsService.LoadFrom(store, _logger);

        // A culture the runtime cannot load (invariant globalization, missing culture data) is logged and skipped: the
        // text stays on the neutral English resources instead of the app refusing to start.
        var culture = LanguageService.Apply(_settings.Current.Language, _logger);
        AppLog.LanguageApplied(_logger, _settings.Current.Language.ToString(), culture.Name);

        // The factory (not an instance) lets Stop/Start rebuild the UDP listener and rebind the port. It reads the port
        // from the settings each time it builds one, so a port change only needs a Stop + Start (the shell does that).
        // The same ILoggerFactory feeds both the service and every listener the factory builds, so bind, first
        // datagram per sender and discarded-datagram messages land in the same file as the app's own events.
        var listenerLogger = loggerFactory.CreateLogger<UdpTelemetryListener>();
        var settings = _settings;

        // Who sends the datagrams, for the connection guide's "packets arriving from ..." line. Reset by every new listener, so a
        // sender from the previous listening session is never shown as current.
        var senders = new DataOutSenderTracker();

        // The engines that need EVERY packet (not the 20 Hz sample the screens get): registered with the service, which
        // calls them from the receive loop; the view models below only read their snapshots. They are built together, with
        // the one ordered list the service is given, so none can be forgotten and the car-change detector runs first.
        var engines = new TelemetryEngines();

        // The recorder is long-lived (it outlives every listener the factory builds) and is handed to each new listener as a RAW
        // datagram observer: it sees every datagram before parsing, copies it and queues it for a background writer, so
        // recording never slows the receive loop. It only records while a recording has been started.
        _recorder = new SessionRecorder();
        var recorder = _recorder;
        _telemetryService = new TelemetryService(
            () =>
            {
                senders.Reset();
                return new UdpTelemetryListener(
                    settings.Current.UdpPort, logger: listenerLogger, datagramObservers: [recorder], senderObserver: senders.Record);
            },
            loggerFactory.CreateLogger<TelemetryService>(),
            observers: engines.Observers);
        _ticker = new DispatcherTimerTicker();

        // Manual composition (no DI container): each object is built here with exactly what it needs. The units service
        // follows the settings; the dashboard and tires read it; the Data Out object is shared by the top bar and the
        // Settings guide, so both always show the same address and port.
        var units = new UnitsService(_settings);

        // The player's car nicknames: read once here (a missing or damaged file is not an error), shared by every screen that shows a
        // car, with one rename box for all the places that offer to change a name.
        var carNames = new CarNameStore(CarNameStore.DefaultPath, loggerFactory.CreateLogger<CarNameStore>());
        StartupGuard.Run(_logger, "load car names", carNames.Load);
        var carRename = new CarRenameViewModel(carNames);
        var clipboard = new WpfClipboardService();

        // The data-out object reads the adapters once (and logs what it read, now and on every later Start), so this
        // root does not enumerate them a second time.
        var dataOut = new DataOutViewModel(
            _settings.Current.UdpPort,
            new NetworkInfo(),
            clipboard,
            loggerFactory.CreateLogger<DataOutViewModel>(),
            new NetworkListManagerProfileInfo(loggerFactory.CreateLogger<NetworkListManagerProfileInfo>()));
        var guide = new ConnectionGuideViewModel(_telemetryService, dataOut, senders, TimeProvider.System, _settings);
        var folderOpener = new ExplorerFolderOpener(loggerFactory.CreateLogger<ExplorerFolderOpener>());
        var settingsPage = new SettingsViewModel(
            _settings,
            dataOut,
            _logging.LogDirectory ?? AppLogging.DefaultLogDirectory,
            folderOpener,
            this,
            guide: guide);

        // The recordings folder, created on demand (%APPDATA%/ForzaTelemetry/recordings), and the page that records into it and
        // replays from it. The page asks the shell (below) to start and stop replays; the shell attaches itself to it.
        var sessionsPage = _sessions = new SessionsViewModel(
            _telemetryService,
            recorder,
            new RecordingLibrary(RecordingLibrary.DefaultFolder, TimeProvider.System, loggerFactory.CreateLogger<RecordingLibrary>()),
            new WpfConfirmationService(),
            folderOpener,
            loggerFactory.CreateLogger<SessionsViewModel>(),
            carNames);

        // The per-car bests are read once here; a missing or damaged file is not an error (the store logs it).
        var history = new PerformanceHistoryStore(
            PerformanceHistoryStore.DefaultPath, TimeProvider.System, loggerFactory.CreateLogger<PerformanceHistoryStore>());
        StartupGuard.Run(_logger, "load performance history", history.Load);
        _performance = new PerformanceViewModel(
            engines.Acceleration, engines.TopSpeed, engines.CarChanges, history, units, new WpfConfirmationService(), carNames, carRename,
            engines.Braking);

        var lapsPage = new LapsViewModel(engines.Laps);

        // The insights pages read the engines the receive loop feeds; they are only refreshed while their tab is showing.
        var analysisPage = new AnalysisViewModel(engines.History, units);
        var mapPage = new MapViewModel(engines.Path, engines.History, units);
        var tuningPage = new TuningViewModel(engines.Dyno, engines.History, engines.Bottoming, units, carNames: carNames);

        var viewModel = new ShellViewModel(
            _telemetryService,
            TimeProvider.System,
            logger: loggerFactory.CreateLogger<ShellViewModel>(),
            clipboard: clipboard,
            dashboard: new DashboardViewModel(units, carNames),
            dataOut: dataOut,
            settings: _settings,
            settingsPage: settingsPage,
            performancePage: _performance,
            lapsPage: lapsPage,
            analysisPage: analysisPage,
            mapPage: mapPage,
            tuningPage: tuningPage,
            engines: engines,
            sessionsPage: sessionsPage,
            guide: guide,
            carRename: carRename);

        // The ticker runs on the UI thread and calls Refresh, which pulls the latest packet into the bound properties.
        _refresh = viewModel.Refresh;
        _ticker.Start(RefreshInterval, _refresh);

        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
        _mainWindowShown = true;

        // First launch only: the guide opens over the window by itself (see ConnectionGuideViewModel.OpenIfFirstRun).
        guide.OpenIfFirstRun();

        // "Start listening when the app opens": run the same command as the Start button, after the window is up so a
        // failure (port in use) shows as the normal red status instead of blocking startup.
        if (_settings.Current.StartListeningOnLaunch)
        {
            viewModel.StartCommand.Execute(null);
        }
    }

    /// <summary>
    /// Restart request from Settings (language change). Shutdown() runs the normal exit path (OnExit), which stops the
    /// listener and then, because of the flag, launches the new instance, so the two never fight over the UDP port.
    /// </summary>
    public void Restart()
    {
        AppLog.Restarting(_logger);
        _restartRequested = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ticker?.Stop();

        // A library scan started by a recording stop must not finish into a closing dispatcher: cancel it and silence it.
        _sessions?.Shutdown();

        // Blocking here is safe: StopAsync awaits with ConfigureAwait(false) all the way down, so it never needs
        // the UI thread to resume (no sync-over-async deadlock), and the process is exiting anyway. Waiting
        // guarantees the receive loop has ended and the UDP socket is released before the process goes away.
        // StopAsync is serialized, so even if a Stop from the UI is still in flight, this call waits for it.
        // Wrapped in try/catch so a failing dispose can never skip base.OnExit (which finishes shutdown).
        try
        {
            _telemetryService?.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Nothing to show while exiting, but the file keeps the evidence.
            AppLog.ShutdownStopFailed(_logger, ex);
        }

        // A recording still running at exit (the shell's Stop was not the way out) must be finished, or its queued tail is lost
        // and the file is left truncated. The listener is stopped above, so nothing feeds the recorder any more. The recorder is
        // disposed directly (not through the view model): its async code never needs the UI thread, so blocking here is safe.
        try
        {
            _recorder?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppLog.ShutdownStopFailed(_logger, ex);
        }

        // The listener has stopped, so no engine is changing any more: save the last car's top speed and any unsaved best.
        try
        {
            _performance?.EndSession();
        }
        catch (Exception ex)
        {
            AppLog.ShutdownStopFailed(_logger, ex);
        }

        if (_restartRequested)
        {
            RelaunchSelf();
        }

        // Last log call, then flush and close the file so the final lines are not lost with the process.
        AppLog.Exiting(_logger, AppInfo.Name);
        _logging?.Dispose();

        base.OnExit(e);
    }

    private void RelaunchSelf()
    {
        try
        {
            // See RelaunchPlan: under the dotnet host the process path is dotnet.exe, so the assembly must be passed to it.
            var plan = RelaunchPlan.Create(
                Environment.ProcessPath,
                Assembly.GetEntryAssembly()?.Location,
                Environment.GetCommandLineArgs().Skip(1).ToList());
            if (plan is null)
            {
                return;
            }

            // ArgumentList (not a joined string) so the OS-level quoting is done for us. It requires UseShellExecute=false,
            // which is what we want anyway: this starts our own executable, it does not "open a document".
            var startInfo = new ProcessStartInfo(plan.FileName) { UseShellExecute = false };
            foreach (var argument in plan.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.RestartFailed(_logger, ex);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // A faulted Task that nobody awaited. Since .NET 4.5 it no longer terminates the process by default; it is
        // raised here when the finalizer collects the Task, which may be long after the failure. Log it (so it is not
        // silently lost) and mark it observed (so nothing escalates it later).
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.UnobservedTaskException(_logger, args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>
    /// An exception thrown on the UI thread (e.g. in a bound property or Refresh) would otherwise tear down the whole
    /// app. Show it and keep running: the dashboard is a live monitor, and losing the window over one bad tick is
    /// worse than a message. Every occurrence is logged, including the ones the dialog guard below suppresses.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        AppLog.UnhandledUiException(_logger, args.Exception);

        // Before the window is shown there is nothing to keep alive: leave the exception unhandled so the process
        // fails visibly instead of lingering headless.
        if (!_mainWindowShown)
        {
            return;
        }

        // MessageBox.Show does not return until the user clicks, but the UI thread is NOT blocked: it runs a NESTED
        // message loop (a second Dispatcher loop inside this call) so the dialog stays responsive. That nested loop
        // keeps dispatching everything else, including our 20 Hz timer. If Refresh throws on every tick, each tick
        // would raise this event again, from inside the previous dialog, and stack a new dialog every 50 ms.
        // Two defenses: ignore (silently) errors that arrive while a dialog is already open, and stop the ticker
        // while it is open so the same fault is not re-raised in the first place.
        args.Handled = true;
        if (_showingErrorDialog)
        {
            return;
        }

        _showingErrorDialog = true;
        _ticker?.Stop();
        try
        {
            MessageBox.Show(args.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingErrorDialog = false;

            // Resume: the fault may have been transient and a frozen dashboard is worse than a repeated, dismissible
            // message. At most ONE dialog is ever open, and a permanent fault shows one per dismissal, not a stack.
            if (_refresh is not null)
            {
                _ticker?.Start(RefreshInterval, _refresh);
            }
        }
    }
}
