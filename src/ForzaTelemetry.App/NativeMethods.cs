using System.Runtime.InteropServices;

namespace ForzaTelemetry.App;

/// <summary>
/// The few user32 calls the borderless windows need (monitor work areas, the maximize rectangle, and the mini window's pixel position).
/// WPF has no managed API for "the work area of THIS monitor": <c>SystemParameters.WorkArea</c> only describes the
/// primary one. All values here are in physical pixels; the caller converts to WPF's device-independent units.
/// </summary>
internal static class NativeMethods
{
    /// <summary>Window message Windows sends right before it maximizes/tracks a window, asking for its limits.</summary>
    public const int WmGetMinMaxInfo = 0x0024;

    // Flag for the Monitor* lookups: if the point/window is on no monitor, answer with the closest one.
    private const int MonitorDefaultToNearest = 2;

    // MONITORINFO.dwFlags bit: this is the primary monitor.
    private const int MonitorInfoFlagPrimary = 1;

    // SetWindowPos flags: keep the z-order, do not activate.
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    /// <summary>A pixel rectangle (Win32 RECT: edges, not width/height).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PixelRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size; // must be set to sizeof(MonitorInfo) before the call
        public PixelRect Monitor; // the whole monitor
        public PixelRect Work; // the monitor minus the taskbar and other docked bars
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PixelPoint Reserved;
        public PixelPoint MaxSize;
        public PixelPoint MaxPosition;
        public PixelPoint MinTrackSize;
        public PixelPoint MaxTrackSize;
    }

    /// <summary>Work area of the monitor that holds (most of) the window; falls back to the nearest monitor.</summary>
    public static bool TryGetWorkArea(IntPtr window, out PixelRect workArea)
    {
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        return TryGetWorkAreaOfMonitor(monitor, out workArea);
    }

    /// <summary>Work area of the monitor under the mouse cursor (where the user is looking when the app starts).</summary>
    public static bool TryGetWorkAreaAtCursor(out PixelRect workArea)
    {
        if (!GetCursorPos(out var cursor))
        {
            workArea = default;
            return false;
        }

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        return TryGetWorkAreaOfMonitor(monitor, out workArea);
    }

    /// <summary>
    /// Answers WM_GETMINMAXINFO for a borderless window: when maximized it must fill only the work area of ITS
    /// monitor (not cover the taskbar). <paramref name="minTrackWidth"/>/<paramref name="minTrackHeight"/> keep the
    /// window's MinWidth/MinHeight enforced, because handling the message ourselves replaces WPF's own answer.
    /// </summary>
    public static void FillMinMaxInfo(IntPtr window, IntPtr lParam, int minTrackWidth, int minTrackHeight)
    {
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        // The maximize position is relative to the top-left of the monitor, not of the virtual desktop.
        minMax.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        minMax.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        minMax.MaxSize.X = info.Work.Width;
        minMax.MaxSize.Y = info.Work.Height;

        // Windows clamps MaxSize to MaxTrackSize; the default track size comes from the PRIMARY monitor, so a bigger
        // secondary monitor would be clamped without this.
        minMax.MaxTrackSize.X = Math.Max(minMax.MaxTrackSize.X, info.Work.Width);
        minMax.MaxTrackSize.Y = Math.Max(minMax.MaxTrackSize.Y, info.Work.Height);

        minMax.MinTrackSize.X = minTrackWidth;
        minMax.MinTrackSize.Y = minTrackHeight;

        Marshal.StructureToPtr(minMax, lParam, fDeleteOld: false);
    }

    /// <summary>
    /// The work area of EVERY monitor, and which one is the primary. The mini window needs the whole desk, not one screen, to decide whether
    /// a remembered position is still on a screen that exists (see <see cref="MiniModeGeometry"/>).
    /// </summary>
    public static IReadOnlyList<(PixelRect WorkArea, bool IsPrimary)> GetMonitorWorkAreas()
    {
        var monitors = new List<(PixelRect, bool)>();

        // The callback runs synchronously inside EnumDisplayMonitors, so capturing the list is safe and nothing outlives the call.
        bool Collect(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                monitors.Add((info.Work, (info.Flags & MonitorInfoFlagPrimary) != 0));
            }

            return true; // keep enumerating
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
        return monitors;
    }

    /// <summary>The window's outer rectangle in physical pixels (shadow margin included).</summary>
    public static bool TryGetWindowRect(IntPtr window, out PixelRect rect) => GetWindowRect(window, out rect);

    /// <summary>
    /// Moves and resizes a window in physical pixels, without activating it or touching its z-order (the mini window's Topmost flag owns that).
    /// Pixels, not WPF's Left/Top: with per-monitor DPI the same number of DIPs is a different place on screens of different scaling, so a
    /// position saved in DIPs would drift every time the window moved between them.
    /// </summary>
    public static bool TrySetWindowBounds(IntPtr window, int left, int top, int width, int height) =>
        SetWindowPos(window, IntPtr.Zero, left, top, width, height, SwpNoZOrder | SwpNoActivate);

    private static bool TryGetWorkAreaOfMonitor(IntPtr monitor, out PixelRect workArea)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            workArea = info.Work;
            return true;
        }

        workArea = default;
        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PixelPoint point, int flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out PixelRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PixelPoint point);
}
