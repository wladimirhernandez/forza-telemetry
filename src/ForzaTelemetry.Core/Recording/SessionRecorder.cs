using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace ForzaTelemetry.Core.Recording;

/// <summary>
/// Records raw datagrams to a <c>.fh6rec</c> stream WITHOUT ever blocking the UDP receive loop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a channel + background task:</b> <see cref="OnDatagram"/> runs inside the receive loop. If it wrote
/// to disk directly, one slow write (antivirus scan, busy USB drive) would stall the socket reads and the
/// game's packets would pile up or be dropped by the OS, hurting the LIVE dashboard too. Instead
/// <see cref="OnDatagram"/> only copies the bytes and enqueues them into a bounded
/// <see cref="Channel{T}"/> (a few hundred nanoseconds); one background task drains the queue into a
/// <see cref="SessionRecordingWriter"/>.
/// </para>
/// <para>
/// <b>Why bounded + drop-on-full:</b> an unbounded queue behind a stalled disk would grow without limit
/// (60 packets/s forever). When the queue is full the newest datagram is dropped and counted in
/// <see cref="DroppedCount"/>; losing a few packets of a recording is far better than lagging the
/// live view or exhausting memory. The default capacity (4096, ~68 s of data, ~1.4 MB) means a drop needs a
/// disk stall of more than a minute.
/// </para>
/// <para>
/// <b>Timing:</b> the elapsed time is captured in <see cref="OnDatagram"/> (arrival time), not when the writer
/// task gets to it, so queueing delay does not distort the recorded timeline. Time comes from an injected
/// <see cref="TimeProvider"/> so tests are deterministic.
/// </para>
/// <para>
/// <b>Threading:</b> <see cref="OnDatagram"/> is meant for ONE thread (the receive loop);
/// <see cref="StartAsync(Stream, bool, CancellationToken)"/>, <see cref="StopAsync"/> and the counters may be used
/// from any other thread (typically the UI). A recorder can be started again after being stopped; counters
/// reset on start.
/// </para>
/// </remarks>
public sealed class SessionRecorder : IDatagramObserver, IAsyncDisposable
{
    /// <summary>Default queue capacity, in datagrams.</summary>
    public const int DefaultQueueCapacity = 4096;

    private readonly TimeProvider _timeProvider;
    private readonly int _queueCapacity;
    private readonly int _bufferSize;
    private readonly Lock _gate = new();

    private Session? _session; // volatile semantics via Volatile.Read/Write
    private Exception? _lastFault;
    private bool _starting;
    private long _datagramCount;
    private long _bytesRecorded;
    private long _droppedCount;
    private long _stoppedDurationTicks = -1;
    private long _startTimestamp;

    /// <param name="timeProvider">Clock used for arrival times; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="queueCapacity">Maximum queued datagrams before new ones are dropped.</param>
    /// <param name="bufferSize">Write-buffer size handed to the <see cref="SessionRecordingWriter"/>.</param>
    public SessionRecorder(
        TimeProvider? timeProvider = null,
        int queueCapacity = DefaultQueueCapacity,
        int bufferSize = SessionRecordingWriter.DefaultBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _queueCapacity = queueCapacity;
        _bufferSize = bufferSize;
    }

    /// <summary>True between a successful <c>StartAsync</c> and <see cref="StopAsync"/>.</summary>
    public bool IsRecording => Volatile.Read(ref _session) is not null;

    /// <summary>Datagrams accepted into the queue since the last start (excludes dropped ones).</summary>
    public long DatagramCount => Interlocked.Read(ref _datagramCount);

    /// <summary>Payload bytes of the accepted datagrams (file overhead of 12 bytes/record and the header are not included).</summary>
    public long BytesRecorded => Interlocked.Read(ref _bytesRecorded);

