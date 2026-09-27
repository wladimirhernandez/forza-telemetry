using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.Udp.Tests;

/// <summary>
/// Minimal, dependency-free fake <see cref="ILogger{TCategoryName}"/> that just records every
/// logged entry's level and rendered message text, so tests can assert "a message like this was
/// logged" without pulling in a mocking library for something this simple.
/// </summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private readonly object _gate = new();

    /// <summary>A snapshot of every entry logged so far, in the order it was logged.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    // No scoped logging is used anywhere in this project, so there is nothing meaningful to track
    // here — returning null is exactly what NullLogger itself does for the same reason.
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (_gate)
        {
            _entries.Add((logLevel, message));
        }
    }

    /// <summary>
    /// True if any entry at or above <paramref name="minimumLevel"/> contains
    /// <paramref name="substring"/> (case-insensitive). A substring match — rather than an exact
    /// match — is deliberate: tests should stay stable if a message's wording is tweaked slightly,
    /// as long as the key fact it reports is still there.
    /// </summary>
    public bool HasEntryContaining(string substring, LogLevel minimumLevel = LogLevel.Trace) =>
        Entries.Any(e => e.Level >= minimumLevel && e.Message.Contains(substring, StringComparison.OrdinalIgnoreCase));
}
