using System.Text.Json;
using Microsoft.Extensions.AI;
using ScriptedChatClient = Maieutics.Testing.ScriptedChatClient;

namespace Maieutics.Agent.Tests;

/// <summary>
///     The tool-approval gate: a tool call suspends before any side effect, a denial is a
///     recoverable tool failure rather than a turn failure, and every non-answer path resolves
///     to a denial instead of hanging or silently executing.
/// </summary>
public sealed class AgentToolApprovalTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 30_000)]
    public async Task ADeniedCallReturnsToolDeniedAndTheTurnContinues()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, Deadline);
        var gate = new RecordingGate(_ => AgentToolApprovalDecision.Deny("Not allowed."));
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("provider-call", "guarded")),
            (_, _) => StreamAsync("I did not run it."));
        var session = CreateSession(client, gate, RequireAll("guarded"));

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Run it."), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        var result = await run.Completion.WaitAsync(deadline.Token);

        // The model sees a typed, recoverable failure — not a turn failure.
        var finished = Assert.Single(events.OfType<AgentToolFinished>());
        Assert.Equal("error", finished.Result.GetProperty("status").GetString());
        Assert.Equal("tool_denied", finished.Result.GetProperty("code").GetString());
        Assert.Equal("Not allowed.", finished.Result.GetProperty("message").GetString());

        Assert.Single(events.OfType<AgentToolApprovalRequested>());
        var resolved = Assert.Single(events.OfType<AgentToolApprovalResolved>());
        Assert.False(resolved.Approved);
        Assert.Equal("Not allowed.", resolved.Reason);

        // The turn committed: the denial is model input, not a rollback.
        Assert.Single(result.Transcript.Turns);
        var toolResult = result.Transcript.Turns[0].Messages
            .SelectMany(static message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single();
        Assert.Equal("tool_denied", Assert.IsType<JsonElement>(toolResult.Result).GetProperty("code").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task AnApprovedCallExecutesTheTool()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, Deadline);
        var invoked = false;
        var gate = new RecordingGate(_ => AgentToolApprovalDecision.Allow("Go ahead."));
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("provider-call", "guarded")),
            (_, _) => StreamAsync("Done."));
        var session = CreateSession(
            client,
            gate,
            RequireAll("guarded"),
            CreateTool("guarded", () => invoked = true));

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Run it."), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        Assert.True(invoked, "the approved call reached the tool");
        Assert.Equal("ok", Assert.Single(events.OfType<AgentToolFinished>()).Result.GetProperty("status").GetString());
        Assert.True(Assert.Single(events.OfType<AgentToolApprovalResolved>()).Approved);
    }

    [Fact(Timeout = 30_000)]
    public async Task AToolThePolicyDoesNotRequireNeverReachesTheGate()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, Deadline);
        var gate = new RecordingGate(_ => throw new InvalidOperationException("the gate must not run"));
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("provider-call", "guarded")),
            (_, _) => StreamAsync("Done."));
        var session = CreateSession(client, gate, new AgentToolApprovalPolicy());

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Run it."), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        Assert.Empty(gate.Requests);
        Assert.Empty(events.OfType<AgentToolApprovalRequested>());
        Assert.Equal("ok", Assert.Single(events.OfType<AgentToolFinished>()).Result.GetProperty("status").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task AlwaysAllowSkipsTheGateForLaterCallsInTheSameTurn()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, Deadline);
        var gate = new RecordingGate(_ => new AgentToolApprovalDecision(true, "Always.", AlwaysAllow: true));
        var memory = new AgentToolApprovalMemory();
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("first", "guarded")),
            (_, _) => StreamAsync(ToolCallUpdate("second", "guarded")),
            (_, _) => StreamAsync("Both done."));
        var session = CreateSession(client, gate, RequireAll("guarded"), memory: memory);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Run it twice."), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        // Two calls, one approval: the second consults the remembered allowance.
        Assert.Single(gate.Requests);
        Assert.Single(events.OfType<AgentToolApprovalRequested>());
        Assert.Equal(2, events.OfType<AgentToolFinished>().Count());
    }

    [Fact]
    public void AlwaysAllowIsScopedToItsSession()
    {
        var memory = new AgentToolApprovalMemory();
        var first = AgentSessionId.Create();
        var second = AgentSessionId.Create();
        memory.AllowAlways(first, "guarded");

        Assert.True(memory.IsAllowed(first, "guarded"));
        Assert.False(memory.IsAllowed(second, "guarded"));

        memory.Clear(first);
        Assert.False(memory.IsAllowed(first, "guarded"));
    }

    [Fact(Timeout = 30_000)]
    public async Task AFailingGateDeniesRatherThanExecuting()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, Deadline);
        var invoked = false;
        var gate = new RecordingGate(_ => throw new InvalidOperationException("approver offline"));
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("provider-call", "guarded")),
            (_, _) => StreamAsync("Understood."));
        var session = CreateSession(
            client,
            gate,
            RequireAll("guarded"),
            CreateTool("guarded", () => invoked = true));

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Run it."), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        Assert.False(invoked, "a broken gate must never turn into silent execution");
        var finished = Assert.Single(events.OfType<AgentToolFinished>());
        Assert.Equal("tool_denied", finished.Result.GetProperty("code").GetString());
        Assert.Contains("approver offline", finished.Result.GetProperty("message").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellingTheTurnWhileWaitingDoesNotHangOrExecute()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, Deadline);
        var invoked = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new RecordingGate(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return AgentToolApprovalDecision.Allow("unreachable");
        });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("provider-call", "guarded")),
            (_, _) => StreamAsync("unreachable"));
        var session = CreateSession(
            client,
            gate,
            RequireAll("guarded"),
            CreateTool("guarded", () => invoked = true));

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Run it."), deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);

        await run.CancelAsync(deadline.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Completion.WaitAsync(deadline.Token));

        Assert.False(invoked, "a cancelled turn must not execute the pending tool");
    }

    [Fact(Timeout = 30_000)]
    public async Task ADestructiveClassificationRequiresApprovalWithoutAnyNameRule()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, Deadline);
        var gate = new RecordingGate(_ => AgentToolApprovalDecision.Allow("ok"));
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("provider-call", "plugin_writer")),
            (_, _) => StreamAsync("Done."));
        var session = CreateSession(
            client,
            gate,
            new AgentToolApprovalPolicy(),
            CreateTool(
                "plugin_writer",
                classification: new AgentToolClassification(ReadOnly: false, Destructive: true)));

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Run it."), deadline.Token);
        await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        var request = Assert.Single(gate.Requests);
        Assert.Contains("destroy", request.Reason);
    }

    [Fact(Timeout = 30_000)]
    public async Task AReadOnlyClassificationIsExemptEvenWhenANameRuleMatches()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, Deadline);
        var gate = new RecordingGate(_ => throw new InvalidOperationException("the gate must not run"));
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("provider-call", "guarded_reader")),
            (_, _) => StreamAsync("Done."));
        var session = CreateSession(
            client,
            gate,
            RequireAll("guarded_*"),
            CreateTool(
                "guarded_reader",
                classification: new AgentToolClassification(ReadOnly: true, Destructive: false)));

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Read it."), deadline.Token);
        await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        Assert.Empty(gate.Requests);
    }

    [Fact]
    public void ThePolicyMatchesGlobPatternsCaseInsensitively()
    {
        var policy = new AgentToolApprovalPolicy { Require = ["workspace_*", "shell"] };

        Assert.True(policy.RequiresApproval("workspace_write", null, out var reason));
        Assert.Contains("workspace_*", reason);
        Assert.True(policy.RequiresApproval("WORKSPACE_WRITE", null, out _));
        Assert.True(policy.RequiresApproval("shell", null, out _));
        Assert.True(policy.RequiresApproval("workspace_read", null, out _));
        Assert.False(policy.RequiresApproval("unrelated", null, out var noReason));
        Assert.Equal(string.Empty, noReason);
    }

    [Fact]
    public void AStarPatternMatchesEveryTool()
    {
        var policy = new AgentToolApprovalPolicy { Require = ["*"] };
        Assert.True(policy.RequiresApproval("anything", null, out _));
        Assert.True(policy.RequiresApproval("other_tool", null, out _));
    }

    [Fact]
    public void ADestructiveDeclarationOutranksAReadOnlyExemption()
    {
        // A tool that declares both read-only and destructive is contradictory; the safe
        // reading is that it needs approval.
        var policy = new AgentToolApprovalPolicy();
        Assert.True(policy.RequiresApproval(
            "contradictory",
            new AgentToolClassification(ReadOnly: true, Destructive: true),
            out _));
    }

    [Fact]
    public void TheClassificationIsReadFromTheFunctionsAdditionalProperties()
    {
        var declared = CreateTool(
            "declared",
            classification: new AgentToolClassification(ReadOnly: true, Destructive: false));
        var undeclared = CreateTool("undeclared");

        Assert.Equal(
            new AgentToolClassification(ReadOnly: true, Destructive: false),
            AgentToolClassification.From(declared));
        Assert.Null(AgentToolClassification.From(undeclared));
    }

    [Fact(Timeout = 30_000)]
    public async Task AnOversizedDenialReasonIsBoundedBeforeItEntersTheTranscript()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, Deadline);
        // The reason can carry a user's typed refusal, so it is untrusted text that must obey
        // the same bounding every other result path does.
        var reason = new string('x', 30_000);
        var gate = new RecordingGate(_ => AgentToolApprovalDecision.Deny(reason));
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("provider-call", "guarded")),
            (_, _) => StreamAsync("Understood."));
        var session = CreateSession(client, gate, RequireAll("guarded"));

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Run it."), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        var result = await run.Completion.WaitAsync(deadline.Token);

        var finished = Assert.Single(events.OfType<AgentToolFinished>());
        var message = finished.Result.GetProperty("message").GetString()!;
        Assert.True(message.Length < 5000, $"the committed reason is bounded (was {message.Length})");
        Assert.EndsWith("…", message);

        var committed = result.Transcript.Turns[0].Messages
            .SelectMany(static entry => entry.Contents)
            .OfType<FunctionResultContent>()
            .Single();
        var committedMessage = Assert.IsType<JsonElement>(committed.Result)
            .GetProperty("message").GetString()!;
        Assert.True(
            committedMessage.Length < 5000,
            $"the transcript copy is bounded too (was {committedMessage.Length})");
    }

    [Fact(Timeout = 30_000)]
    public async Task AnEmptyDenialReasonStillCarriesAMessage()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, Deadline);
        var gate = new RecordingGate(_ => new AgentToolApprovalDecision(false, "   ", false));
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("provider-call", "guarded")),
            (_, _) => StreamAsync("Understood."));
        var session = CreateSession(client, gate, RequireAll("guarded"));

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Run it."), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        var finished = Assert.Single(events.OfType<AgentToolFinished>());
        Assert.False(string.IsNullOrWhiteSpace(finished.Result.GetProperty("message").GetString()));
    }

    [Fact]
    public void AChangedDeclarationIsAskedAgainRatherThanInheritingTheAllowance()
    {
        // A plugin reload can change what the same tool name declares. The user approved the
        // declaration they were shown, so a tool that becomes destructive must re-ask.
        var memory = new AgentToolApprovalMemory();
        var session = AgentSessionId.Create();
        var readOnly = new AgentToolClassification(ReadOnly: true, Destructive: false);
        var destructive = new AgentToolClassification(ReadOnly: false, Destructive: true);

        memory.AllowAlways(session, "remove_file", readOnly);

        Assert.True(memory.IsAllowed(session, "remove_file", readOnly));
        Assert.False(
            memory.IsAllowed(session, "remove_file", destructive),
            "a tool whose declaration changed must be approved again");
        Assert.False(memory.IsAllowed(session, "remove_file", classification: null));
    }

    [Fact]
    public void TheMemoryBoundIsTheDeclaredCapacity()
    {
        // The eviction test above only pins "some cap"; this pins the cap itself, so a change
        // to the constant is a deliberate edit rather than a silent regression.
        var memory = new AgentToolApprovalMemory();
        var oldest = AgentSessionId.Create();
        memory.AllowAlways(oldest, "oldest");

        // 1024 is the capacity: filling it exactly must keep the first entry.
        for (var index = 0; index < 1023; index++)
            memory.AllowAlways(AgentSessionId.Create(), $"tool-{index}");
        Assert.True(memory.IsAllowed(oldest, "oldest"), "the first entry survives a full table");

        // One more evicts it.
        memory.AllowAlways(AgentSessionId.Create(), "overflow");
        Assert.False(memory.IsAllowed(oldest, "oldest"), "the oldest entry is evicted at the cap");
    }

    private static AgentToolApprovalPolicy RequireAll(string pattern) =>
        new() { Require = [pattern] };

    private static AIFunction CreateTool(
        string name,
        Action? onInvoke = null,
        AgentToolClassification? classification = null)
    {
        async ValueTask<JsonElement?> InvokeAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            _ = arguments;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            onInvoke?.Invoke();
            return JsonSerializer.SerializeToElement(new { echoed = true });
        }

        var function = AIFunctionFactory.Create(
            (Func<AIFunctionArguments, CancellationToken, ValueTask<JsonElement?>>)InvokeAsync,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = $"Test tool {name}.",
                ExcludeResultSchema = true
            });
        return classification is null
            ? function
            : new ClassifiedTool(function, classification);
    }

    /// <summary>Attaches a classification the way a real producer does (the plugin bridge
    /// overrides <see cref="AIFunction.AdditionalProperties" /> the same way).</summary>
    private sealed class ClassifiedTool : DelegatingAIFunction
    {
        private readonly AdditionalPropertiesDictionary _properties;

        internal ClassifiedTool(AIFunction inner, AgentToolClassification classification)
            : base(inner)
        {
            _properties = new AdditionalPropertiesDictionary
            {
                [AgentToolClassification.PropertiesKey] = classification,
            };
        }

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => _properties;
    }

    private static AgentSession CreateSession(
        ScriptedChatClient client,
        IAgentToolApprovalGate gate,
        AgentToolApprovalPolicy policy,
        AIFunction? tool = null,
        AgentToolApprovalMemory? memory = null)
    {
        return new AgentSession(
            client,
            new AgentSessionOptions
            {
                ToolApproval = new AgentToolApprovalOptions
                {
                    Gate = gate,
                    Policy = policy,
                    Memory = memory,
                },
            },
            tools: [tool ?? CreateTool("guarded")]);
    }

    private static async Task<IReadOnlyList<AgentEvent>> ReadEventsAsync(
        IAgentRun run,
        CancellationToken cancellationToken)
    {
        var events = new List<AgentEvent>();
        await foreach (var agentEvent in run.Events.WithCancellation(cancellationToken)) events.Add(agentEvent);

        return events;
    }

    private static ChatResponseUpdate ToolCallUpdate(string callId, string name) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, name, new Dictionary<string, object?>())]);

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(string text)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(ChatResponseUpdate update)
    {
        await Task.Yield();
        yield return update;
    }

    /// <summary>Records every approval request and answers from the supplied function.</summary>
    private sealed class RecordingGate : IAgentToolApprovalGate
    {
        private readonly Func<AgentToolApprovalRequest, CancellationToken, ValueTask<AgentToolApprovalDecision>> _body;

        internal RecordingGate(Func<AgentToolApprovalRequest, AgentToolApprovalDecision> body)
            : this((request, _) => ValueTask.FromResult(body(request)))
        {
        }

        internal RecordingGate(
            Func<AgentToolApprovalRequest, CancellationToken, ValueTask<AgentToolApprovalDecision>> body)
        {
            this._body = body;
        }

        internal List<AgentToolApprovalRequest> Requests { get; } = [];

        public ValueTask<AgentToolApprovalDecision> RequestApprovalAsync(
            AgentToolApprovalRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _body(request, cancellationToken);
        }
    }
}
