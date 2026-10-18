using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using ForzaTelemetry.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForzaTelemetry.Udp;

/// <summary>
/// Listens for Forza Horizon 6 "Data Out" UDP telemetry packets and exposes them as an
/// async stream of parsed <see cref="TelemetryPacket"/> values.
/// </summary>
/// <remarks>
/// This type lives in its own project, not inside the console app, because it is reused as-is by
/// the WPF UI (<c>ForzaTelemetry.App</c>'s <c>TelemetryService</c>) exactly like the console
/// app does today. Keeping it here means the UI never has to know about sockets.
///
/// This is the concrete ADAPTER side of a hexagonal port/adapter split: <see cref="ITelemetrySource"/>
/// (in <c>ForzaTelemetry.Core</c>) is the PORT the rest of the app depends on — "some live stream of
/// telemetry packets" — and this class is one way of implementing it, using a real UDP socket.
/// Depending on the port instead of this concrete type is what lets tests substitute a fake,
/// in-memory source with no socket involved at all (see <c>ForzaTelemetry.App.Tests</c>'s
/// <c>FakeTelemetrySource</c>).
///
/// Why <see cref="IAsyncDisposable"/> instead of plain <see cref="IDisposable"/>: every other
/// public member of this type is async (the two ReadXAsync streams), so a caller — especially a
/// UI shutdown path, which is itself usually async — should be able to write `await using` and
/// stay consistent with the rest of the type's shape. The underlying <see cref="UdpClient"/>'s
/// own Dispose() is actually synchronous (there is no real async cleanup to do), so
/// <see cref="DisposeAsync"/> just wraps it in an already-completed <see cref="ValueTask"/>.
///
/// This type also logs diagnostics (bound endpoint, first datagram per sender, wrong-size
/// datagrams, socket errors, and a "no datagrams" heartbeat) through an injected
/// <see cref="ILogger{TCategoryName}"/> — see the constructor's <c>logger</c> parameter. None of
/// this changes the shape of the async streams themselves: a caller that never passes a logger
/// gets exactly the same packets/behavior as before, just with logging silently disabled.
/// </remarks>
public sealed class UdpTelemetryListener : ITelemetrySource
{
    /// <summary>Default UDP port Forza Horizon 6's "Data Out" feature sends to.</summary>
    public const int DefaultPort = 5300;

    /// <summary>
    /// How long to wait after a socket error before retrying the receive. This is not about
    /// recovering the socket (nothing special is done to it) — it exists purely to guard against a
    /// hot loop: if the socket keeps immediately faulting with the same error, this backoff stops
    /// the receive loop from spinning at 100% CPU retrying instantly, forever.
    /// </summary>
    private static readonly TimeSpan SocketErrorBackoff = TimeSpan.FromMilliseconds(100);

    private readonly UdpClient _udpClient;
    private readonly ILogger<UdpTelemetryListener> _logger;
    private readonly HeartbeatOptions _heartbeatOptions;

    /// <summary>
    /// How a single datagram is actually received. Always <c>_udpClient.ReceiveAsync</c> in
    /// production; a test can wrap it (see the internal constructor below) to inject a faulting or
    /// otherwise fake receive without touching the real socket handling around it.
    /// </summary>
    private readonly Func<CancellationToken, ValueTask<UdpReceiveResult>> _receive;

    /// <summary>
    /// Remote endpoints a datagram has already been seen from, used only to log "first datagram
    /// from {Remote}" exactly once per sender — this is how the user discovers the Xbox's actual IP
    /// address the first time a real packet arrives from it.
    /// </summary>
    private readonly HashSet<IPEndPoint> _seenRemoteEndPoints = new();

    /// <summary>
    /// Tracks how long it has been since the last datagram arrived (or since this listener was
    /// constructed, if none ever has). Restarted every time a datagram is received; read by the
    /// heartbeat check to decide whether it is time to log another "no datagrams" message.
    /// </summary>
    private readonly Stopwatch _silenceStopwatch = Stopwatch.StartNew();

    private long _discardedCount;

