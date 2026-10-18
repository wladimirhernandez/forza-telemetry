using System.Runtime.CompilerServices;

namespace ForzaTelemetry.Core.Recording;

/// <summary>
/// Plays a recording back as a stream of raw datagrams, honoring the recorded timing.
/// This is the datagram-level building block of <see cref="ReplayTelemetrySource"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Timing model:</b> before delivering record <c>i</c> the source waits
/// <c>(elapsed[i] - elapsed[i-1]) / Speed</c>. Waiting for the DIFFERENCE (not until an absolute
/// deadline) means pausing, changing speed and seeking never need clock arithmetic, and a slow consumer
/// simply slows the replay rather than causing a burst of catch-up. The delay uses an injected
/// <see cref="TimeProvider"/> (<c>Task.Delay(delay, timeProvider, ct)</c>) so tests drive time manually
/// with <c>FakeTimeProvider</c>. The first record is delivered immediately (any leading silence in the
/// recording is skipped).
/// </para>
/// <para>
/// <b>Controls</b> (<see cref="Speed"/>, <see cref="Pause"/>, <see cref="Resume"/>, <see cref="SeekToStart"/>)
/// are thread-safe and meant to be called from the UI while another thread enumerates. A speed change
/// applies from the next gap (a gap is ~16 ms, so this is imperceptible). A pause takes effect before the
/// next record is delivered. <see cref="SeekToStart"/> interrupts any wait and restarts the recording from its
/// first record.
/// </para>
/// <para>
/// <b>End of recording:</b> by default (<see cref="HoldAtEnd"/> = true) the enumeration does NOT finish when
/// the last record has been delivered. It stays open in the "finished" state (<see cref="IsFinished"/>) so
/// that <see cref="SeekToStart"/> restarts playback inside the SAME enumeration, which is what a player
/// UI wants ("replay" button after the end). It ends only when the caller cancels. With
/// <c>HoldAtEnd = false</c> the enumeration simply ends after the last record (useful for batch
/// processing and tests).
/// </para>
/// <para>
/// <b>Cancellation</b> at any point (initial scan, reading, waiting, holding at the end) ends the
/// enumeration cleanly without an exception, matching the live UDP adapter.
/// </para>
/// <para>
/// Only ONE enumeration at a time is supported. The recording is re-opened (via the supplied factory)
/// for the duration scan and for each restart, so it streams from disk and never loads fully into memory.
/// </para>
/// </remarks>
public sealed class ReplayDatagramSource
{
    private readonly Func<Stream> _openRecording;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    private double _speed = ReplaySpeed.Normal;
    private bool _paused;
    private TaskCompletionSource _resumeSignal = NewSignal();
    private bool _restartRequested;
    private CancellationTokenSource _interrupt = new();
    private long _positionTicks;
    private long _durationTicks;
    private int _enumerating;
    private bool _finished;
    private volatile bool _holdAtEnd = true;

    /// <param name="openRecording">
    /// Opens a FRESH readable stream positioned at the start of the recording each time it is called
    /// (e.g. <c>() =&gt; File.OpenRead(path)</c> or <c>() =&gt; new MemoryStream(bytes)</c>). The replay disposes each stream it opens.
    /// </param>
    /// <param name="timeProvider">Clock used for waiting; defaults to <see cref="TimeProvider.System"/>.</param>
    public ReplayDatagramSource(Func<Stream> openRecording, TimeProvider? timeProvider = null)
    {
        _openRecording = openRecording ?? throw new ArgumentNullException(nameof(openRecording));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Convenience constructor that replays the file at <paramref name="path"/>.</summary>
    public ReplayDatagramSource(string path, TimeProvider? timeProvider = null)
        : this(() => OpenFile(path), timeProvider)
    {
    }

    /// <summary>
    /// Playback speed as a multiple of real time; see <see cref="ReplaySpeed"/> for named values
    /// (<see cref="ReplaySpeed.Max"/> = no delay). Must be greater than zero.
    /// </summary>
    public double Speed
    {
        get { lock (_gate) { return _speed; } }
        set
        {
            if (double.IsNaN(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Speed must be greater than zero.");
            }

            lock (_gate)
            {
                _speed = value;
            }
        }
    }

    /// <summary>
    /// When true (default) the enumeration stays open after the last record until cancelled or sought, see the
    /// class remarks. When false it ends after the last record. Read when the end is reached.
    /// </summary>
    public bool HoldAtEnd
    {
        get => _holdAtEnd;
        set => _holdAtEnd = value;
    }

    /// <summary>True once the last record has been delivered and playback is waiting for a seek or cancellation.</summary>
    public bool IsFinished
    {
        get { lock (_gate) { return _finished; } }
    }

    /// <summary>Whether delivery is currently held by <see cref="Pause"/>.</summary>
    public bool IsPaused
    {
        get { lock (_gate) { return _paused; } }
    }

    /// <summary>Recording time of the last delivered record (zero before the first, and right after a seek).</summary>
    public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref _positionTicks));

