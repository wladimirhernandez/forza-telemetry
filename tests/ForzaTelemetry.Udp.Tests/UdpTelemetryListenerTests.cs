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
