namespace ForzaTelemetry.App.Services;

/// <summary>
/// Abstraction over "invoke this <see cref="Action"/> repeatedly, on the UI thread, at a fixed
/// interval". Exists so <c>MainViewModel.Refresh</c> can be driven deterministically from a test
/// (see <c>ForzaTelemetry.App.Tests</c>'s <c>ManualTicker</c>, which fires the tick only when a test
/// explicitly asks it to) instead of depending on a real <see cref="System.Windows.Threading.DispatcherTimer"/>
/// and a live UI thread just to exercise "what happens when the ticker fires".
/// </summary>
public interface IUiTicker
{
    /// <summary>Starts calling <paramref name="tick"/> every <paramref name="interval"/> until <see cref="Stop"/> is called.</summary>
    void Start(TimeSpan interval, Action tick);

    /// <summary>Stops calling the action passed to <see cref="Start"/>. Safe to call even if never started.</summary>
    void Stop();
}
