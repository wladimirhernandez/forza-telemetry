using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App.Tests;

/// <summary>
/// Test double for <see cref="IUiTicker"/>: instead of firing on a real timer/dispatcher, a test
/// calls <see cref="Tick"/> itself, whenever it wants — deterministically, with no need to wait on
/// real wall-clock time or a real WPF UI thread just to exercise "what happens on a refresh".
/// </summary>
internal sealed class ManualTicker : IUiTicker
{
    private Action? _tick;

    public void Start(TimeSpan interval, Action tick) => _tick = tick;

    public void Stop() => _tick = null;

    /// <summary>Invokes the action passed to <see cref="Start"/>, as if the interval had just elapsed.</summary>
    public void Tick() => _tick?.Invoke();
}
