using System.Net;
using System.Net.Sockets;
using ForzaTelemetry.Core;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.Udp.Tests;

/// <summary>
/// Covers the diagnostic logging added to <see cref="UdpTelemetryListener"/>: first-datagram-per-sender,
/// wrong-size-datagram warnings, and the "no datagrams" / "flowing again" heartbeat. A real
/// <see cref="SocketException"/> from <c>ReceiveAsync</c> is hard to force reliably in a test (it
/// depends on OS/network-stack behavior like Windows' WSAECONNRESET quirk), so that resilience path
/// is exercised manually instead — see the T3 feature doc — and is deliberately not duplicated here.
/// </summary>
public sealed class UdpTelemetryListenerLoggingTests
{
    // Same reasoning as UdpTelemetryListenerTests: every test is bounded so a regression fails fast
    // instead of hanging indefinitely on a network operation that never completes.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ReadPacketsAsync_FirstDatagramFromNewSender_LogsInformation()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var logger = new ListLogger<UdpTelemetryListener>();
        await using var listener = new UdpTelemetryListener(port: 0, bindAddress: IPAddress.Loopback, logger: logger);
        var listenerEndpoint = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        using var sender = new UdpClient(0);
        // A zero-filled buffer is already a valid (if uninteresting) 324-byte packet — see
        // UdpTelemetryListenerTests' BuildValidPacket doc comment for the same observation.
        await sender.SendAsync(new byte[TelemetryParser.PacketSize], listenerEndpoint);

        await foreach (var _ in listener.ReadPacketsAsync(cts.Token))
        {
            break; // one packet is enough to have exercised the "first datagram" log path
        }

        Assert.True(logger.HasEntryContaining("First datagram", LogLevel.Information));
    }

    [Fact]
    public async Task ReadPacketsAsync_WrongSizeDatagram_LogsWarning()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var logger = new ListLogger<UdpTelemetryListener>();
        await using var listener = new UdpTelemetryListener(port: 0, bindAddress: IPAddress.Loopback, logger: logger);
        var listenerEndpoint = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        using var sender = new UdpClient(0);
        await sender.SendAsync(new byte[100], listenerEndpoint); // wrong size: not a valid 324-byte packet
        await sender.SendAsync(new byte[TelemetryParser.PacketSize], listenerEndpoint); // valid, so the loop can move on

        await foreach (var _ in listener.ReadPacketsAsync(cts.Token))
        {
            break;
        }

        Assert.True(logger.HasEntryContaining("Discarded", LogLevel.Warning));
    }

    [Fact]
    public async Task Heartbeat_LogsSilenceThenLogsFlowingAgainOnceADatagramArrives()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var logger = new ListLogger<UdpTelemetryListener>();
        // Short thresholds so this test observes the heartbeat in a fraction of a second instead of
        // waiting on the real production 5 s/10 s values.
        var heartbeatOptions = new HeartbeatOptions(
            InitialSilence: TimeSpan.FromMilliseconds(200),
            RepeatInterval: TimeSpan.FromMilliseconds(200));
        await using var listener = new UdpTelemetryListener(
            port: 0, bindAddress: IPAddress.Loopback, logger: logger, heartbeatOptions: heartbeatOptions);
        var listenerEndpoint = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        // Start enumerating with nothing sent yet: TryReceiveAsync is now awaiting a datagram that
        // never arrives (until this test sends one below), which is exactly when the heartbeat
        // should fire on its own — no second thread or timer needed, just this loop's own await.
        var receiveTask = Task.Run(async () =>
        {
            await foreach (var _ in listener.ReadPacketsAsync(cts.Token))
            {
                break;
            }
        });

        // Give the 200 ms heartbeat threshold time to fire at least once before any datagram exists.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);
        Assert.True(logger.HasEntryContaining("No datagrams received", LogLevel.Information));

        using var sender = new UdpClient(0);
        await sender.SendAsync(new byte[TelemetryParser.PacketSize], listenerEndpoint);

        await receiveTask; // completes once the datagram above is received and parsed
        Assert.True(logger.HasEntryContaining("Datagrams flowing again", LogLevel.Information));
    }
}