    /// <summary>
    /// Running count of <see cref="SocketException"/>s seen on receive, used only to rate-limit
    /// <see cref="Log.SocketReceiveError"/> the same way <see cref="_discardedCount"/> rate-limits
    /// the wrong-size warning (see <see cref="TryReceiveAsync"/>'s catch block). Plain, not
    /// <see cref="Interlocked"/>: unlike <see cref="DiscardedCount"/>, nothing reads this from
    /// outside the single enumerator this type supports.
    /// </summary>
    private long _socketErrorCount;

    /// <summary>How far into the current silence streak the next heartbeat log should fire.</summary>
    private TimeSpan _nextHeartbeatThreshold;

    /// <summary>Whether a "no datagrams" message has already been logged during the current silence streak (so the next successful receive knows to log "flowing again").</summary>
    private bool _heartbeatLogged;

    /// <summary>
    /// The remote endpoint of the most recently received datagram. Used only so the wrong-size
    /// warning (logged one layer up, in <see cref="ReadPacketsAsync"/>, which only sees raw bytes)
    /// can still report which sender it came from. Safe to keep as a single field — not a
    /// per-datagram value threaded through return types — because this type supports only one
    /// concurrent enumerator: the next datagram is never received until the current one has been
    /// fully processed (see the class remarks on <see cref="DiscardedCount"/>).
    /// </summary>
    private IPEndPoint? _lastRemoteEndPoint;

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
    /// <param name="logger">
    /// Where diagnostic messages go. Optional and defaults to <see cref="NullLogger{T}"/> (i.e. no
    /// logging at all) so every existing caller — including this project's own tests — keeps
    /// working unchanged; the console app passes a real <see cref="ILogger{TCategoryName}"/> backed
    /// by Serilog.
    /// </param>
    /// <param name="heartbeatOptions">
    /// Thresholds for the "no datagrams received" heartbeat. Defaults to
    /// <see cref="HeartbeatOptions.Default"/> (5 s, then every further 10 s); tests pass short
    /// values here instead of waiting on the real production thresholds.
    /// </param>
    public UdpTelemetryListener(
        int port = DefaultPort,
        IPAddress? bindAddress = null,
        ILogger<UdpTelemetryListener>? logger = null,
        HeartbeatOptions? heartbeatOptions = null)
    {
        // Bind first: if the port is already in use, this throws SocketException before anything
        // else (like the logger) is touched — the caller (the console app) is expected to catch
        // that and print/log a friendly "port already in use" message instead of a raw exception.
        _udpClient = new UdpClient(new IPEndPoint(bindAddress ?? IPAddress.Any, port));

        _logger = logger ?? NullLogger<UdpTelemetryListener>.Instance;
        _heartbeatOptions = heartbeatOptions ?? HeartbeatOptions.Default;
        _nextHeartbeatThreshold = _heartbeatOptions.InitialSilence;
        _receive = ct => _udpClient.ReceiveAsync(ct);

        Log.ListenerBound(_logger, (IPEndPoint)_udpClient.Client.LocalEndPoint!);
    }

