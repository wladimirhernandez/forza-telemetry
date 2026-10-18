using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ForzaTelemetry.App.Services;

/// <summary>Real <see cref="IFileSaveDialog"/>: the standard Windows Save dialog, owned by the main window.</summary>
public sealed class WpfFileSaveDialog : IFileSaveDialog
{
    public string? PickSavePath(string title, string suggestedFileName, string filter)
    {
        // Must run on the UI thread (it does: view model commands do). ShowDialog runs a nested message loop and returns when the
        // dialog closes.
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = suggestedFileName,
            Filter = filter,
            DefaultExt = Path.GetExtension(suggestedFileName),
            AddExtension = true,

            // The dialog itself asks "replace it?" for an existing file, so no code of ours has to.
            OverwritePrompt = true,
            CheckPathExists = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        var owner = Application.Current?.MainWindow;
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true ? dialog.FileName : null;
    }
}
