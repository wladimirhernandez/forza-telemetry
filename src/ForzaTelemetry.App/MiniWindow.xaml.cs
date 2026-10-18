using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ForzaTelemetry.App;

/// <summary>
/// The compact always-on-top window (see <see cref="MiniModeViewModel"/>). Code-behind does only what XAML cannot: put the window where it was
/// last time (validated against the monitors that exist NOW, and sized in the scale of the monitor it lands on), refuse to be minimized or
/// maximized, start a drag from anywhere on the card, and save its normal position when it closes.
/// </summary>
public partial class MiniWindow : Window
{
    // Default size, in DIPs; the limits are the ones the XAML declares (MinWidth and friends), also in DIPs.
    private static readonly Size DefaultSize = new(360, 140);

    private readonly MiniModeViewModel _viewModel;

    public MiniWindow(MiniModeViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>
    /// The earliest moment the window has a handle but is not yet visible, so it never flashes at the wrong place.
    /// <para>
    /// <b>Position first, size second.</b> A window's scale follows the monitor it is on, and WPF re-scales the window when Windows says the
    /// monitor changed. Applying a pixel size before the window has moved would be undone by that re-scale, and the size would drift every
    /// time the window reopened on a monitor with a different scaling. So the window is MOVED to the remembered spot first (Windows then
    /// reports the new scale), and only then given its size, computed from the saved device-independent size in the scale it actually has.
    /// </para>
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;

        // A HUD you can minimize or maximize (Win+Down, Win+Up, snapping) is one you can lose; see WndProc for the commands that remain.
        NativeMethods.RemoveMinMaxBoxes(handle);
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);

        var (setup, workAreas, primaryIndex) = ReadMonitors();
        var saved = _viewModel.SavedPlacement(setup);

        // The scale the window will have: that of the monitor the remembered spot is on (the primary one when nothing is remembered).
        var anchor = saved is { } s ? new Point(s.Left, s.Top) : workAreas.Count > 0 ? Center(workAreas[primaryIndex]) : new Point(0, 0);
        var dpi = NativeMethods.GetDpiAtPoint((int)Math.Round(anchor.X), (int)Math.Round(anchor.Y));

        var bounds = Plan(saved, workAreas, primaryIndex, dpi);
        NativeMethods.TrySetWindowPosition(handle, (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top));

        // Now the window knows its real scale; if it differs from the one assumed, plan again with it before sizing.
        var actualDpi = NativeMethods.GetWindowDpi(handle);
        if (actualDpi != dpi)
        {
            bounds = Plan(saved, workAreas, primaryIndex, actualDpi);
        }

        NativeMethods.TrySetWindowBounds(
            handle,
            (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top), (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
    }

    // The saved size is in DIPs: turn it, the default and the limits into pixels at one scale, then let the pure geometry decide.
    private Rect Plan(Rect? saved, IReadOnlyList<Rect> workAreas, int primaryIndex, double dpi)
    {
        Size ToPixels(double width, double height) =>
            new(MiniModeGeometry.ToPixels(width, dpi), MiniModeGeometry.ToPixels(height, dpi));

        Rect? savedPixels = saved is { } s
            ? new Rect(s.Left, s.Top, MiniModeGeometry.ToPixels(s.Width, dpi), MiniModeGeometry.ToPixels(s.Height, dpi))
            : null;

        return MiniModeGeometry.Place(
            savedPixels,
            workAreas,
            primaryIndex,
            ToPixels(DefaultSize.Width, DefaultSize.Height),
            ToPixels(MinWidth, MinHeight),
            ToPixels(MaxWidth, MaxHeight));
    }

    /// <summary>
    /// The system commands the style bits do not cover (the keyboard and the window menu) would still minimize or maximize the window; they
    /// are swallowed here. Combined with the removed boxes, the window has exactly one state, normal.
    /// </summary>
    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (NativeMethods.IsMinMaxSystemCommand(msg, wParam))
        {
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Saves the NORMAL position (never a minimized or maximized one, see <see cref="NativeMethods.TryGetNormalBounds"/>) and the opacity. The
    /// size is stored in DIPs at the scale the window has now. The monitor arrangement is read again here, not reused from startup: if a
    /// screen was plugged or removed while the window was open, the position belongs to the arrangement that exists now.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        var handle = new WindowInteropHelper(this).Handle;
        if (NativeMethods.TryGetNormalBounds(handle, out var rect))
        {
            var dpi = NativeMethods.GetWindowDpi(handle);
            var (setup, _, _) = ReadMonitors();
            _viewModel.SaveWindowState(
                setup,
                new Rect(rect.Left, rect.Top, MiniModeGeometry.ToDips(rect.Width, dpi), MiniModeGeometry.ToDips(rect.Height, dpi)));
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

    private static Point Center(Rect area) => new(area.Left + area.Width / 2, area.Top + area.Height / 2);

    // The desk as the geometry wants it: the setup key from the monitors' full bounds, and the work areas (where a window may sit).
    private static (string Setup, List<Rect> WorkAreas, int PrimaryIndex) ReadMonitors()
    {
        var bounds = new List<Rect>();
        var workAreas = new List<Rect>();
        var primary = 0;
        foreach (var monitor in NativeMethods.GetMonitors())
        {
            if (monitor.IsPrimary)
            {
                primary = workAreas.Count;
            }

            bounds.Add(new Rect(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height));
            workAreas.Add(new Rect(monitor.WorkArea.Left, monitor.WorkArea.Top, monitor.WorkArea.Width, monitor.WorkArea.Height));
        }

        return (MiniModeGeometry.SetupKey(bounds), workAreas, primary);
    }
}
