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
        _telemetryService?.StopAsync().GetAwaiter().GetResult();

        base.OnExit(e);
    }
}
