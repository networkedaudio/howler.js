using System.Collections.Concurrent;

namespace RTPTransmitter.Services;

/// <summary>
/// A single captured log entry for display in the UI.
/// </summary>
public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public LogLevel Level { get; init; }
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
}

/// <summary>
/// Thread-safe circular buffer that stores the most recent log entries
/// and notifies subscribers when new entries arrive.
/// </summary>
public sealed class LogBuffer
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly int _maxEntries;

    public LogBuffer(int maxEntries = 500)
    {
        _maxEntries = maxEntries;
    }

    /// <summary>
    /// Raised (on an arbitrary thread) whenever a new entry is added.
    /// </summary>
    public event Action? OnNewEntry;

    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry);

        // Trim to capacity
        while (_entries.Count > _maxEntries && _entries.TryDequeue(out _)) { }

        OnNewEntry?.Invoke();
    }

    /// <summary>
    /// Returns a snapshot of the current entries (oldest first).
    /// </summary>
    public IReadOnlyList<LogEntry> GetEntries() => [.. _entries];

    /// <summary>
    /// Clears all buffered log entries.
    /// </summary>
    public void Clear() => _entries.Clear();
}

/// <summary>
/// ILoggerProvider that forwards log messages to a shared <see cref="LogBuffer"/>.
/// </summary>
[ProviderAlias("LogBuffer")]
public sealed class LogBufferProvider : ILoggerProvider
{
    private readonly LogBuffer _buffer;
    private readonly ConcurrentDictionary<string, LogBufferLogger> _loggers = new();

    public LogBufferProvider(LogBuffer buffer)
    {
        _buffer = buffer;
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new LogBufferLogger(name, _buffer));

    public void Dispose() => _loggers.Clear();

    private sealed class LogBufferLogger(string category, LogBuffer buffer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (exception is not null)
                message = $"{message} — {exception.GetType().Name}: {exception.Message}";

            // Shorten the category to the last segment for readability
            var shortCategory = category;
            var lastDot = category.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < category.Length - 1)
                shortCategory = category[(lastDot + 1)..];

            buffer.Add(new LogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = logLevel,
                Category = shortCategory,
                Message = message
            });
        }
    }
}
