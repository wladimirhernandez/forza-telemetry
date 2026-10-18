namespace ForzaTelemetry.App.Services;

/// <summary>Closes the app and starts it again (used after a language change). An interface so the Settings view model does not depend on WPF's Application.</summary>
public interface IAppRestarter
{
    /// <summary>Requests a restart: the app shuts down cleanly (stopping the listener, flushing the log) and then relaunches.</summary>
    void Restart();
}
