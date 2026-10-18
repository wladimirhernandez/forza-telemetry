using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// Source-generated log messages of the app shell and <see cref="TelemetryService"/> (same technique as the
/// <c>Log</c> class in ForzaTelemetry.Udp: no per-call allocation, and every message is declared in one place).
/// </summary>
internal static partial class AppLog
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "{AppName} {Version} starting on {OS} (.NET {Runtime}).")]
    public static partial void Starting(ILogger logger, string appName, string version, string os, string runtime);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Data Out candidates (this PC's LAN IPv4 addresses): {Addresses}.")]
    public static partial void LanAddresses(ILogger logger, string addresses);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "{AppName} exiting.")]
    public static partial void Exiting(ILogger logger, string appName);

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

    // ---- Settings (120s) ----

    [LoggerMessage(EventId = 120, Level = LogLevel.Information, Message = "No settings file at {Path}; using the defaults.")]
    public static partial void SettingsMissing(ILogger logger, string path);

    [LoggerMessage(EventId = 121, Level = LogLevel.Warning, Message = "Could not read the settings file {Path}; using the defaults.")]
    public static partial void SettingsUnreadable(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 122, Level = LogLevel.Warning, Message = "The settings file {Path} is corrupt; using the defaults (a copy is kept next to it as .corrupt).")]
    public static partial void SettingsCorrupt(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 123, Level = LogLevel.Warning, Message = "The settings file was written by a newer version (schema {Found}, this build reads {Supported}); fields it does not know are kept and written back.")]
    public static partial void SettingsFromNewerVersion(ILogger logger, int found, int supported);

    [LoggerMessage(EventId = 124, Level = LogLevel.Information, Message = "Settings loaded from {Path}.")]
    public static partial void SettingsLoaded(ILogger logger, string path);

    [LoggerMessage(EventId = 125, Level = LogLevel.Information, Message = "Settings saved to {Path}.")]
    public static partial void SettingsSaved(ILogger logger, string path);

    [LoggerMessage(EventId = 126, Level = LogLevel.Warning, Message = "Could not save the settings to {Path}; the change applies until the app closes.")]
    public static partial void SettingsSaveFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 127, Level = LogLevel.Information, Message = "UDP port changed from {OldPort} to {NewPort}; restarting the listener if it is running.")]
    public static partial void PortChanged(ILogger logger, int oldPort, int newPort);

    [LoggerMessage(EventId = 128, Level = LogLevel.Error, Message = "Restarting the listener after a port change failed.")]
    public static partial void PortRestartFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 129, Level = LogLevel.Warning, Message = "Could not open the folder {Path}.")]
    public static partial void OpenFolderFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 130, Level = LogLevel.Information, Message = "Display language: {Language} (UI culture {Culture}).")]
    public static partial void LanguageApplied(ILogger logger, string language, string culture);

    [LoggerMessage(EventId = 131, Level = LogLevel.Information, Message = "Restarting the application on request.")]
    public static partial void Restarting(ILogger logger);

    [LoggerMessage(EventId = 132, Level = LogLevel.Error, Message = "Could not start the new instance while restarting.")]
    public static partial void RestartFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 133, Level = LogLevel.Warning, Message = "Ignoring the unusable value of setting '{Field}' in {Path}; that setting uses its default.")]
    public static partial void SettingsFieldRejected(ILogger logger, string field, string path);

    // ---- Network profile (140s) ----

    [LoggerMessage(EventId = 140, Level = LogLevel.Warning, Message = "Could not read the Windows network profile; the Public-network warning is skipped.")]
    public static partial void NetworkProfileUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 141, Level = LogLevel.Information, Message = "Network profile of {Address}: {Category}.")]
    public static partial void NetworkProfile(ILogger logger, string address, string category);
}
