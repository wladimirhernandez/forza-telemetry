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
    private const uint SwpNoSize = 0x0001;

    // GetWindowPlacement showCmd of a window that is neither minimized nor maximized.
    private const int ShowNormal = 1;

    // GetDpiForMonitor: the scaling the user chose for that monitor.
    private const int EffectiveDpi = 0;

    // Window style bits and system commands for the "no minimize / maximize" rule.
    private const int GwlStyle = -16;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;
    private const int ScMaximize = 0xF030;

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

    // Win32 WINDOWPLACEMENT (the classic 44-byte layout every Windows version accepts).
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public PixelPoint MinPosition;
        public PixelPoint MaxPosition;
        public PixelRect Normal;
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

    /// <summary>One monitor: its full bounds (what identifies a desk layout), its work area (where a window may sit) and whether it is the primary.</summary>
    internal readonly record struct MonitorBounds(PixelRect Bounds, PixelRect WorkArea, bool IsPrimary);

    /// <summary>
    /// EVERY monitor. The mini window needs the whole desk, not one screen, to decide whether a remembered position is still on a screen
    /// that exists (see <see cref="MiniModeGeometry"/>).
    /// </summary>
    public static IReadOnlyList<MonitorBounds> GetMonitors()
    {
        var monitors = new List<MonitorBounds>();

        // The callback runs synchronously inside EnumDisplayMonitors, so capturing the list is safe and nothing outlives the call.
        bool Collect(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                monitors.Add(new MonitorBounds(info.Monitor, info.Work, (info.Flags & MonitorInfoFlagPrimary) != 0));
            }

            return true; // keep enumerating
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
        return monitors;
    }

    /// <summary>
    /// The window's NORMAL rectangle in screen pixels: where it sits when it is neither minimized nor maximized. A minimized window's
    /// GetWindowRect is a parked icon position (-32000) and a maximized one's is the whole work area, and neither is somewhere to put the
    /// window back. For a normal window the live rectangle is exact; otherwise the saved normal rectangle is used, converted from the
    /// "workspace" coordinates GetWindowPlacement reports (relative to the work area) to screen coordinates.
    /// </summary>
    public static bool TryGetNormalBounds(IntPtr window, out PixelRect rect)
    {
        rect = default;
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(window, ref placement))
        {
            return false;
        }

        if (placement.ShowCmd == ShowNormal && GetWindowRect(window, out rect))
        {
            return true;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var dx = monitorInfo.Work.Left - monitorInfo.Monitor.Left;
        var dy = monitorInfo.Work.Top - monitorInfo.Monitor.Top;
        rect = new PixelRect
        {
            Left = placement.Normal.Left + dx,
            Top = placement.Normal.Top + dy,
            Right = placement.Normal.Right + dx,
            Bottom = placement.Normal.Bottom + dy
        };
        return true;
    }

    /// <summary>The DPI (96 = 100%) of the monitor that holds the pixel point, falling back to the nearest monitor; 96 when it cannot be read.</summary>
    public static double GetDpiAtPoint(int x, int y)
    {
        var monitor = MonitorFromPoint(new PixelPoint { X = x, Y = y }, MonitorDefaultToNearest);
        return monitor != IntPtr.Zero && GetDpiForMonitor(monitor, EffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX : 96;
    }

    /// <summary>The DPI the window is currently scaled for (it follows the monitor it is on); 96 when it cannot be read.</summary>
    public static double GetWindowDpi(IntPtr window)
    {
        var dpi = GetDpiForWindow(window);
        return dpi > 0 ? dpi : 96;
    }

    /// <summary>
    /// Takes the minimize and maximize boxes (and so Win+Down, Win+Up and edge snapping) away from a window. A HUD that can be minimized
    /// or maximized is a window the user can lose, and the style bits are what Windows consults before it honors those gestures.
    /// </summary>
    public static void RemoveMinMaxBoxes(IntPtr window)
    {
        var style = GetWindowLong(window, GwlStyle);
        SetWindowLong(window, GwlStyle, style & ~(WsMinimizeBox | WsMaximizeBox));
    }

    /// <summary>True for a system command that would minimize or maximize (or snap) the window: the keyboard and title-bar routes the style bits do not cover.</summary>
    public static bool IsMinMaxSystemCommand(int message, IntPtr wParam)
    {
        if (message != WmSysCommand)
        {
            return false;
        }

        // The low four bits of the command are used by Windows itself and must be masked off before comparing.
        var command = (int)(wParam.ToInt64() & 0xFFF0);
        return command is ScMinimize or ScMaximize;
    }

    /// <summary>The window's outer rectangle in physical pixels (shadow margin included).</summary>
    public static bool TryGetWindowRect(IntPtr window, out PixelRect rect) => GetWindowRect(window, out rect);

    /// <summary>
    /// Moves a window to a pixel position, keeping its size and without activating it or touching its z-order (the mini window's Topmost flag
    /// owns that). Moving it FIRST is what lets Windows tell WPF which monitor (and DPI) it now lives on, before the size is applied.
    /// </summary>
    public static bool TrySetWindowPosition(IntPtr window, int left, int top) =>
        SetWindowPos(window, IntPtr.Zero, left, top, 0, 0, SwpNoZOrder | SwpNoActivate | SwpNoSize);

    /// <summary>
    /// Moves and resizes a window in physical pixels, without activating it or touching its z-order. Pixels, not WPF's Left/Top: with
    /// per-monitor DPI the same number of DIPs is a different place on screens of different scaling.
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
    private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

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
