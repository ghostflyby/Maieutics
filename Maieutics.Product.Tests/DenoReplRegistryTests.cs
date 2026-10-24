using System.Text.Json;
using Maieutics.Agent;
using Maieutics.DenoRepl;
using Maieutics.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

public sealed class DenoReplRegistryTests
{
    [Fact(Timeout = 30_000)]
    public async Task FunctionsExposeFiveStrictReplSchemas()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var workspace = Workspace.Create(Directory.GetCurrentDirectory(), Directory.GetCurrentDirectory());
        await using var registry = new DenoReplRegistry(
            workspace,
            new DenoReplOptions(),
            new DenoReplSessionTests.ControlledFactory(),
            new UnusedPresentationRouter(),
            NullLogger<DenoReplSession>.Instance);

        var functions = new DenoReplFunctions(registry).Functions;

        Assert.Equal(["repl_execute", "repl_create", "repl_list", "repl_restart", "repl_close"], functions.Select(static function => function.Name));
        Assert.All(functions, static function => Assert.True(function.JsonSchema.ValueKind == JsonValueKind.Object &&
                                                             function.JsonSchema.GetProperty("type").GetString() == "object"));
        Assert.Equal(new[] { "code", "sessionId" }, SchemaProperties(functions.Single(static function => function.Name == "repl_execute")));
        Assert.Equal(["code"], functions.Single(static function => function.Name == "repl_execute")
            .JsonSchema.GetProperty("required").EnumerateArray()
            .Select(static value => value.GetString()));
        Assert.Empty(SchemaProperties(functions.Single(static function => function.Name == "repl_create")));
        Assert.Empty(SchemaProperties(functions.Single(static function => function.Name == "repl_list")));
        Assert.Equal(new[] { "sessionId" }, SchemaProperties(functions.Single(static function => function.Name == "repl_restart")));
        Assert.Equal(new[] { "sessionId" }, SchemaProperties(functions.Single(static function => function.Name == "repl_close")));
        Assert.Contains("private reasoning", functions.Single(static function => function.Name == "repl_execute").Description);
        Assert.Contains("Deno.jupyter.display", functions.Single(static function => function.Name == "repl_execute").Description);
        Assert.Contains("raw: true", functions.Single(static function => function.Name == "repl_execute").Description);
        Assert.Contains("display_id", functions.Single(static function => function.Name == "repl_execute").Description);
        Assert.Contains("update: true", functions.Single(static function => function.Name == "repl_execute").Description);
        Assert.Contains("text/plain", functions.Single(static function => function.Name == "repl_execute").Description);
    }

    [Fact(Timeout = 30_000)]
    public async Task ExplicitSessionsAreBoundedAndCaptureWorkspaceAtCreation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-repl-registry-{Guid.NewGuid():N}");
        var firstRoot = Path.Combine(root, "first");
        Directory.CreateDirectory(firstRoot);
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
            var second = await registry.CreateAsync(owner, TestContext.Current.CancellationToken);

            Assert.Matches("^[0-9a-f]{32}$", first.SessionId);
            Assert.Matches("^[0-9a-f]{32}$", second.SessionId);
            Assert.NotEqual(first.SessionId, second.SessionId);
            Assert.Equal(firstRoot, first.Cwd);
            Assert.Equal(firstRoot, second.Cwd);
            Assert.Equal(2, factory.Generations.Count());
            Assert.Equal([firstRoot, firstRoot], factory.Starts.Select(static start => start.WorkingDirectory));
            Assert.Equal(2, registry.List(owner).Sessions.Count());

            var limitFailure = await Assert.ThrowsAnyAsync<AgentToolException>(async () =>
            {
                await (Registry: registry, Owner: owner).Registry.CreateAsync(
                    (Registry: registry, Owner: owner).Owner,
                    TestContext.Current.CancellationToken);
            });
            Assert.Equal("repl_session_limit", limitFailure.Code);

            var restarted = await registry.RestartAsync(
                owner,
                first.SessionId,
                TestContext.Current.CancellationToken);
            Assert.Equal(2, restarted.Generation);
            Assert.Equal("idle", restarted.State);
            Assert.Equal(3, factory.Generations.Count());
            Assert.Equal(1, factory.Generations[0].DisposeCount);
            Assert.Equal((firstRoot, first.SessionId, 2), factory.Starts[^1]);

            var closed = await registry.CloseAsync(
                owner,
                second.SessionId,
                TestContext.Current.CancellationToken);
            Assert.Equal(new DenoReplCloseResult(second.SessionId, true), closed);
            Assert.Equal(first.SessionId, Assert.Single(registry.List(owner).Sessions).SessionId);
        }
        finally
        {
            await TempArea.DeleteAsync(root, CancellationToken.None);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ClosingOneReplKeepsTheOwnersCachedPolicyWhileSiblingsRemain()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-repl-owner-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var workspace = Workspace.Create(root, root);
        var ownerCache = new ReplOwnerPolicyCache();
        await using var registry = new DenoReplRegistry(
            workspace,
            new DenoReplOptions { MaxSessionsPerAgent = 2 },
            new DenoReplSessionTests.ControlledFactory(),
            new UnusedPresentationRouter(),
            NullLogger<DenoReplSession>.Instance,
            ownerPolicyCache: ownerCache);
        var owner = AgentSessionId.Create();

        try
        {
            var first = await registry.CreateAsync(owner, TestContext.Current.CancellationToken);
            var second = await registry.CreateAsync(owner, TestContext.Current.CancellationToken);
            var policy = Maieutics.Permissions.PermissionLayerStore.Build(
                [],
                new Maieutics.Permissions.VariableTable(new NullVariableSource()));
            ownerCache.Cache(owner.Value.ToString("N"), policy);

            // Closing one of the owner's REPLs must not drop the owner-keyed snapshot: the
            // surviving sibling's bridged calls still judge against it, not the wider
            // model-tool baseline.
            await registry.CloseAsync(owner, second.SessionId, TestContext.Current.CancellationToken);
            Assert.NotNull(ownerCache.TryGet(owner.Value.ToString("N")));

            await registry.CloseAsync(owner, first.SessionId, TestContext.Current.CancellationToken);
            Assert.Null(ownerCache.TryGet(owner.Value.ToString("N")));
        }
        finally
        {
            await TempArea.DeleteAsync(root, CancellationToken.None);
        }
    }

    private sealed class NullVariableSource : Maieutics.Execution.IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return null;
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
            var failure = await Assert.ThrowsAnyAsync<AgentToolException>(async () =>
            {
                await (Registry: registry, Owner: owner).Registry.CreateAsync(
                    (Registry: registry, Owner: owner).Owner,
                    TestContext.Current.CancellationToken);
            });
            Assert.Equal("repl_start_failed", failure.Code);

            var faulted = Assert.Single(registry.List(owner).Sessions);
            Assert.Equal("faulted", faulted.State);
            Assert.Equal(1, faulted.Generation);

            var restarted = await registry.RestartAsync(
                owner,
                faulted.SessionId,
                TestContext.Current.CancellationToken);
            Assert.Equal(2, restarted.Generation);
            Assert.Equal("idle", restarted.State);
            Assert.Equal(new[] { 1, 2 }, factory.Attempts);
        }
        finally
        {
            await TempArea.DeleteAsync(root, CancellationToken.None);
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

            Assert.Equivalent(new
            {
                created.SessionId,
                State = "closing"
            }, Assert.Single(registry.List(owner).Sessions));
            var second = registry.CloseAsync(owner, created.SessionId, TestContext.Current.CancellationToken);
            Assert.False(second.IsCompleted);

            generation.AllowDisposal.TrySetResult();
            Assert.All(await Task.WhenAll(first, second), result => Assert.True(result.Closed));
            Assert.Empty(registry.List(owner).Sessions);
            Assert.Equal(1, generation.DisposeCount);
        }
        finally
        {
            generation.AllowDisposal.TrySetResult();
            await TempArea.DeleteAsync(root, CancellationToken.None);
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
            Assert.False(second.IsCompleted);

            generation.AllowDisposal.TrySetResult();
            await Task.WhenAll(first, second);
            Assert.Equal(1, generation.DisposeCount);
        }
        finally
        {
            generation.AllowDisposal.TrySetResult();
            await registry.DisposeAsync();
            await TempArea.DeleteAsync(root, CancellationToken.None);
        }
    }

    private static string[] SchemaProperties(AIFunction function)
    {
        return
        [
            .. function.JsonSchema.GetProperty("properties").EnumerateObject()
                .Select(static property => property.Name),
        ];
    }

    private sealed class FailOnceFactory : IDenoReplSessionFactory
    {
        internal List<int> Attempts { get; } = [];

        public Task<IDenoReplGeneration> StartAsync(
            string workingDirectory,
            string sessionId,
            AgentSessionId ownerSessionId,
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
    }
}
