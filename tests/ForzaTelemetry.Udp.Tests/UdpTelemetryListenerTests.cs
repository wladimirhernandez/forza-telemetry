using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ForzaTelemetry.Core;
using ForzaTelemetry.Udp;

namespace ForzaTelemetry.Udp.Tests;

/// <summary>
/// Integration tests exercising <see cref="UdpTelemetryListener"/> against a real UDP socket on
/// loopback: a genuine end-to-end exercise of bind -&gt; send -&gt; receive -&gt; parse, closer to
/// production behavior than mocking the socket would be, at the cost of touching real (if local)
/// networking.
/// </summary>
public sealed class UdpTelemetryListenerTests
{
    // No test here should ever be able to hang on a network operation that never completes; every
    // test binds its CancellationTokenSource to this timeout so a regression fails fast instead of
    // hanging CI indefinitely.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ReadPacketsAsync_ValidDatagram_YieldsParsedPacketWithExpectedValues()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        // port: 0 lets the OS assign a free port, so this test never collides with another one
        // (or with a real Forza Horizon 6 instance) running on the fixed default port 5300.
        await using var listener = new UdpTelemetryListener(port: 0, bindAddress: IPAddress.Loopback);
        var listenerEndpoint = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        using var sender = new UdpClient(0);
        var datagram = BuildValidPacket(currentEngineRpm: 6500f, gear: 3);
        await sender.SendAsync(datagram, listenerEndpoint);

        TelemetryPacket? received = null;
        await foreach (var packet in listener.ReadPacketsAsync(cts.Token))
        {
            received = packet;
            break;
        }

        Assert.NotNull(received);
        Assert.Equal(6500f, received!.CurrentEngineRpm);
        Assert.Equal((byte)3, received.Gear);
    }

    [Fact]
    public async Task ReadPacketsAsync_WrongSizeDatagram_IsDiscardedAndNextValidOneStillArrives()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await using var listener = new UdpTelemetryListener(port: 0, bindAddress: IPAddress.Loopback);
        var listenerEndpoint = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        using var sender = new UdpClient(0);
        await sender.SendAsync(new byte[100], listenerEndpoint); // wrong size: not a valid 324-byte packet
        var validDatagram = BuildValidPacket(currentEngineRpm: 1234f, gear: 5);
        await sender.SendAsync(validDatagram, listenerEndpoint);

        TelemetryPacket? received = null;
        await foreach (var packet in listener.ReadPacketsAsync(cts.Token))
        {
            received = packet;
            break;
        }

        Assert.NotNull(received);
        Assert.Equal(1234f, received!.CurrentEngineRpm);
        Assert.Equal((byte)5, received.Gear);
        Assert.Equal(1, listener.DiscardedCount);
    }

    [Fact]
    public async Task ReadPacketsAsync_Cancellation_EndsEnumerationCleanlyWithoutThrowing()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await using var listener = new UdpTelemetryListener(port: 0, bindAddress: IPAddress.Loopback);

        // No datagram is ever sent, so without this cancellation ReadPacketsAsync would block on
        // ReceiveAsync forever. This is both the test's stimulus and its own safety timeout.
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var receivedAny = false;
        var threw = false;
        try
        {
            await foreach (var _ in listener.ReadPacketsAsync(cts.Token))
            {
                receivedAny = true;
            }
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        Assert.False(receivedAny);
        Assert.False(threw); // documented behavior: cancellation ends the loop, it does not throw
    }

    [Fact]
    public async Task ReadPacketsAsync_ReEnumerateAfterCancellationWhileReceivePending_StillReceivesLaterDatagram()
    {
        // Regression test for T5 review finding #1: a receive that is still pending when its
        // enumeration's token is cancelled used to leave a stale, now-cancelled-token-bound task
        // behind for the NEXT enumeration on the same listener, which then ended immediately
        // instead of ever reading a real datagram.
        //
        // The bug only manifests when the heartbeat delay - not the receive - is the task that
        // wins Task.WhenAny at the moment of cancellation. Racing that deterministically against a
        // REAL socket receive would make this test timing-dependent, so instead the very first
        // receive call across the listener's whole lifetime is replaced with one that deliberately
        // never completes and never observes its token: that guarantees the heartbeat delay is the
        // only one of the two race participants that can ever become canceled, so it always "wins".
        var receiveCallCount = 0;
        var firstReceiveNeverCompletes = new TaskCompletionSource<UdpReceiveResult>();

        await using var listener = new UdpTelemetryListener(
            0,
            IPAddress.Loopback,
            logger: null,
            heartbeatOptions: null,
            wrapReceive: realReceive => ct =>
            {
                if (Interlocked.Increment(ref receiveCallCount) == 1)
                {
                    return new ValueTask<UdpReceiveResult>(firstReceiveNeverCompletes.Task);
                }

                return realReceive(ct);
            });
        var listenerEndpoint = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        // First enumeration: starts the receive that never completes above, then gets cancelled
        // while it is still pending.
        using (var firstEnumerationCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await foreach (var _ in listener.ReadPacketsAsync(firstEnumerationCts.Token))
            {
                Assert.Fail("No datagram was ever sent during the first enumeration.");
            }
        }

        // Second enumeration on the SAME listener with a brand-new token. Before the T5 fix, the
        // stale pending receive from above would be reused here (bound to the first, now-cancelled
        // token) and immediately fault with OperationCanceledException, ending this enumeration
        // before the datagram sent below is ever read.
        using var secondEnumerationCts = new CancellationTokenSource(TestTimeout);
        using var sender = new UdpClient(0);
        var datagram = BuildValidPacket(currentEngineRpm: 4242f, gear: 2);

        TelemetryPacket? received = null;
        var secondEnumeration = Task.Run(async () =>
        {
            await foreach (var packet in listener.ReadPacketsAsync(secondEnumerationCts.Token))
            {
                received = packet;
                break;
            }
        });

        // Give the second enumeration's receive a moment to actually start before sending, so the
        // datagram below is not sent before anything is listening for it.
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await sender.SendAsync(datagram, listenerEndpoint);
        await secondEnumeration;

        Assert.NotNull(received);
        Assert.Equal(4242f, received!.CurrentEngineRpm);
        Assert.Equal((byte)2, received.Gear);
    }

    /// <summary>
    /// Builds a 324-byte fake telemetry packet, writing only the two fields these tests care
    /// about at their spec offsets (CurrentEngineRpm: float at offset 16, Gear: byte at offset
    /// 319) — independently of <see cref="TelemetryParser"/>'s own sequential cursor, the same
    /// way ForzaTelemetry.Core.Tests' PacketBuilder does. The rest of the buffer stays zero-filled,
    /// which is a valid (if uninteresting) packet.
    /// </summary>
    private static byte[] BuildValidPacket(float currentEngineRpm, byte gear)
    {
        var buffer = new byte[TelemetryParser.PacketSize];
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(16, sizeof(float)), currentEngineRpm);
        buffer[319] = gear;
        return buffer;
    }
}
