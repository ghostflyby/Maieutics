using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Execution;
using Maieutics.Frontend;
using Maieutics.Permissions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maieutics.Product.Tests;

/// <summary>The model-facing subagent tools end to end: agent_spawn through a real Agent run,
/// then the task plane addressing the spawned child by its URI — wait, read, ownership, and
/// idempotent cancel (ADR 0030 phase 2a).</summary>
public sealed class AgentSubagentFunctionsTests
{
    [Fact(Timeout = 30_000)]
    public async Task SpawnHandleWaitLoopReturnsTheChildReportThroughTheTaskPlane()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var harness = CreateHarness();
        var session = harness.Session;

        var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        // The in-turn loop: agent_spawn(wait=false) returned the handle, task_wait addressed
        // the child through its task:// URI while the parent run was still live.
        var finishes = events.OfType<AgentToolFinished>().ToList();
        finishes.Should().HaveCount(2);
        var spawnValue = finishes[0].Result.GetProperty("value");
        var taskUri = spawnValue.GetProperty("taskUri").GetString();
        taskUri.Should().StartWith("task://agent/");
        spawnValue.GetProperty("runId").GetString().Should().NotBeNullOrWhiteSpace();
        finishes[1].Result.GetProperty("status").GetString().Should().Be("ok");
        var waitValue = finishes[1].Result.GetProperty("value");
        waitValue.GetProperty("task").GetString().Should().Be(taskUri);
        waitValue.GetProperty("status").GetString().Should().Be("complete");
        waitValue.GetProperty("kind").GetString().Should().Be("agent");
        waitValue.GetProperty("report").GetString().Should().Be("child report");
        // The scripted provider reports no usage, so the snapshot carries "usage": null.
        waitValue.TryGetProperty("usage", out var usage).Should().BeTrue();
        usage.ValueKind.Should().Be(JsonValueKind.Null);

        // The spawned child streamed its activity into the display-plane buffer, which
        // outlives the join: the parent run forgot the child at commit, and later turns
        // refetch the report from this envelope instead of the plane.
        harness.Buffer.KnownChildren.Should().ContainSingle();
        harness.Buffer.Read(harness.Buffer.KnownChildren[0]).Should().NotBeEmpty();

