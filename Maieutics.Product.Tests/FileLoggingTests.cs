using Microsoft.Extensions.Logging;

namespace Maieutics.Product.Tests;

/// <summary>Unit tests for the opt-in file logging provider: directory creation, record
/// formatting, exception rendering, append semantics, and single-shot disposal.</summary>
public sealed class FileLoggingTests : IDisposable
{
    private readonly string logDirectory = Path.Combine(
        Path.GetTempPath(),
        "maieutics-file-logging-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(logDirectory))
        {
            Directory.Delete(logDirectory, recursive: true);
        }
    }

    private string LogFilePath => Path.Combine(logDirectory, $"maieutics-{Environment.ProcessId}.log");

    [Fact]
    public void CreatingTheProviderCreatesTheLogDirectory()
    {
        Assert.False(Directory.Exists(logDirectory));

        using var provider = new FileLoggerProvider(logDirectory);

        Assert.True(Directory.Exists(logDirectory));
    }

    [Fact]
    public void LoggersFromDifferentCategoriesWriteFormattedRecordLines()
    {
        using var provider = new FileLoggerProvider(logDirectory);
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

    [Fact]
    public void ExceptionLoggingIncludesTheExceptionTypeOnItsOwnLine()
    {
        using var provider = new FileLoggerProvider(logDirectory);
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

    [Fact]
    public void ASecondProviderOnTheSameDirectoryAppendsWithoutTruncating()
    {
        using (var first = new FileLoggerProvider(logDirectory))
        {
            first.CreateLogger("Maieutics.Tests.Append").LogInformation("first write");
        }

        using (var second = new FileLoggerProvider(logDirectory))
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
        var provider = new FileLoggerProvider(logDirectory);

        provider.Dispose();
        provider.Dispose();
    }
}
