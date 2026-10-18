using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// Owns the app's logging backend: a Serilog rolling daily file exposed as a Microsoft.Extensions.Logging
/// <see cref="ILoggerFactory"/> (the type <c>ForzaTelemetry.Udp</c> asks for). Disposing it flushes and closes the file.
/// </summary>
public sealed class AppLogging : IDisposable
{
    // Two weeks of daily files is plenty to diagnose "it stopped working yesterday" without growing forever.
    private const int RetainedFiles = 14;

    private AppLogging(ILoggerFactory factory, string? logDirectory)
    {
        Factory = factory;
        LogDirectory = logDirectory;
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\ForzaTelemetry\logs</c>. Deliberately NOT next to the exe: once installed under
    /// Program Files that folder is read-only for a normal user, so the first write would fail. LocalAppData is
    /// always writable, per user, and survives upgrades of the install directory.
    /// </summary>
    public static string DefaultLogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForzaTelemetry", "logs");

    /// <summary>The factory to create <see cref="ILogger{T}"/> instances from. Never null, even if the file could not be opened.</summary>
    public ILoggerFactory Factory { get; }

    /// <summary>Folder the log files are written to, or null when file logging could not be started.</summary>
    public string? LogDirectory { get; }

    /// <summary>
    /// Creates the file logger in <paramref name="logDirectory"/>. Never throws: a diagnostics feature must not be
    /// able to stop the app from starting (read-only disk, bad policy, full drive), so on failure it falls back to a
    /// logger that discards everything and <see cref="LogDirectory"/> is null.
    /// </summary>
    public static AppLogging Create(string logDirectory)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);

            // An explicit logger instance (not the static Serilog.Log.Logger): nothing else needs ambient access,
            // and dispose: true makes disposing the factory the one place that flushes and closes the file.
            // shared: true lets a second instance (which will fail to bind the port anyway) still write its error.
            var serilog = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(logDirectory, "telemetry-.log"), // Serilog inserts the date before ".log"
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: RetainedFiles,
                    shared: true)
                .CreateLogger();

            return new AppLogging(LoggerFactory.Create(builder => builder.AddSerilog(serilog, dispose: true)), logDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new AppLogging(NullLoggerFactory.Instance, null);
        }
    }

    /// <summary>Flushes pending entries and closes the file.</summary>
    public void Dispose() => Factory.Dispose();
}