    /// <summary>
    /// Total length of the recording (elapsed time of its last record). Zero until the enumeration has started
    /// and finished its initial scan.
    /// </summary>
    public TimeSpan Duration => TimeSpan.FromTicks(Interlocked.Read(ref _durationTicks));

    /// <summary>Holds delivery before the next record until <see cref="Resume"/>.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!_paused)
            {
                _paused = true;
                _resumeSignal = NewSignal();
            }
        }
    }

    /// <summary>Releases a <see cref="Pause"/>. The held record is delivered immediately (no catch-up burst).</summary>
    public void Resume()
    {
        TaskCompletionSource? signal = null;
        lock (_gate)
        {
            if (_paused)
            {
                _paused = false;
                signal = _resumeSignal;
            }
        }

        // Completed outside the lock (and the signal runs continuations asynchronously) so no waiter's
        // code ever executes inside our critical section or inline on the caller's (UI) thread.
        signal?.TrySetResult();
    }

    /// <summary>
    /// Restarts playback from the first record, interrupting any wait or the end-of-recording hold. Keeps
    /// the paused state. Only effective while an enumeration is running (it is what plays the recording).
    /// </summary>
    public void SeekToStart()
    {
        CancellationTokenSource interrupt;
        lock (_gate)
        {
            _restartRequested = true;
            _finished = false;
            Interlocked.Exchange(ref _positionTicks, 0);
            interrupt = _interrupt;
        }

        // Cancel() runs registered callbacks inline on THIS thread. Doing it outside the lock keeps those
        // callbacks from executing while we hold _gate (and from deadlocking on it).
        interrupt.Cancel();
    }

    /// <summary>
    /// Delivers the recording's datagrams in order, paced by the recorded timing and the current
    /// <see cref="Speed"/>. See the class remarks for end-of-recording and cancellation behavior.
    /// </summary>
    /// <exception cref="InvalidRecordingException">The stream is not a (supported) recording; thrown on the first MoveNext.</exception>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadDatagramsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _enumerating, 1) == 1)
        {
            throw new InvalidOperationException("This replay source is already being enumerated.");
        }

        try
        {
            // One scan up front gives the UI its total duration (and validates the file before playback).
            var info = await TryScanAsync(ct).ConfigureAwait(false);
            if (info is null)
            {
                yield break; // cancelled
            }

            Interlocked.Exchange(ref _durationTicks, info.Duration.Ticks);

            var restart = true;
            while (restart)
            {
                if (ct.IsCancellationRequested)
                {
                    yield break;
                }

                restart = false;
                ConsumeRestart();

                var reader = await TryOpenAsync(ct).ConfigureAwait(false);
                if (reader is null)
                {
                    yield break; // cancelled
                }

                await using (reader.ConfigureAwait(false))
                {
                    // Manual enumerator (instead of await foreach) so a cancellation raised by the READ itself
                    // can be caught: a yield may not sit inside a try that has a catch.
                    await using var records = reader.ReadAllAsync(ct).GetAsyncEnumerator(ct);

                    var previous = TimeSpan.Zero;
                    var first = true;

                    while (true)
                    {
                        var hasNext = await MoveNextOrCancelledAsync(records, ct).ConfigureAwait(false);
                        if (hasNext is null)
                        {
                            yield break; // cancelled
                        }

                        if (!hasNext.Value)
                        {
                            break; // end of recording
                        }

                        var record = records.Current;
                        var gap = first ? TimeSpan.Zero : record.Elapsed - previous;
                        first = false;
                        previous = record.Elapsed;

                        var outcome = await PaceAsync(gap, ct).ConfigureAwait(false);
                        if (outcome == WaitOutcome.Cancelled)
                        {
                            yield break;
                        }

                        if (outcome == WaitOutcome.Restart)
                        {
                            restart = true;
                            break;
                        }

                        Interlocked.Exchange(ref _positionTicks, record.Elapsed.Ticks);
                        yield return record.Payload;
                    }
                }

                if (restart || !_holdAtEnd)
                {
                    continue; // a seek interrupted playback, or the caller wants the enumeration to end
                }

                // Reached the end: stay "Finished" until the player seeks or the caller stops.
                lock (_gate)
                {
                    if (!_restartRequested)
                    {
                        _finished = true;
                    }
                }

                restart = await HoldAtEndAsync(ct).ConfigureAwait(false) == WaitOutcome.Restart;
            }
        }
        finally
        {
            lock (_gate)
            {
                _finished = false;
            }

            Volatile.Write(ref _enumerating, 0);
        }
    }

    private enum WaitOutcome { Ready, Restart, Cancelled }

    /// <summary>Waits for pause release and for the (speed-scaled) gap. Reports if a seek or cancellation cut it short.</summary>
    private async Task<WaitOutcome> PaceAsync(TimeSpan gap, CancellationToken ct)
    {
        CancellationToken interrupt;
        double speed;
        lock (_gate)
        {
            interrupt = _interrupt.Token;
            speed = _speed;
        }

        if (IsRestartRequested())
        {
            return WaitOutcome.Restart;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, interrupt);

        try
        {
            if (!await WaitWhilePausedAsync(linked.Token).ConfigureAwait(false))
            {
                return WaitOutcome.Restart;
            }

            if (!double.IsPositiveInfinity(speed) && gap > TimeSpan.Zero)
            {
                var delay = TimeSpan.FromTicks((long)(gap.Ticks / speed));
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, linked.Token).ConfigureAwait(false);
                }

                // A pause pressed DURING the wait must still hold this record back.
                if (!await WaitWhilePausedAsync(linked.Token).ConfigureAwait(false))
                {
                    return WaitOutcome.Restart;
                }
            }

            return IsRestartRequested() ? WaitOutcome.Restart : WaitOutcome.Ready;
        }
        catch (OperationCanceledException)
        {
            // Two different tokens feed `linked`: the caller's (stop) and ours (seek). Tell them apart.
            return ct.IsCancellationRequested ? WaitOutcome.Cancelled : WaitOutcome.Restart;
        }
    }

    /// <summary>Blocks at the end of the recording until a seek (Restart) or cancellation.</summary>
    private async Task<WaitOutcome> HoldAtEndAsync(CancellationToken ct)
    {
        CancellationToken interrupt;
        lock (_gate)
        {
            interrupt = _interrupt.Token;
        }

        if (IsRestartRequested())
        {
            return WaitOutcome.Restart;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, interrupt);
        try
        {
            // An infinite delay without a TimeProvider creates no timer: it just waits for the token.
            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
            return WaitOutcome.Ready; // unreachable: the delay never completes on its own
        }
        catch (OperationCanceledException)
        {
            return ct.IsCancellationRequested ? WaitOutcome.Cancelled : WaitOutcome.Restart;
        }
    }

    private async Task<bool> WaitWhilePausedAsync(CancellationToken token)
    {
        while (true)
        {
            Task? gate;
            lock (_gate)
            {
                gate = _paused ? _resumeSignal.Task : null;
            }

            if (gate is null)
            {
                return true;
            }

            await gate.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private bool IsRestartRequested()
    {
        lock (_gate)
        {
            return _restartRequested;
        }
    }

    /// <summary>Clears the restart request and replaces the (already cancelled) interrupt source with a fresh one.</summary>
    private void ConsumeRestart()
    {
        lock (_gate)
        {
            if (_restartRequested || _interrupt.IsCancellationRequested)
            {
                _restartRequested = false;

                // Deliberately NOT disposed: a concurrent SeekToStart may still hold this instance and
                // call Cancel() on it outside the lock; Cancel on a disposed source would throw. It has no
                // timer or handle, so letting the GC collect it costs nothing.
                _interrupt = new CancellationTokenSource();
            }
        }
    }

    private async Task<RecordingFileInfo?> TryScanAsync(CancellationToken ct)
    {
        try
        {
            return await RecordingFileInfo.ScanAsync(_openRecording(), leaveOpen: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<SessionRecordingReader?> TryOpenAsync(CancellationToken ct)
    {
        try
        {
            return await SessionRecordingReader.OpenAsync(_openRecording(), leaveOpen: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>MoveNext that reports cancellation as null instead of throwing.</summary>
    private static async ValueTask<bool?> MoveNextOrCancelledAsync(IAsyncEnumerator<RecordedDatagram> records, CancellationToken ct)
    {
        try
        {
            return await records.MoveNextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // RunContinuationsAsynchronously: Resume() is called from the UI thread; without this flag the
    // replay loop's continuation would run synchronously inside that call.
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static FileStream OpenFile(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
}
