using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Maieutics.DenoExecution;
using Maieutics.Permissions;
using Microsoft.Extensions.Logging;

namespace Maieutics.Jupyter.Tests;

public sealed class DenoRunProcessTests
{
    private static readonly DenoPermissionBroker SharedBroker =
        DenoPermissionBroker.Create(new NullLogger());

    private static DenoRunProcess StartChild(
        ProcessStartInfo startInfo,
        InternalDenoProcessKind kind,
        ILogger logger,
        bool captureStandardError = false)
    {
        // The process tests exercise drain/stop/exit observation, not permissions; the broker is
        // required by the launch surface, so reuse one class-level broker with the empty policy.
        return DenoRunProcess.Start(
            startInfo,
            kind,
            logger,
            SharedBroker,
            EffectivePolicy.Default,
            captureStandardError);
    }

    [Fact(Timeout = 30_000)]
    public async Task CompletesWithTheChildExitCode()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var startInfo = new ProcessStartInfo
        {
            FileName = "deno",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("eval");
        startInfo.ArgumentList.Add("console.log('ready'); Deno.exit(7)");

        await using var process = StartChild(startInfo, InternalDenoProcessKind.DenoRepl, new NullLogger());

        process.ExitCode.Should().BeNull();
        await process.Completion.WaitAsync(deadline.Token);
        process.ExitCode.Should().Be(7);
    }

    [Fact(Timeout = 30_000)]
    public async Task CapturesBoundedStandardError()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var startInfo = new ProcessStartInfo
        {
            FileName = "deno",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("eval");
        startInfo.ArgumentList.Add("console.error('boom')");

        await using var process = StartChild(startInfo, InternalDenoProcessKind.DenoRepl, new NullLogger(), true);

        await process.Completion.WaitAsync(deadline.Token);
        process.StandardError.Should().NotBeNull();
        (await process.StandardError!.WaitAsync(deadline.Token)).Should().Contain("boom");
    }

    [Fact(Timeout = 30_000)]
    public async Task DrainsVoluminousOutputWithoutBackpressureDeadlock()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var startInfo = new ProcessStartInfo
        {
            FileName = "deno",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("eval");
        startInfo.ArgumentList.Add("for (let i = 0; i < 200_000; i++) console.log('payload-' + i)");
        var logger = new CollectingLogger();

        await using var process = StartChild(startInfo, InternalDenoProcessKind.PluginHost, logger);

        await process.Completion.WaitAsync(deadline.Token);
        process.ExitCode.Should().Be(0);
        logger.Messages.Should().Contain(message =>
            message.Text.Contains("Plugin host", StringComparison.Ordinal) &&
            message.Text.Contains("stdout logging was truncated", StringComparison.Ordinal));
    }

    [Fact(Timeout = 30_000)]
    public async Task StopIsIdempotentAndTerminatesARunningChild()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var startInfo = new ProcessStartInfo
        {
            FileName = "deno",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("eval");
        startInfo.ArgumentList.Add("await new Promise(() => {})");

        await using var process = StartChild(startInfo, InternalDenoProcessKind.DenoRepl, new NullLogger());

        var firstStop = process.StopAsync();
        var secondStop = process.StopAsync();
        secondStop.Should().BeSameAs(firstStop);
        await firstStop.WaitAsync(deadline.Token);
        process.ExitCode.Should().NotBeNull();
    }

    private sealed class CollectingLogger : ILogger
    {
        internal ConcurrentQueue<LogMessage> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(new LogMessage(formatter(state, exception)));
        }
    }

    private sealed class NullLogger : ILogger<DenoPermissionBroker>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return false;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed record LogMessage(string Text);
}
