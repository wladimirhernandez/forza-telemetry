using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ForzaTelemetry.App;

/// <summary>
/// The compact always-on-top window (see <see cref="MiniModeViewModel"/>). Code-behind does only what XAML cannot: put the window where it was
/// last time (in physical pixels, validated against the monitors that exist NOW), start a drag from anywhere on the card, and save its position
/// when it closes.
/// </summary>
public partial class MiniWindow : Window
{
    // Default size, and the same limits the XAML declares, in DIPs; converted to pixels where the geometry needs them.
    private static readonly Size DefaultSize = new(360, 140);

    private readonly MiniModeViewModel _viewModel;

    public MiniWindow(MiniModeViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>
    /// The earliest moment the window has a handle but is not yet visible, so it never flashes at the wrong place: the remembered position for
    /// THIS monitor arrangement is applied here, after checking that enough of it is still on a screen.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var (workAreas, primaryIndex) = ReadMonitors();
        var setup = MiniModeGeometry.SetupKey(workAreas);

        var dpi = VisualTreeHelper.GetDpi(this);
        Size ToPixels(double width, double height) => new(width * dpi.DpiScaleX, height * dpi.DpiScaleY);

        var bounds = MiniModeGeometry.Place(
            _viewModel.SavedPlacement(setup),
            workAreas,
            primaryIndex,
            ToPixels(DefaultSize.Width, DefaultSize.Height),
            ToPixels(MinWidth, MinHeight),
            ToPixels(MaxWidth, MaxHeight));

        NativeMethods.TrySetWindowBounds(
            new WindowInteropHelper(this).Handle,
            (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top), (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
    }

    /// <summary>
    /// Saves the position and the opacity. The monitor arrangement is read again here, not reused from startup: if a screen was plugged or
    /// removed while the window was open, the position belongs to the arrangement that exists now.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        if (NativeMethods.TryGetWindowRect(new WindowInteropHelper(this).Handle, out var rect))
        {
            var (workAreas, _) = ReadMonitors();
            _viewModel.SaveWindowState(
                MiniModeGeometry.SetupKey(workAreas),
                new Rect(rect.Left, rect.Top, rect.Width, rect.Height));
        }
    }

    /// <summary>
    /// Drag from anywhere on the card. A press on a button, the slider or the pin must keep its own meaning (the pin toggles, the slider
    /// moves), so those are skipped by looking at what was actually pressed rather than relying on them to mark the event handled.
    /// </summary>
    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || IsInteractive(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DragMove();
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        for (var node = source; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or RangeBase or Thumb)
            {
                return true;
            }
        }

        return false;
    }

    private static (List<Rect> WorkAreas, int PrimaryIndex) ReadMonitors()
    {
        var areas = new List<Rect>();
        var primary = 0;
        foreach (var (workArea, isPrimary) in NativeMethods.GetMonitorWorkAreas())
        {
            if (isPrimary)
            {
                primary = areas.Count;
            }

            areas.Add(new Rect(workArea.Left, workArea.Top, workArea.Width, workArea.Height));
        }

        return (areas, primary);
    }
}
