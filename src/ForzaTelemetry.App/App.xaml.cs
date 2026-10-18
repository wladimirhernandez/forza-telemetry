using System.Windows;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Udp;

namespace ForzaTelemetry.App;

/// <summary>Composition root: builds the services by hand (no DI container), then the view model and the window.</summary>
public partial class App : Application
{
    // How often the UI samples the latest packet (~20 Hz); see DispatcherTimerTicker for why sampling.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(50);

    private TelemetryService? _telemetryService;
    private DispatcherTimerTicker? _ticker;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        RegisterGlobalExceptionHandlers();

        // The factory (not an instance) lets Stop/Start rebuild the UDP listener and rebind the port.
        _telemetryService = new TelemetryService(() => new UdpTelemetryListener(UdpTelemetryListener.DefaultPort));
        _ticker = new DispatcherTimerTicker();

        var viewModel = new MainViewModel(_telemetryService, TimeProvider.System);

        // The ticker runs on the UI thread and calls Refresh, which pulls the latest packet into the bound properties.
        _ticker.Start(RefreshInterval, viewModel.Refresh);

        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
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
        catch (Exception)
        {
            // Nothing useful to do while exiting; logging arrives in T4.
        }

        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        // An exception thrown on the UI thread (e.g. in a bound property or Refresh) would otherwise tear down
        // the whole app. Show it and keep running: the dashboard is a live monitor, and losing the window over
        // one bad tick is worse than a message. Logging arrives in T4.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // A faulted Task nobody awaited: mark it observed so it is not silently dropped/escalated.
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
    }
}
