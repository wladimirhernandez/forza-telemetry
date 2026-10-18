using System.Windows.Threading;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// Real <see cref="IUiTicker"/>, backed by WPF's <see cref="DispatcherTimer"/>.
/// </summary>
/// <remarks>
/// <b>Why sample at a fixed rate instead of updating the UI on every packet:</b> WPF has the
/// same rule as WinForms/most UI frameworks — ONLY the UI thread may touch bound properties or
/// controls. FH6 sends telemetry at ~30 packets/sec (60 in performance mode), so naively marshalling
/// EVERY packet onto the UI thread (e.g. via <c>Dispatcher.Post</c> from the receive loop) would mean
/// 30-60 dispatcher messages per second. That is wasteful — a human cannot perceive UI updates that
/// fast anyway — and under load it risks the dispatcher queue backing up behind other UI work
/// (input, layout, rendering), making the whole app feel less responsive, not more "live".
/// <para>
/// Instead, <c>TelemetryService</c> keeps only the LATEST packet in memory (see its own remarks),
/// and this ticker calls <c>MainViewModel.Refresh()</c> on a fixed schedule — <c>DispatcherTimer</c>'s
/// callback already runs on the UI thread, so no extra marshalling is needed inside <c>Refresh</c>.
/// ~20 Hz (a 50 ms interval) is smooth enough to look "live" to a human while being well below the
/// network's own rate — this decouples "how fast the network delivers data" from "how fast the
/// screen redraws", the same idea the console app already uses for its own print-rate limiting
/// (see <c>ForzaTelemetry.Console</c>'s <c>RunTelemetryViewAsync</c>).
/// </para>
/// </remarks>
public sealed class DispatcherTimerTicker : IUiTicker
{
    private DispatcherTimer? _timer;

    public void Start(TimeSpan interval, Action tick)
    {
        Stop();

        _timer = new DispatcherTimer
        {
            Interval = interval
        };
        _timer.Tick += (_, _) => tick();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }
}
