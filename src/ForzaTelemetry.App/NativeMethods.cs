using System.Runtime.InteropServices;

namespace ForzaTelemetry.App;

/// <summary>
/// The few user32 calls the borderless window needs (monitor work areas and the maximize rectangle).
/// WPF has no managed API for "the work area of THIS monitor": <c>SystemParameters.WorkArea</c> only describes the
/// primary one. All values here are in physical pixels; the caller converts to WPF's device-independent units.
/// </summary>
internal static class NativeMethods
{
    /// <summary>Window message Windows sends right before it maximizes/tracks a window, asking for its limits.</summary>
    public const int WmGetMinMaxInfo = 0x0024;

    // Flag for the Monitor* lookups: if the point/window is on no monitor, answer with the closest one.
    private const int MonitorDefaultToNearest = 2;

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
    private static extern bool GetCursorPos(out PixelPoint point);
}
