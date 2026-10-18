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
        lock (_gate)
        {
            if (_paused)
            {
                _paused = false;
                _resumeSignal.TrySetResult();
            }
        }
    }

    /// <summary>Restarts playback from the first record, interrupting any wait. Keeps the paused state.</summary>
    public void SeekToStart()
    {
        lock (_gate)
        {
            _restartRequested = true;
            Interlocked.Exchange(ref _positionTicks, 0);
            _interrupt.Cancel();
        }
    }

    /// <summary>
    /// Delivers the recording's datagrams in order, paced by the recorded timing and the current
    /// <see cref="Speed"/>. Ends when the recording ends (a truncated tail just ends it earlier) or when
    /// <paramref name="ct"/> is cancelled (cancellation ends the enumeration cleanly, matching the live UDP adapter).
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
            var info = await RecordingFileInfo.ScanAsync(_openRecording(), leaveOpen: false, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _durationTicks, info.Duration.Ticks);

            var restart = true;
            while (restart && !ct.IsCancellationRequested)
            {
                restart = false;
                ConsumeRestart();

                await using var reader = await SessionRecordingReader.OpenAsync(_openRecording(), leaveOpen: false, ct).ConfigureAwait(false);

                var previous = TimeSpan.Zero;
                var first = true;

                await foreach (var record in reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var gap = first ? TimeSpan.Zero : record.Elapsed - previous;
                    first = false;
                    previous = record.Elapsed;

                    var outcome = await PaceAsync(gap, ct).ConfigureAwait(false);
                    if (outcome == PaceOutcome.Cancelled)
                    {
                        yield break;
                    }

                    if (outcome == PaceOutcome.Restart)
                    {
                        restart = true;
                        break;
                    }

                    Interlocked.Exchange(ref _positionTicks, record.Elapsed.Ticks);
                    yield return record.Payload;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _enumerating, 0);
        }
    }

    private enum PaceOutcome { Ready, Restart, Cancelled }

    /// <summary>Waits for pause release and for the (speed-scaled) gap. Reports if a seek or cancellation cut it short.</summary>
    private async Task<PaceOutcome> PaceAsync(TimeSpan gap, CancellationToken ct)
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
            return PaceOutcome.Restart;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, interrupt);

        try
        {
            if (!await WaitWhilePausedAsync(linked.Token).ConfigureAwait(false))
            {
                return PaceOutcome.Restart;
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
                    return PaceOutcome.Restart;
                }
            }

            return IsRestartRequested() ? PaceOutcome.Restart : PaceOutcome.Ready;
        }
        catch (OperationCanceledException)
        {
            // Two different tokens feed `linked`: the caller's (stop) and ours (seek). Tell them apart.
            return ct.IsCancellationRequested ? PaceOutcome.Cancelled : PaceOutcome.Restart;
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
                _interrupt.Dispose();
                _interrupt = new CancellationTokenSource();
            }
        }
    }

    // RunContinuationsAsynchronously: Resume() is called from the UI thread; without this flag the
    // replay loop's continuation would run synchronously inside that call.
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static FileStream OpenFile(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
}
