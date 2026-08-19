using System.Collections.Concurrent;
using FluentAssertions;
using Maieutics.DenoExecution;
using Maieutics.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

public sealed class PluginHostProcessTests
{
    private static readonly DenoPermissionBroker SharedBroker =
        DenoPermissionBroker.Create(new NullLogger<DenoPermissionBroker>());

    [Fact(Timeout = 30_000)]
    public async Task DrainsStdoutAndStderrConcurrentlyWithBoundedLogging()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var root = Path.Combine(Path.GetTempPath(), $"mc-plugin-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "host.ts");
            var denoConfigPath = Path.Combine(root, "deno.json");
            await File.WriteAllTextAsync(
                scriptPath,
                """
                const chunk = new Uint8Array(8192).fill("x".charCodeAt(0));
                for (let index = 0; index < 512; index++) {
                  await Deno.stdout.write(chunk);
                  await Deno.stderr.write(chunk);
                }
                """,
                deadline.Token);
            await File.WriteAllTextAsync(denoConfigPath, "{}", deadline.Token);
            var logger = new CollectingLogger();
            await using var process = PluginHostProcess.Start(
                new PluginHostProcessOptions(
                    "deno",
                    scriptPath,
                    Path.Combine(root, "control.sock"),
                    Path.Combine(root, "plugins.json"),
                    "test-host",
                    scriptPath,
                    scriptPath,
                    denoConfigPath,
                    new PluginHostProcessGrants(
                        false,
                        [],
                        false,
                        [],
                        false,
                        [],
                        false,
                        [],
                        false,
                        []),
                    SharedBroker),
                logger);

            await process.Completion.WaitAsync(deadline.Token);
            process.ExitCode.Should().Be(0);
            var firstStop = process.StopAsync();
            var secondStop = process.StopAsync();
            secondStop.Should().BeSameAs(firstStop);
            await firstStop.WaitAsync(deadline.Token);

            var debug = logger.Messages.Where(static message => message.Level == LogLevel.Debug).ToArray();
            debug.Should().Contain(message =>
                message.Text.Contains("stdout logging was truncated", StringComparison.Ordinal));
            debug.Should().Contain(message =>
                message.Text.Contains("stderr logging was truncated", StringComparison.Ordinal));
            debug.Max(static message => message.Text.Length).Should().BeLessThan(5000);
            debug.Should().HaveCountLessThanOrEqualTo(20);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class CollectingLogger : ILogger
    {
        internal ConcurrentQueue<LogMessage> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
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
            Messages.Enqueue(new LogMessage(logLevel, formatter(state, exception)));
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed record LogMessage(LogLevel Level, string Text);
}
