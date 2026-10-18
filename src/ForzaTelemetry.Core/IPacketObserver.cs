namespace ForzaTelemetry.Core;

/// <summary>
/// A per-packet hook: the telemetry service calls <see cref="OnPacket"/> for EVERY parsed packet,
/// from the background receive loop (not from the UI's 20 Hz refresh tick).
/// </summary>
/// <remarks>
/// WHY a hook instead of reading the "latest packet" from the UI: the game sends ~60 packets/s but the
/// UI samples at 20 Hz, so two out of three packets would be invisible. Engines that need exact timing
/// (0-100 km/h) or a complete record (lap samples, ring buffers) must see every packet.
///
/// Contract for implementers:
/// <list type="bullet">
/// <item>Called from ONE thread at a time (the receive loop), so <see cref="OnPacket"/> needs no
/// internal synchronization against itself.</item>
/// <item>Must be fast and must not throw or block: it runs inside the receive loop.</item>
/// <item>Readers (the UI thread) get data through immutable snapshots or locked copies, never by
/// touching the observer's mutable internals. Each implementation documents how.</item>
/// </list>
/// </remarks>
public interface IPacketObserver
{
    /// <summary>Consumes one decoded packet. Never retain mutable state shared with readers without a documented hand-off.</summary>
    void OnPacket(TelemetryPacket packet);
}
