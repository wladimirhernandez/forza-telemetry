using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core.Recording;

namespace ForzaTelemetry.App.Tests;

/// <summary>Writes small, real <c>.fh6rec</c> files for tests (the format is the one the recorder writes: nothing is faked).</summary>
internal static class RecordingFiles
{
    private static readonly DateTimeOffset DefaultStart = new(2026, 10, 18, 1, 23, 45, TimeSpan.Zero);

    /// <summary>One 324-byte datagram with the fields the screens read; the rest is zero (a valid, if quiet, packet).</summary>
    public static byte[] Datagram(int car = 1234, int performanceIndex = 780, float speedMs = 30, bool raceOn = true, int sequence = 0)
    {
        var buffer = new byte[324];
        BitConverter.GetBytes(raceOn ? 1 : 0).CopyTo(buffer, 0);
        BitConverter.GetBytes((uint)(1000 + (sequence * 16))).CopyTo(buffer, 4);
        BitConverter.GetBytes(8000f).CopyTo(buffer, 8);
        BitConverter.GetBytes(4500f + sequence).CopyTo(buffer, 16);
        BitConverter.GetBytes(car).CopyTo(buffer, 212);
        BitConverter.GetBytes(3).CopyTo(buffer, 216);
        BitConverter.GetBytes(performanceIndex).CopyTo(buffer, 220);
        BitConverter.GetBytes(2).CopyTo(buffer, 224);
        BitConverter.GetBytes(sequence * 2f).CopyTo(buffer, 244);
        BitConverter.GetBytes(sequence * 1f).CopyTo(buffer, 252);
        BitConverter.GetBytes(speedMs).CopyTo(buffer, 256);
        buffer[319] = 3;
        return buffer;
    }

    /// <summary>The bytes of a recording of <paramref name="packets"/> datagrams, <paramref name="intervalMs"/> apart.</summary>
    public static async Task<byte[]> BytesAsync(
        int packets = 10, int intervalMs = 100, int car = 1234, DateTimeOffset? startedAt = null)
    {
        var stream = new MemoryStream();
        await using (var writer = await SessionRecordingWriter.CreateAsync(stream, startedAt ?? DefaultStart, leaveOpen: true))
        {
            for (var i = 0; i < packets; i++)
            {
                await writer.WriteAsync(Datagram(car, sequence: i), TimeSpan.FromMilliseconds(i * intervalMs));
            }
        }

        return stream.ToArray();
    }

    /// <summary>Writes a recording file and returns its path.</summary>
    public static async Task<string> WriteAsync(
        string folder, string name, int packets = 10, int intervalMs = 100, int car = 1234, DateTimeOffset? startedAt = null)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + RecordingLibrary.Extension);
        await File.WriteAllBytesAsync(path, await BytesAsync(packets, intervalMs, car, startedAt));
        return path;
    }
}
