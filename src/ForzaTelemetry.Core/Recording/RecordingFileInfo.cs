namespace ForzaTelemetry.Core.Recording;

/// <summary>
/// Summary metadata of a recording, for a "Sessions" list: when it was made, how long it is, how many
/// packets it holds and which cars appear in it.
/// </summary>
/// <param name="StartedAtUtc">From the header.</param>
/// <param name="Duration">Elapsed time of the last complete record (zero for an empty recording).</param>
/// <param name="PacketCount">Number of complete records (every datagram, including ones that do not parse).</param>
/// <param name="CarOrdinals">Distinct car ordinals seen in packets with <see cref="TelemetryPacket.IsRaceOn"/> true, ascending.
/// Menu/garage packets are ignored because their ordinal is not necessarily the car being driven.</param>
/// <param name="FileSizeBytes">Total size of the stream when it is seekable, otherwise 0.</param>
/// <param name="IsTruncated">The file ended in a half-written record (the app crashed while recording).</param>
public sealed record RecordingFileInfo(
    DateTimeOffset StartedAtUtc,
    TimeSpan Duration,
    long PacketCount,
    IReadOnlyList<int> CarOrdinals,
    long FileSizeBytes,
    bool IsTruncated)
{
    /// <summary>
    /// Scans a recording once, front to back, and gathers its metadata. Cost is linear in file size but
    /// allocation-light; run it off the UI thread for big files.
    /// </summary>
    /// <remarks>
    /// WHY not store this in the header: the header is written first, when none of it is known, and
    /// rewriting it on stop would not survive a crash. Scanning keeps the format append-only and the
    /// numbers always truthful, even for crashed recordings.
    /// </remarks>
    /// <exception cref="InvalidRecordingException">Not a recording, or a newer version.</exception>
    public static async Task<RecordingFileInfo> ScanAsync(
        Stream source, bool leaveOpen = false, CancellationToken ct = default)
    {
        var size = source.CanSeek ? source.Length : 0;

        await using var reader = await SessionRecordingReader.OpenAsync(source, leaveOpen, ct).ConfigureAwait(false);

        var ordinals = new SortedSet<int>();
        var duration = TimeSpan.Zero;
        long count = 0;

        await foreach (var record in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            count++;
            duration = record.Elapsed;

            if (TelemetryParser.TryParse(record.Payload.Span, out var packet) && packet.IsRaceOn)
            {
                ordinals.Add(packet.CarOrdinal);
            }
        }

        return new RecordingFileInfo(
            reader.Header.StartedAtUtc, duration, count, ordinals.ToArray(), size, reader.IsTruncated);
    }

    /// <summary>Scans the recording file at <paramref name="path"/>.</summary>
    public static async Task<RecordingFileInfo> ScanFileAsync(string path, CancellationToken ct = default)
    {
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ScanAsync(stream, leaveOpen: false, ct).ConfigureAwait(false);
    }
}
