using System.Text;
using Microsoft.Extensions.Logging;

namespace Maieutics;

/// <summary>
/// Opt-in file logging sink used by CI and local diagnostics. Production hosts stay console-only
/// unless <c>MAIEUTICS_LOG_DIR</c> is set, in which case <see cref="MaieuticsHost"/> registers this
/// provider and every record that passes the Microsoft.Extensions.Logging filter rules is appended
/// to <c>maieutics-{pid}.log</c> in that directory. The provider never filters by level itself and
/// does not rotate, truncate, or bound the file — retention is the operator's responsibility
/// (CI deletes nothing; artifacts expire after their configured retention days).
/// </summary>
/// <param name="logDirectory">Directory that receives the log file; created on demand.</param>
internal sealed class FileLoggerProvider(string logDirectory) : ILoggerProvider
{
    // ReadWrite sharing lets multiple in-process hosts (test runs) and a concurrent reader
    // hold the same file; each handle appends, so writers must not truncate on open.
    private const FileShare SharedAccess = FileShare.ReadWrite | FileShare.Delete;

    private readonly Lock gate = new();
    private readonly StreamWriter writer = OpenWriter(logDirectory);
    private int disposed;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        writer.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static StreamWriter OpenWriter(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(logDirectory, $"maieutics-{Environment.ProcessId}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, SharedAccess);
        // No BOM: append mode would re-emit it at every reopen point in the middle of the file.
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    private void Write(LogLevel logLevel, string category, string message, Exception? exception)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {logLevel}] {category}: {message}";
        lock (gate)
        {
            // A record racing disposal is dropped rather than thrown into the logging pipeline.
            if (Volatile.Read(ref disposed) != 0) return;
            writer.WriteLine(line);
            if (exception is not null)
            {
                writer.WriteLine(exception.ToString());
            }
        }
    }

    /// <summary>Appends provider-formatted records for one category; scopes are not supported.</summary>
    private sealed class FileLogger(FileLoggerProvider provider, string categoryName) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            provider.Write(logLevel, categoryName, formatter(state, exception), exception);
        }
    }
}
