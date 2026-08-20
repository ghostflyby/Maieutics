using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.DenoRepl;
using Maieutics.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

public sealed class DenoReplRegistryTests
{
    [Fact]
    public async Task FunctionsExposeFiveStrictReplSchemas()
    {
        var workspace = Workspace.Create(Directory.GetCurrentDirectory(), Directory.GetCurrentDirectory());
        await using var registry = new DenoReplRegistry(
            workspace,
            new DenoReplOptions(),
            new DenoReplSessionTests.ControlledFactory(),
            new UnusedPresentationRouter(),
            NullLogger<DenoReplSession>.Instance);

        var functions = new DenoReplFunctions(registry).Functions;

        functions.Select(static function => function.Name).Should().Equal(
            "repl_execute",
            "repl_create",
            "repl_list",
            "repl_restart",
            "repl_close");
        functions.Should().OnlyContain(static function =>
            function.JsonSchema.ValueKind == JsonValueKind.Object &&
            function.JsonSchema.GetProperty("type").GetString() == "object");
        SchemaProperties(functions.Single(static function => function.Name == "repl_execute"))
            .Should().Equal("code", "sessionId");
        functions.Single(static function => function.Name == "repl_execute")
            .JsonSchema.GetProperty("required").EnumerateArray()
            .Select(static value => value.GetString())
            .Should().Equal("code");
        SchemaProperties(functions.Single(static function => function.Name == "repl_create")).Should().BeEmpty();
        SchemaProperties(functions.Single(static function => function.Name == "repl_list")).Should().BeEmpty();
        SchemaProperties(functions.Single(static function => function.Name == "repl_restart"))
            .Should().Equal("sessionId");
        SchemaProperties(functions.Single(static function => function.Name == "repl_close"))
            .Should().Equal("sessionId");
        functions.Single(static function => function.Name == "repl_execute").Description.Should()
            .Contain("private reasoning")
            .And.Contain("Deno.jupyter.display")
            .And.Contain("raw: true")
            .And.Contain("display_id")
            .And.Contain("update: true")
            .And.Contain("text/plain");
    }

    [Fact(Timeout = 30_000)]
    public async Task ExplicitSessionsAreBoundedAndCaptureWorkspaceAtCreation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-repl-registry-{Guid.NewGuid():N}");
        var firstRoot = Path.Combine(root, "first");
        var secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var workspace = Workspace.Create(firstRoot, root);
        var factory = new DenoReplSessionTests.ControlledFactory();
        await using var registry = new DenoReplRegistry(
            workspace,
            new DenoReplOptions { MaxSessionsPerAgent = 2 },
            factory,
            new UnusedPresentationRouter(),
            NullLogger<DenoReplSession>.Instance);
        var owner = AgentSessionId.Create();

