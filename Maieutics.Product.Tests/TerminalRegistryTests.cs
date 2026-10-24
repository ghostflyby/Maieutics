using Maieutics.Agent;
using Maieutics.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

public sealed class TerminalRegistryTests
{
    private static TerminalRegistry CreateRegistry(FakeTerminalProcess process, int maxSessions = 2)
    {
        return new TerminalRegistry(
            Workspace.Create(Directory.GetCurrentDirectory(), Directory.GetCurrentDirectory()),
            new TerminalOptions
            {
                MaxSessionsPerAgent = maxSessions,
                SettleTimeout = TimeSpan.FromMilliseconds(30)
            },
            new FakeTerminalProcessFactory(process),
            NullLogger<TerminalSession>.Instance,
            TestPermissionPolicies.Unconfigured());
    }

    [Fact(Timeout = 10_000)]
    public async Task OneShotSessionsReleaseTheirSlotAndDisposeAfterCompletion()
    {
        var process = new FakeTerminalProcess();
        // MaxSessionsPerAgent is 2 here: before the fix, two completed one-shot runs exhausted
        // the cap permanently; five sequential runs must now succeed.
        await using var registry = CreateRegistry(process);
        var owner = AgentSessionId.Create();

        for (var run = 0; run < 5; run++)
        {
            var runTask = registry.RunOnceAsync(
                owner,
                "sh",
                ["-c", "echo done"],
                TimeSpan.FromSeconds(5),
                new TerminalSnapshotRequest(),
                TestContext.Current.CancellationToken);

            // The fake child exits as soon as the one-shot run is waiting on it.
            process.EndOfOutput();
            process.RaiseExited(0);

            var result = await runTask;
            Assert.True(result.Settled, $"run {run} must complete");
            Assert.Equal(0, result.ExitCode);
            Assert.True(process.Disposed, $"run {run} must dispose its session");
            Assert.Empty(registry.List(owner));
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task TimedOutOneShotStaysRegisteredAsThePollableHandle()
    {
        var process = new FakeTerminalProcess();
        await using var registry = CreateRegistry(process);
        var owner = AgentSessionId.Create();

        var runTask = registry.RunOnceAsync(
            owner,
            "sh",
            ["-c", "sleep 60"],
            TimeSpan.FromMilliseconds(200),
            new TerminalSnapshotRequest(),
            TestContext.Current.CancellationToken);

        // The child never exits; the deadline turns the run into a pollable handle.
        var result = await runTask;
        Assert.False(result.Settled);
        Assert.Equal(result.SessionId, Assert.Single(registry.List(owner)).SessionId);

        await registry.CloseAsync(owner, result.SessionId, TestContext.Current.CancellationToken);
        Assert.Empty(registry.List(owner));
    }

    private static async Task<TerminalRunResult> RunAsync(
        TerminalRegistry registry,
        AgentSessionId owner,
        CancellationToken cancellationToken)
    {
        // No timeout: terminal_run creates and starts a persistent session.
        return await registry.RunOnceAsync(
            owner,
            "sh",
            [],
            null,
            new TerminalSnapshotRequest(),
            cancellationToken);
    }

    [Fact(Timeout = 10_000)]
    public async Task ExplicitSessionsAreBoundedAndListedPerAgent()
    {
        var process = new FakeTerminalProcess();
        await using var registry = CreateRegistry(process);
        var owner = AgentSessionId.Create();

        var first = await RunAsync(registry, owner, TestContext.Current.CancellationToken);
        var second = await RunAsync(registry, owner, TestContext.Current.CancellationToken);

        Assert.Matches("^[0-9a-f]{32}$", first.SessionId);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal("idle", first.State);
        Assert.Equal(2, registry.List(owner).Count());

        Assert.Equal("terminal_session_limit", (await Assert.ThrowsAnyAsync<AgentToolException>(
            async () => { await RunAsync(registry, owner, TestContext.Current.CancellationToken); })).Code);
    }

    [Fact(Timeout = 10_000)]
    public async Task CloseRemovesTheSessionAndReportsClosed()
    {
        var process = new FakeTerminalProcess();
        await using var registry = CreateRegistry(process);
        var owner = AgentSessionId.Create();

        var created = await RunAsync(registry, owner, TestContext.Current.CancellationToken);
        var closed = await registry.CloseAsync(owner, created.SessionId, TestContext.Current.CancellationToken);

        Assert.Equal(new TerminalCloseResult(), closed);
        Assert.Empty(registry.List(owner));
    }

    [Fact(Timeout = 10_000)]
    public async Task CloseOfUnknownSessionFailsWithNotFound()
    {
        var process = new FakeTerminalProcess();
        await using var registry = CreateRegistry(process);
        var owner = AgentSessionId.Create();

        Assert.Equal("terminal_session_not_found", (await Assert.ThrowsAnyAsync<AgentToolException>(() => registry.CloseAsync(owner, "missing", TestContext.Current.CancellationToken))).Code);
    }

    [Fact(Timeout = 10_000)]
    public async Task RegistryDisposeWaitsForOwnedSessionCleanup()
    {
        var process = new FakeTerminalProcess();
        var registry = CreateRegistry(process);
        var owner = AgentSessionId.Create();

        await RunAsync(registry, owner, TestContext.Current.CancellationToken);
        await registry.DisposeAsync();

        Assert.True(process.Disposed);
    }

    [Fact(Timeout = 10_000)]
    public async Task SnapshotOfMissingSessionFailsWithoutStartingOne()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var process = new FakeTerminalProcess();
        await using var registry = CreateRegistry(process);
        var owner = AgentSessionId.Create();

        Assert.Equal("terminal_session_not_found", Assert.ThrowsAny<AgentToolException>(() => registry.Snapshot(owner, new TerminalSnapshotRequest(), null)).Code);
        Assert.Empty(registry.List(owner));
    }

    [Fact(Timeout = 10_000)]
    public async Task InterruptOfMissingSessionFailsWithoutStartingOne()
    {
        var process = new FakeTerminalProcess();
        await using var registry = CreateRegistry(process);
        var owner = AgentSessionId.Create();

        await Assert.ThrowsAnyAsync<AgentToolException>(() => registry.InterruptAsync(
            owner,
            new TerminalSnapshotRequest(),
            null,
            TestContext.Current.CancellationToken));
        Assert.Empty(registry.List(owner));
    }
}
