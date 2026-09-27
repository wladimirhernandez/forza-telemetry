using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using ForzaTelemetry.Core;

namespace ForzaTelemetry.Udp;

/// <summary>
/// Listens for Forza Horizon 6 "Data Out" UDP telemetry packets and exposes them as an
/// async stream of parsed <see cref="TelemetryPacket"/> values.
/// </summary>
/// <remarks>
/// This type lives in its own project, not inside the console app, because it will be reused
/// as-is by an Avalonia UI later: a view model only needs to `await foreach` over
/// <see cref="ReadPacketsAsync"/> and push each packet into bound properties, exactly like the
/// console app does today. Keeping it here means the UI never has to know about sockets.
///
/// Why <see cref="IAsyncDisposable"/> instead of plain <see cref="IDisposable"/>: every other
/// public member of this type is async (the two ReadXAsync streams), so a caller — especially a
/// UI shutdown path, which is itself usually async — should be able to write `await using` and
/// stay consistent with the rest of the type's shape. The underlying <see cref="UdpClient"/>'s
/// own Dispose() is actually synchronous (there is no real async cleanup to do), so
/// <see cref="DisposeAsync"/> just wraps it in an already-completed <see cref="ValueTask"/>.
/// </remarks>
public sealed class UdpTelemetryListener : IAsyncDisposable
{
    /// <summary>Default UDP port Forza Horizon 6's "Data Out" feature sends to.</summary>
    public const int DefaultPort = 5300;

    private readonly UdpClient _udpClient;
    private long _discardedCount;

    /// <summary>
    /// Creates a listener bound to <paramref name="port"/> and starts listening immediately
    /// (the underlying socket is bound in the constructor, matching how <see cref="UdpClient"/>
    /// itself works).
    /// </summary>
    /// <param name="port">
    /// UDP port to listen on. Defaults to <see cref="DefaultPort"/>. Pass 0 to let the OS assign
    /// a free port (useful in tests that need several independent listeners); read
    /// <see cref="LocalPort"/> afterwards to find out which port was actually bound.
    /// </param>
    /// <param name="bindAddress">
    /// Local address to bind to. Defaults to <see cref="IPAddress.Any"/>. Binding to
    /// <see cref="IPAddress.Loopback"/> only would be enough when the game runs on the very same
    /// PC as this listener, but Forza Horizon 6 can also run on an Xbox or on a different PC on
    /// the same LAN and send its "Data Out" packets to this machine's network address — only
    /// <see cref="IPAddress.Any"/> can receive those.
    /// </param>
    public UdpTelemetryListener(int port = DefaultPort, IPAddress? bindAddress = null)
    {
        _udpClient = new UdpClient(new IPEndPoint(bindAddress ?? IPAddress.Any, port));
    }

    /// <summary>
    /// The UDP port actually bound. Mainly useful when constructed with <c>port: 0</c> so the OS
    /// picks a free ephemeral port — e.g. so tests can run several listeners side by side without
    /// colliding on a hardcoded port number.
    /// </summary>
    public int LocalPort => ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port;

    /// <summary>
    /// Number of received datagrams that were not a valid 324-byte telemetry packet and were
    /// therefore skipped instead of being yielded by <see cref="ReadPacketsAsync"/>.
    /// </summary>
    /// <remarks>
    /// Incremented with <see cref="Interlocked"/> so the count can be safely read from a
    /// different thread than the one enumerating <see cref="ReadPacketsAsync"/> — e.g. a UI
    /// thread polling this property to refresh a status label while a background task keeps
    /// enumerating. This does NOT make the listener itself safe for concurrent enumeration: only
    /// one caller is expected to enumerate (and therefore increment this counter) at a time.
    /// </remarks>
    public long DiscardedCount => Interlocked.Read(ref _discardedCount);

    /// <summary>
    /// Reads raw UDP datagrams as they arrive, with no parsing at all. This is the low-level
    /// building block <see cref="ReadPacketsAsync"/> is implemented on top of — most callers want
    /// that higher-level stream instead. It is exposed publicly so a diagnostic tool can inspect
    /// bytes the parser would otherwise silently interpret or discard (e.g. the console app's
    /// <c>--raw</c> mode, which prints the inferred trailing padding byte to sanity-check that
    /// inference against real game traffic).
    /// </summary>
    /// <param name="ct">
    /// Cancelling this token ends the enumeration cleanly: the consuming `await foreach` loop
    /// simply finishes, it does not throw <see cref="OperationCanceledException"/>. This mirrors
    /// ordinary C# stream-ending semantics (like reaching end-of-file) so every caller doesn't
    /// have to wrap its loop in a try/catch just to handle an expected shutdown request.
    /// </param>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadDatagramsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            // The receive-and-catch logic lives in a separate method because C# iterator
            // methods do not allow a `yield return` inside a try block that has a catch clause —
            // splitting it out keeps the cancellation handling and the yielding cleanly separate.
            var datagram = await TryReceiveAsync(ct).ConfigureAwait(false);
            if (datagram is null)
            {
                yield break;
            }

            yield return datagram.Value;
        }
    }

    /// <summary>
    /// Reads and parses telemetry packets as they arrive. This is the stream a consumer (the
    /// console app today, an Avalonia view model tomorrow) normally wants: a pull-based sequence
    /// of typed values that maps naturally onto a UI update loop (`await foreach` -&gt; update
    /// view model -&gt; repaint), without the consumer ever touching raw bytes or a background
    /// thread directly.
    /// </summary>
    /// <param name="ct">See <see cref="ReadDatagramsAsync"/> for the cancellation behavior.</param>
    public async IAsyncEnumerable<TelemetryPacket> ReadPacketsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var datagram in ReadDatagramsAsync(ct).ConfigureAwait(false))
        {
            // TryParse (not Parse) is deliberate: a stray, truncated, or otherwise unrelated
            // datagram arriving on this port must not throw and kill the whole receive loop — it
            // should just be counted and skipped, so the next (likely valid) datagram still gets
            // processed instead of the whole listener dying on one bad packet.
            if (TelemetryParser.TryParse(datagram.Span, out var packet))
            {
                yield return packet;
            }
            else
            {
                Interlocked.Increment(ref _discardedCount);
            }
        }
    }

    private async Task<ReadOnlyMemory<byte>?> TryReceiveAsync(CancellationToken ct)
    {
        try
        {
            var result = await _udpClient.ReceiveAsync(ct).ConfigureAwait(false);
            return result.Buffer;
        }
        catch (OperationCanceledException)
        {
            // Chosen cancellation behavior: end the enumeration cleanly instead of throwing —
            // see the ReadDatagramsAsync doc comment above.
            return null;
        }
        catch (ObjectDisposedException)
        {
            // The listener was disposed while a receive was in flight (e.g. the app is shutting
            // down and disposed the listener before the pending ReceiveAsync completed). Treat it
            // the same as cancellation rather than surfacing a confusing exception from an
            // already-closed socket.
            return null;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _udpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
