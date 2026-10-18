using System.Net;
using System.Net.Sockets;
using ForzaTelemetry.Core;
using ForzaTelemetry.Udp;

namespace ForzaTelemetry.Udp.Tests;

/// <summary>The sender hook of the listener: how a UI learns who is sending, without parsing anything itself.</summary>
public sealed class UdpSenderObserverTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task The_observer_sees_the_sender_of_every_datagram_even_one_that_does_not_parse()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var seen = new List<IPEndPoint>();
        await using var listener = new UdpTelemetryListener(
            port: 0, bindAddress: IPAddress.Loopback, senderObserver: seen.Add);
        var target = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        using var sender = new UdpClient(0);
        var senderPort = ((IPEndPoint)sender.Client.LocalEndPoint!).Port;
        await sender.SendAsync(new byte[100], target); // wrong size: discarded by the parser, still a datagram from this sender
        await sender.SendAsync(new byte[100], target);

        var count = 0;
        await foreach (var _ in listener.ReadDatagramsAsync(cts.Token))
        {
            if (++count == 2)
            {
                break;
            }
        }

        Assert.Equal(2, seen.Count);
        Assert.All(seen, endpoint =>
        {
            Assert.Equal(IPAddress.Loopback, endpoint.Address);
            Assert.Equal(senderPort, endpoint.Port);
        });
    }

    [Fact]
    public async Task An_observer_that_throws_never_ends_the_receive_loop()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await using var listener = new UdpTelemetryListener(
            port: 0, bindAddress: IPAddress.Loopback, senderObserver: _ => throw new InvalidOperationException("observer bug"));
        var target = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        using var sender = new UdpClient(0);
        await sender.SendAsync(new byte[10], target);
        await sender.SendAsync(new byte[20], target);

        var lengths = new List<int>();
        await foreach (var datagram in listener.ReadDatagramsAsync(cts.Token))
        {
            lengths.Add(datagram.Length);
            if (lengths.Count == 2)
            {
                break;
            }
        }

        Assert.Equal([10, 20], lengths);
    }
}
