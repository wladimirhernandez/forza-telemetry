using System.Windows;

namespace ForzaTelemetry.App;

/// <summary>
/// Pure window-placement math in WPF device-independent units. Kept free of any window or P/Invoke so it can be
/// unit-tested; <see cref="MainWindow"/> only feeds it numbers and applies the result.
/// </summary>
public static class WindowGeometry
{
    /// <summary>Caps a desired size to the work area, so the title bar can never end up off-screen.</summary>
    public static Size FitSize(Size desired, Rect workArea) =>
        new(Math.Min(desired.Width, workArea.Width), Math.Min(desired.Height, workArea.Height));

    /// <summary>Top-left corner that centers a window of <paramref name="size"/> in the work area.</summary>
    public static Point CenterIn(Rect workArea, Size size) =>
        new(workArea.Left + (workArea.Width - size.Width) / 2, workArea.Top + (workArea.Height - size.Height) / 2);

    /// <summary>
    /// Where the restored window goes when the user drags a maximized window by its title bar, so it "comes off"
    /// under the cursor like a native window. Horizontally the cursor keeps its PROPORTIONAL position (grabbing the
    /// middle of the maximized bar grabs the middle of the restored one). Vertically the title bar stays where it was
    /// (at the top of the work area), so the cursor is still on it: the window starts <paramref name="chromeMargin"/>
    /// above the work area because that margin is transparent. The horizontal result is clamped to the work area.
    /// </summary>
    /// <param name="cursor">Cursor position, in screen DIPs.</param>
    /// <param name="workArea">The work area the maximized window fills, in screen DIPs.</param>
    /// <param name="restoredSize">Size the window returns to (its RestoreBounds).</param>
    /// <param name="chromeMargin">Transparent shadow margin around the restored window; the title bar starts below it.</param>
    public static Point RestoreUnderCursor(Point cursor, Rect workArea, Size restoredSize, double chromeMargin)
    {
        var proportion = workArea.Width > 0 ? (cursor.X - workArea.Left) / workArea.Width : 0.5;
        var left = cursor.X - (proportion * restoredSize.Width);
        var top = workArea.Top - chromeMargin;

        var maxLeft = Math.Max(workArea.Left, workArea.Right - restoredSize.Width);
        return new Point(Math.Clamp(left, workArea.Left, maxLeft), top);
    }
}
