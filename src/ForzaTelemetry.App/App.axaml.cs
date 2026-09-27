using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.App.ViewModels;
using ForzaTelemetry.App.Views;
using ForzaTelemetry.Udp;

namespace ForzaTelemetry.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    // Avalonia's "lifetime" is how the OS hosts the app, and it is different per platform, so this
    // is where the same App/MainViewModel wiring branches into whichever shell the platform expects:
    //   - IClassicDesktopStyleApplicationLifetime: a classic desktop app (Windows/Linux/macOS) that
    //     owns an OS window. We create a MainWindow and hand it the MainViewModel as DataContext.
    //   - IActivityApplicationLifetime: Android hosts Avalonia inside an Android Activity, which can
    //     be recreated by the OS (rotation, low memory) independently of the app process, so it asks
    //     for a *factory* that can build a fresh MainView on demand instead of a single instance.
    //   - ISingleViewApplicationLifetime: the general "single view, no window chrome" case (iOS,
    //     Browser, and other single-view hosts) - one MainView fills the whole screen.
    // Only desktop and Android are wired up in this app (iOS/Browser heads were dropped).
    public override void OnFrameworkInitializationCompleted()
    {
        // ---- Composition root --------------------------------------------------------------
        // Every dependency below is constructed and wired together explicitly, by hand, right
        // here — this is "manual DI" (dependency injection without a DI container). For an app
        // this small, a container (e.g. Microsoft.Extensions.DependencyInjection) would add
        // ceremony — service registration, a ServiceProvider, resolving — without buying much;
        // introducing one later, once there are many more services, is a drop-in change that would
        // not require touching TelemetryService or MainViewModel themselves.
        //
        // The factory `() => new UdpTelemetryListener(...)` (not a shared UdpTelemetryListener
        // instance) is what lets TelemetryService build a BRAND NEW listener every time it starts —
        // see TelemetryService's remarks for why that matters (the socket is bound in the listener's
        // own constructor, so releasing port 5300 requires disposing the listener entirely).
        var telemetryService = new TelemetryService(() => new UdpTelemetryListener(UdpTelemetryListener.DefaultPort));
        var mainViewModel = new MainViewModel(telemetryService, TimeProvider.System);

        // The ticker calls Refresh() on a fixed schedule INSTEAD of MainViewModel reacting to every
        // packet — see TelemetryService's and DispatcherTimerTicker's remarks for the full
        // reasoning. ~20 Hz (a 50 ms interval) is smooth enough to look live and comfortably below
        // FH6's own 30-60 packets/sec.
        var uiTicker = new DispatcherTimerTicker();
        uiTicker.Start(TimeSpan.FromMilliseconds(50), mainViewModel.Refresh);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };

            // Stop the background receive loop (and release the UDP port) when the app closes.
            // Without this, the Task.Run loop inside TelemetryService would keep running — and the
            // socket would stay bound — even after the window is gone, until the process itself
            // exits. This is fire-and-forget deliberately: ShutdownRequested has no async overload,
            // and by this point nothing is left to await the result anyway.
            desktop.ShutdownRequested += (_, _) =>
            {
                uiTicker.Stop();
                _ = telemetryService.StopAsync();
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory = () => new MainView { DataContext = mainViewModel };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = mainViewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
