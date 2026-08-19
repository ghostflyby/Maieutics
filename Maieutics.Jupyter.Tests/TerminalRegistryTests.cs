using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

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
            NullLogger<TerminalSession>.Instance);
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

    [Fact(Timeout = 10_000)]
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
