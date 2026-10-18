using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ForzaTelemetry.App;

/// <summary>Borderless dashboard window: custom title bar (drag, minimize, maximize, close) around the layout.</summary>
public partial class MainWindow : Window
{
    // Where the mouse went down on the title bar while maximized. The drag only turns into "restore and move" once the
    // mouse travels a few pixels, so a plain click on the title bar of a maximized window does not un-maximize it.
    private Point? _pendingRestoreDrag;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>Copies the best Data Out IP to the clipboard (one address, even when several are listed).</summary>
    private void OnCopyIpClick(object sender, RoutedEventArgs e)
    {
        if ((DataContext as MainViewModel)?.DataOutPrimaryAddress is not { } address)
        {
            return;
        }

        try
        {
            // SetText can fail with a COMException when another process holds the clipboard open; a missed copy is
            // not worth an error dialog, the user can simply click again.
            Clipboard.SetText(address);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }

    /// <summary>
    /// The earliest moment the window has a Win32 handle (HWND) but is not yet visible. Everything that needs the
    /// handle (monitor lookups, the message hook) starts here.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // WPF exposes the raw Win32 message stream through the HwndSource; a hook sees each message and can answer
        // it. We only answer WM_GETMINMAXINFO (see WndProc).
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);

        PlaceOnCursorMonitor();
    }

    /// <summary>
    /// Sizes and centers the window on the monitor under the mouse cursor. WPF's own CenterScreen always uses the
    /// primary monitor and <c>SystemParameters.WorkArea</c> only describes it too, so on a multi-monitor setup the
    /// window could open on a screen it was not sized for. On a small or high-DPI display 1440x900 DIPs can also be
    /// taller than the usable area (the title bar would end up off-screen), so the size is capped to the work area
    /// (monitor minus taskbar). Best effort: if the monitor lookup fails, the primary work area is used.
    /// </summary>
    private void PlaceOnCursorMonitor()
    {
        var workArea = NativeMethods.TryGetWorkAreaAtCursor(out var pixels)
            ? ToDips(pixels)
            : SystemParameters.WorkArea;

        var size = WindowGeometry.FitSize(new Size(Width, Height), workArea);
        Width = size.Width;
        Height = size.Height;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);

        var topLeft = WindowGeometry.CenterIn(workArea, size);
        Left = topLeft.X;
        Top = topLeft.Y;
    }

    /// <summary>
    /// Why a hook instead of setting MaxWidth/MaxHeight on state change: a borderless window maximizes to the WHOLE
    /// monitor (over the taskbar) because Windows asks the window "how big may you get?" with WM_GETMINMAXINFO and
    /// WPF answers with the monitor size. Answering that question ourselves, per monitor, is what the OS itself
    /// consults, so it also covers Win+Up, snapping, and maximizing on a different monitor with a different taskbar.
    /// MaxWidth/MaxHeight would be a fixed number that goes stale as soon as the window moves to another monitor, and
    /// it would also cap the NORMAL window size.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmGetMinMaxInfo)
        {
            // Replacing WPF's answer also drops its MinWidth/MinHeight limits, so hand them back (in pixels).
            var dpi = VisualTreeHelper.GetDpi(this);
            NativeMethods.FillMinMaxInfo(
                hwnd,
                lParam,
                (int)Math.Ceiling(MinWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(MinHeight * dpi.DpiScaleY));
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        // Double-click on the title bar toggles maximize, like a native caption.
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            // Wait for the mouse to actually move before restoring; capture keeps the move events coming to us even
            // if the pointer leaves the title bar quickly.
            _pendingRestoreDrag = e.GetPosition((IInputElement)sender);
            ((UIElement)sender).CaptureMouse();
            return;
        }

        DragMove();
    }

    private void OnDragAreaMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingRestoreDrag is not { } origin || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition((IInputElement)sender) - origin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // Releasing the capture also clears _pendingRestoreDrag (see OnDragAreaLostCapture).
        ((UIElement)sender).ReleaseMouseCapture();
        RestoreUnderCursorAndDrag(e);
    }

    private void OnDragAreaMouseUp(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    private void OnDragAreaLostCapture(object sender, MouseEventArgs e) => _pendingRestoreDrag = null;

    /// <summary>Un-maximizes so the window appears under the cursor (proportionally), then continues as a normal drag.</summary>
    private void RestoreUnderCursorAndDrag(MouseEventArgs e)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var cursorPixels = PointToScreen(e.GetPosition(this));
        var cursor = new Point(cursorPixels.X / dpi.DpiScaleX, cursorPixels.Y / dpi.DpiScaleY);
        var workArea = GetCurrentWorkArea();
        var restoredSize = RestoreBounds.Size; // the size before maximizing; only valid until the state changes

        WindowState = WindowState.Normal;

        var margin = ((Thickness)FindResource("ChromeMargin")).Top;
        var topLeft = WindowGeometry.RestoreUnderCursor(cursor, workArea, restoredSize, margin);
        Left = topLeft.X;
        Top = topLeft.Y;

        // The left button is still down, so the OS can take over moving the (now normal) window.
        DragMove();
    }

    private Rect GetCurrentWorkArea() =>
        NativeMethods.TryGetWorkArea(new WindowInteropHelper(this).Handle, out var pixels)
            ? ToDips(pixels)
            : SystemParameters.WorkArea;

    /// <summary>Win32 reports pixels; WPF positions in device-independent units (1/96 inch): divide by the DPI scale.</summary>
    private Rect ToDips(NativeMethods.PixelRect pixels)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(
            pixels.Left / dpi.DpiScaleX,
            pixels.Top / dpi.DpiScaleY,
            pixels.Width / dpi.DpiScaleX,
            pixels.Height / dpi.DpiScaleY);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
