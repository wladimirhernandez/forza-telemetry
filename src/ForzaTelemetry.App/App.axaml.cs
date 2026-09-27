using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ForzaTelemetry.App.ViewModels;
using ForzaTelemetry.App.Views;

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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel()
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory = () => new MainView { DataContext = new MainViewModel() };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
