using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App.Tests;

/// <summary>Minimal fake <see cref="ILogger{TCategoryName}"/> that records level, rendered message and exception.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];
    private readonly object _gate = new();

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
