using System.Windows.Media;

namespace ForzaTelemetry.App.Controls;

/// <summary>Small drawing helpers shared by the custom controls.</summary>
internal static class RenderHelpers
{
    /// <summary>
    /// Builds a pen once and freezes it when possible. A frozen Freezable is immutable, so WPF can share it across
    /// threads and skips change tracking: the render thread draws it with no per-frame bookkeeping. A pen can only be
    /// frozen if its brush is frozen too, which the shared token brushes in App.xaml are (po:Freeze="True").
    /// </summary>
    public static Pen? CreatePen(Brush? brush, double thickness)
    {
        if (brush is null)
        {
            return null;
        }

        var pen = new Pen(brush, thickness);
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }
}
