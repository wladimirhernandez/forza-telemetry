namespace ForzaTelemetry.Core;

/// <summary>
/// A per-datagram hook, the RAW-bytes sibling of <see cref="IPacketObserver"/>: called for every UDP
/// datagram exactly as received, before (or regardless of) parsing.
/// </summary>
/// <remarks>
/// WHY a second hook: a recording must keep the original bytes so it can be re-parsed after a parser fix.
/// A <see cref="IPacketObserver"/> only sees an already-decoded <see cref="TelemetryPacket"/> (and never the
/// wrong-size datagrams the parser rejects), so it cannot be used for this.
///
/// Contract for implementers (same spirit as <see cref="IPacketObserver"/>):
/// <list type="bullet">
/// <item>Called from ONE thread at a time (the receive loop).</item>
/// <item>Must be fast, must not block and must not throw: it runs inside the receive loop.</item>
/// <item>The memory is only guaranteed valid during the call; copy it if it must outlive it.</item>
/// </list>
/// </remarks>
public interface IDatagramObserver
{
    /// <summary>Consumes one raw datagram. Copy <paramref name="datagram"/> if you need it after returning.</summary>
    void OnDatagram(ReadOnlyMemory<byte> datagram);
}
