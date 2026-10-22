using Serilog.Core;
using Serilog.Events;

namespace SkyrimModPacker.Infrastructure.Logging;

/// <summary>
/// Represents a structured log entry captured in memory.
/// </summary>
public sealed record LogEntry(
    DateTime Timestamp,
    LogEventLevel Level,
    string Message,
    string? ExceptionMessage = null)
{
    public string FormattedTimestamp => Timestamp.ToString("HH:mm:ss.fff");

    public string LevelBadge => Level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "LOG"
    };
}

/// <summary>
/// Service interface for observing live log events in the UI.
/// </summary>
public interface ILogEventService
{
    /// <summary>Fired whenever a new log event is emitted.</summary>
    event Action<LogEntry>? LogReceived;

    /// <summary>Gets recent buffered log entries.</summary>
    IReadOnlyList<LogEntry> GetRecentEntries();

    /// <summary>Clears the log buffer.</summary>
    void Clear();
}

/// <summary>
/// In-memory log event service and Serilog sink.
/// Captures logs for display in the live UI log viewer.
/// </summary>
public sealed class InMemoryLogSink : ILogEventSink, ILogEventService
{
    private const int MaxBufferSize = 1000;
    private readonly List<LogEntry> _buffer = [];
    private readonly object _lock = new();

    public event Action<LogEntry>? LogReceived;

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage();
        var exMessage = logEvent.Exception?.ToString();

        var entry = new LogEntry(
            logEvent.Timestamp.LocalDateTime,
            logEvent.Level,
            message,
            exMessage);

        lock (_lock)
        {
            if (_buffer.Count >= MaxBufferSize)
            {
                _buffer.RemoveAt(0);
            }
            _buffer.Add(entry);
        }

        LogReceived?.Invoke(entry);
    }

    /// <inheritdoc />
    public IReadOnlyList<LogEntry> GetRecentEntries()
    {
        lock (_lock)
        {
            return _buffer.ToList();
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
        }
    }
}
