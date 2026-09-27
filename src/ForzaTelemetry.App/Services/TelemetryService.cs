using System.Net.Sockets;
using ForzaTelemetry.Core;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// Owns one telemetry connection (started/stopped on demand) and keeps only the LATEST received
/// packet in memory, plus a running counter and a fault, for a UI to sample at its own pace.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a factory (<see cref="Func{ITelemetrySource}"/>), not a single long-lived source:</b> the
/// real adapter, <c>UdpTelemetryListener</c>, binds its UDP socket inside its OWN constructor (see
/// that type's remarks) — so the only way to free port 5300 between a Stop and a later Start is to
/// dispose that listener entirely and construct a brand-new one next time. Storing a factory
/// instead of a single instance means <see cref="StartAsync"/> can always build a fresh source, and
/// <see cref="StopAsync"/> can always dispose it — and this service never needs to know whether the
/// source behind the factory is a real UDP socket or a fake used in tests.
/// </para>
/// <para>
/// <b>Why the receive loop runs on <see cref="Task.Run(Action)"/>:</b> an <c>await foreach</c> over
/// <see cref="ITelemetrySource.ReadPacketsAsync"/> never completes on its own while the game keeps
/// sending data — it is a live, unbounded stream. Awaiting it directly on the UI thread would freeze
/// the whole application: Avalonia's UI thread also has to keep pumping window/input messages and
/// repainting, and it cannot do that while blocked awaiting the next packet. <see cref="Task.Run"/>
/// hands the whole loop to a thread-pool thread instead, so the UI thread stays free. This is also
/// exactly why nothing in this class may touch a bound UI property directly — see
/// <c>MainViewModel.Refresh</c> and <c>DispatcherTimerTicker</c> for how the UI safely reads back
/// out of this service instead.
/// </para>
/// <para>
/// <b>Why only the LATEST packet is kept, not a queue/list of everything received:</b> nothing in
/// this dashboard ever needs packet HISTORY — it only ever wants "what is true right now" (current
/// RPM, current speed, current gear). A queue would grow forever unless something kept draining it,
/// for no benefit this app actually has a use for.
/// </para>
/// <para>
/// <b>Why <see cref="Volatile"/>/<see cref="Interlocked"/>, not a <c>lock</c>:</b> exactly one
/// thread — the background receive loop — ever WRITES <see cref="Latest"/>, <see cref="ReceivedCount"/>
/// and the last-packet timestamp; one or more other threads (the UI thread, via
/// <c>MainViewModel.Refresh</c>) only ever READ them. That single-writer/many-reader shape is
/// exactly what these primitives are for: they guarantee a reader never observes a half-written
/// value, without paying for a full lock on every one of the ~30-60 packets/sec this can receive.
/// </para>
/// </remarks>
public sealed class TelemetryService : IAsyncDisposable
{
    private readonly Func<ITelemetrySource> _sourceFactory;

    private ITelemetrySource? _source;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    private TelemetryPacket? _latest;
    private long _receivedCount;
    private Exception? _fault;
    private bool _isRunning;

    public TelemetryService(Func<ITelemetrySource> sourceFactory)
    {
        _sourceFactory = sourceFactory;
    }

    /// <summary>Most recently received packet, or null if none has arrived yet.</summary>
    public TelemetryPacket? Latest => Volatile.Read(ref _latest);

    /// <summary>Total packets received since this service was constructed (Stop/Start does not reset it).</summary>
    public long ReceivedCount => Interlocked.Read(ref _receivedCount);

    /// <summary>True while a background receive loop is active.</summary>
    public bool IsRunning => Volatile.Read(ref _isRunning);