    /// <summary>Datagrams discarded because the queue was full or the writer had failed.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>Recording length: live while recording, frozen at the stop instant afterwards, zero before the first start.</summary>
    public TimeSpan Duration
    {
        get
        {
            var frozen = Interlocked.Read(ref _stoppedDurationTicks);
            if (frozen >= 0)
            {
                return TimeSpan.FromTicks(frozen);
            }

            return IsRecording ? _timeProvider.GetElapsedTime(Interlocked.Read(ref _startTimestamp)) : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// The I/O error that stopped the writer, if any. Datagrams after it are counted as dropped. Kept after
    /// <see cref="StopAsync"/> until the next start, so a UI can still show why the last recording failed.
    /// </summary>
    public Exception? Fault => Volatile.Read(ref _session)?.Fault ?? Volatile.Read(ref _lastFault);

    /// <summary>
    /// Starts recording to a file. The caller picks the path and ensures its folder exists.
    /// </summary>
    /// <param name="path">Destination file.</param>
    /// <param name="overwrite">
    /// When false (default) an existing file is never touched and an <see cref="IOException"/> is thrown:
    /// a recording is irreplaceable (a drive cannot be repeated), so overwriting must be a deliberate choice.
    /// </param>
    /// <exception cref="InvalidOperationException">Already recording (checked BEFORE the file is opened, so nothing is truncated).</exception>
    public async Task StartAsync(string path, bool overwrite = false, CancellationToken ct = default)
    {
        Reserve();
        try
        {
            var stream = new FileStream(
                path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                bufferSize: 0, FileOptions.Asynchronous);
            try
            {
                await StartCoreAsync(stream, leaveOpen: false, ct).ConfigureAwait(false);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            Release();
        }
    }

    /// <summary>Starts recording into <paramref name="destination"/>. Writes the file header before returning.</summary>
    /// <exception cref="InvalidOperationException">Already recording.</exception>
    public async Task StartAsync(Stream destination, bool leaveOpen = false, CancellationToken ct = default)
    {
        Reserve();
        try
        {
            await StartCoreAsync(destination, leaveOpen, ct).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    // Claims the "starting" slot synchronously so two concurrent starts cannot both proceed.
    private void Reserve()
    {
        lock (_gate)
        {
            if (_session is not null || _starting)
            {
                throw new InvalidOperationException("The recorder is already recording.");
            }

            _starting = true;
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            _starting = false;
        }
    }

    private async Task StartCoreAsync(Stream destination, bool leaveOpen, CancellationToken ct)
    {
        var startTimestamp = _timeProvider.GetTimestamp();
        var writer = await SessionRecordingWriter
            .CreateAsync(destination, _timeProvider.GetUtcNow(), leaveOpen, _bufferSize, ct)
            .ConfigureAwait(false);

        Interlocked.Exchange(ref _datagramCount, 0);
        Interlocked.Exchange(ref _bytesRecorded, 0);
        Interlocked.Exchange(ref _droppedCount, 0);
        Interlocked.Exchange(ref _stoppedDurationTicks, -1);
        Interlocked.Exchange(ref _startTimestamp, startTimestamp);
        Volatile.Write(ref _lastFault, null);

        Volatile.Write(ref _session, new Session(writer, startTimestamp, _queueCapacity));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Copies the bytes into a pooled buffer (the caller's buffer may be reused) and enqueues; never blocks
    /// and never throws. Pooling avoids allocating ~336 bytes sixty times a second, which would otherwise
    /// feed the garbage collector from the very loop that must stay smooth.
    /// </remarks>
    public void OnDatagram(ReadOnlyMemory<byte> datagram)
    {
        var session = Volatile.Read(ref _session);
        if (session is null)
        {
            return; // not recording: observing is free
        }

        // Writer already failed: do not even copy, the datagram cannot be stored.
        if (session.Fault is not null)
        {
            Interlocked.Increment(ref _droppedCount);
            return;
        }

        // Arrival time is taken NOW, on the receive thread, not by the writer task.
        var elapsed = _timeProvider.GetElapsedTime(session.StartTimestamp);

        var buffer = ArrayPool<byte>.Shared.Rent(datagram.Length);
        datagram.Span.CopyTo(buffer);

        if (session.Channel.Writer.TryWrite(new QueuedDatagram(elapsed, buffer, datagram.Length)))
        {
            Interlocked.Increment(ref _datagramCount);
            Interlocked.Add(ref _bytesRecorded, datagram.Length);
        }
        else
        {
            ArrayPool<byte>.Shared.Return(buffer); // never queued: nobody else will return it
            Interlocked.Increment(ref _droppedCount);
        }
    }

    /// <summary>
    /// Stops accepting datagrams, waits until everything already queued is on disk, flushes and closes the
    /// file. Does nothing when not recording. Rethrows the I/O error if the writer had failed.
    /// </summary>
    public async Task StopAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        // Freeze the duration at the stop request, not after the (possibly slow) drain.
        Interlocked.Exchange(ref _stoppedDurationTicks, _timeProvider.GetElapsedTime(session.StartTimestamp).Ticks);

        session.Channel.Writer.TryComplete();
        await session.WriterTask.ConfigureAwait(false);

        Exception? closeError = null;
        try
        {
            await session.Writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            closeError = ex;
        }

        var error = session.Fault ?? closeError;
        if (error is not null)
        {
            Volatile.Write(ref _lastFault, error);
            ExceptionDispatchInfo.Throw(error);
        }
    }

    /// <summary>Stops the recording if one is running. Never throws (use <see cref="StopAsync"/> to observe errors).</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already surfaced through Fault / a previous StopAsync; disposal must not throw.
        }
    }

    /// <summary>A queued datagram; <c>Buffer</c> is rented from the pool and only its first <c>Length</c> bytes are valid.</summary>
    private readonly record struct QueuedDatagram(TimeSpan Elapsed, byte[] Buffer, int Length);

    /// <summary>State of one start..stop cycle: its queue, writer and the task that links them.</summary>
    private sealed class Session
    {
        private Exception? _fault;

        public Session(SessionRecordingWriter writer, long startTimestamp, int capacity)
        {
            Writer = writer;
            StartTimestamp = startTimestamp;
            Channel = System.Threading.Channels.Channel.CreateBounded<QueuedDatagram>(
                new BoundedChannelOptions(capacity)
                {
                    // Wait + TryWrite: TryWrite returns false when full (we drop), instead of
                    // silently overwriting older data like the DropOldest modes would.
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                });

            // Task.Run so the loop never runs on (or captures) the caller's context.
            WriterTask = Task.Run(DrainAsync);
        }

        public SessionRecordingWriter Writer { get; }

        public long StartTimestamp { get; }

        public Channel<QueuedDatagram> Channel { get; }

        public Task WriterTask { get; }

        public Exception? Fault => Volatile.Read(ref _fault);

        private async Task DrainAsync()
        {
            try
            {
                await foreach (var item in Channel.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    try
                    {
                        await Writer.WriteAsync(item.Buffer.AsMemory(0, item.Length), item.Elapsed).ConfigureAwait(false);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(item.Buffer);
                    }
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _fault, ex);

                // Completing with the error makes further TryWrite calls return false, so the receive
                // loop keeps running and the lost datagrams are counted as dropped.
                Channel.Writer.TryComplete(ex);

                // Give back the buffers of everything still queued; nothing will ever write them.
                while (Channel.Reader.TryRead(out var abandoned))
                {
                    ArrayPool<byte>.Shared.Return(abandoned.Buffer);
                }
            }
        }
    }
}
