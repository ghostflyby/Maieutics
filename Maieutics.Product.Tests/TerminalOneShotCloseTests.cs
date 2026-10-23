using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Permissions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

/// <summary>F3 regression: closing a one-shot terminal settles its exit signal. The close path
/// clears the session's process field, so the reaper's <c>Exited</c> event can no longer reach
/// the signal; the close must therefore settle it itself, or a wait armed on the signal parks
/// until its own timeout — and never returns at all when that timeout is infinite
/// (ADR 0030 decision 5).</summary>
public sealed class TerminalOneShotCloseTests
{
    /// <summary>The bounded window a settling signal must fit in. The waits below carry a far
    /// larger timeout of their own (30s), so a signal that settles only through that timeout is
    /// distinguishable from one that settles on the close.</summary>
    private static readonly TimeSpan ObservationBound = TimeSpan.FromSeconds(5);

    [Fact(Timeout = 30_000)]
    public async Task ClosingAOneShotSettlesTheExitSignalAWaitIsArmedOn()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateOneShotSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        // Armed before the close, exactly as the task plane's wait arms it.
        var armedBeforeClose = session.WaitExitedAsync(TestContext.Current.CancellationToken);
        await session.DisposeAsync();

        Assert.Equal("closed", session.GetSnapshot().State);
        await armedBeforeClose.WaitAsync(ObservationBound, TestContext.Current.CancellationToken);

        // Resolve-once semantics: a wait armed after the close settles from the same signal.
        var armedAfterClose = session.WaitExitedAsync(TestContext.Current.CancellationToken);
        await armedAfterClose.WaitAsync(ObservationBound, TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task AnArmedTaskWaitSettlesWhenTheOneShotIsClosed()
    {
        var fake = new FakeTerminalProcess();
        await using var registry = new TerminalRegistry(
            Workspace.Create(Directory.GetCurrentDirectory(), Directory.GetCurrentDirectory()),
            new TerminalOptions { SettleTimeout = TimeSpan.FromMilliseconds(30) },
            new FakeTerminalProcessFactory(fake),
            NullLogger<TerminalSession>.Instance,
            TestPermissionPolicies.Unconfigured());
        var provider = new TaskResourceProvider([new TerminalTaskResourceSource(registry)]);
        var owner = AgentSessionId.Create();

        // The deadline turns the still-running child into a pollable task resource.
        var run = await registry.RunOnceAsync(
            owner,
            "sh",
            ["-c", "sleep 60"],
            TimeSpan.FromMilliseconds(200),
            new TerminalSnapshotRequest(),
            TestContext.Current.CancellationToken);
        Assert.False(run.Settled);
        var taskUri = run.TaskUri ??
                      throw new InvalidOperationException("A timed-out one-shot must carry its task URI.");

        // The task-plane wait arms on the session's exit signal as the call is made; the cancel
        // then closes the one-shot through the same close that backs terminal_close. The child
        // never exits, so only the close can settle the armed wait.
        var wait = provider
            .WaitTaskAsync(taskUri, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
            .AsTask();
        var cancelled = await provider.CancelTaskAsync(taskUri, owner, TestContext.Current.CancellationToken);
        Assert.Equal("cancel", cancelled.Status);

        // A wait left on a signal that never settles ends only through its own 30s timeout, so the
        // bound tells a settled wait from a parked one. The wait's own outcome (a typed miss is
        // legitimate) stays inside the task for the observation below, so the budget wraps a
        // normalized copy instead of awaiting the wait itself.
        await wait.ContinueWith(static _ => { }, TaskScheduler.Default)
            .WaitAsync(ObservationBound, TestContext.Current.CancellationToken);

        // Settling by close has exactly two outcomes: the settled snapshot, or the registry's typed
        // miss when the close already removed the session before the wait resumed. A timeout would
        // mean the signal never settled.
        var failure = await Record.ExceptionAsync(() => wait);
        if (failure is null)
        {
            var snapshot = await wait;
            Assert.Equal("cancel", snapshot.Status);
        }
        else
        {
            Assert.IsType<ArgumentException>(failure);
        }
    }

    private static TerminalSession CreateOneShotSession(FakeTerminalProcess process)
    {
        return new TerminalSession(
            AgentSessionId.Create(),
            Guid.NewGuid().ToString("N"),
            false,
            Directory.GetCurrentDirectory(),
            TerminalSessionKind.OneShot,
            "sleep",
            ["60"],
            EffectivePolicy.Default,
            new TerminalOptions
            {
                Columns = 40,
                Rows = 10,
                SettleTimeout = TimeSpan.FromMilliseconds(30)
            },
            new FakeTerminalProcessFactory(process),
            NullLogger<TerminalSession>.Instance);
    }
}
