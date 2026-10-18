using System.Runtime.InteropServices;
using System.Windows;

namespace ForzaTelemetry.App.Services;

/// <summary>Real <see cref="IClipboardService"/> backed by WPF's <see cref="Clipboard"/>.</summary>
public sealed class WpfClipboardService : IClipboardService
{
    public bool TrySetText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (COMException)
        {
            // CLIPBRD_E_CANT_OPEN: another process holds the clipboard open for a moment. A missed copy is not worth an
            // error dialog; the user can simply click again.
            return false;
        }
    }
}
