using System.Net.Sockets;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForzaTelemetry.App.Services;

/// <summary>Where the packets of the current (or last) session come from.</summary>
public enum TelemetrySourceMode
{
    /// <summary>The game, over UDP.</summary>
    Live,

    /// <summary>A recording played back from a file.</summary>
    Replay
}

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
/// the whole application: WPF's UI thread also has to keep pumping window/input messages and
/// repainting, and it cannot do that while blocked awaiting the next packet. <see cref="Task.Run"/>
/// hands the whole loop to a thread-pool thread instead, so the UI thread stays free. This is also
/// exactly why nothing in this class may touch a bound UI property directly — see
/// <c>ShellViewModel.Refresh</c> and <c>DispatcherTimerTicker</c> for how the UI safely reads back
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
/// <c>ShellViewModel.Refresh</c>) only ever READ them. That single-writer/many-reader shape is
/// exactly what these primitives are for: they guarantee a reader never observes a half-written
/// value, without paying for a full lock on every one of the ~30-60 packets/sec this can receive.
/// </para>
/// <para>
/// <b>Why per-packet observers exist next to "only the latest packet":</b> the UI samples <see cref="Latest"/> at 20 Hz,
/// but the game sends up to ~60 packets/s, so two of every three packets are never seen by the UI. That is fine for a
/// gauge and wrong for an engine that needs EXACT timing (the 0-100 km/h timer interpolates between consecutive packets)
/// or a complete record (the lap tracker's distance samples). Each registered <see cref="IPacketObserver"/> is therefore
/// called for EVERY packet, on the receive loop's thread, before the packet is published as <see cref="Latest"/>. Two
/// consequences: an observer must be fast (it runs inside the loop), and once <see cref="ReceivedCount"/> shows N packets
/// every observer has already consumed all N, which keeps "what the engines say" consistent with "what the UI was told".
/// An observer that throws is logged and skipped for that packet: one faulty engine must never stop the receive loop, the
/// other engines, or the dashboard.
/// </para>
/// <para>
/// <b>Live and replay share this one loop.</b> A replayed recording is just another <see cref="ITelemetrySource"/>
/// (<see cref="ReplayTelemetrySource"/>), so <see cref="StartReplayAsync"/> goes through the same serialized start path as a live
/// Start: the observers, the engines and every screen are driven by it exactly like by live data, and cannot tell the difference.
/// The two are mutually exclusive by construction (there is one source at a time): starting either one first stops whatever was
/// running, releasing its UDP port or its file, before the new source is built.
/// </para>
/// </remarks>
public sealed class TelemetryService : IAsyncDisposable
{
    /// <summary>After the first failure of an observer, log again only every this many failures (~10 s at 60 packets/s).</summary>
    private const int ObserverLogEvery = 600;

    private readonly Func<ITelemetrySource> _sourceFactory;
    private readonly Func<string, ReplayTelemetrySource> _replayFactory;
    private readonly ILogger<TelemetryService> _logger;

    // Fixed at construction, so the receive loop can iterate it without a lock. Failure counters are only touched by the loop.
    private readonly IPacketObserver[] _observers;
    private readonly int[] _observerFailures;

    // Serializes Start and Stop. Stop nulls the shared fields BEFORE it awaits the loop and the dispose, so
    // without this a second Stop (e.g. App.OnExit while the Stop button's Stop is still in flight) would see
    // "nothing to do" and return immediately — while the first Stop is still releasing the UDP socket. With the
    // gate, the second caller waits until the first has fully finished, so "StopAsync returned" always means
    // "loop ended and source disposed". A SemaphoreSlim (not a lock) because it must be held across awaits.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ITelemetrySource? _source;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private ReplayTelemetrySource? _replay;
    private string? _replayPath;
    private int _mode; // a TelemetrySourceMode; an int so Volatile can publish it

    private TelemetryPacket? _latest;
    private long _receivedCount;
    private Exception? _fault;
    private bool _isRunning;

