namespace ForzaTelemetry.App.Services;

/// <summary>
/// Asks the user a yes/no question before something destructive. An interface so a view model can ask without referencing
/// any window (the WPF implementation opens a dialog; a test answers instantly), and so the question's wording stays in the
/// view model's resources, not in dialog code.
/// </summary>
public interface IConfirmationService
{
    /// <summary>Shows the question and waits; true only when the user explicitly confirmed (closing or cancelling is false).</summary>
    /// <param name="title">Short headline of the dialog.</param>
    /// <param name="message">What will happen if the user confirms.</param>
    /// <param name="confirmText">Label of the destructive button.</param>
    /// <param name="cancelText">Label of the safe button, which is the default.</param>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText);
}
