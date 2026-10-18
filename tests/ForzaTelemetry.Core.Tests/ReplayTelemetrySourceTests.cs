using ForzaTelemetry.Core.Recording;
using Microsoft.Extensions.Time.Testing;

namespace ForzaTelemetry.Core.Tests;

public class ReplayTelemetrySourceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Packet(int ordinal, bool raceOn = true) =>
        new PacketBuilder().WithIsRaceOn(raceOn).WithCarOrdinal(ordinal).Build();

    /// <summary>Builds an in-memory recording: one record per (payload, milliseconds-since-start).</summary>
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

    private static async Task<byte[]> ThreePacketsAsync() =>
        await RecordingAsync((Packet(1), 0), (Packet(2), 100), (Packet(3), 300));

    /// <summary>
    /// A FakeTimeProvider that tells the test when somebody starts waiting on it. Without this the test
    /// could call Advance() BEFORE the replay registered its delay timer, and the advance would be lost.
    /// </summary>
    private sealed class ObservableTimeProvider : FakeTimeProvider
    {
        private readonly SemaphoreSlim _timerCreated = new(0);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                _timerCreated.Release();
            }

            return timer;
        }

        public async Task WaitForTimerAsync() =>
            Assert.True(await _timerCreated.WaitAsync(TimeSpan.FromSeconds(5)), "the replay never started waiting");
    }

    /// <summary>Consumes a replay on a background task, recording each received packet's ordinal.</summary>
    private sealed class Consumer
    {
        private readonly SemaphoreSlim _received = new(0);
        private readonly CancellationTokenSource _cts = new();
        private readonly List<int> _ordinals = [];
        private readonly Lock _lock = new();

        public Consumer(ReplayTelemetrySource source)
        {
            Completion = Task.Run(async () =>
            {
                await foreach (var packet in source.ReadPacketsAsync(_cts.Token))
                {
                    lock (_lock)
                    {
                        _ordinals.Add(packet.CarOrdinal);
                    }

                    _received.Release();
                }
            });
        }

        public Task Completion { get; }

        public IReadOnlyList<int> Ordinals
        {
            get { lock (_lock) { return _ordinals.ToArray(); } }
        }

        public async Task WaitForPacketAsync() =>
            Assert.True(await _received.WaitAsync(TimeSpan.FromSeconds(5)), "no packet arrived in time");

        /// <summary>Gives the pipeline real time to (wrongly) deliver something; used for "nothing yet" assertions.</summary>
        public static Task SettleAsync() => Task.Delay(50);

        public async Task StopAsync()
        {
            await _cts.CancelAsync();
            await Completion;
        }
    }

    [Fact]
    public async Task RealTime_DeliversEachPacketOnlyAfterItsRecordedGap()
    {
        var time = new ObservableTimeProvider();
        var source = new ReplayTelemetrySource(
            (Func<Stream>)(() => new MemoryStream(ThreePacketsAsync().Result)), time) { HoldAtEnd = false };
        var consumer = new Consumer(source);

        await consumer.WaitForPacketAsync(); // first record is immediate
        Assert.Equal([1], consumer.Ordinals);

        await time.WaitForTimerAsync();
        time.Advance(TimeSpan.FromMilliseconds(99));
        await Consumer.SettleAsync();
        Assert.Equal([1], consumer.Ordinals);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await consumer.WaitForPacketAsync();
        Assert.Equal([1, 2], consumer.Ordinals);
        Assert.Equal(TimeSpan.FromMilliseconds(100), source.Position);

        await time.WaitForTimerAsync();
        time.Advance(TimeSpan.FromMilliseconds(200)); // the 100 ms -> 300 ms gap
        await consumer.WaitForPacketAsync();
        await consumer.Completion; // recording exhausted: the enumeration ends by itself
        Assert.Equal([1, 2, 3], consumer.Ordinals);
        Assert.Equal(TimeSpan.FromMilliseconds(300), source.Duration);
        Assert.Equal(source.Duration, source.Position);
    }

    [Theory]
    [InlineData(0.5, 200)]
    [InlineData(2.0, 50)]
    [InlineData(4.0, 25)]
    public async Task SpeedFactor_ScalesTheWaitingTime(double speed, int expectedDelayMs)
    {
        var time = new ObservableTimeProvider();
        var bytes = await RecordingAsync((Packet(1), 0), (Packet(2), 100));
        var source = new ReplayTelemetrySource(() => new MemoryStream(bytes), time) { Speed = speed, HoldAtEnd = false };
        var consumer = new Consumer(source);

        await consumer.WaitForPacketAsync();
        await time.WaitForTimerAsync();
        time.Advance(TimeSpan.FromMilliseconds(expectedDelayMs - 1));
        await Consumer.SettleAsync();
        Assert.Equal([1], consumer.Ordinals);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await consumer.WaitForPacketAsync();
        Assert.Equal([1, 2], consumer.Ordinals);
        await consumer.Completion;
    }

    [Fact]
    public async Task MaxSpeed_DeliversEverythingWithoutAnyTimeAdvancing()
    {
        var time = new FakeTimeProvider();
        var bytes = await RecordingAsync((Packet(1), 0), (Packet(2), 60_000), (Packet(3), 3_600_000));
        var source = new ReplayTelemetrySource(() => new MemoryStream(bytes), time) { Speed = ReplaySpeed.Max, HoldAtEnd = false };

        var ordinals = new List<int>();
        await foreach (var packet in source.ReadPacketsAsync())
        {
            ordinals.Add(packet.CarOrdinal);
        }

        Assert.Equal([1, 2, 3], ordinals);
    }

    [Fact]
    public async Task Pause_HoldsDeliveryEvenWhenTimePasses_AndResumeReleasesIt()
    {
        var time = new ObservableTimeProvider();
        var bytes = await RecordingAsync((Packet(1), 0), (Packet(2), 100));
        var source = new ReplayTelemetrySource(() => new MemoryStream(bytes), time) { HoldAtEnd = false };
        var consumer = new Consumer(source);

        await consumer.WaitForPacketAsync();
        await time.WaitForTimerAsync(); // now waiting for record 2
        source.Pause();
        Assert.True(source.IsPaused);

        time.Advance(TimeSpan.FromSeconds(10)); // the gap elapses, but we are paused
        await Consumer.SettleAsync();
        Assert.Equal([1], consumer.Ordinals);

        source.Resume();
        await consumer.WaitForPacketAsync();
        Assert.Equal([1, 2], consumer.Ordinals);
        Assert.False(source.IsPaused);
        await consumer.Completion;
    }

    [Fact]
    public async Task PausedBeforeStart_DeliversNothingUntilResumed()
    {
        var time = new ObservableTimeProvider();
        var bytes = await RecordingAsync((Packet(1), 0));
        var source = new ReplayTelemetrySource(() => new MemoryStream(bytes), time) { HoldAtEnd = false };
        source.Pause();
        var consumer = new Consumer(source);

        await Consumer.SettleAsync();
        Assert.Empty(consumer.Ordinals);

        source.Resume();
        await consumer.WaitForPacketAsync();
        await consumer.Completion;
    }

    [Fact]
    public async Task SeekToStart_InterruptsTheWaitAndReplaysFromTheFirstRecord()
    {
        var time = new ObservableTimeProvider();
        var bytes = await ThreePacketsAsync();
        var source = new ReplayTelemetrySource(() => new MemoryStream(bytes), time);
        var consumer = new Consumer(source);

        await consumer.WaitForPacketAsync();
        await time.WaitForTimerAsync();
        time.Advance(TimeSpan.FromMilliseconds(100));
        await consumer.WaitForPacketAsync();
        Assert.Equal([1, 2], consumer.Ordinals);

        await time.WaitForTimerAsync(); // waiting for record 3
        source.SeekToStart();
        Assert.Equal(TimeSpan.Zero, source.Position);

        await consumer.WaitForPacketAsync(); // record 1 again, with no time advance needed
        Assert.Equal([1, 2, 1], consumer.Ordinals);

        await time.WaitForTimerAsync();
        time.Advance(TimeSpan.FromMilliseconds(100));
        await consumer.WaitForPacketAsync();
        Assert.Equal([1, 2, 1, 2], consumer.Ordinals);

        await consumer.StopAsync();
    }

    [Fact]
    public async Task Cancellation_EndsTheEnumerationCleanly()
    {
        var time = new ObservableTimeProvider();
        var source = new ReplayTelemetrySource(() => new MemoryStream(ThreePacketsAsync().Result), time);
        var consumer = new Consumer(source);

        await consumer.WaitForPacketAsync();
        await time.WaitForTimerAsync();
        await consumer.StopAsync(); // no exception, enumeration just ends

        Assert.Equal([1], consumer.Ordinals);
    }

    [Fact]
    public async Task ReachingTheEnd_HoldsTheEnumerationOpen_AndSeekRestartsInTheSameEnumeration()
    {
        var bytes = await RecordingAsync((Packet(1), 0), (Packet(2), 100));
        var source = new ReplayTelemetrySource(() => new MemoryStream(bytes)) { Speed = ReplaySpeed.Max };
        var consumer = new Consumer(source);

        await consumer.WaitForPacketAsync();
        await consumer.WaitForPacketAsync();
        await WaitUntilAsync(() => source.IsFinished);
        Assert.False(consumer.Completion.IsCompleted, "a player stays open at the end");
        Assert.Equal(source.Duration, source.Position);

        source.SeekToStart(); // the "replay" button after the end
        Assert.False(source.IsFinished);
        Assert.Equal(TimeSpan.Zero, source.Position);

        await consumer.WaitForPacketAsync();
        await consumer.WaitForPacketAsync();
        Assert.Equal([1, 2, 1, 2], consumer.Ordinals);
        await WaitUntilAsync(() => source.IsFinished);

        await consumer.StopAsync(); // cancelling while finished ends cleanly
        Assert.False(source.IsFinished);
    }

    [Fact]
    public async Task Cancellation_DuringTheInitialScan_EndsCleanly()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var source = new ReplayTelemetrySource(() => new MemoryStream(ThreePacketsAsync().Result));

        var count = 0;
        await foreach (var _ in source.ReadPacketsAsync(cts.Token))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task SeekToStart_FromAnotherThread_DoesNotRunCancellationCallbacksInsideTheLock()
    {
        // Regression: Cancel() used to run under the gate; a callback touching the source would deadlock.
        var time = new ObservableTimeProvider();
        var source = new ReplayTelemetrySource(() => new MemoryStream(ThreePacketsAsync().Result), time);
        var consumer = new Consumer(source);
        await consumer.WaitForPacketAsync();
        await time.WaitForTimerAsync();

        var seek = Task.Run(() =>
        {
            source.SeekToStart();
            _ = source.IsPaused; // takes the same gate
            _ = source.IsFinished;
        });

        Assert.Same(seek, await Task.WhenAny(seek, Task.Delay(TimeSpan.FromSeconds(5))));
        await consumer.StopAsync();
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

    [Fact]
    public async Task NonPacketDatagrams_AreSkippedAndCounted()
    {
        var bytes = await RecordingAsync((Packet(1), 0), (new byte[10], 10), (Packet(2), 20));
        var source = new ReplayTelemetrySource(() => new MemoryStream(bytes)) { Speed = ReplaySpeed.Max, HoldAtEnd = false };

        var ordinals = new List<int>();
        await foreach (var packet in source.ReadPacketsAsync())
        {
            ordinals.Add(packet.CarOrdinal);
        }

        Assert.Equal([1, 2], ordinals);
        Assert.Equal(1, source.DiscardedCount);
    }

    [Fact]
    public async Task InvalidFile_ThrowsOnFirstRead()
    {
        var source = new ReplayTelemetrySource(() => new MemoryStream(new byte[100]));

        await Assert.ThrowsAsync<InvalidRecordingException>(async () =>
        {
            await foreach (var _ in source.ReadPacketsAsync())
            {
            }
        });
    }

    [Fact]
    public async Task SecondConcurrentEnumeration_IsRejected()
    {
        var time = new ObservableTimeProvider();
        var source = new ReplayTelemetrySource(() => new MemoryStream(ThreePacketsAsync().Result), time);
        var consumer = new Consumer(source);
        await consumer.WaitForPacketAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in source.ReadPacketsAsync())
            {
            }
        });

        await consumer.StopAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Speed_MustBePositive(double value)
    {
        var source = new ReplayTelemetrySource(() => new MemoryStream());

        Assert.Throws<ArgumentOutOfRangeException>(() => source.Speed = value);
    }
}
