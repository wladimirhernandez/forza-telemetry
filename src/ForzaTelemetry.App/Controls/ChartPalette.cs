using System.Windows;
using System.Windows.Media;

namespace ForzaTelemetry.App.Controls;

/// <summary>
/// The design-system colors, fonts and ready-made frozen brushes/pens the chart controls draw with.
/// </summary>
/// <remarks>
/// <b>Why not DependencyProperty brushes like the older controls?</b> A chart needs a dozen brushes and pens (one per
/// lane, plus grid, label and glow colors); exposing each as a property that every page would have to style is noise.
/// Instead the palette reads the SAME color tokens declared in <c>App.xaml</c> (<c>ColdColor</c>, <c>ThrottleColor</c>...)
/// the first time it is used, so the tokens stay the single source of truth, and falls back to the same hex values when
/// no <see cref="Application"/> exists (unit tests, an offscreen render harness). The tokens never change at runtime, so
/// reading them once is enough. Brushes and pens are frozen: immutable, shareable, and WPF skips change tracking for them.
/// </remarks>
public static class ChartPalette
{
    public static readonly Color Canvas = Token("CanvasColor", Color.FromRgb(0x08, 0x09, 0x0B));
    public static readonly Color Card = Token("CardColor", Color.FromRgb(0x16, 0x18, 0x1C));
    public static readonly Color CardRaised = Token("CardRaisedColor", Color.FromRgb(0x1E, 0x21, 0x26));
    public static readonly Color Hairline = Token("HairlineColor", Color.FromRgb(0x2A, 0x2E, 0x34));
    public static readonly Color Text = Token("TextColor", Color.FromRgb(0xE8, 0xEA, 0xED));
    public static readonly Color Muted = Token("MutedColor", Color.FromRgb(0x8A, 0x90, 0x99));
    public static readonly Color Throttle = Token("ThrottleColor", Color.FromRgb(0x3D, 0xDC, 0x84));
    public static readonly Color Brake = Token("BrakeColor", Color.FromRgb(0xFF, 0x4D, 0x4F));
    public static readonly Color Cold = Token("ColdColor", Color.FromRgb(0x3A, 0xA0, 0xFF));
    public static readonly Color Warm = Token("WarmColor", Color.FromRgb(0xFF, 0xB0, 0x20));

    /// <summary>A soft neutral for secondary traces (RPM): readable next to the white speed line without adding a hue.</summary>
    public static readonly Color Neutral = Color.FromRgb(0xB8, 0xBE, 0xC7);

    public static readonly Brush CardBrush = Solid(Card);
    public static readonly Brush TextBrush = Solid(Text);
    public static readonly Brush MutedBrush = Solid(Muted);
    public static readonly Brush HairlineBrush = Solid(Hairline);
    public static readonly Brush CardRaisedBrush = Solid(CardRaised);
    public static readonly Brush ThrottleBrush = Solid(Throttle);
    public static readonly Brush BrakeBrush = Solid(Brake);
    public static readonly Brush ColdBrush = Solid(Cold);
    public static readonly Brush WarmBrush = Solid(Warm);
    public static readonly Brush NeutralBrush = Solid(Neutral);

    /// <summary>Numerals: thin Bahnschrift, same family as the dashboard's big readouts.</summary>
    public static readonly FontFamily NumeralFont = Font("NumeralFont", "Bahnschrift Light, Bahnschrift");

    /// <summary>Labels: Segoe UI Variable, same as the rest of the UI.</summary>
    public static readonly FontFamily LabelFont = Font("LabelFont", "Segoe UI Variable Text, Segoe UI");

    /// <summary>A frozen solid brush of <paramref name="color"/> at <paramref name="opacity"/> (0..1).</summary>
    public static SolidColorBrush Solid(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    /// <summary>A frozen pen with round joins and caps, which makes polylines look smooth instead of spiky.</summary>
    public static Pen Pen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    /// <summary>Same color with a different alpha (0..1).</summary>
    public static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

    private static Color Token(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) is Color color ? color : fallback;

    private static FontFamily Font(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily(fallback);
}
