using System.Windows;
using ForzaTelemetry.App.Views;

namespace ForzaTelemetry.App.Services;

/// <summary>Real <see cref="IConfirmationService"/>: a modal <see cref="ConfirmDialog"/> centered over the main window.</summary>
public sealed class WpfConfirmationService : IConfirmationService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        // Must run on the UI thread (it does: view model commands do). ShowDialog runs a nested message loop and returns
        // when the dialog closes, so the Task is already complete when it is handed back: the caller's await resumes at once.
        var dialog = new ConfirmDialog(title, message, confirmText, cancelText)
        {
            Owner = Application.Current?.MainWindow
        };

        return Task.FromResult(dialog.ShowDialog() == true);
    }
}
