using System.Buffers;

namespace ForzaTelemetry.Core.Recording;

/// <summary>
/// Streams raw datagrams into a <c>.fh6rec</c> recording (see <see cref="RecordingFormat"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Streaming I/O:</b> each record is appended as it arrives; nothing is accumulated in memory. A
/// <see cref="BufferedStream"/> sits in front of the destination so ~60 tiny (336-byte) writes per second
/// become a few large disk writes instead of 60 syscalls/s. The trade-off is that a crash can lose the
/// unflushed tail (at most one buffer, ~1 s of driving); the reader copes with the resulting half-written
/// last record (<see cref="SessionRecordingReader.IsTruncated"/>).
/// </para>
/// <para>
/// <b>Thread safety:</b> NOT thread-safe. There must be exactly one writer at a time (calls may come
/// from different threads, but never concurrently). <see cref="SessionRecorder"/> guarantees this with a
/// single background task that owns the writer.
/// </para>
/// </remarks>
public sealed class SessionRecordingWriter : IAsyncDisposable
{
    /// <summary>Default write-buffer size: 64 KiB, about 3 s of 60 Hz FH6 data.</summary>
    public const int DefaultBufferSize = 64 * 1024;

    private readonly Stream _destination;
    private readonly BufferedStream _buffered;
    private readonly bool _leaveOpen;

    private long _lastElapsedMicroseconds;
    private bool _disposed;
    private bool _faulted;

    private SessionRecordingWriter(Stream destination, bool leaveOpen, int bufferSize)
    {
        _destination = destination;
        _leaveOpen = leaveOpen;
        _buffered = new BufferedStream(destination, bufferSize);
    }

    /// <summary>Number of records written so far.</summary>
    public long RecordCount { get; private set; }

    /// <summary>Bytes handed to the stream so far, header included (buffered bytes count).</summary>
    public long BytesWritten { get; private set; }

    /// <summary>
    /// Creates a writer and writes the file header immediately, so even a recording with zero datagrams
    /// is a valid file.
    /// </summary>
    /// <param name="destination">A writable stream (a <see cref="FileStream"/> in the app, a <see cref="MemoryStream"/> in tests).</param>
    /// <param name="startedAtUtc">The moment the recording starts; record times are relative to it.</param>
    /// <param name="leaveOpen">When true, <see cref="DisposeAsync"/> flushes but does not close <paramref name="destination"/>.</param>
    /// <param name="bufferSize">Write-buffer size in bytes.</param>
    public static async ValueTask<SessionRecordingWriter> CreateAsync(
        Stream destination,
        DateTimeOffset startedAtUtc,
        bool leaveOpen = false,
        int bufferSize = DefaultBufferSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        }

        var writer = new SessionRecordingWriter(destination, leaveOpen, bufferSize);

        var header = new byte[RecordingFormat.HeaderSize];
        RecordingFormat.WriteHeader(header, startedAtUtc);
        await writer._buffered.WriteAsync(header, ct).ConfigureAwait(false);
        writer.BytesWritten = header.Length;
        return writer;
    }

    /// <summary>Creates (or truncates) a recording file at <paramref name="path"/> and returns a writer for it.</summary>
    /// <remarks>
    /// <c>FileOptions.Asynchronous</c> makes the OS do real async I/O instead of blocking a thread-pool
    /// thread; <c>bufferSize: 0</c> disables FileStream's own buffer because our <see cref="BufferedStream"/>
    /// already buffers (double buffering would only copy bytes twice).
    /// </remarks>
    public static ValueTask<SessionRecordingWriter> CreateFileAsync(
        string path, DateTimeOffset startedAtUtc, CancellationToken ct = default)
    {
        var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 0, FileOptions.Asynchronous);
        return CreateAsync(stream, startedAtUtc, leaveOpen: false, DefaultBufferSize, ct);
    }

    /// <summary>
    /// Appends one record.
    /// </summary>
    /// <param name="datagram">The raw datagram bytes (copied into the buffer; the caller may reuse its memory afterwards).</param>
    /// <param name="elapsed">Arrival time relative to the recording start. Must not be negative.</param>
    /// <param name="ct">Honored only BEFORE the record starts being written. Once the first byte is handed to the
    /// stream the write always runs to completion: abandoning it half way would leave a torn record in the middle
    /// of the file, which readers could not recover from.</param>
    /// <exception cref="InvalidOperationException">A previous write failed; the file may hold a torn record, so the writer refuses to append more.</exception>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> datagram, TimeSpan elapsed, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        if (_faulted)
        {
            throw new InvalidOperationException("A previous write failed; the recording can no longer be appended to.");
        }

        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time cannot be negative.");
        }

        if (datagram.Length > RecordingFormat.MaxPayloadLength)
        {
            throw new ArgumentException(
                $"A datagram cannot exceed {RecordingFormat.MaxPayloadLength} bytes (the UDP limit).", nameof(datagram));
        }

        // Timestamps must never go backwards: replay computes delays as differences, so a decreasing
        // value would mean "wait a negative time". Clamp instead of failing so one clock hiccup
        // does not abort a whole recording.
        var micros = Math.Max(RecordingFormat.ToMicroseconds(elapsed), _lastElapsedMicroseconds);
        _lastElapsedMicroseconds = micros;

        // Header + payload go out as ONE write from one pooled buffer, so the record is never split
        // across two awaits (where a cancellation or failure could tear it).
        var total = RecordingFormat.RecordHeaderSize + datagram.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            RecordingFormat.WriteRecordHeader(buffer, micros, datagram.Length);
            datagram.Span.CopyTo(buffer.AsSpan(RecordingFormat.RecordHeaderSize));

            try
            {
                await _buffered.WriteAsync(buffer.AsMemory(0, total), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                _faulted = true; // unknown how much reached the file
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        RecordCount++;
        BytesWritten += total;
    }

    /// <summary>Pushes buffered bytes to the destination (e.g. so a crash loses less).</summary>
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _buffered.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Flushes the buffer and closes the destination (unless created with <c>leaveOpen</c>).</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_leaveOpen)
        {
            // Disposing the BufferedStream would also close the caller's stream, so only flush.
            await _buffered.FlushAsync().ConfigureAwait(false);
            await _destination.FlushAsync().ConfigureAwait(false);
        }
        else
        {
            // BufferedStream.DisposeAsync flushes, then disposes the inner stream.
            await _buffered.DisposeAsync().ConfigureAwait(false);
        }
    }
}