    /// <param name="sourceFactory">Builds a fresh telemetry source for every Start (see the class remarks).</param>
    /// <param name="logger">Where lifecycle events and faults are recorded; optional (defaults to discarding them), like the UDP listener's.</param>
    /// <param name="observers">Engines that must see every packet (see the class remarks); called in the given order.</param>
    /// <param name="replayFactory">
    /// Builds the replay source for a recording path (fresh for every replay: a replay source is not reusable once stopped).
    /// Defaults to a real-time replay on the system clock; a test passes one driven by a fake clock.
    /// </param>
    public TelemetryService(
        Func<ITelemetrySource> sourceFactory,
        ILogger<TelemetryService>? logger = null,
        IEnumerable<IPacketObserver>? observers = null,
        Func<string, ReplayTelemetrySource>? replayFactory = null)
    {
        _sourceFactory = sourceFactory;
        _replayFactory = replayFactory ?? (path => new ReplayTelemetrySource(path));
        _logger = logger ?? NullLogger<TelemetryService>.Instance;
        _observers = observers?.ToArray() ?? [];
        _observerFailures = new int[_observers.Length];
    }

    /// <summary>Most recently received packet of the CURRENT session, or null if none has arrived yet (cleared on Start and Stop).</summary>
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

    /// <summary>
    /// Where the current session's packets come from. While the service is stopped this is the mode of the LAST start attempt
    /// (so a fault can still be told apart as "the recording failed" versus "the port is busy").
    /// </summary>
    public TelemetrySourceMode Mode => (TelemetrySourceMode)Volatile.Read(ref _mode);

    /// <summary>True while a recording is being played back (the loop is running and its source is a replay).</summary>
    public bool IsReplaying => IsRunning && Mode == TelemetrySourceMode.Replay && Replay is not null;

    /// <summary>
    /// The playback controls (pause, speed, restart, position) of the replay in progress; null when nothing is being replayed.
    /// </summary>
    public ReplayTelemetrySource? Replay => Volatile.Read(ref _replay);

    /// <summary>The file being replayed; null when nothing is.</summary>
    public string? ReplayPath => Volatile.Read(ref _replayPath);

    // Note: this service deliberately does NOT track "time since last packet" itself — that would
    // need its own clock, and ShellViewModel already needs an injected TimeProvider for
    // PacketsPerSecond. Keeping only ReceivedCount here lets ShellViewModel derive "how long since a
    // NEW packet arrived" from ONE clock (see its Refresh method), instead of two clocks
    // (this service's real one, and the view model's injected one) that could disagree in a test.

    /// <summary>
    /// Builds a fresh source from the factory and starts its background receive loop. A no-op if
    /// already running. Safe to call again after <see cref="StopAsync"/> — every call builds a
    /// brand-new source, so e.g. a freed UDP port can be rebound.
    /// </summary>
    public async Task StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StartCoreAsync(TelemetrySourceMode.Live, _sourceFactory).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Plays the recording at <paramref name="path"/> through the same loop, observers and screens as live data. Whatever was
    /// running (live listening or another replay) is stopped first, so the two never overlap; replaying the same file again
    /// restarts it as a fresh session. A file that is missing or not a recording surfaces as <see cref="Fault"/> (the open
    /// happens on the first read), exactly like a live source that fails mid-stream.
    /// </summary>
    public async Task StartReplayAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        AppLog.ReplayStarted(_logger, path);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StartCoreAsync(TelemetrySourceMode.Replay, () => _replayFactory(path), path).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartCoreAsync(TelemetrySourceMode mode, Func<ITelemetrySource> build, string? replayPath = null)
    {
        // Already listening live and asked to listen live: nothing to do. Every other combination (replay over live, live over
        // replay, replay over replay) must replace the running source, so it falls through to the stop below.
        if (IsRunning && mode == TelemetrySourceMode.Live && Mode == TelemetrySourceMode.Live)
        {
            return;
        }

        // A loop that ended on its own (a mid-stream fault) leaves its source, CTS and task behind:
        // RunLoopAsync only flips IsRunning. Release them before building a new source — for the
        // real UDP adapter the old socket still holds the port, so the new listener would otherwise
        // fail with "address already in use" and the user could never recover without a restart.
        await StopCoreAsync().ConfigureAwait(false);

        Volatile.Write(ref _fault, null);
        Volatile.Write(ref _mode, (int)mode);

        // Latest is also cleared on Stop; repeated here as a belt for a loop that faulted (no Stop ran).
        Volatile.Write(ref _latest, null);

        ITelemetrySource source;
        try
        {
            // The factory itself can throw SYNCHRONOUSLY here — e.g. UdpTelemetryListener's
            // constructor binds a socket immediately and throws SocketException if the port is
            // already taken. Catching it right here, instead of letting it propagate out of
            // StartAsync, is what "no exceptions escape to the UI thread unobserved" means for this
            // failure path: a bad Start becomes a Fault the view model can turn into a friendly
            // status message, not a crash of whatever awaited StartAsync.
            source = build();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _fault, ex);
            AppLog.StartFailed(_logger, ex);
            return;
        }

