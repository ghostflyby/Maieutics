using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Maieutics.DenoExecution;
using Maieutics.Permissions;
using Microsoft.Extensions.Logging;

namespace Maieutics.Product.Tests;

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

    [Fact(Timeout = 60_000)]
    public async Task StopReturnsWithinItsBudgetWhenAReparentedGrandchildHoldsTheDrain()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Uses `sleep` as the grandchild and a pid file under the temp path.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        var pidFilePath = Path.Combine(Path.GetTempPath(), $"mc-grandchild-{Guid.NewGuid():N}.pid");
        try
        {
            // The eval spawns `sleep` with inherited stdio (the child keeps our redirected stdout
            // pipe open) and exits immediately; the re-parented grandchild would hold the drain
            // forever without a bounded stop.
            var startInfo = new ProcessStartInfo
            {
                FileName = "deno",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("eval");
            startInfo.ArgumentList.Add("--allow-run");
            startInfo.ArgumentList.Add($"--allow-write={pidFilePath}");
            // Deno 2 defaults spawned children to null stdio; explicit inherit keeps `sleep`
            // holding the eval's redirected stdout pipe, and unref lets the eval exit while the
            // grandchild lives — the re-parented drain holder a plain kill cannot reap.
            startInfo.ArgumentList.Add(
                $"const c = new Deno.Command(\"sleep\", {{ args: [\"120\"], stdin: \"inherit\", " +
                $"stdout: \"inherit\", stderr: \"inherit\" }}).spawn(); c.unref(); " +
                $"await Deno.writeTextFile(\"{pidFilePath}\", String(c.pid));");
            var logger = new CollectingLogger();

            var process = DenoRunProcess.Start(
                startInfo,
                InternalDenoProcessKind.DenoRepl,
                logger,
                SharedBroker,
                EffectivePolicy.Default,
                stopBudget: TimeSpan.FromSeconds(2));

            try
            {
                // Wait until the eval finished spawning (and is about to exit) before stopping.
                var grandchildPid = 0;
                var spawnDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (grandchildPid == 0)
                {
                    if (DateTime.UtcNow >= spawnDeadline)
                        throw new TimeoutException("the eval never spawned its grandchild");
                    if (File.Exists(pidFilePath) &&
                        int.TryParse(await File.ReadAllTextAsync(pidFilePath, deadline.Token), out var parsed))
                        grandchildPid = parsed;
                    else
                        await Task.Delay(50, deadline.Token);
                }

                // Give the parent a moment to actually exit, then verify the grandchild is live.
                await Task.Delay(500, deadline.Token);
                using (var grandchild = Process.GetProcessById(grandchildPid))
                {
                    grandchild.HasExited.Should().BeFalse("the test requires a live drain holder");
                }

                // Stop must return within the 2s budget even though the grandchild holds stdout.
                var stopDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                await process.StopAsync().WaitAsync(deadline.Token);
                DateTime.UtcNow.Should().BeBefore(stopDeadline, "stop must honor its total budget");
                logger.Messages.Should().Contain(message =>
                    message.Text.Contains("abandoning its completion", StringComparison.Ordinal));
            }
            finally
            {
                await process.DisposeAsync();
            }
        }
        finally
        {
            TryKillPid(pidFilePath);
            TryDelete(pidFilePath);
        }
    }

    private static void TryKillPid(string pidFilePath)
    {
        try
        {
            if (!File.Exists(pidFilePath) ||
                !int.TryParse(File.ReadAllText(pidFilePath), out var pid))
                return;

            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) process.Kill();
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The grandchild already exited; nothing to clean up.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
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
