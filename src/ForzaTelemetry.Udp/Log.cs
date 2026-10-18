using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.Udp;

/// <summary>
/// Source-generated logging methods for <see cref="UdpTelemetryListener"/>.
/// </summary>
/// <remarks>
/// Using <c>[LoggerMessage]</c> partial methods instead of calling <c>ILogger.LogInformation(...)</c>
/// etc. directly avoids the params-array allocation and boxing that ordinary "interpolated string"
/// logging calls incur on every single invocation - the compiler generates a dedicated, allocation-free
/// method body for each one instead. <see cref="UdpTelemetryListener.ReadPacketsAsync"/> sits on a path
/// that runs up to ~60 times/second while Forza Horizon 6 is actively sending telemetry, so even though
/// most of the specific messages below are deliberately low-frequency by design (first-per-sender,
/// rate-limited warnings, once-per-heartbeat), keeping the whole logging surface source-generated means
/// nobody has to remember "don't accidentally add an allocating log call to the hot path" later.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Bound local endpoint {LocalEndPoint}.")]
    public static partial void ListenerBound(ILogger logger, IPEndPoint localEndPoint);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "First datagram from {Remote} ({Length} bytes).")]
    public static partial void FirstDatagramFromSender(ILogger logger, IPEndPoint remote, int length);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Discarded a {Length}-byte datagram from {Remote}: expected exactly {ExpectedSize} bytes (discarded so far: {DiscardedCount}).")]
    public static partial void WrongSizeDatagram(ILogger logger, int length, IPEndPoint? remote, int expectedSize, long discardedCount);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "Socket error on receive: {SocketErrorCode} ({Message}). Still listening (errors so far: {ErrorCount}).")]
    public static partial void SocketReceiveError(ILogger logger, SocketError socketErrorCode, string message, long errorCount);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "No datagrams received for {Seconds}s - check Data Out IP/port, firewall, VPN LAN access.")]
    public static partial void NoDatagramsReceived(ILogger logger, int seconds);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Datagrams flowing again.")]
    public static partial void DatagramsFlowingAgain(ILogger logger);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Error,
        Message = "Datagram observer {Observer} threw (failure number {Failures}); the receive loop continues.")]
    public static partial void DatagramObserverFailed(ILogger logger, Exception exception, string observer, int failures);
}
