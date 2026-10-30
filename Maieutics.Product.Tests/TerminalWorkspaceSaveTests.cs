using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

public sealed class TerminalWorkspaceSaveTests
{
    [Theory(Timeout = 30_000)]
    [InlineData("terminal_run", "{\"executable\":\"fake-build\",\"saveWorkspace\":true}")]
    [InlineData("terminal_input", "{\"input\":\"t build\\nk <CR>\",\"saveWorkspace\":true}")]
    [InlineData("terminal_paste", "{\"text\":\"build\\n\",\"saveWorkspace\":true}")]
    public async Task SaveFailurePreventsAllTerminalLaunchesAndInput(string tool, string json)
    {
        using var workspace = TemporaryWorkspace.Create();
        var factory = new Factory();
        await using var registry = Registry(workspace.Path, factory);
        var preparation = new Preparation { Failure = new AgentToolException("ide_save_failed", "read-only document") };
        preparation.Release.TrySetResult();
        var functions = new TerminalFunctions(registry, preparation);
        var owner = AgentSessionId.Create();
        var failure = await Assert.ThrowsAsync<AgentToolException>(() => functions.Functions.Single(function => function.Name == tool)
            .InvokeAsync(Arguments(json, owner), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("ide_save_failed", failure.Code);
        Assert.Equal(owner, await preparation.Entered.Task);
        Assert.Equal(0, factory.Starts);
        Assert.Empty(registry.List(owner));
        Assert.Empty(factory.Process.HexWrites);
    }

    [Fact(Timeout = 30_000)]
    public async Task BuildWaitsForTheSaveBarrierBeforeAllocatingATerminal()
    {
        using var workspace = TemporaryWorkspace.Create();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        var factory = new Factory();
        await using var registry = Registry(workspace.Path, factory);
        var preparation = new Preparation();
        var functions = new TerminalFunctions(registry, preparation);
        var owner = AgentSessionId.Create();
        var operation = functions.Functions.Single(function => function.Name == "terminal_run")
            .InvokeAsync(Arguments("""{"executable":"fake-build","saveWorkspace":true}""", owner), budget.Token).AsTask();
        Assert.Equal(owner, await preparation.Entered.Task.WaitAsync(budget.Token));
        Assert.Equal(0, factory.Starts);
        Assert.Empty(registry.List(owner));
        preparation.Release.TrySetResult();
        await operation.WaitAsync(budget.Token);
        Assert.Equal(1, factory.Starts);
        Assert.Single(registry.List(owner));
        factory.Process.EndOfOutput();
        factory.Process.RaiseExited(0);
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellationWhileSavingNeverLaunchesTheDependentCommand()
    {
        using var workspace = TemporaryWorkspace.Create();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        var factory = new Factory();
        await using var registry = Registry(workspace.Path, factory);
        var preparation = new Preparation();
        var owner = AgentSessionId.Create();
        var functions = new TerminalFunctions(registry, preparation);
        var operation = functions.Functions.Single(function => function.Name == "terminal_run")
            .InvokeAsync(Arguments("""{"executable":"fake-build","saveWorkspace":true}""", owner), budget.Token).AsTask();
        await preparation.Entered.Task.WaitAsync(budget.Token);
        await budget.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(0, factory.Starts);
        Assert.Empty(registry.List(owner));
    }

    private static TerminalRegistry Registry(string root, Factory factory) => new(
        Workspace.Create(root, root), new TerminalOptions(), factory,
        NullLogger<TerminalSession>.Instance, TestPermissionPolicies.Unconfigured());

    private static AIFunctionArguments Arguments(string json, AgentSessionId owner)
    {
        using var document = JsonDocument.Parse(json);
        return new AIFunctionArguments(document.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name, static property => (object?)property.Value.Clone()))
        {
            Context = new Dictionary<object, object?>
            {
                [typeof(AgentToolContext)] = new AgentToolContext(owner, AgentRunId.Create(), AgentToolCallId.Create(),
                    static (_, _) => ValueTask.CompletedTask)
            }
        };
    }

    private sealed class Preparation : IWorkspaceSavePreparation
    {
        internal TaskCompletionSource<AgentSessionId> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal AgentToolException? Failure { get; init; }
        public async Task SaveAllAsync(AgentSessionId sessionId, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(sessionId);
            await Release.Task.WaitAsync(cancellationToken);
            if (Failure is { } failure) throw failure;
        }
    }

    private sealed class Factory : ITerminalProcessFactory
    {
        private int _starts;
        internal int Starts => Volatile.Read(ref _starts);
        internal FakeTerminalProcess Process { get; } = new();
        public ITerminalProcess Start(string shell, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string?> environment, int columns, int rows)
        {
            Interlocked.Increment(ref _starts);
            return Process;
        }
    }
}
