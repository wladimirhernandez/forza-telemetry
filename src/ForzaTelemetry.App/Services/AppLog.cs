using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// Source-generated log messages of the app shell and <see cref="TelemetryService"/> (same technique as the
/// <c>Log</c> class in ForzaTelemetry.Udp: no per-call allocation, and every message is declared in one place).
/// </summary>
internal static partial class AppLog
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Forza Telemetry {Version} starting on {OS} (.NET {Runtime}).")]
    public static partial void Starting(ILogger logger, string version, string os, string runtime);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Data Out candidates (this PC's LAN IPv4 addresses): {Addresses}.")]
    public static partial void LanAddresses(ILogger logger, string addresses);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "Forza Telemetry exiting.")]
    public static partial void Exiting(ILogger logger);

    [LoggerMessage(EventId = 103, Level = LogLevel.Critical, Message = "Startup failed.")]
    public static partial void StartupFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 104, Level = LogLevel.Error, Message = "Unhandled exception on the UI thread.")]
    public static partial void UnhandledUiException(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 105, Level = LogLevel.Error, Message = "Unobserved task exception (a faulted Task nobody awaited).")]
    public static partial void UnobservedTaskException(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 106, Level = LogLevel.Error, Message = "Stopping the telemetry service during shutdown failed.")]
    public static partial void ShutdownStopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 110, Level = LogLevel.Information, Message = "Telemetry receive loop started.")]
    public static partial void ServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 111, Level = LogLevel.Information, Message = "Telemetry receive loop stopped ({ReceivedCount} packets received in total).")]
    public static partial void ServiceStopped(ILogger logger, long receivedCount);

    [LoggerMessage(EventId = 112, Level = LogLevel.Error, Message = "Could not start the telemetry source.")]
    public static partial void StartFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 113, Level = LogLevel.Error, Message = "The telemetry receive loop faulted.")]
    public static partial void LoopFaulted(ILogger logger, Exception exception);
}
