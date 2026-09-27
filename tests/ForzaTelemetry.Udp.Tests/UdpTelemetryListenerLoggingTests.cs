using System.Net;
using System.Net.Sockets;
using ForzaTelemetry.Core;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.Udp.Tests;

/// <summary>
/// Covers the diagnostic logging added to <see cref="UdpTelemetryListener"/>: first-datagram-per-sender,
/// wrong-size-datagram warnings, and the "no datagrams" / "flowing again" heartbeat. A real
/// <see cref="SocketException"/> from <c>ReceiveAsync</c> is hard to force reliably against a real
/// socket (it depends on OS/network-stack behavior like Windows' WSAECONNRESET quirk), so the
/// SocketException-resilience tests below use the internal <c>wrapReceive</c> constructor seam (see
/// T5 review finding #5) to inject one on demand instead of relying on that quirk actually
/// reproducing under test.
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

    [Fact]
    public async Task ReadPacketsAsync_WrongSizeDatagrams_LogsAreRateLimitedToFirstFiveThenEveryHundredth()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var logger = new ListLogger<UdpTelemetryListener>();
        await using var listener = new UdpTelemetryListener(port: 0, bindAddress: IPAddress.Loopback, logger: logger);
        var listenerEndpoint = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        using var sender = new UdpClient(0);

        // 105 wrong-size datagrams: with "first 5 then every 100th", exactly 6 should be logged
        // (discarded counts 1-5, then 100) out of the 105 - proving the rate limit actually
        // suppresses the bulk of a sustained flood instead of just happening to log a handful.
        const int wrongSizeDatagramCount = 105;
        for (var i = 0; i < wrongSizeDatagramCount; i++)
        {
            await sender.SendAsync(new byte[100], listenerEndpoint);
        }
        await sender.SendAsync(new byte[TelemetryParser.PacketSize], listenerEndpoint); // lets the loop move on

        await foreach (var _ in listener.ReadPacketsAsync(cts.Token))
        {
            break;
        }

        Assert.Equal(wrongSizeDatagramCount, listener.DiscardedCount);
        var discardedLogCount = logger.Entries.Count(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Discarded", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(6, discardedLogCount);
    }

    [Fact]
    public async Task ReadPacketsAsync_SocketExceptionOnReceive_DoesNotEndStream_AndLaterDatagramIsStillYielded()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var logger = new ListLogger<UdpTelemetryListener>();

        await using var listener = new UdpTelemetryListener(
            0,
            IPAddress.Loopback,
            logger,
            heartbeatOptions: null,
            wrapReceive: realReceive =>
            {
                var callCount = 0;
                return async ct =>
                {
                    // The very first receive fails like the real Windows WSAECONNRESET quirk this
                    // resilience path exists for (see UdpTelemetryListener.TryReceiveAsync); every
                    // later call behaves normally.
                    if (Interlocked.Increment(ref callCount) == 1)
                    {
                        throw new SocketException((int)SocketError.ConnectionReset);
                    }

                    return await realReceive(ct).ConfigureAwait(false);
                };
            });
        var listenerEndpoint = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        using var sender = new UdpClient(0);
        var datagram = BuildValidPacket(currentEngineRpm: 7777f, gear: 4);

        TelemetryPacket? received = null;
        var receiveTask = Task.Run(async () =>
        {
            await foreach (var packet in listener.ReadPacketsAsync(cts.Token))
            {
                received = packet;
                break;
            }
        });

        // The faulting first receive needs a moment to run (and back off for
        // UdpTelemetryListener's 100 ms SocketErrorBackoff) before the real receive it forwards to
        // is even started.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await sender.SendAsync(datagram, listenerEndpoint);
        await receiveTask;

        Assert.NotNull(received);
        Assert.Equal(7777f, received!.CurrentEngineRpm);
        Assert.Equal((byte)4, received.Gear);
        Assert.True(logger.HasEntryContaining("Socket error on receive", LogLevel.Warning));
    }

    [Fact]
    public async Task ReadPacketsAsync_RepeatedSocketExceptions_AreRateLimitedInLogs()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var logger = new ListLogger<UdpTelemetryListener>();

        const int socketErrorCount = 7;
        await using var listener = new UdpTelemetryListener(
            0,
            IPAddress.Loopback,
            logger,
            heartbeatOptions: null,
            wrapReceive: realReceive =>
            {
                var callCount = 0;
                return async ct =>
                {
                    // The first socketErrorCount calls all fail: with "first 5 then every 100th",
                    // only 5 of these 7 should actually be logged.
                    if (Interlocked.Increment(ref callCount) <= socketErrorCount)
                    {
                        throw new SocketException((int)SocketError.ConnectionReset);
                    }

                    return await realReceive(ct).ConfigureAwait(false);
                };
            });
        var listenerEndpoint = new IPEndPoint(IPAddress.Loopback, listener.LocalPort);

        using var sender = new UdpClient(0);
        var receiveTask = Task.Run(async () =>
        {
            await foreach (var _ in listener.ReadPacketsAsync(cts.Token))
            {
                break;
            }
        });

        // Give all socketErrorCount faulting receives (each backed off by 100 ms) time to run
        // before the real receive they eventually forward to is started.
        await Task.Delay(TimeSpan.FromMilliseconds(200 + (socketErrorCount * 100)));
        await sender.SendAsync(new byte[TelemetryParser.PacketSize], listenerEndpoint);
        await receiveTask;

        var socketErrorLogCount = logger.Entries.Count(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Socket error on receive", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(5, socketErrorLogCount);
    }

    /// <summary>
    /// Same fake-packet layout as <see cref="UdpTelemetryListenerTests.BuildValidPacket"/> - kept as
    /// its own private copy rather than shared, since these two test classes are otherwise
    /// independent and duplicating four lines is simpler than introducing a shared test helper type
    /// for it.
    /// </summary>
    private static byte[] BuildValidPacket(float currentEngineRpm, byte gear)
    {
        var buffer = new byte[TelemetryParser.PacketSize];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(16, sizeof(float)), currentEngineRpm);
        buffer[319] = gear;
        return buffer;
    }
}
