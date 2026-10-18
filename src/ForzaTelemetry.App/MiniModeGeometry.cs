using System.Globalization;
using System.Windows;

namespace ForzaTelemetry.App;

/// <summary>
/// Pure placement math for the mini-mode window. Like <see cref="WindowGeometry"/> it knows nothing about windows or Win32: the caller
/// feeds it the monitors' work areas and the remembered position, and applies the rectangle that comes back. All values are in ONE unit
/// (the app passes physical pixels, which is what the monitors report and what survives a change of display scaling), so the math
/// never converts.
/// </summary>
public static class MiniModeGeometry
{
    /// <summary>
    /// A remembered position is kept only while at least this share of the window is still on a screen. Below it the monitor it was on is
    /// gone (undocked laptop, switched-off display) or the window was left half off the edge, and a window the user cannot reach is worse
    /// than one that jumped to the middle of the main screen.
    /// </summary>
    public const double MinVisibleShare = 0.5;

    /// <summary>
    /// A stable name for an arrangement of monitors: their full bounds (not their work areas), ordered left to right then top to bottom,
    /// as text. The same desk always gives the same text, and any change (a monitor added, moved or switched to another resolution) gives a
    /// different one. Bounds, because the work area also shrinks and moves when the taskbar is resized, hidden or docked elsewhere, and that
    /// is not a different desk: it must not forget where the window was.
    /// </summary>
    public static string SetupKey(IEnumerable<Rect> monitorBounds) =>
        string.Join(
            ';',
            monitorBounds
                .OrderBy(area => area.Left)
                .ThenBy(area => area.Top)
                .Select(area => string.Create(
                    CultureInfo.InvariantCulture, $"{Math.Round(area.Left)},{Math.Round(area.Top)},{Math.Round(area.Width)}x{Math.Round(area.Height)}")));

    /// <summary>
    /// Where the mini window goes. With no usable remembered position it is centered on the primary monitor at <paramref name="defaultSize"/>.
    /// A remembered one is honored (size clamped to the limits and to the screen it is on) as long as enough of it is visible; then it is
    /// nudged fully inside the screen it mostly sits on, so a window saved half off the edge comes back reachable. Otherwise the saved size
    /// is kept but the window is centered on the primary monitor.
    /// </summary>
    /// <param name="saved">The position saved for the CURRENT monitor arrangement, or null.</param>
    /// <param name="workAreas">The work area (monitor minus taskbar) of every monitor.</param>
    /// <param name="primaryIndex">Index in <paramref name="workAreas"/> of the primary monitor; out of range falls back to the first.</param>
    /// <param name="defaultSize">Size used when nothing is remembered.</param>
    /// <param name="minimumSize">Smallest allowed size.</param>
    /// <param name="maximumSize">Largest allowed size.</param>
    public static Rect Place(
        Rect? saved,
        IReadOnlyList<Rect> workAreas,
        int primaryIndex,
        Size defaultSize,
        Size minimumSize,
        Size maximumSize)
    {
        var size = saved is { } s && IsReal(s) ? ClampSize(s.Size, minimumSize, maximumSize) : ClampSize(defaultSize, minimumSize, maximumSize);

        if (workAreas.Count == 0)
        {
            return new Rect(new Point(0, 0), size); // no monitor information at all: the OS will place it
        }

        var primary = workAreas[primaryIndex >= 0 && primaryIndex < workAreas.Count ? primaryIndex : 0];

        if (saved is not { } remembered || !IsReal(remembered))
        {
            return CenteredIn(primary, size);
        }

        // The screen that holds most of the remembered window.
        var best = workAreas.MaxBy(area => Overlap(new Rect(remembered.Location, size), area));
        var windowArea = size.Width * size.Height;
        if (Overlap(new Rect(remembered.Location, size), best) < windowArea * MinVisibleShare)
        {
            return CenteredIn(primary, size);
        }

        // The size can never exceed the screen it sits on (a window bigger than the work area has no valid position).
        size = new Size(Math.Min(size.Width, best.Width), Math.Min(size.Height, best.Height));
        var left = Math.Clamp(remembered.Left, best.Left, best.Right - size.Width);
        var top = Math.Clamp(remembered.Top, best.Top, best.Bottom - size.Height);
        return new Rect(left, top, size.Width, size.Height);
    }

    /// <summary>Clamps an opacity into the range the mini window allows; NaN (a damaged value) becomes fully opaque.</summary>
    public static double ClampOpacity(double opacity) => Services.AppSettings.ClampMiniOpacity(opacity);

    /// <summary>Device-independent units to pixels at <paramref name="dpi"/> (96 = 100%).</summary>
    public static double ToPixels(double dips, double dpi) => dips * dpi / 96.0;

    /// <summary>Pixels to device-independent units at <paramref name="dpi"/>.</summary>
    public static double ToDips(double pixels, double dpi) => pixels * 96.0 / dpi;

    private static bool IsReal(Rect rect) =>
        !rect.IsEmpty && double.IsFinite(rect.Left) && double.IsFinite(rect.Top) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height)
        && rect.Width > 0 && rect.Height > 0;

    private static Size ClampSize(Size size, Size minimum, Size maximum) =>
        new(Math.Clamp(size.Width, minimum.Width, maximum.Width), Math.Clamp(size.Height, minimum.Height, maximum.Height));

    private static Rect CenteredIn(Rect area, Size size)
    {
        // A window larger than the screen is shrunk first, so centering cannot push its corner off the top-left.
        var fitted = WindowGeometry.FitSize(size, area);
        var topLeft = WindowGeometry.CenterIn(area, fitted);
        return new Rect(topLeft, fitted);
    }

    private static double Overlap(Rect a, Rect b)
    {
        var intersection = Rect.Intersect(a, b);
        return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
    }
}
