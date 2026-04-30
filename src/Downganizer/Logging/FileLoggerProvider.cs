using System.Text;

namespace Downganizer.Logging;

/// <summary>
/// Bare-metal rolling daily file logger. No external dependencies, no allocations
/// in the hot path beyond the message StringBuilder.
///
/// Files: C:\Downganizer\logs\downganizer-YYYY-MM-DD.log, append-only.
/// Locking: a single object lock around File.AppendAllText is plenty for our throughput
/// (a few writes per minute under normal use). All logging errors are swallowed -
/// a logging failure must never take the service down.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDir;
    private readonly object _writeLock = new();

    public FileLoggerProvider(string logDir)
    {
        _logDir = logDir;
        Directory.CreateDirectory(_logDir);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { /* nothing to release - we open/close per write */ }

    /// <summary>
    /// Write a single line to today's log file. The path is recomputed every write,
    /// which makes the daily roll effectively free (no timer, no roll detection logic).
    /// </summary>
    internal void Write(string category, LogLevel level, string message, Exception? ex)
    {
        var sb = new StringBuilder(256)
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(LevelLabel(level)).Append("] ")
            .Append(category).Append(": ")
            .Append(message);

        if (ex != null)
        {
            sb.AppendLine().Append(ex);
        }
        sb.AppendLine();

        var path = Path.Combine(_logDir, $"downganizer-{DateTime.Now:yyyy-MM-dd}.log");

        lock (_writeLock)
        {
            try
            {
                File.AppendAllText(path, sb.ToString());
            }
            catch
            {
                // Logging must never throw - swallow.
            }
        }
    }

    private static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Trace       => "TRACE",
        LogLevel.Debug       => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning     => "WARN ",
        LogLevel.Error       => "ERROR",
        LogLevel.Critical    => "CRIT ",
        _                    => "?    ",
    };
}

/// <summary>The per-category ILogger handed out by the provider.</summary>
internal sealed class FileLogger : ILogger
{
    private readonly FileLoggerProvider _provider;
    private readonly string _category;

    public FileLogger(FileLoggerProvider provider, string category)
    {
        _provider = provider;
        _category = category;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
        => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        _provider.Write(_category, logLevel, message, exception);
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }
}
