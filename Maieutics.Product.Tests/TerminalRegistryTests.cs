using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Permissions;
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
            EffectivePolicy.Default);
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
            result.Settled.Should().BeTrue($"run {run} must complete");
            result.ExitCode.Should().Be(0);
            process.Disposed.Should().BeTrue($"run {run} must dispose its session");
            registry.List(owner).Should().BeEmpty($"run {run} must release its registry slot");
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
        result.Settled.Should().BeFalse();
        registry.List(owner).Should().ContainSingle().Which.SessionId.Should().Be(result.SessionId);

        await registry.CloseAsync(owner, result.SessionId, TestContext.Current.CancellationToken);
        registry.List(owner).Should().BeEmpty();
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

        first.SessionId.Should().MatchRegex("^[0-9a-f]{32}$");
        second.SessionId.Should().NotBe(first.SessionId);
        first.State.Should().Be("idle");
        registry.List(owner).Should().HaveCount(2);

        var limitFailure = () => RunAsync(registry, owner, TestContext.Current.CancellationToken);
        (await limitFailure.Should().ThrowAsync<AgentToolException>())
            .Which.Code.Should().Be("terminal_session_limit");
    }

    [Fact(Timeout = 10_000)]
    public async Task CloseRemovesTheSessionAndReportsClosed()
    {
        var process = new FakeTerminalProcess();
        await using var registry = CreateRegistry(process);
        var owner = AgentSessionId.Create();

        var created = await RunAsync(registry, owner, TestContext.Current.CancellationToken);
        var closed = await registry.CloseAsync(owner, created.SessionId, TestContext.Current.CancellationToken);

        closed.Should().Be(new TerminalCloseResult());
        registry.List(owner).Should().BeEmpty();
    }

    [Fact(Timeout = 10_000)]
    public async Task CloseOfUnknownSessionFailsWithNotFound()
    {
        var process = new FakeTerminalProcess();
        await using var registry = CreateRegistry(process);
        var owner = AgentSessionId.Create();

        var failure = () => registry.CloseAsync(owner, "missing", TestContext.Current.CancellationToken);
        (await failure.Should().ThrowAsync<AgentToolException>())
            .Which.Code.Should().Be("terminal_session_not_found");
    }

    [Fact(Timeout = 10_000)]
    public async Task RegistryDisposeWaitsForOwnedSessionCleanup()
    {
        var process = new FakeTerminalProcess();
        var registry = CreateRegistry(process);
        var owner = AgentSessionId.Create();

        await RunAsync(registry, owner, TestContext.Current.CancellationToken);
        await registry.DisposeAsync();

        process.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task SnapshotOfMissingSessionFailsWithoutStartingOne()
    {
        var process = new FakeTerminalProcess();
        await using var registry = CreateRegistry(process);
        var owner = AgentSessionId.Create();

        var failure = () => registry.Snapshot(owner, new TerminalSnapshotRequest(), null);
        failure.Should().Throw<AgentToolException>()
            .Which.Code.Should().Be("terminal_session_not_found");
        registry.List(owner).Should().BeEmpty();
    }

    [Fact(Timeout = 10_000)]
    public async Task InterruptOfMissingSessionFailsWithoutStartingOne()
    {
        var process = new FakeTerminalProcess();
        await using var registry = CreateRegistry(process);
        var owner = AgentSessionId.Create();

        var failure = () => registry.InterruptAsync(
            owner,
            new TerminalSnapshotRequest(),
            null,
            TestContext.Current.CancellationToken);
        await failure.Should().ThrowAsync<AgentToolException>();
        registry.List(owner).Should().BeEmpty();
    }
}
