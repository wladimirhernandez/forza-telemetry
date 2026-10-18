using System.Windows;
using System.Windows.Input;

namespace ForzaTelemetry.App;

/// <summary>Borderless dashboard window: custom title bar (drag, minimize, maximize, close) around the layout.</summary>
public partial class MainWindow : Window
{
    // The transparent margin that holds the drop shadow; removed while maximized so the window fills the screen.
    private static readonly Thickness NormalChromeMargin = new(12);

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        FitToWorkArea();
        DataContext = viewModel;
        StateChanged += OnStateChanged;
    }

    /// <summary>
    /// Keeps the default size on screen: on a small or high-DPI display 1440x900 DIPs can be taller than the usable
    /// area (the title bar would end up off-screen), so the window is capped to the work area (screen minus taskbar).
    /// </summary>
    private void FitToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
    }

    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        // Double-click on the title bar toggles maximize, like a native caption; a single press starts a drag.
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Square, shadowless and margin-free while maximized; rounded with shadow otherwise.</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        Chrome.Margin = maximized ? new Thickness(0) : NormalChromeMargin;
        Chrome.CornerRadius = maximized ? new CornerRadius(0) : (CornerRadius)FindResource("WindowRadius");
    }
}
