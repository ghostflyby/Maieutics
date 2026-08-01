using System.Runtime.CompilerServices;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;
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
            new RecordingFactory(),
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
            function.JsonSchema.ValueKind == System.Text.Json.JsonValueKind.Object &&
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
            .Contain("private reasoning").And.Contain("Deno.jupyter.display");
    }

    [Fact]
    public async Task ExplicitSessionsAreBoundedAndCaptureWorkspaceAtCreation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-repl-registry-{Guid.NewGuid():N}");
        var firstRoot = Path.Combine(root, "first");
        var secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var workspace = Workspace.Create(firstRoot, root);
        var options = new DenoReplOptions { MaxSessionsPerAgent = 2 };
        var factory = new RecordingFactory();
        await using var registry = new DenoReplRegistry(
            workspace,
            options,
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
            factory.Managers.Should().HaveCount(2);
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
            factory.Managers[0].RestartCount.Should().Be(1);

            var closed = await registry.CloseAsync(
                owner,
                second.SessionId,
                TestContext.Current.CancellationToken);
            closed.Should().Be(new DenoReplCloseResult(second.SessionId, true));
            registry.List(owner).Sessions.Should().ContainSingle().Which.SessionId.Should().Be(first.SessionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedStartupRemainsFaultedUntilExplicitRestart()
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
            faulted.SessionId.Should().MatchRegex("^[0-9a-f]{32}$");

            var restarted = await registry.RestartAsync(
                owner,
                faulted.SessionId,
                TestContext.Current.CancellationToken);
            restarted.Generation.Should().Be(2);
            restarted.State.Should().Be("idle");
            factory.Attempts.Should().Be(2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CloseRetainsRegistryEntryAndConcurrentCallWaitsForTheSameCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-repl-close-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = new BlockingShutdownFactory();
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
            await factory.Manager.ShutdownStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

            registry.List(owner).Sessions.Should().ContainSingle().Which.Should().BeEquivalentTo(new
            {
                SessionId = created.SessionId,
                State = "closing"
            });
            var second = registry.CloseAsync(owner, created.SessionId, TestContext.Current.CancellationToken);
            second.IsCompleted.Should().BeFalse();

            factory.Manager.AllowShutdown.TrySetResult();
            (await Task.WhenAll(first, second)).Should().OnlyContain(result => result.Closed);
            registry.List(owner).Sessions.Should().BeEmpty();
            factory.Manager.ShutdownCount.Should().Be(1);
            factory.Manager.DisposeCount.Should().Be(1);
        }
        finally
        {
            factory.Manager.AllowShutdown.TrySetResult();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentRegistryDisposeWaitsForOwnedSessionCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-repl-dispose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = new BlockingShutdownFactory();
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
            await factory.Manager.ShutdownStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            var second = registry.DisposeAsync().AsTask();
            second.IsCompleted.Should().BeFalse();

            factory.Manager.AllowShutdown.TrySetResult();
            await Task.WhenAll(first, second);
            factory.Manager.ShutdownCount.Should().Be(1);
            factory.Manager.DisposeCount.Should().Be(1);
        }
        finally
        {
            factory.Manager.AllowShutdown.TrySetResult();
            await registry.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string[] SchemaProperties(Microsoft.Extensions.AI.AIFunction function) =>
        function.JsonSchema.GetProperty("properties").EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();

    private sealed class RecordingFactory : IDenoReplSessionFactory
    {
        public List<RecordingManager> Managers { get; } = [];

        public Task<IJupyterKernelManager> StartAsync(
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manager = new RecordingManager();
            Managers.Add(manager);
            return Task.FromResult<IJupyterKernelManager>(manager);
        }
    }

    private sealed class FailOnceFactory : IDenoReplSessionFactory
    {
        public int Attempts { get; private set; }

        public Task<IJupyterKernelManager> StartAsync(
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            if (Attempts == 1)
            {
                throw new FileNotFoundException("deno");
            }

            return Task.FromResult<IJupyterKernelManager>(new RecordingManager());
        }
    }

    private sealed class BlockingShutdownFactory : IDenoReplSessionFactory
    {
        public BlockingShutdownManager Manager { get; } = new();

        public Task<IJupyterKernelManager> StartAsync(
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IJupyterKernelManager>(Manager);
        }
    }

    private sealed class BlockingShutdownManager : IJupyterKernelManager
    {
        public TaskCompletionSource ShutdownStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowShutdown { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IJupyterClient Client { get; } = new RecordingClient();

        public int ShutdownCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            ShutdownCount++;
            ShutdownStarted.TrySetResult();
            await AllowShutdown.Task.WaitAsync(cancellationToken);
        }

        public Task TerminateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AllowShutdown.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingManager : IJupyterKernelManager
    {
        public IJupyterClient Client { get; } = new RecordingClient();

        public int RestartCount { get; private set; }

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RestartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestartCount++;
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TerminateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingClient : IJupyterClient
    {
        public async IAsyncEnumerable<JupyterClientEvent> WatchEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new JupyterClientConnected();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task<JupyterKernelInfo> GetKernelInfoAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IJupyterExecution> ExecuteAsync(
            JupyterExecuteRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<JupyterCompleteReply> CompleteAsync(
            JupyterCompleteRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<JupyterInspectReply> InspectAsync(
            JupyterInspectRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<JupyterIsCompleteReply> IsCompleteAsync(
            JupyterIsCompleteRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnusedPresentationRouter : IDenoReplPresentationRouter
    {
        public ValueTask<IDenoReplPresentationSink> WaitForCallAsync(
            AgentSessionId sessionId,
            AgentToolCallId callId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public bool TryGetCurrentSink(AgentSessionId sessionId, out IDenoReplPresentationSink? sink)
        {
            sink = null;
            return false;
        }
    }
}
