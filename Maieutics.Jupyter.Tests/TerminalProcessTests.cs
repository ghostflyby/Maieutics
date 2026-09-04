using FluentAssertions;
using Maieutics.Execution;

namespace Maieutics.Jupyter.Tests;

/// <summary>Integration coverage for the real PtyProcess-to-ITerminalProcess adapter: the reaper event
/// must forward the terminal result, distinguishing a normal exit code from a Unix signal termination
/// (Ghostflyby.Pty 0.5.0 publishes exactly one of the two once the child is reaped).</summary>
public sealed class TerminalProcessTests
{
    [Fact(Timeout = 10_000)]
    public async Task RealProcessReportsNormalExitCode()
    {
        if (OperatingSystem.IsWindows()) return; // TerminationSignal is Unix-only by contract.

        await using var process = Start(["sleep 0.2; exit 7"]);

        var reaped = await AwaitReapedAsync(process, TestContext.Current.CancellationToken);

        reaped.ExitCode.Should().Be(7);
        reaped.TerminationSignal.Should().BeNull();
    }

    [Fact(Timeout = 10_000)]
    public async Task RealProcessReportsSignalTerminationWithoutAnExitCode()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var process = Start(["sleep 0.2; kill -9 $$"]);

        var reaped = await AwaitReapedAsync(process, TestContext.Current.CancellationToken);

        reaped.TerminationSignal.Should().Be(9);
        reaped.ExitCode.Should().BeNull();
    }

    private static ITerminalProcess Start(IReadOnlyList<string> command)
    {
        // The brief sleep keeps the child alive until the reaper subscription below is in place;
        // the pty exit event carries no history, so a subscriber added after the reap never fires.
        var process = new LocalTerminalProcessFactory().Start(
            "/bin/sh",
            ["-c", string.Join("; ", command)],
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string?> { ["PATH"] = "/bin:/usr/bin" },
            40,
            10);
        process.GracefulExitTimeout = TimeSpan.FromSeconds(2);
        return process;
    }

    private static async Task<ITerminalProcess> AwaitReapedAsync(
        ITerminalProcess process,
        CancellationToken cancellationToken)
    {
        var reaped = new TaskCompletionSource<ITerminalProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += value => reaped.TrySetResult(value);
        return await reaped.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }
}
