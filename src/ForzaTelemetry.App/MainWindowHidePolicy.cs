using System.Windows;

namespace ForzaTelemetry.App;

/// <summary>
/// Decides when the main window is minimized for mini mode and when it is brought back. Pure state, no window: the controller feeds it the
/// main window's state and does what it answers, which is what makes these rules testable.
/// </summary>
/// <remarks>
/// <b>Two rules.</b> The "Hide main window" setting is read ONCE, when the mini window opens (<see cref="BeginOpen"/>), and the answer is
/// remembered: closing never looks at the setting again, so one session cannot minimize the window and then refuse to restore it. And only
/// the minimized state WE put it in is ever undone: the moment the main window leaves the minimized state by any other hand (the user
/// clicked its taskbar button) the policy lets go of it for good, even if the user minimizes it again, because from then on it is theirs.
/// </remarks>
internal sealed class MainWindowHidePolicy
{
    // The state the main window had before we minimized it; null when we did not (or when the user has taken it back since).
    private WindowState? _stateBeforeHide;

    /// <summary>
    /// The mini window is opening. Returns true when the caller must now minimize the main window (the setting is on and it is not minimized
    /// already); the state to return to is remembered.
    /// </summary>
    public bool BeginOpen(bool hideMainWindow, WindowState current)
    {
        _stateBeforeHide = hideMainWindow && current != WindowState.Minimized ? current : null;
        return _stateBeforeHide is not null;
    }

    /// <summary>The main window's state changed (the caller reports every change, including the one our own minimize causes).</summary>
    public void OnMainWindowStateChanged(WindowState state)
    {
        if (_stateBeforeHide is not null && state != WindowState.Minimized)
        {
            _stateBeforeHide = null; // the user brought it back themselves: never touch it again, even if they minimize it later
        }
    }

    /// <summary>
    /// The mini window closed. Returns the state to restore the main window to, or null to leave it alone (we never minimized it, or the user
    /// has handled it since). Forgets everything either way.
    /// </summary>
    public WindowState? EndClose(WindowState current)
    {
        var restoreTo = _stateBeforeHide;
        _stateBeforeHide = null;
        return restoreTo is not null && current == WindowState.Minimized ? restoreTo : null;
    }
}