    /// <summary>
    /// Test-only seam, reachable from <c>ForzaTelemetry.Udp.Tests</c> via this assembly's
    /// <c>InternalsVisibleTo</c> (see <c>AssemblyInfo.cs</c>): builds a listener exactly like the
    /// public constructor, then lets <paramref name="wrapReceive"/> replace <see cref="_receive"/>
    /// with a wrapper around the real one.
    /// </summary>
    /// <remarks>
    /// <paramref name="wrapReceive"/> takes the REAL receive delegate (already bound to this
    /// instance's socket) and returns whatever delegate <see cref="TryReceiveAsync"/> should
    /// actually call - typically one that fails a few times (e.g. throws a
    /// <see cref="SocketException"/> to simulate <c>WSAECONNRESET</c> on demand) and then forwards
    /// to the real delegate it was given. Threading the real delegate through like this, instead of
    /// exposing the underlying <see cref="UdpClient"/> itself, means a test never has to duplicate
    /// this type's own binding/disposal logic just to get a working fallback receive.
    /// </remarks>
    internal UdpTelemetryListener(
        int port,
        IPAddress? bindAddress,
        ILogger<UdpTelemetryListener>? logger,
        HeartbeatOptions? heartbeatOptions,
        Func<Func<CancellationToken, ValueTask<UdpReceiveResult>>, Func<CancellationToken, ValueTask<UdpReceiveResult>>> wrapReceive)
        : this(port, bindAddress, logger, heartbeatOptions)
    {
        _receive = wrapReceive(_receive);
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
    /// A <see cref="SocketException"/> from the underlying receive (e.g. Windows' spurious
    /// WSAECONNRESET on a UDP socket) does NOT end the enumeration either: it is logged as a
    /// warning and the loop keeps listening — see <see cref="TryReceiveAsync"/> for why.
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
    /// console app today, a WPF view model tomorrow) normally wants: a pull-based sequence
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
                var discardedCount = Interlocked.Increment(ref _discardedCount);

                // Rate-limited so a burst of garbage on the port cannot flood the log file: the
                // first few are useful (they immediately tell the user SOMETHING wrong-sized is
                // arriving), after that only every 100th is logged so a sustained flood does not
                // turn into gigabytes of near-identical log lines.
                if (discardedCount <= 5 || discardedCount % 100 == 0)
                {
                    Log.WrongSizeDatagram(_logger, datagram.Length, _lastRemoteEndPoint, TelemetryParser.PacketSize, discardedCount);
                }
            }
        }
    }

    private async Task<ReadOnlyMemory<byte>?> TryReceiveAsync(CancellationToken ct)
    {
        // Declared once per CALL (i.e. once per datagram this method ends up returning,
        // cancellation, or dispose) rather than as an instance field. It used to be a field, which
        // was a bug (T5 review finding #1): if this method returned null because the heartbeat
        // delay below won its race against a still-pending receive - which can happen exactly when
        // `ct` is cancelled, since both tasks are watching the same token - a field would leave that
        // pending receive, bound to this now-cancelled `ct`, sitting around for the NEXT call. A
        // later enumeration (with a fresh token) would then reuse it via `??=`, immediately observe
        // the old cancellation, and end right away without ever issuing a real receive. Scoping it
        // as a local guarantees every call starts clean, while still surviving the `continue`
        // statements below within THIS call, which is the only place it ever needed to survive.
        Task<UdpReceiveResult>? pendingReceive = null;

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                return null;
            }

            // Reuse the SAME pending receive across heartbeat timeouts: if a previous loop
            // iteration below timed out waiting for the heartbeat check (not for a datagram), we
            // must keep awaiting this exact task next time instead of starting a second
            // ReceiveAsync — otherwise we would either abandon the first datagram that eventually
            // arrives on it, or end up with two receives racing on one socket.
            var receiveTask = pendingReceive ??= _receive(ct).AsTask();

            var remainingUntilHeartbeat = _nextHeartbeatThreshold - _silenceStopwatch.Elapsed;
            if (remainingUntilHeartbeat < TimeSpan.Zero)
            {
                remainingUntilHeartbeat = TimeSpan.Zero;
            }

            // Linked (not just `ct` directly) so the delay itself can be cancelled the moment the
            // receive wins below (T5 review finding #4): `using` disposes this every iteration, but
            // disposing alone does not stop Task.Delay's own internal timer early, only explicitly
            // cancelling it does. Without this, every iteration where the receive wins would leave
            // its heartbeat delay (up to the full InitialSilence/RepeatInterval duration) still
            // ticking away uselessly - and at 60 Hz, that is hundreds of live, pointless timers at
            // any given moment.
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Racing the pending receive against a delay — rather than spinning up a second
            // thread or a System.Threading.Timer — is what lets one await point notice "no
            // datagram AND no cancellation yet, but the heartbeat threshold passed". Recomputing
            // this delay fresh every loop pass is cheap and always correct; only the receive
            // itself must never be recreated (see above).
            var heartbeatDelayTask = Task.Delay(remainingUntilHeartbeat, heartbeatCts.Token);

            var completedTask = await Task.WhenAny(receiveTask, heartbeatDelayTask).ConfigureAwait(false);

            if (completedTask != receiveTask)
            {
                // Task.Delay(..., ct) also completes (canceled) when ct fires, so a real
                // cancellation can look exactly like a heartbeat timeout here — check explicitly
                // instead of misreporting a shutdown as "still silent".
                if (ct.IsCancellationRequested)
                {
                    return null;
                }

                ReportSilenceTick();
                continue; // receiveTask is still pending; loop back and keep waiting on it.
            }

            // The receive won: release the still-pending delay's timer right now instead of
            // leaving it to elapse on its own (see the WHY-comment on heartbeatCts above).
            heartbeatCts.Cancel();

            pendingReceive = null; // Consumed; the next iteration (if any) starts a fresh receive.

            UdpReceiveResult result;
            try
            {
                result = await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Chosen cancellation behavior: end the enumeration cleanly instead of throwing —
                // see the ReadDatagramsAsync doc comment above.
                return null;
            }
            catch (ObjectDisposedException)
            {
                // The listener was disposed while a receive was in flight (e.g. the app is
                // shutting down and disposed the listener before the pending ReceiveAsync
                // completed). Treat it the same as cancellation rather than surfacing a confusing
                // exception from an already-closed socket.
                return null;
            }
            catch (SocketException ex)
            {
                // On Windows, a UDP socket can surface a PREVIOUS datagram's ICMP "port
                // unreachable" response as WSAECONNRESET (SocketError.ConnectionReset) on the
                // NEXT unrelated ReceiveAsync call — a well-known, documented quirk of UDP on
                // Windows, not a fatal condition. Before this change, that exception propagated
                // out of ReadDatagramsAsync and silently ended the whole enumeration, which meant
                // the console app would just stop receiving telemetry with no obvious reason why.
                // Now it is logged and the loop keeps listening, which is what "keep receiving
                // telemetry for the whole play session" should mean.
                var socketErrorCount = ++_socketErrorCount;

                // Rate-limited the same way the wrong-size warning is (see ReadPacketsAsync): a
                // socket that keeps faulting (e.g. a flaky VPN adapter) must not flood the log file
                // with an identical warning on every single retry.
                if (socketErrorCount <= 5 || socketErrorCount % 100 == 0)
                {
                    Log.SocketReceiveError(_logger, ex.SocketErrorCode, ex.Message, socketErrorCount);
                }

                try
                {
                    await Task.Delay(SocketErrorBackoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                continue;
            }

            ReportDatagramReceived(result.RemoteEndPoint, result.Buffer.Length);
            return result.Buffer;
        }
    }

    /// <summary>Logs the "no datagrams received for Ns" heartbeat and schedules the next one.</summary>
    private void ReportSilenceTick()
    {
        Log.NoDatagramsReceived(_logger, (int)_silenceStopwatch.Elapsed.TotalSeconds);
        _heartbeatLogged = true;
        _nextHeartbeatThreshold += _heartbeatOptions.RepeatInterval;
    }

    /// <summary>
    /// Records bookkeeping for a successfully received datagram: resets the silence tracking,
    /// logs "flowing again" if we had gone quiet, and logs "first datagram" the first time this
    /// particular sender is seen.
    /// </summary>
    private void ReportDatagramReceived(IPEndPoint remote, int length)
    {
        if (_heartbeatLogged)
        {
            Log.DatagramsFlowingAgain(_logger);
            _heartbeatLogged = false;
        }

        _silenceStopwatch.Restart();
        _nextHeartbeatThreshold = _heartbeatOptions.InitialSilence;
        _lastRemoteEndPoint = remote;

        if (_seenRemoteEndPoints.Add(remote))
        {
            // This is how the user discovers the Xbox's actual IP address: the very first
            // datagram from a sender that has never been seen before on this listener.
            Log.FirstDatagramFromSender(_logger, remote, length);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _udpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
