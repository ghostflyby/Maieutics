using FluentAssertions;
using Maieutics;
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
        Directory.Exists(logDirectory).Should().BeFalse();

        using var provider = new FileLoggerProvider(logDirectory);

        Directory.Exists(logDirectory).Should().BeTrue();
    }

    [Fact]
    public void LoggersFromDifferentCategoriesWriteFormattedRecordLines()
    {
        using var provider = new FileLoggerProvider(logDirectory);
        var first = provider.CreateLogger("Maieutics.Tests.First");
        var second = provider.CreateLogger("Maieutics.Tests.Second");

        first.LogInformation("first message");
        second.LogWarning("second message");
        provider.Dispose();

        var lines = File.ReadAllLines(LogFilePath);
        lines.Single(line => line.Contains("Maieutics.Tests.First", StringComparison.Ordinal))
            .Should().MatchRegex(
                @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} Information\] Maieutics\.Tests\.First: first message$");
        lines.Single(line => line.Contains("Maieutics.Tests.Second", StringComparison.Ordinal))
            .Should().MatchRegex(
                @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} Warning\] Maieutics\.Tests\.Second: second message$");
    }

    [Fact]
    public void ExceptionLoggingIncludesTheExceptionTypeOnItsOwnLine()
    {
        using var provider = new FileLoggerProvider(logDirectory);
        var logger = provider.CreateLogger("Maieutics.Tests.Errors");

        logger.LogError(new InvalidOperationException("boom"), "operation failed");
        provider.Dispose();

        var lines = File.ReadAllLines(LogFilePath);
        lines.Should().HaveCount(2);
        lines[0].Should().MatchRegex(
            @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} Error\] Maieutics\.Tests\.Errors: operation failed$");
        lines[1].Should().Contain("InvalidOperationException").And.Contain("boom");
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
        content.Should().Contain("first write");
        content.Should().Contain("second write");
        content.IndexOf("first write", StringComparison.Ordinal)
            .Should().BeLessThan(content.IndexOf("second write", StringComparison.Ordinal));
    }

    [Fact]
    public void DisposingTwiceIsIdempotent()
    {
        var provider = new FileLoggerProvider(logDirectory);

        provider.Dispose();

        var second = () => provider.Dispose();
        second.Should().NotThrow();
    }
}