    /// <summary>
    /// Set when the source factory or the background loop threw — e.g. a <see cref="SocketException"/>
    /// for "UDP port already in use". Cleared at the start of every <see cref="StartAsync"/> call, so
    /// a later successful Start clears an earlier failure.
    /// </summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    // Note: this service deliberately does NOT track "time since last packet" itself — that would
    // need its own clock, and MainViewModel already needs an injected TimeProvider for
    // PacketsPerSecond. Keeping only ReceivedCount here lets MainViewModel derive "how long since a
    // NEW packet arrived" from ONE clock (see its Refresh method), instead of two clocks
    // (this service's real one, and the view model's injected one) that could disagree in a test.

    /// <summary>
    /// Builds a fresh source from the factory and starts its background receive loop. A no-op if
    /// already running. Safe to call again after <see cref="StopAsync"/> — every call builds a
    /// brand-new source, so e.g. a freed UDP port can be rebound.
    /// </summary>
    public async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        // A loop that ended on its own (a mid-stream fault) leaves its source, CTS and task behind:
        // RunLoopAsync only flips IsRunning. Release them before building a new source — for the
        // real UDP adapter the old socket still holds the port, so the new listener would otherwise
        // fail with "address already in use" and the user could never recover without a restart.
        await StopAsync().ConfigureAwait(false);

        Volatile.Write(ref _fault, null);

        ITelemetrySource source;
        try
        {
            // The factory itself can throw SYNCHRONOUSLY here — e.g. UdpTelemetryListener's
            // constructor binds a socket immediately and throws SocketException if the port is
            // already taken. Catching it right here, instead of letting it propagate out of
            // StartAsync, is what "no exceptions escape to the UI thread unobserved" means for this
            // failure path: a bad Start becomes a Fault the view model can turn into a friendly
            // status message, not a crash of whatever awaited StartAsync.
            source = _sourceFactory();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _fault, ex);
            return;
        }

        _source = source;
        _cts = new CancellationTokenSource();
        Volatile.Write(ref _isRunning, true);

        // See the class remarks for why this loop must run off the calling thread.
        _loopTask = Task.Run(() => RunLoopAsync(source, _cts.Token));
    }

    private async Task RunLoopAsync(ITelemetrySource source, CancellationToken ct)
    {
        try
        {
            await foreach (var packet in source.ReadPacketsAsync(ct).ConfigureAwait(false))
            {
                Volatile.Write(ref _latest, packet);
                Interlocked.Increment(ref _receivedCount);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: StopAsync cancelled the token. Not a fault.
        }
        catch (Exception ex)
        {
            // Any OTHER exception from the source — a fake failing in a test, or a real socket
            // failing mid-stream — is captured here instead of becoming an unobserved exception on
            // a background Task.Run that nobody is awaiting. An unobserved Task exception is exactly
            // the kind of failure that either crashes the process or vanishes silently depending on
            // the runtime, neither of which is acceptable for "the network hiccuped" in a UI app —
            // recording it as Fault lets MainViewModel show it instead.
            Volatile.Write(ref _fault, ex);
        }
        finally
        {
            Volatile.Write(ref _isRunning, false);
        }
    }

    /// <summary>
    /// Cancels the background loop, waits for it to actually finish, then disposes the current
    /// source — which, for the real UDP adapter, releases the port so a later <see cref="StartAsync"/>
    /// can rebind it. A no-op if never started (or already stopped).
    /// </summary>
    public async Task StopAsync()
    {
        var cts = _cts;
        var loopTask = _loopTask;
        var source = _source;

        _cts = null;
        _loopTask = null;
        _source = null;

        if (cts is null)
        {
            return;
        }

        cts.Cancel();

        if (loopTask is not null)
        {
            // RunLoopAsync already catches everything it can throw (see above), so this await
            // cannot itself throw — it exists so that by the time StopAsync returns, the loop has
            // genuinely finished touching Latest/ReceivedCount, and the source below is safe to
            // dispose without racing an in-flight read.
            await loopTask.ConfigureAwait(false);
        }

        if (source is not null)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }

        cts.Dispose();
        Volatile.Write(ref _isRunning, false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
