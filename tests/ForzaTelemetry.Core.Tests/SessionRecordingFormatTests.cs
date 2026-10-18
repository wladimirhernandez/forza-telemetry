using System.Buffers.Binary;
using ForzaTelemetry.Core.Recording;

namespace ForzaTelemetry.Core.Tests;

public class SessionRecordingFormatTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 30, 0, TimeSpan.Zero);

    private static byte[] Datagram(byte fill, int length = TelemetryParser.PacketSize) =>
        Enumerable.Repeat(fill, length).ToArray();

    private static async Task<byte[]> WriteRecordingAsync(params (byte[] Payload, TimeSpan Elapsed)[] records)
    {
        var stream = new MemoryStream();
        await using (var writer = await SessionRecordingWriter.CreateAsync(stream, Start, leaveOpen: true))
        {
            foreach (var (payload, elapsed) in records)
            {
                await writer.WriteAsync(payload, elapsed);
            }
        }

        return stream.ToArray();
    }

    private static async Task<(List<RecordedDatagram> Records, SessionRecordingReader Reader)> ReadAllAsync(byte[] bytes)
    {
        var reader = await SessionRecordingReader.OpenAsync(new MemoryStream(bytes));
        var records = new List<RecordedDatagram>();
        await foreach (var record in reader.ReadAllAsync())
        {
            records.Add(record);
        }

        return (records, reader);
    }

    [Fact]
    public async Task RoundTrip_PreservesBytesAndTimestampsExactly()
    {
        var first = Datagram(1);
        var second = Datagram(2);
        var odd = Datagram(3, length: 10); // wrong-size datagrams are stored verbatim too
        var bytes = await WriteRecordingAsync(
            (first, TimeSpan.Zero),
            (second, TimeSpan.FromMicroseconds(16_667)),
            (odd, TimeSpan.FromSeconds(3.5)));

        var (records, reader) = await ReadAllAsync(bytes);

        Assert.Equal(3, records.Count);
        Assert.Equal(first, records[0].Payload.ToArray());
        Assert.Equal(second, records[1].Payload.ToArray());
        Assert.Equal(odd, records[2].Payload.ToArray());
        Assert.Equal(TimeSpan.Zero, records[0].Elapsed);
        Assert.Equal(TimeSpan.FromMicroseconds(16_667), records[1].Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(3.5), records[2].Elapsed);
        Assert.Equal(Start, reader.Header.StartedAtUtc);
        Assert.Equal(RecordingFormat.CurrentVersion, reader.Header.Version);
        Assert.False(reader.IsTruncated);
    }

    [Fact]
    public async Task Layout_MatchesTheDocumentedBytes()
    {
        var bytes = await WriteRecordingAsync((new byte[] { 0xAA, 0xBB }, TimeSpan.FromMicroseconds(0x0102)));

        Assert.Equal("FH6REC"u8.ToArray(), bytes[..6]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.Equal(Start.UtcTicks, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(0x0102, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(32)));
        Assert.Equal(new byte[] { 0xAA, 0xBB }, bytes[36..]);
        Assert.Equal(24 + 12 + 2, bytes.Length);
    }

    [Fact]
    public async Task EmptyRecording_IsValidAndHasNoRecords()
    {
        var (records, reader) = await ReadAllAsync(await WriteRecordingAsync());

        Assert.Empty(records);
        Assert.False(reader.IsTruncated);
    }

    [Fact]
    public async Task Writer_ClampsBackwardsTimestamps()
    {
        var bytes = await WriteRecordingAsync(
            (Datagram(1), TimeSpan.FromMilliseconds(10)),
            (Datagram(2), TimeSpan.FromMilliseconds(5)));

        var (records, _) = await ReadAllAsync(bytes);

        Assert.Equal(TimeSpan.FromMilliseconds(10), records[1].Elapsed);
    }

    [Fact]
    public async Task Writer_RejectsNegativeElapsedAndOversizedPayload()
    {
        await using var writer = await SessionRecordingWriter.CreateAsync(new MemoryStream(), Start);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await writer.WriteAsync(Datagram(1), TimeSpan.FromMilliseconds(-1)));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await writer.WriteAsync(new byte[RecordingFormat.MaxPayloadLength + 1], TimeSpan.Zero));
    }

    [Fact]
    public async Task Writer_LeaveOpenKeepsTheStreamUsable_OtherwiseClosesIt()
    {
        var kept = new MemoryStream();
        await (await SessionRecordingWriter.CreateAsync(kept, Start, leaveOpen: true)).DisposeAsync();
        Assert.True(kept.CanWrite);
        Assert.Equal(RecordingFormat.HeaderSize, kept.Length);

        var closed = new MemoryStream();
        await (await SessionRecordingWriter.CreateAsync(closed, Start)).DisposeAsync();
        Assert.False(closed.CanWrite);
    }

    [Fact]
    public async Task BadMagic_ThrowsInvalidRecording()
    {
        var bytes = await WriteRecordingAsync((Datagram(1), TimeSpan.Zero));
        bytes[0] = (byte)'X';

        var ex = await Assert.ThrowsAsync<InvalidRecordingException>(
            async () => await SessionRecordingReader.OpenAsync(new MemoryStream(bytes)));

        Assert.Contains("magic", ex.Message);
        Assert.False(RecordingFormat.TryReadHeader(bytes, out _, out var status));
        Assert.Equal(RecordingHeaderStatus.BadMagic, status);
    }

    [Fact]
    public async Task NewerVersion_ThrowsUnsupportedVersionWithBothVersions()
    {
        var bytes = await WriteRecordingAsync((Datagram(1), TimeSpan.Zero));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 7);

        var ex = await Assert.ThrowsAsync<UnsupportedRecordingVersionException>(
            async () => await SessionRecordingReader.OpenAsync(new MemoryStream(bytes)));

        Assert.Equal(7, ex.FileVersion);
        Assert.Equal(RecordingFormat.CurrentVersion, ex.SupportedVersion);

        // The Try API reports the same thing without throwing, and still exposes the version found.
        Assert.False(RecordingFormat.TryReadHeader(bytes, out var header, out var status));
        Assert.Equal(RecordingHeaderStatus.UnsupportedVersion, status);
        Assert.Equal(7, header.Version);
    }

    [Fact]
    public async Task VersionZero_IsCorrupt()
    {
        var bytes = await WriteRecordingAsync();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 0);

        Assert.False(RecordingFormat.TryReadHeader(bytes, out _, out var status));
        Assert.Equal(RecordingHeaderStatus.Corrupt, status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(23)]
    public async Task TruncatedHeader_ThrowsInvalidRecording(int length)
    {
        var bytes = (await WriteRecordingAsync()).AsSpan(0, length).ToArray();

        await Assert.ThrowsAsync<InvalidRecordingException>(
            async () => await SessionRecordingReader.OpenAsync(new MemoryStream(bytes)));
    }

    [Theory]
    [InlineData(1)]   // cut inside the 12-byte record prefix
    [InlineData(11)]  // one byte short of a full prefix
    [InlineData(12)]  // prefix complete, payload missing entirely
    [InlineData(100)] // cut inside the payload
    [InlineData(335)] // one byte short of the whole record
    public async Task TruncatedTail_ReturnsCompleteRecordsAndFlagsTruncation(int extraBytesKept)
    {
        var bytes = await WriteRecordingAsync(
            (Datagram(1), TimeSpan.Zero),
            (Datagram(2), TimeSpan.FromMilliseconds(16)),
            (Datagram(3), TimeSpan.FromMilliseconds(33)));
        var recordSize = RecordingFormat.RecordHeaderSize + TelemetryParser.PacketSize;
        var cut = bytes.AsSpan(0, RecordingFormat.HeaderSize + (2 * recordSize) + extraBytesKept).ToArray();

        var (records, reader) = await ReadAllAsync(cut);

        Assert.Equal(2, records.Count);
        Assert.Equal(Datagram(2), records[1].Payload.ToArray());
        Assert.True(reader.IsTruncated);
    }

    [Fact]
    public async Task ImpossiblePayloadLength_StopsCleanlyAsTruncated()
    {
        var bytes = await WriteRecordingAsync((Datagram(1), TimeSpan.Zero), (Datagram(2), TimeSpan.FromMilliseconds(16)));
        var secondRecord = RecordingFormat.HeaderSize + RecordingFormat.RecordHeaderSize + TelemetryParser.PacketSize;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(secondRecord + 8), int.MaxValue);

        var (records, reader) = await ReadAllAsync(bytes);

        Assert.Single(records);
        Assert.True(reader.IsTruncated);
    }

    [Fact]
    public async Task Reader_CanOnlyBeEnumeratedOnce()
    {
        var reader = await SessionRecordingReader.OpenAsync(new MemoryStream(await WriteRecordingAsync()));
        await foreach (var _ in reader.ReadAllAsync())
        {
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in reader.ReadAllAsync())
            {
            }
        });
    }
}
