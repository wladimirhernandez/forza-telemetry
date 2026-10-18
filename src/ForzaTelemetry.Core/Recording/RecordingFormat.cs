using System.Buffers.Binary;

namespace ForzaTelemetry.Core.Recording;

/// <summary>Why a header could not be accepted by <see cref="RecordingFormat.TryReadHeader"/>.</summary>
public enum RecordingHeaderStatus
{
    /// <summary>The header is valid and its version is supported.</summary>
    Ok,

    /// <summary>Fewer than <see cref="RecordingFormat.HeaderSize"/> bytes were available (empty or cut-off file).</summary>
    TooShort,

    /// <summary>The first six bytes are not "FH6REC": this is not a recording at all.</summary>
    BadMagic,

    /// <summary>A valid recording, but written by a NEWER app version than this reader understands.</summary>
    UnsupportedVersion,

    /// <summary>The magic is fine but a field holds an impossible value (version 0, or a start time outside the DateTime range).</summary>
    Corrupt,
}

/// <summary>The decoded fixed-size file header of a <c>.fh6rec</c> recording.</summary>
/// <param name="Version">Format version the file was written with.</param>
/// <param name="StartedAtUtc">Wall-clock instant the recording started (UTC). Every record's elapsed time is relative to this.</param>
/// <param name="Flags">Reserved feature bits. Always 0 in version 1; readers must ignore bits they do not know.</param>
public readonly record struct RecordingHeader(ushort Version, DateTimeOffset StartedAtUtc, uint Flags);

/// <summary>
/// Constants and low-level encode/decode helpers for the <c>.fh6rec</c> binary format.
/// The full layout is also documented in <c>Recording/FORMAT.md</c>.
/// </summary>
/// <remarks>
/// <para><b>Layout (all integers little-endian):</b></para>
/// <code>
/// HEADER (24 bytes)
///   0   6  magic  "FH6REC" (ASCII)
///   6   2  format version (uint16, currently 1)
///   8   8  recording start, UTC ticks (int64, 100 ns since 0001-01-01)
///   16  4  flags (uint32, reserved, 0)
///   20  4  reserved (uint32, 0)
/// RECORD (repeated until end of file)
///   0   8  elapsed since start, MICROSECONDS (int64)
///   8   4  payload length (int32)
///   12  N  payload: the raw UDP datagram (324 bytes for FH6 today)
/// </code>
/// <para>
/// WHY a magic: a cheap, explicit "is this even our file" check, so opening a random file fails with a
/// clear message instead of decoding garbage.
/// </para>
/// <para>
/// WHY a version: the format will evolve. A reader that sees a newer version refuses it explicitly
/// (<see cref="RecordingHeaderStatus.UnsupportedVersion"/>) instead of silently misreading fields. The header
/// size is fixed so a future reader can always find the first record of an older file.
/// </para>
/// <para>
/// WHY little-endian via <see cref="BinaryPrimitives"/>: the bytes on disk are identical on every CPU, and
/// it matches the wire format of the game's own packets (see <see cref="TelemetryParser"/>).
/// </para>
/// <para>
/// WHY microseconds (not ticks or milliseconds): UDP arrival times at ~60 Hz need better than 1 ms so
/// replay does not visibly quantize (16.67 ms intervals would become 16/17 ms jitter), while 100 ns ticks
/// are false precision for socket arrival times. An int64 of microseconds still lasts ~292,000 years.
/// </para>
/// <para>
/// WHY raw datagrams (and not parsed packets): recordings stay re-parseable if the parser changes or had a bug.
/// </para>
/// </remarks>
public static class RecordingFormat
{
    /// <summary>Highest format version this build can read and the one it writes.</summary>
    public const ushort CurrentVersion = 1;

    /// <summary>Length of the magic marker.</summary>
    public const int MagicLength = 6;

    /// <summary>Fixed size of the file header, in bytes.</summary>
    public const int HeaderSize = 24;

    /// <summary>Fixed size of the per-record prefix (elapsed + length), in bytes.</summary>
    public const int RecordHeaderSize = 12;

    /// <summary>
    /// Largest payload a record may carry. A UDP datagram cannot exceed 65,535 bytes, so anything bigger
    /// in a length field can only be corruption; the reader uses this to avoid allocating gigabytes for
    /// a damaged length.
    /// </summary>
    public const int MaxPayloadLength = 65_535;

    /// <summary>The ASCII bytes "FH6REC" (a UTF-8 literal compiles to a static read-only span: no allocation).</summary>
    internal static ReadOnlySpan<byte> Magic => "FH6REC"u8;

    /// <summary>
    /// Non-throwing header decode. Returns true only for <see cref="RecordingHeaderStatus.Ok"/>; otherwise
    /// <paramref name="status"/> says why. <paramref name="header"/> is populated whenever the version could be
    /// read (even for an unsupported version, so callers can show "file version 3, this app supports 1").
    /// </summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> data, out RecordingHeader header, out RecordingHeaderStatus status)
    {
        header = default;

        if (data.Length < HeaderSize)
        {
            status = RecordingHeaderStatus.TooShort;
            return false;
        }

        if (!data[..MagicLength].SequenceEqual(Magic))
        {
            status = RecordingHeaderStatus.BadMagic;
            return false;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        var startTicks = BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);

        // Version 0 never existed; it can only mean damage. Anything above ours is "from the future".
        if (version == 0 || startTicks < 0 || startTicks > DateTime.MaxValue.Ticks)
        {
            status = RecordingHeaderStatus.Corrupt;
            return false;
        }

        header = new RecordingHeader(version, new DateTimeOffset(startTicks, TimeSpan.Zero), flags);

        if (version > CurrentVersion)
        {
            status = RecordingHeaderStatus.UnsupportedVersion;
            return false;
        }

        status = RecordingHeaderStatus.Ok;
        return true;
    }

    internal static void WriteHeader(Span<byte> destination, DateTimeOffset startedAtUtc)
    {
        destination[..HeaderSize].Clear(); // flags + reserved stay zero
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], CurrentVersion);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], startedAtUtc.UtcTicks);
    }

    internal static void WriteRecordHeader(Span<byte> destination, long elapsedMicroseconds, int payloadLength)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, elapsedMicroseconds);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], payloadLength);
    }

    internal static void ReadRecordHeader(ReadOnlySpan<byte> source, out long elapsedMicroseconds, out int payloadLength)
    {
        elapsedMicroseconds = BinaryPrimitives.ReadInt64LittleEndian(source);
        payloadLength = BinaryPrimitives.ReadInt32LittleEndian(source[8..]);
    }

    internal static TimeSpan FromMicroseconds(long microseconds) =>
        TimeSpan.FromTicks(microseconds * TimeSpan.TicksPerMicrosecond);

    internal static long ToMicroseconds(TimeSpan elapsed) => elapsed.Ticks / TimeSpan.TicksPerMicrosecond;
}