        _source = source;
        Volatile.Write(ref _replay, source as ReplayTelemetrySource);
        Volatile.Write(ref _replayPath, replayPath);
        var cts = new CancellationTokenSource();
        _cts = cts;
        Volatile.Write(ref _isRunning, true);

        // See the class remarks for why this loop must run off the calling thread. The token is read into a local
        // BEFORE Task.Run: the lambda runs later on a pool thread, and reading _cts there could see null if a
        // Stop ran in between (a NullReferenceException that would fault the loop task).
        var token = cts.Token;
        _loopTask = Task.Run(() => RunLoopAsync(source, token));
        AppLog.ServiceStarted(_logger);
    }

    private async Task RunLoopAsync(ITelemetrySource source, CancellationToken ct)
    {
        try
        {
            await foreach (var packet in source.ReadPacketsAsync(ct).ConfigureAwait(false))
            {
                NotifyObservers(packet);
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
            // recording it as Fault lets ShellViewModel show it instead.
            //
            // ORDER MATTERS: the loop is already dead here, so it is marked not-running BEFORE the fault is published.
            // A reader that sees Fault != null must never also see IsRunning == true: that pair is a state that never
            // existed (a faulted loop is not running), and it was a real race. The finally below used to be the only
            // place that cleared IsRunning, so for a moment (the fault write plus the log call) a thread polling
            // "Fault is not null" could read IsRunning == true. Volatile.Write is a release store, so a reader that
            // observes the fault (an acquire read) is guaranteed to observe the earlier IsRunning write too.
            Volatile.Write(ref _isRunning, false);
            Volatile.Write(ref _fault, ex);
            AppLog.LoopFaulted(_logger, ex);
        }
        finally
        {
            Volatile.Write(ref _isRunning, false);
        }
    }

    /// <summary>
    /// Hands one packet to every observer, each in its own try/catch. Without the guard, the first exception from any engine
    /// would propagate into <see cref="RunLoopAsync"/>'s catch, which treats it as a SOURCE failure: the receive loop would
    /// end and the whole app would show a socket-style error because, say, the lap tracker had a bug.
    /// <para>
    /// Logging is throttled per observer: a bug that fires on every packet would otherwise write ~60 lines a second. The
    /// first failure is logged, then one in every <see cref="ObserverLogEvery"/>.
    /// </para>
    /// </summary>
    private void NotifyObservers(TelemetryPacket packet)
    {
        for (var i = 0; i < _observers.Length; i++)
        {
            try
            {
                _observers[i].OnPacket(packet);
            }
            catch (Exception ex)
            {
                var failures = ++_observerFailures[i];
                if (failures == 1 || failures % ObserverLogEvery == 0)
                {
                    AppLog.ObserverFailed(_logger, ex, _observers[i].GetType().Name, failures);
                }
            }
        }
    }

    /// <summary>
    /// Cancels the background loop, waits for it to actually finish, then disposes the current
    /// source — which, for the real UDP adapter, releases the port so a later <see cref="StartAsync"/>
    /// can rebind it. A no-op if never started (or already stopped).
    /// </summary>
    public async Task StopAsync()
    {
        // Concurrent callers queue here and each runs after the previous one fully finished (see _gate).
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Must only be called while holding _gate.
    private async Task StopCoreAsync()
    {
        var cts = _cts;
        var loopTask = _loopTask;
        var source = _source;

        _cts = null;
        _loopTask = null;
        _source = null;
        Volatile.Write(ref _replay, null);
        Volatile.Write(ref _replayPath, null);

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

        // The session is over: drop its last packet, or a stopped dashboard keeps re-reading it as if it were live
        // (stale RPM/speed, a shift light stuck blinking). The loop has ended, so nothing writes Latest any more.
        // ReceivedCount is NOT reset: it is a lifetime counter and consumers derive rates from its deltas.
        Volatile.Write(ref _latest, null);

        cts.Dispose();
        Volatile.Write(ref _isRunning, false);
        AppLog.ServiceStopped(_logger, ReceivedCount);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
