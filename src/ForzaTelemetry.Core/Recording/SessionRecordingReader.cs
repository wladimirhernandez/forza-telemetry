using System.Runtime.CompilerServices;

namespace ForzaTelemetry.Core.Recording;

/// <summary>
/// Reads a <c>.fh6rec</c> recording as a stream of <see cref="RecordedDatagram"/>s, one record at a time
/// (never loading the whole file).
/// </summary>
/// <remarks>
/// <para>
/// <b>Crash tolerance:</b> if the app dies mid-write the file ends in the middle of a record. Reading such
/// a file must still give you everything before that point, so a short read of a record header or
/// payload (or an impossible length) ends the enumeration cleanly and sets <see cref="IsTruncated"/>
/// instead of throwing. Only a bad HEADER throws, because then there is nothing meaningful to read.
/// </para>
/// <para>Not thread-safe; enumerate once from one consumer.</para>
/// </remarks>
public sealed class SessionRecordingReader : IAsyncDisposable
{
    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private bool _enumerated;

    private SessionRecordingReader(Stream source, bool leaveOpen, RecordingHeader header)
    {
        _source = source;
        _leaveOpen = leaveOpen;
        Header = header;
    }

    /// <summary>The validated file header.</summary>
    public RecordingHeader Header { get; }

    /// <summary>
    /// True when the last record was cut off (or its length was impossible), i.e. the file was not
    /// closed cleanly. Only meaningful after <see cref="ReadAllAsync"/> has been enumerated to its end.
    /// </summary>
    public bool IsTruncated { get; private set; }

    /// <summary>
    /// Reads and validates the header. Throws <see cref="InvalidRecordingException"/> for a wrong or cut-off
    /// header and <see cref="UnsupportedRecordingVersionException"/> for a newer format version. For a
    /// non-throwing check use <see cref="RecordingFormat.TryReadHeader"/>.
    /// </summary>
    /// <param name="source">A readable stream positioned at the start of the recording.</param>
    /// <param name="leaveOpen">When true, disposing the reader does not close <paramref name="source"/>.</param>
    public static async ValueTask<SessionRecordingReader> OpenAsync(
        Stream source, bool leaveOpen = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            var buffer = new byte[RecordingFormat.HeaderSize];
            var read = await source.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);

            if (!RecordingFormat.TryReadHeader(buffer.AsSpan(0, read), out var header, out var status))
            {
                throw status switch
                {
                    RecordingHeaderStatus.TooShort => new InvalidRecordingException(
                        "The file is too short to be a recording (the header is incomplete)."),
                    RecordingHeaderStatus.BadMagic => new InvalidRecordingException(
                        "This is not a Forza telemetry recording (bad magic bytes)."),
                    RecordingHeaderStatus.UnsupportedVersion => new UnsupportedRecordingVersionException(
                        header.Version, RecordingFormat.CurrentVersion),
                    _ => new InvalidRecordingException("The recording header is corrupt."),
                };
            }

            return new SessionRecordingReader(source, leaveOpen, header);
        }
        catch
        {
            // We took ownership of the stream; do not leak the handle when validation fails.
            if (!leaveOpen)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Opens a recording file for sequential reading and validates its header.</summary>
    public static ValueTask<SessionRecordingReader> OpenFileAsync(string path, CancellationToken ct = default)
    {
        // FileShare.ReadWrite: allow listing/replaying a file another process (or a still-running
        // recorder) has open. SequentialScan hints the OS to read ahead.
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return OpenAsync(stream, leaveOpen: false, ct);
    }

    /// <summary>Yields every complete record in file order. See the class remarks for truncation behavior.</summary>
    public async IAsyncEnumerable<RecordedDatagram> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_enumerated)
        {
            throw new InvalidOperationException("A recording can only be enumerated once; open it again to re-read.");
        }

        _enumerated = true;
        var recordHeader = new byte[RecordingFormat.RecordHeaderSize];

        while (true)
        {
            var read = await _source.ReadAtLeastAsync(recordHeader, recordHeader.Length, throwOnEndOfStream: false, ct)
                .ConfigureAwait(false);

            if (read == 0)
            {
                yield break; // clean end of file exactly on a record boundary
            }

            if (read < recordHeader.Length)
            {
                IsTruncated = true;
                yield break;
            }

            RecordingFormat.ReadRecordHeader(recordHeader, out var micros, out var length);

            if (length < 0 || length > RecordingFormat.MaxPayloadLength || micros < 0)
            {
                // Damaged length/time: we cannot know where the next record starts, so stop here.
                IsTruncated = true;
                yield break;
            }

            var payload = new byte[length];
            var payloadRead = await _source.ReadAtLeastAsync(payload, length, throwOnEndOfStream: false, ct)
                .ConfigureAwait(false);

            if (payloadRead < length)
            {
                IsTruncated = true;
                yield break;
            }

            yield return new RecordedDatagram(RecordingFormat.FromMicroseconds(micros), payload);
        }
    }

    /// <summary>Closes the underlying stream unless the reader was opened with <c>leaveOpen</c>.</summary>
    public ValueTask DisposeAsync() => _leaveOpen ? ValueTask.CompletedTask : _source.DisposeAsync();
}