        // After the turn the child is no longer on the plane (join-before-complete), and the
        // plane reports that as a typed miss rather than a stale snapshot.
        var gone = () => harness.Provider.WaitTaskAsync(
            taskUri!, TimeSpan.FromSeconds(1), deadline.Token);
        (await gone.Should().ThrowAsync<ResourceException>())
            .Which.Code.Should().Be("resource_not_found");
    }

    [Fact(Timeout = 30_000)]
    public async Task SpawnRegistersTheChildScopeWithThePermissionRegistry()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var buffer = new SubagentEventBuffer();
        var reference = new SessionRef();
        var overrides = new PermissionOverrideRegistry();
        var source = new AgentTaskResourceSource(
            sessionId => reference.Session is { } live && live.Id == sessionId ? live : null,
            () => reference.Session is { } live ? [live.Id] : []);
        var provider = new TaskResourceProvider([source]);
        // The REAL agent_spawn adapter: the child-scope registration lives inside it, so the
        // session must run the adapter, not a test-local stand-in.
        var functions = new AgentSubagentFunctions(provider, overrides);
        var parentLayer = new PermissionLayer
        {
            Kinds = new Dictionary<PermissionKind, PermissionKindRules>
            {
                [PermissionKind.Read] = new PermissionKindRules { Deny = ["/secret"] }
            }
        };
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("spawn", "agent_spawn", ("input", "child task"))),
            (_, _) => StreamAsync("done"),
            (_, _) => StreamAsync("done"));
        var session = new AgentSession(
            client,
            new AgentSessionOptions
            {
                Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = buffer }
            },
            functions.Functions);
        reference.Session = session;
        overrides.Set(session.Id, parentLayer);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        // The real agent_spawn adapter registered the child scope under the calling session
        // (ADR 0030 decision 3), so the child resolves through the parent's override.
        overrides.ChildScopes.Should().ContainSingle();
        overrides.ChildScopes[0].Parent.Should().Be(session.Id);
        overrides.TryGet(overrides.ChildScopes[0].Child).Should().BeSameAs(parentLayer);
    }

    [Fact(Timeout = 30_000)]
    public async Task ManagerConfigurationDecoratesLeasesWithTheSpawnContext()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("probe", "subagent_probe")),
            (_, _) => StreamAsync("done"));
        using var manager = new MaieuticsAgentSessionManager(
            new FixedProfileProvider(new AgentRunProfile(
                client,
                new AgentSessionOptions(),
                tools: [CreateProbeTool()])),
            familiesRoot: null,
            storeFactory: null,
            NullLogger<MaieuticsAgentSessionManager>.Instance,
            subagents: new AgentSubagentOptions
            {
                MaxDepth = 1,
                MaxChildrenPerTurn = 2,
                EventSink = new SubagentEventBuffer()
            });

        var session = (AgentSession)manager.Resolve(manager.Id);
        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Probe"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        // The probe tool reports whether the spawn context was attached to its arguments.
        events.OfType<AgentToolFinished>().Single().Result.GetProperty("value")
            .GetProperty("status").GetString().Should().Be("attached");
    }

    private static Harness CreateHarness()
    {
        var buffer = new SubagentEventBuffer();
        var reference = new SessionRef();
        var source = new AgentTaskResourceSource(
            sessionId => reference.Session is { } live && live.Id == sessionId ? live : null,
            () => reference.Session is { } live ? [live.Id] : []);
        var provider = new TaskResourceProvider([source]);
        var functions = new AgentSubagentFunctions(provider);
        var postToolCalls = 0;
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("spawn", "agent_spawn", ("input", "child task"), ("wait", false))),
            // The spawn returns before the child's only request, so requests race for slots:
            // route by shape. A single-user-message request is the child; a request carrying a
            // function result is the parent, whose first post-tool call waits on the task URI
            // extracted from the spawn envelope in its own request messages.
            Route,
            Route,
            Route);
        var session = new AgentSession(
            client,
            new AgentSessionOptions
            {
                Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = buffer }
            },
            functions.Functions);
        reference.Session = session;
        return new Harness(session, provider, functions, buffer);

        IAsyncEnumerable<ChatResponseUpdate> Route(IReadOnlyList<ChatMessage> messages, CancellationToken token)
        {
            if (!messages.Any(message =>
                    message.Contents.Any(content => content is FunctionResultContent)))
                return StreamAsync("child report");

            return Interlocked.Increment(ref postToolCalls) == 1
                ? StreamAsync(ToolCallUpdate(
                    "wait",
                    "task_wait",
                    ("uri", ExtractTaskUri(messages)),
                    ("timeoutMs", 5_000)))
                : StreamAsync("done");
        }
    }

    private static string ExtractTaskUri(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var content in messages.SelectMany(message => message.Contents)
                     .OfType<FunctionResultContent>())
        {
            if (content.Result is JsonElement envelope &&
                envelope.ValueKind == JsonValueKind.Object &&
                envelope.TryGetProperty("value", out var value) &&
                value.TryGetProperty("taskUri", out var taskUri) &&
                taskUri.GetString() is { } uri)
                return uri;
        }

        throw new InvalidOperationException("The scripted parent request carried no spawn envelope.");
    }

    private sealed class Harness(
        AgentSession session,
        TaskResourceProvider provider,
        AgentSubagentFunctions functions,
        SubagentEventBuffer buffer)
    {
        internal AgentSession Session { get; } = session;

        internal TaskResourceProvider Provider { get; } = provider;

        internal AgentSubagentFunctions Functions { get; } = functions;

        internal SubagentEventBuffer Buffer { get; } = buffer;
    }

    private sealed class SessionRef
    {
        public AgentSession? Session { get; set; }
    }

    private static AIFunction CreateProbeTool()
    {
        async ValueTask<JsonElement> Invoke(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            _ = AgentSubagentContext.GetRequired(arguments);
            var attached = arguments.Context is not null &&
                           arguments.Context.ContainsKey(typeof(IAgentSubagentSpawner));
            return JsonSerializer.SerializeToElement(
                new ProbeValue(attached ? "attached" : "missing"),
                ProbeJsonContext.Default.ProbeValue);
        }

        return AIFunctionFactory.Create(
            (Func<AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)Invoke,
            new AIFunctionFactoryOptions
            {
                Name = "subagent_probe",
                Description = "Reports whether the spawn context is attached.",
                SerializerOptions = ProbeJsonContext.Default.Options,
                ExcludeResultSchema = true
            });
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return deadline;
    }

    private static AIFunction CreateSpawnTool(
        string name,
        Func<AIFunctionArguments, CancellationToken, Task<JsonElement>> body)
    {
        ValueTask<JsonElement> Invoke(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            return new ValueTask<JsonElement>(body(arguments, cancellationToken));
        }

        return AIFunctionFactory.Create(
            (Func<AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)Invoke,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = $"Test spawn tool {name}.",
                SerializerOptions = SubagentFrameJsonContext.Default.Options,
                ExcludeResultSchema = true
            });
    }

    private static async Task<List<AgentEvent>> ReadEventsAsync(IAgentRun run, CancellationToken cancellationToken)
    {
        var events = new List<AgentEvent>();
        await foreach (var agentEvent in run.Events.WithCancellation(cancellationToken))
            events.Add(agentEvent);
        return events;
    }

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

    private sealed class FixedProfileProvider(AgentRunProfile profile) : IAgentRunProfileProvider
    {
        public Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IAgentRunProfileLease>(new Lease(profile));
        }

        private sealed class Lease(AgentRunProfile profile) : IAgentRunProfileLease
        {
            public AgentRunProfile Profile { get; } = profile;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static ChatResponseUpdate ToolCallUpdate(
        string callId,
        string name,
        params (string Name, object? Value)[] arguments)
    {
        return new ChatResponseUpdate(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    callId,
                    name,
                    arguments.ToDictionary(static argument => argument.Name, static argument => argument.Value))
            ]);
    }

    private sealed class ScriptedChatClient(
        params Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>[] responses)
        : IChatClient
    {
        private readonly Lock gate = new();

        private readonly Queue<Func<IReadOnlyList<ChatMessage>, CancellationToken,
            IAsyncEnumerable<ChatResponseUpdate>>> responses = new(responses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<ChatResponse>(new NotSupportedException());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var request = messages.Select(message => message.Clone()).ToArray();
            Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> response;
            lock (gate)
            {
                response = responses.Dequeue();
            }

            return response(request, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }
}

internal sealed record ProbeValue(string Status);

[JsonSerializable(typeof(ProbeValue))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ProbeJsonContext : JsonSerializerContext;

