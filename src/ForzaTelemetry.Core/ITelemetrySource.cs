namespace ForzaTelemetry.Core;

/// <summary>
/// Port (a hexagonal-architecture term): "a live stream of telemetry packets, from wherever they
/// actually come from". The rest of the app — <c>TelemetryService</c>, <c>MainViewModel</c>, and
/// their tests — depends on THIS abstraction, never on <c>UdpTelemetryListener</c> directly.
/// </summary>
/// <remarks>
/// Why this indirection matters: <c>UdpTelemetryListener</c> is one ADAPTER — a concrete way of
/// implementing this port using a real UDP socket. A unit test can implement this same interface
/// with an in-memory fake (no socket, no network, fully deterministic) and hand it to
/// <c>TelemetryService</c> instead; as far as the service and the view model are concerned, nothing
/// changed. A future adapter — e.g. replaying a recorded session from a file — could be added later
/// the same way, again without touching any app code that already depends on this interface.
/// </remarks>
public interface ITelemetrySource : IAsyncDisposable
{
    /// <summary>
    /// Reads and yields telemetry packets as they arrive. See
    /// <c>ForzaTelemetry.Udp.UdpTelemetryListener.ReadPacketsAsync</c> (in the sibling
    /// ForzaTelemetry.Udp project — not referenced here, hence plain <c>&lt;c&gt;</c> instead of a
    /// resolvable <c>cref</c>) for the concrete UDP adapter's cancellation and error-handling
    /// behavior; any other adapter implementing this port should follow the same contract (end the
    /// enumeration cleanly on cancellation; do not throw for a single bad/transient reading unless
    /// the source is truly unrecoverable).
    /// </summary>
    IAsyncEnumerable<TelemetryPacket> ReadPacketsAsync(CancellationToken ct = default);
}
