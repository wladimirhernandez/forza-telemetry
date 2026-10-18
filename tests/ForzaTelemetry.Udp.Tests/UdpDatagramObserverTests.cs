using System.Net;
using System.Net.Sockets;
using ForzaTelemetry.Core;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.Udp.Tests;

/// <summary>
/// The raw-datagram tap of <see cref="UdpTelemetryListener"/>: every observer sees every datagram, before parsing, in order,
/// and one that throws can neither stop the receive loop nor starve the others.
/// </summary>
public sealed class UdpDatagramObserverTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private sealed class RecordingObserver : IDatagramObserver
    {
        public List<byte[]> Seen { get; } = [];

        public List<int> Threads { get; } = [];

        public void OnDatagram(ReadOnlyMemory<byte> datagram)
        {
            Seen.Add(datagram.ToArray());
            Threads.Add(Environment.CurrentManagedThreadId);
        }
    }

    private sealed class ThrowingObserver : IDatagramObserver
    {
        public int Calls { get; private set; }

        public void OnDatagram(ReadOnlyMemory<byte> datagram)
        {
            Calls++;
            throw new InvalidOperationException("observer bug");
        }
    }

    private static byte[] Datagram(int length, byte fill) => Enumerable.Repeat(fill, length).ToArray();

    [Fact]
    public async Task Every_observer_sees_every_raw_datagram_in_order_including_ones_the_parser_rejects()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var first = new RecordingObserver();
        var second = new RecordingObserver();
        await using var listener = new UdpTelemetryListener(
            port: 0, bindAddress: IPAddress.Loopback, datagramObservers: [first, second]);
        var endpoint = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        using var sender = new UdpClient(0);
        await sender.SendAsync(Datagram(100, 1), endpoint); // wrong size: the parser will discard it, a recording must keep it
        await sender.SendAsync(Datagram(TelemetryParser.PacketSize, 2), endpoint);
        await sender.SendAsync(Datagram(TelemetryParser.PacketSize, 3), endpoint);

        var parsed = 0;
        await foreach (var _ in listener.ReadPacketsAsync(cts.Token))
        {
            if (++parsed == 2)
            {
                break;
            }
        }

        foreach (var observer in new[] { first, second })
        {
            Assert.Equal(3, observer.Seen.Count);
            Assert.Equal(100, observer.Seen[0].Length);
            Assert.All(observer.Seen[1], b => Assert.Equal(2, b));
            Assert.All(observer.Seen[2], b => Assert.Equal(3, b));
        }

        Assert.Equal(1, listener.DiscardedCount);
    }

    [Fact]
    public async Task Observers_run_on_the_thread_that_receives_never_on_one_of_their_own()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var observer = new RecordingObserver();
        await using var listener = new UdpTelemetryListener(port: 0, bindAddress: IPAddress.Loopback, datagramObservers: [observer]);
        var endpoint = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        using var sender = new UdpClient(0);
        await sender.SendAsync(Datagram(TelemetryParser.PacketSize, 1), endpoint);
        await sender.SendAsync(Datagram(TelemetryParser.PacketSize, 1), endpoint);

        var count = 0;
        var consumerThreads = new List<int>();
        await foreach (var _ in listener.ReadDatagramsAsync(cts.Token))
        {
            consumerThreads.Add(Environment.CurrentManagedThreadId);
            if (++count == 2)
            {
                break;
            }
        }

        // The observer is called inside the enumeration, right before each datagram is yielded: the same thread as the
        // consumer's body for that datagram (a pool thread, since nothing here has a synchronization context).
        Assert.Equal(consumerThreads, observer.Threads);
    }

    [Fact]
    public async Task An_observer_that_throws_is_logged_and_skipped_without_stopping_the_loop_or_the_next_observer()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var bad = new ThrowingObserver();
        var good = new RecordingObserver();
        var logger = new ListLogger<UdpTelemetryListener>();
        await using var listener = new UdpTelemetryListener(
            port: 0, bindAddress: IPAddress.Loopback, logger: logger, datagramObservers: [bad, good]);
        var endpoint = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        using var sender = new UdpClient(0);
        for (var i = 0; i < 3; i++)
        {
            await sender.SendAsync(Datagram(TelemetryParser.PacketSize, (byte)i), endpoint);
        }

        var parsed = 0;
        await foreach (var _ in listener.ReadPacketsAsync(cts.Token))
        {
            if (++parsed == 3)
            {
                break;
            }
        }

        Assert.Equal(3, parsed); // the live (parsed) path never noticed
        Assert.Equal(3, bad.Calls); // it was still offered every datagram
        Assert.Equal(3, good.Seen.Count); // and the observer after it was never starved
        var failures = logger.Entries.Where(e => e.Level == LogLevel.Error && e.Message.Contains("ThrowingObserver")).ToList();
        Assert.Single(failures); // throttled: the first failure only, not one line per datagram
    }

    [Fact]
    public async Task Without_observers_the_listener_behaves_exactly_as_before()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await using var listener = new UdpTelemetryListener(port: 0, bindAddress: IPAddress.Loopback);
        var endpoint = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        using var sender = new UdpClient(0);
        await sender.SendAsync(Datagram(TelemetryParser.PacketSize, 0), endpoint);

        await foreach (var _ in listener.ReadPacketsAsync(cts.Token))
        {
            return;
        }

        Assert.Fail("The datagram was never delivered.");
    }
}