        try
        {
            var first = await registry.CreateAsync(owner, TestContext.Current.CancellationToken);
            workspace.Use(secondRoot);
            var second = await registry.CreateAsync(owner, TestContext.Current.CancellationToken);

            first.SessionId.Should().MatchRegex("^[0-9a-f]{32}$");
            second.SessionId.Should().MatchRegex("^[0-9a-f]{32}$").And.NotBe(first.SessionId);
            first.Cwd.Should().Be(firstRoot);
            second.Cwd.Should().Be(secondRoot);
            factory.Generations.Should().HaveCount(2);
            factory.Starts.Select(static start => start.WorkingDirectory).Should().Equal(firstRoot, secondRoot);
            registry.List(owner).Sessions.Should().HaveCount(2);

            var limitFailure = await (Registry: registry, Owner: owner)
                .Awaiting(static state => state.Registry.CreateAsync(
                    state.Owner,
                    TestContext.Current.CancellationToken))
                .Should().ThrowAsync<AgentToolException>();
            limitFailure.Which.Code.Should().Be("repl_session_limit");

            var restarted = await registry.RestartAsync(
                owner,
                first.SessionId,
                TestContext.Current.CancellationToken);
            restarted.Generation.Should().Be(2);
            restarted.State.Should().Be("idle");
            factory.Generations.Should().HaveCount(3);
            factory.Generations[0].DisposeCount.Should().Be(1);
            factory.Starts[^1].Should().Be((firstRoot, first.SessionId, 2));

            var closed = await registry.CloseAsync(
                owner,
                second.SessionId,
                TestContext.Current.CancellationToken);
            closed.Should().Be(new DenoReplCloseResult(second.SessionId, true));
            registry.List(owner).Sessions.Should().ContainSingle().Which.SessionId.Should().Be(first.SessionId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task FailedStartupRemainsFaultedUntilExplicitRestartCreatesGenerationTwo()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-repl-fault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = new FailOnceFactory();
        await using var registry = new DenoReplRegistry(
            Workspace.Create(root, root),
            new DenoReplOptions(),
            factory,
            new UnusedPresentationRouter(),
            NullLogger<DenoReplSession>.Instance);
        var owner = AgentSessionId.Create();

        try
        {
            var failure = await (Registry: registry, Owner: owner)
                .Awaiting(static state => state.Registry.CreateAsync(
                    state.Owner,
                    TestContext.Current.CancellationToken))
                .Should().ThrowAsync<AgentToolException>();
            failure.Which.Code.Should().Be("repl_start_failed");

            var faulted = registry.List(owner).Sessions.Should().ContainSingle().Subject;
            faulted.State.Should().Be("faulted");
            faulted.Generation.Should().Be(1);

            var restarted = await registry.RestartAsync(
                owner,
                faulted.SessionId,
                TestContext.Current.CancellationToken);
            restarted.Generation.Should().Be(2);
            restarted.State.Should().Be("idle");
            factory.Attempts.Should().Equal(1, 2);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ConcurrentCloseCallsWaitForTheSameGenerationCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-repl-close-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var generation = new DenoReplSessionTests.ControlledGeneration { BlockDisposal = true };
        var factory = new DenoReplSessionTests.ControlledFactory(() => generation);
        await using var registry = new DenoReplRegistry(
            Workspace.Create(root, root),
            new DenoReplOptions(),
            factory,
            new UnusedPresentationRouter(),
            NullLogger<DenoReplSession>.Instance);
        var owner = AgentSessionId.Create();

        try
        {
            var created = await registry.CreateAsync(owner, TestContext.Current.CancellationToken);
            var first = registry.CloseAsync(owner, created.SessionId, TestContext.Current.CancellationToken);
            await generation.DisposalStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

            registry.List(owner).Sessions.Should().ContainSingle().Which.Should().BeEquivalentTo(new
            {
                created.SessionId,
                State = "closing"
            });
            var second = registry.CloseAsync(owner, created.SessionId, TestContext.Current.CancellationToken);
            second.IsCompleted.Should().BeFalse();

            generation.AllowDisposal.TrySetResult();
            (await Task.WhenAll(first, second)).Should().OnlyContain(result => result.Closed);
            registry.List(owner).Sessions.Should().BeEmpty();
            generation.DisposeCount.Should().Be(1);
        }
        finally
        {
            generation.AllowDisposal.TrySetResult();
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ConcurrentRegistryDisposeWaitsForOwnedGenerationCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-repl-dispose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var generation = new DenoReplSessionTests.ControlledGeneration { BlockDisposal = true };
        var factory = new DenoReplSessionTests.ControlledFactory(() => generation);
        var registry = new DenoReplRegistry(
            Workspace.Create(root, root),
            new DenoReplOptions(),
            factory,
            new UnusedPresentationRouter(),
            NullLogger<DenoReplSession>.Instance);

        try
        {
            await registry.CreateAsync(AgentSessionId.Create(), TestContext.Current.CancellationToken);
            var first = registry.DisposeAsync().AsTask();
            await generation.DisposalStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            var second = registry.DisposeAsync().AsTask();
            second.IsCompleted.Should().BeFalse();

            generation.AllowDisposal.TrySetResult();
            await Task.WhenAll(first, second);
            generation.DisposeCount.Should().Be(1);
        }
        finally
        {
            generation.AllowDisposal.TrySetResult();
            await registry.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    private static string[] SchemaProperties(AIFunction function)
    {
        return function.JsonSchema.GetProperty("properties").EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
    }

    private sealed class FailOnceFactory : IDenoReplSessionFactory
    {
        internal List<int> Attempts { get; } = [];

        public Task<IDenoReplGeneration> StartAsync(
            string workingDirectory,
            string sessionId,
            int generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.Add(generation);
            if (Attempts.Count == 1) throw new FileNotFoundException("deno");
            return Task.FromResult<IDenoReplGeneration>(new DenoReplSessionTests.ControlledGeneration
            {
                Generation = generation
            });
        }
    }

    private sealed class UnusedPresentationRouter : IDenoReplPresentationRouter
    {
        public ValueTask<IDenoReplPresentationSink> WaitForCallAsync(
            AgentSessionId sessionId,
            AgentToolCallId callId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public bool TryGetCurrentSink(
            AgentSessionId sessionId,
            [NotNullWhen(true)] out IDenoReplPresentationSink? sink)
        {
            sink = null;
            return false;
        }
    }
}
