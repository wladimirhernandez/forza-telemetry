namespace ForzaTelemetry.App.Services;

/// <summary>
/// Asks the user where to save a file. An interface so a view model can offer "export" without referencing any window (the WPF
/// implementation opens the standard Save dialog; a test answers with a path or with "cancelled" instantly).
/// </summary>
public interface IFileSaveDialog
{
    /// <summary>
    /// Shows the dialog and waits.
    /// </summary>
    /// <param name="title">The dialog's title.</param>
    /// <param name="suggestedFileName">The file name offered, with its extension.</param>
    /// <param name="filter">The file type filter in the Windows dialog format (<c>Label|*.csv</c>).</param>
    /// <returns>The full path the user chose (an existing file has already been confirmed for overwriting), or null when cancelled.</returns>
    string? PickSavePath(string title, string suggestedFileName, string filter);
}
