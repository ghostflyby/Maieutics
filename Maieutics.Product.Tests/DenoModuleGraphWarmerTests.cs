using System.Diagnostics;
using FluentAssertions;
using Maieutics.DenoRepl;
using Microsoft.Extensions.Logging;

namespace Maieutics.Product.Tests;

public sealed class DenoModuleGraphWarmerTests
{
    [Fact(Timeout = 120_000)]
    public async Task WarmSucceedsWithARealDenoCacheAndDoesNotThrowOnFailure()
    {
        if (!DenoOnPath())
            Assert.Skip("deno is not on PATH; the warm needs a real Deno executable.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var logger = new CollectingLogger();
        var options = new DenoReplOptions { Executable = "deno" };
        var modules = new DenoReplModule();
        var warmer = new DenoModuleGraphWarmer(options, modules, logger);

        // StartAsync fires the background warm and returns immediately; it must not throw even if
        // the warm fails (e.g. network unavailable), because a cold module graph must never take
        // down the host.
        await warmer.StartAsync(timeout.Token);

        // The warm either succeeds or logs a failure; either way the host stays up. Await the
        // internal completion signal (the test seam) instead of polling.
        warmer.WarmCompletion.Should().NotBeNull();
        await warmer.WarmCompletion!.WaitAsync(timeout.Token);
        await warmer.StopAsync(timeout.Token);

        // Observable success: the warm finished with the success log and no failure log,
        // not merely "the task completed" (a failure also completes the task).
        logger.Lines.Should().Contain(line => line.Contains("Deno REPL module graph warmed successfully."));
        logger.Lines.Should().NotContain(line => line.Contains("Deno REPL module-graph warm failed"));
    }

    /// <summary>True when a <c>deno</c> executable is resolvable on the current PATH.</summary>
    private static bool DenoOnPath()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) return false;
        string[] candidates = OperatingSystem.IsWindows() ? ["deno.exe", "deno.cmd"] : ["deno"];
        foreach (var directory in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = directory.Trim('"');
            foreach (var candidate in candidates)
            {
                if (File.Exists(Path.Combine(trimmed, candidate))) return true;
            }
        }

        return false;
    }

    private sealed class CollectingLogger : ILogger<DenoModuleGraphWarmer>
    {
        private readonly Lock gate = new();
        private readonly List<string> lines = [];

        public IReadOnlyList<string> Lines { get { lock (gate) return [.. lines]; } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            lock (gate) lines.Add($"{logLevel}: {line}");
        }
    }
}
