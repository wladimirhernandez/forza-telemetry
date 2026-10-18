using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App.Tests;

/// <summary>The per-packet observer hook of <see cref="TelemetryService"/>: every packet reaches every observer, and one faulty observer cannot stop the loop.</summary>
public class TelemetryServiceObserverTests
{
    private sealed class RecordingObserver : IPacketObserver
    {
        private readonly object _gate = new();
        private readonly List<uint> _timestamps = [];

        public IReadOnlyList<uint> Timestamps
        {
            get
            {
                lock (_gate)
                {
                    return [.. _timestamps];
                }
            }
        }

        public void OnPacket(TelemetryPacket packet)
        {
            lock (_gate)
            {
                _timestamps.Add(packet.TimestampMS);
            }
        }
    }

    private sealed class ThrowingObserver : IPacketObserver
    {
        public int Calls;

        public void OnPacket(TelemetryPacket packet)
        {
            Calls++;
            throw new InvalidOperationException("observer bug");
        }
    }

    /// <summary>Remembers what <c>Latest</c> and <c>ReceivedCount</c> said at the moment it was called.</summary>
    private sealed class ProbeObserver : IPacketObserver
    {
        public TelemetryService? Service;
        public readonly List<(long Count, uint? LatestTimestamp)> Seen = [];

        public void OnPacket(TelemetryPacket packet) => Seen.Add((Service!.ReceivedCount, Service.Latest?.TimestampMS));
    }

    private static TelemetryPacket At(uint ms) => TestPackets.Sample() with { TimestampMS = ms };

    [Fact]
    public async Task Every_packet_reaches_every_observer_in_order_not_just_the_latest()
    {
        var first = new RecordingObserver();
        var second = new RecordingObserver();
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake, observers: [first, second]);
        await service.StartAsync();

        for (uint ms = 10; ms <= 100; ms += 10)
        {
            fake.Publish(At(ms)); // ten packets published back to back: a 20 Hz sampler of Latest would see few of them
        }

        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 10));
        uint[] expected = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        Assert.Equal(expected, first.Timestamps);
        Assert.Equal(expected, second.Timestamps);
        await service.StopAsync();
    }

    [Fact]
    public async Task Observers_run_before_the_packet_is_published_as_Latest_and_counted()
    {
        var probe = new ProbeObserver();
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake, observers: [probe]);
        probe.Service = service;
        await service.StartAsync();

        fake.Publish(At(10));
        fake.Publish(At(20));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 2));

        // While observing packet 1 nothing was counted or published yet: once ReceivedCount shows N, the engines have seen N.
        Assert.Equal([(0L, (uint?)null), (1L, (uint?)10u)], probe.Seen);
        await service.StopAsync();
    }

    [Fact]
    public async Task A_throwing_observer_is_isolated_the_loop_the_other_observers_and_Latest_keep_working()
    {
        var logger = new ListLogger<TelemetryService>();
        var faulty = new ThrowingObserver();
        var healthy = new RecordingObserver();
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake, logger, observers: [faulty, healthy]);
        await service.StartAsync();

        for (uint ms = 1; ms <= 5; ms++)
        {
            fake.Publish(At(ms));
        }

        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 5));
        Assert.True(service.IsRunning);
        Assert.Null(service.Fault); // an observer bug is not a source fault
        Assert.Equal(5, faulty.Calls); // it keeps being called: one bad packet should not silence an engine forever
        Assert.Equal<uint>([1, 2, 3, 4, 5], healthy.Timestamps); // the next observer still saw every packet
        Assert.Equal(5u, service.Latest?.TimestampMS);
        await service.StopAsync();
    }

    [Fact]
    public async Task A_failing_observer_is_logged_once_not_on_every_packet()
    {
        var logger = new ListLogger<TelemetryService>();
        var faulty = new ThrowingObserver();
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake, logger, observers: [faulty]);
        await service.StartAsync();

        for (uint ms = 1; ms <= 50; ms++)
        {
            fake.Publish(At(ms));
        }

        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 50));
        var errors = logger.Entries.Where(e => e.Level == LogLevel.Error && e.Message.Contains(nameof(ThrowingObserver))).ToList();
        Assert.Single(errors); // 50 failures, one line (a bug that fires at 60 Hz must not flood the log)
        Assert.IsType<InvalidOperationException>(errors[0].Exception);
        await service.StopAsync();
    }

    [Fact]
    public async Task Without_observers_the_service_behaves_as_before()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        await service.StartAsync();

        fake.Publish(At(7));

        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        Assert.Equal(7u, service.Latest?.TimestampMS);
        await service.StopAsync();
    }
}
