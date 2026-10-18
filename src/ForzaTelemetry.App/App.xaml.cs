using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Udp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForzaTelemetry.App;

/// <summary>Composition root: builds the services by hand (no DI container), then the shell view model and the window.</summary>
public partial class App : Application
{
    // How often the UI samples the latest packet (~20 Hz); see DispatcherTimerTicker for why sampling.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(50);

    // Created first in OnStartup so that even a composition failure is logged; flushed and closed in OnExit.
    private AppLogging? _logging;
    private ILogger<App> _logger = NullLogger<App>.Instance;

    private TelemetryService? _telemetryService;
    private DispatcherTimerTicker? _ticker;
    private Action? _refresh;

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
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
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
            MessageBox.Show(ex.Message, "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ComposeAndShowMainWindow()
    {
        // The factory (not an instance) lets Stop/Start rebuild the UDP listener and rebind the port.
        // The same ILoggerFactory feeds both the service and every listener the factory builds, so bind, first
        // datagram per sender and discarded-datagram messages land in the same file as the app's own events.
        var loggerFactory = _logging!.Factory;
        var listenerLogger = loggerFactory.CreateLogger<UdpTelemetryListener>();
        _telemetryService = new TelemetryService(
            () => new UdpTelemetryListener(UdpTelemetryListener.DefaultPort, logger: listenerLogger),
            loggerFactory.CreateLogger<TelemetryService>());
        _ticker = new DispatcherTimerTicker();

        // The view model reads the adapters once (and logs what it read, now and on every later Start), so this
        // root does not enumerate them a second time.
        var viewModel = new ShellViewModel(
            _telemetryService,
            TimeProvider.System,
            new NetworkInfo(),
            loggerFactory.CreateLogger<ShellViewModel>(),
            new WpfClipboardService());

        // The ticker runs on the UI thread and calls Refresh, which pulls the latest packet into the bound properties.
        _refresh = viewModel.Refresh;
        _ticker.Start(RefreshInterval, _refresh);

        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
        _mainWindowShown = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ticker?.Stop();

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

        // Last log call, then flush and close the file so the final lines are not lost with the process.
        AppLog.Exiting(_logger);
        _logging?.Dispose();

        base.OnExit(e);
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
