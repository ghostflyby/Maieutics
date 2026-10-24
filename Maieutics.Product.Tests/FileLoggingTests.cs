using Microsoft.Extensions.Logging;

namespace Maieutics.Product.Tests;

/// <summary>Unit tests for the opt-in file logging provider: directory creation, record
/// formatting, exception rendering, append semantics, and single-shot disposal.</summary>
public sealed class FileLoggingTests : IAsyncDisposable
{
    private readonly string _logDirectory = Path.Combine(
        Path.GetTempPath(),
        "maieutics-file-logging-tests",
        Guid.NewGuid().ToString("N"));

    /// <summary>Best-effort teardown: a log file still held by a scanner must not fail a
    /// test, but the retry keeps the common case from leaving the directory behind.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await TempArea.DeleteAsync(_logDirectory, CancellationToken.None);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private string LogFilePath => Path.Combine(_logDirectory, $"maieutics-{Environment.ProcessId}.log");

    [Fact]
    public void CreatingTheProviderCreatesTheLogDirectory()
    {
        Assert.False(Directory.Exists(_logDirectory));

        using var provider = new FileLoggerProvider(_logDirectory);

        Assert.True(Directory.Exists(_logDirectory));
    }

    [Fact]
    public void LoggersFromDifferentCategoriesWriteFormattedRecordLines()
    {
        // The mid-test Dispose is the point of the test (the provider holds the log file
        // exclusively), so the provider is owned explicitly instead of a using scope.
        var provider = new FileLoggerProvider(_logDirectory);
        try
        {
            var first = provider.CreateLogger("Maieutics.Tests.First");
            var second = provider.CreateLogger("Maieutics.Tests.Second");

            first.LogInformation("first message");
            second.LogWarning("second message");

            // Dispose before reading: the provider holds the log file exclusively, and
            // Windows rejects a concurrent read with a sharing violation.
            provider.Dispose();
            var lines = File.ReadAllLines(LogFilePath);
            Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} Information\] Maieutics\.Tests\.First: first message$", lines.Single(line => line.Contains("Maieutics.Tests.First", StringComparison.Ordinal)));
            Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} Warning\] Maieutics\.Tests\.Second: second message$", lines.Single(line => line.Contains("Maieutics.Tests.Second", StringComparison.Ordinal)));
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact]
    public void ExceptionLoggingIncludesTheExceptionTypeOnItsOwnLine()
    {
        // The mid-test Dispose is the point of the test (the provider holds the log file
        // exclusively), so the provider is owned explicitly instead of a using scope.
        var provider = new FileLoggerProvider(_logDirectory);
        try
        {
            var logger = provider.CreateLogger("Maieutics.Tests.Errors");

            logger.LogError(new InvalidOperationException("boom"), "operation failed");

            // Dispose before reading: the provider holds the log file exclusively, and
            // Windows rejects a concurrent read with a sharing violation.
            provider.Dispose();
            var lines = File.ReadAllLines(LogFilePath);
            Assert.Equal(2, lines.Length);
            Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} Error\] Maieutics\.Tests\.Errors: operation failed$", lines[0]);
            Assert.Contains("InvalidOperationException", lines[1]);
            Assert.Contains("boom", lines[1]);
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact]
    public void ASecondProviderOnTheSameDirectoryAppendsWithoutTruncating()
    {
        using (var first = new FileLoggerProvider(_logDirectory))
        {
            first.CreateLogger("Maieutics.Tests.Append").LogInformation("first write");
        }

        using (var second = new FileLoggerProvider(_logDirectory))
        {
            second.CreateLogger("Maieutics.Tests.Append").LogInformation("second write");
        }

        var content = File.ReadAllText(LogFilePath);
        Assert.Contains("first write", content);
        Assert.Contains("second write", content);
        Assert.True(content.IndexOf("first write", StringComparison.Ordinal) < content.IndexOf("second write", StringComparison.Ordinal));
    }

    [Fact]
    public void DisposingTwiceIsIdempotent()
    {
        var provider = new FileLoggerProvider(_logDirectory);

        provider.Dispose();
        provider.Dispose();
    }
}
