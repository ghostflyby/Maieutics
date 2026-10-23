using System.Collections.Concurrent;
using Maieutics.Plugins;
using Microsoft.Extensions.Logging;

namespace Maieutics.Product.Tests;

public sealed class PluginHostProcessTests
{
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
                    denoConfigPath),
                logger);

            await process.Completion.WaitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            var firstStop = process.StopAsync();
            var secondStop = process.StopAsync();
            Assert.Same(firstStop, secondStop);
            await firstStop.WaitAsync(deadline.Token);

            var debug = logger.Messages.Where(static message => message.Level == LogLevel.Debug).ToArray();
            Assert.Contains(debug, message =>
                message.Text.Contains("stdout logging was truncated", StringComparison.Ordinal));
            Assert.Contains(debug, message =>
                message.Text.Contains("stderr logging was truncated", StringComparison.Ordinal));
            Assert.True(debug.Max(static message => message.Text.Length) < 5000);
            Assert.True(debug.Count() <= 20);
        }
        finally
        {
            await TempArea.DeleteAsync(root, CancellationToken.None);
        }
    }

    private sealed class CollectingLogger : ILogger
    {
        internal ConcurrentQueue<LogMessage> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
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
