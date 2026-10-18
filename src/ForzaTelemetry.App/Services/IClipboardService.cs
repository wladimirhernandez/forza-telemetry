namespace ForzaTelemetry.App.Services;

/// <summary>
/// Puts text on the system clipboard. An interface so a view model can offer a "copy" command and a test can check
/// what was copied without touching the real clipboard (which needs a UI thread and is shared with the whole desktop).
/// </summary>
public interface IClipboardService
{
    /// <summary>Copies <paramref name="text"/>; false when the clipboard could not be opened (another process holds it).</summary>
    bool TrySetText(string text);
}
