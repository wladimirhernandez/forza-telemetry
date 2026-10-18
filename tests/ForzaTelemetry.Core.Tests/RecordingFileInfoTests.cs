using ForzaTelemetry.Core.Recording;

namespace ForzaTelemetry.Core.Tests;

public class RecordingFileInfoTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 8, 15, 0, TimeSpan.Zero);

    private static async Task<byte[]> RecordingAsync(params (byte[] Payload, int Ms)[] records)
    {
        var stream = new MemoryStream();
        await using (var writer = await SessionRecordingWriter.CreateAsync(stream, Start, leaveOpen: true))
        {
            foreach (var (payload, ms) in records)
            {
                await writer.WriteAsync(payload, TimeSpan.FromMilliseconds(ms));
            }
        }

        return stream.ToArray();
    }

    private static byte[] Packet(int ordinal, bool raceOn = true) =>
        new PacketBuilder().WithIsRaceOn(raceOn).WithCarOrdinal(ordinal).Build();

    [Fact]
    public async Task Scan_ReportsStartDurationCountCarsAndSize()
    {
        var bytes = await RecordingAsync(
            (Packet(900), 0),
            (Packet(100), 1_000),
            (Packet(900), 2_000),
            (new byte[5], 2_500), // does not parse: counted as a record, contributes no car
            (Packet(555, raceOn: false), 3_000)); // menu packet: its ordinal is ignored

        var info = await RecordingFileInfo.ScanAsync(new MemoryStream(bytes));

        Assert.Equal(Start, info.StartedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(3), info.Duration);
        Assert.Equal(5, info.PacketCount);
        Assert.Equal([100, 900], info.CarOrdinals);
        Assert.Equal(bytes.Length, info.FileSizeBytes);
        Assert.False(info.IsTruncated);
    }

    [Fact]
    public async Task Scan_OfAnEmptyRecording_IsZeros()
    {
        var info = await RecordingFileInfo.ScanAsync(new MemoryStream(await RecordingAsync()));

        Assert.Equal(TimeSpan.Zero, info.Duration);
        Assert.Equal(0, info.PacketCount);
        Assert.Empty(info.CarOrdinals);
    }

    [Fact]
    public async Task Scan_OfACrashedRecording_ReportsWhatSurvivedAndFlagsIt()
    {
        var bytes = await RecordingAsync((Packet(1), 0), (Packet(2), 500), (Packet(3), 1_000));
        var cut = bytes[..^50];

        var info = await RecordingFileInfo.ScanAsync(new MemoryStream(cut));

        Assert.True(info.IsTruncated);
        Assert.Equal(2, info.PacketCount);
        Assert.Equal(TimeSpan.FromMilliseconds(500), info.Duration);
    }

    [Fact]
    public async Task Scan_OfANonRecording_Throws()
    {
        await Assert.ThrowsAsync<InvalidRecordingException>(
            async () => await RecordingFileInfo.ScanAsync(new MemoryStream(new byte[64])));
    }

    [Fact]
    public async Task ScanFile_WorksOnARealFile_AndWriterFileHelperRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fh6-{Guid.NewGuid():N}.fh6rec");
        try
        {
            await using (var writer = await SessionRecordingWriter.CreateFileAsync(path, Start))
            {
                await writer.WriteAsync(Packet(42), TimeSpan.Zero);
                await writer.WriteAsync(Packet(42), TimeSpan.FromSeconds(2));
            }

            var info = await RecordingFileInfo.ScanFileAsync(path);

            Assert.Equal(2, info.PacketCount);
            Assert.Equal([42], info.CarOrdinals);
            Assert.Equal(new FileInfo(path).Length, info.FileSizeBytes);

            await using var reader = await SessionRecordingReader.OpenFileAsync(path);
            Assert.Equal(Start, reader.Header.StartedAtUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
