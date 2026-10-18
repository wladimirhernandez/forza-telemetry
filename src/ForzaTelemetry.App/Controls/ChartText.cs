using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ForzaTelemetry.App.Controls;

/// <summary>
/// A small cache of <see cref="FormattedText"/> for one control.
/// </summary>
/// <remarks>
/// Building a <see cref="FormattedText"/> runs the text shaper, which is the most expensive thing an axis label does.
/// Axis labels repeat from frame to frame ("0", "30", "60"...), so remembering them by (text, size, weight, font, brush)
/// removes nearly all text allocations from steady-state rendering. The cache belongs to a control instance because the
/// result depends on the monitor DPI (<c>pixelsPerDip</c>); it empties itself when the DPI changes or grows too large
/// (a hover readout of ever-changing numbers must not leak).
/// </remarks>
internal sealed class ChartText
{
    private const int MaxEntries = 400;

    private readonly Dictionary<(string Text, double Size, bool Numeral, bool Bold, Brush Brush), FormattedText> _cache = [];
    private double _dpi;

    /// <summary>Returns (and remembers) the formatted text.</summary>
    public FormattedText Get(string text, double size, Brush brush, double pixelsPerDip, bool numeral = false, bool bold = false)
    {
        if (Math.Abs(pixelsPerDip - _dpi) > 0.001 || _cache.Count > MaxEntries)
        {
            _cache.Clear();
            _dpi = pixelsPerDip;
        }

        var key = (text, size, numeral, bold, brush);
        if (!_cache.TryGetValue(key, out var formatted))
        {
            var family = numeral ? ChartPalette.NumeralFont : ChartPalette.LabelFont;
            var typeface = new Typeface(family, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
            formatted = new FormattedText(
                text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, pixelsPerDip);
            _cache[key] = formatted;
        }

        return formatted;
    }

    /// <summary>Draws text with its top-left corner at <paramref name="x"/>, <paramref name="y"/>.</summary>
    public void Draw(DrawingContext dc, string text, double x, double y, double size, Brush brush, double pixelsPerDip, bool numeral = false, bool bold = false) =>
        dc.DrawText(Get(text, size, brush, pixelsPerDip, numeral, bold), new Point(x, y));

    /// <summary>Draws text whose RIGHT edge is at <paramref name="right"/>.</summary>
    public void DrawRight(DrawingContext dc, string text, double right, double y, double size, Brush brush, double pixelsPerDip, bool numeral = false, bool bold = false)
    {
        var formatted = Get(text, size, brush, pixelsPerDip, numeral, bold);
        dc.DrawText(formatted, new Point(right - formatted.Width, y));
    }

    /// <summary>Draws text horizontally centered on <paramref name="centerX"/>.</summary>
    public void DrawCentered(DrawingContext dc, string text, double centerX, double y, double size, Brush brush, double pixelsPerDip, bool numeral = false, bool bold = false)
    {
        var formatted = Get(text, size, brush, pixelsPerDip, numeral, bold);
        dc.DrawText(formatted, new Point(centerX - formatted.Width / 2, y));
    }
}
