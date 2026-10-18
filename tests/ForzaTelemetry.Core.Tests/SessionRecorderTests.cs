using ForzaTelemetry.Core.Recording;
using Microsoft.Extensions.Time.Testing;

namespace ForzaTelemetry.Core.Tests;

public class SessionRecorderTests
{
    private static byte[] Datagram(byte fill) => Enumerable.Repeat(fill, TelemetryParser.PacketSize).ToArray();

    private static async Task<List<RecordedDatagram>> ReadAsync(byte[] bytes)
    {
        await using var reader = await SessionRecordingReader.OpenAsync(new MemoryStream(bytes));
        var records = new List<RecordedDatagram>();
        await foreach (var r in reader.ReadAllAsync())
        {
            records.Add(r);
        }

        return records;
    }

    [Fact]
    public async Task Records_ArrivalTimes_FromTheInjectedClock_AndCountsEverything()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
        var stream = new MemoryStream();
        await using var recorder = new SessionRecorder(time);
        await recorder.StartAsync(stream, leaveOpen: true);

        recorder.OnDatagram(Datagram(1));
        time.Advance(TimeSpan.FromMilliseconds(16));
        recorder.OnDatagram(Datagram(2));
        time.Advance(TimeSpan.FromMilliseconds(17));
        Assert.Equal(TimeSpan.FromMilliseconds(33), recorder.Duration);
        await recorder.StopAsync();

        var records = await ReadAsync(stream.ToArray());
        Assert.Equal(2, records.Count);
        Assert.Equal(TimeSpan.Zero, records[0].Elapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(16), records[1].Elapsed);
        Assert.Equal(Datagram(2), records[1].Payload.ToArray());
        Assert.Equal(2, recorder.DatagramCount);
        Assert.Equal(2 * TelemetryParser.PacketSize, recorder.BytesRecorded);
        Assert.Equal(0, recorder.DroppedCount);
        Assert.False(recorder.IsRecording);

        // Duration is frozen at the stop instant.
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromMilliseconds(33), recorder.Duration);
    }

    [Fact]
    public async Task HeaderStartTime_ComesFromTheClock()
    {
        var startUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var stream = new MemoryStream();
        await using var recorder = new SessionRecorder(new FakeTimeProvider(startUtc));
        await recorder.StartAsync(stream, leaveOpen: true);
        await recorder.StopAsync();

        await using var reader = await SessionRecordingReader.OpenAsync(new MemoryStream(stream.ToArray()));
        Assert.Equal(startUtc, reader.Header.StartedAtUtc);
    }

    [Fact]
    public async Task CopiesTheDatagram_SoTheCallerCanReuseItsBuffer()
    {
        var stream = new MemoryStream();
        await using var recorder = new SessionRecorder();
        await recorder.StartAsync(stream, leaveOpen: true);

        var buffer = Datagram(7);
        recorder.OnDatagram(buffer);
        Array.Fill(buffer, (byte)9); // the receive loop reuses its buffer
        await recorder.StopAsync();

        var records = await ReadAsync(stream.ToArray());
        Assert.Equal(Datagram(7), records[0].Payload.ToArray());
    }

    [Fact]
    public async Task OnDatagram_WhenNotRecording_IsIgnored()
    {
        await using var recorder = new SessionRecorder();

        recorder.OnDatagram(Datagram(1));

        Assert.Equal(0, recorder.DatagramCount);
        Assert.Equal(0, recorder.DroppedCount);
        Assert.Equal(TimeSpan.Zero, recorder.Duration);
    }

    [Fact]
    public async Task StartTwice_Throws_AndRestartAfterStopResetsCounters()
    {
        await using var recorder = new SessionRecorder();
        await recorder.StartAsync(new MemoryStream());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await recorder.StartAsync(new MemoryStream()));

        recorder.OnDatagram(Datagram(1));
        await recorder.StopAsync();
        Assert.Equal(1, recorder.DatagramCount);

        await recorder.StartAsync(new MemoryStream());
        Assert.Equal(0, recorder.DatagramCount);
        Assert.True(recorder.IsRecording);
    }

    [Fact]
    public async Task SlowStream_NeverBlocksTheReceiveLoop_AndOverflowIsCountedAsDropped()
    {
        // A tiny buffer + tiny queue so the disk stall backs up quickly; the gate is closed only AFTER the
        // header has been written (StartAsync itself legitimately waits for the header).
        var slow = new GatedStream();
        await using var recorder = new SessionRecorder(queueCapacity: 4, bufferSize: 16);
        await recorder.StartAsync(slow, leaveOpen: true);
        slow.CloseGate();

        // OnDatagram is synchronous: if it ever waited on the stalled stream this loop would hang the
        // test (and the 5 s timeout below would fail it).
        var loop = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                recorder.OnDatagram(Datagram((byte)i));
            }
        });
        Assert.Same(loop, await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(5))));
        await loop;

        Assert.Equal(200, recorder.DatagramCount + recorder.DroppedCount);
        Assert.True(recorder.DroppedCount > 0, "a full queue must drop, not block");
        Assert.True(recorder.DatagramCount >= 4, "the queue still accepted what fit");

        // Once the disk recovers everything accepted is written and Stop completes.
        slow.OpenGate();
        await recorder.StopAsync();
        var records = await ReadAsync(slow.ToArray());
        Assert.Equal(recorder.DatagramCount, records.Count);
    }

    [Fact]
    public async Task WriterFailure_IsSurfacedOnStop_AndLaterDatagramsAreDropped()
    {
        var failing = new GatedStream();
        await using var recorder = new SessionRecorder(queueCapacity: 8, bufferSize: 16);
        await recorder.StartAsync(failing, leaveOpen: true);
        failing.FailWrites();

        recorder.OnDatagram(Datagram(1));
        await WaitUntilAsync(() => recorder.Fault is not null);
        recorder.OnDatagram(Datagram(2));

        Assert.True(recorder.DroppedCount >= 1);
        await Assert.ThrowsAsync<IOException>(recorder.StopAsync);
        Assert.False(recorder.IsRecording);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// A fake destination stream whose writes can be stalled (a slow disk) or made to fail, while capturing
    /// what was written.
    /// </summary>
    private sealed class GatedStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private volatile TaskCompletionSource? _gate;
        private volatile bool _fail;

        public void CloseGate() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OpenGate() => _gate?.TrySetResult();

        public void FailWrites() => _fail = true;

        public byte[] ToArray() => _inner.ToArray();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_fail)
            {
                throw new IOException("disk failure");
            }

            if (_gate is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }

            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Flush()
        {
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
