using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Frontend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

/// <summary>Subagent frames on the session events stream (ADR 0030 phase 2b): a spawned
/// child's activity and lifecycle frames flow on the parent run's stream under the child's
/// own runId, replay best-effort, and the run cancel endpoint resolves live children.</summary>
public sealed class SubagentFrontendFramesTests
{
    [Fact(Timeout = 30_000)]
    public async Task ChildFramesFlowOnTheParentStreamUnderTheChildRunId()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var harness = CreateHarness(postToolParentResponse: "done");

        var accepted = await harness.Service.StartTurnAsync(
            harness.Manager.Id.ToString(), "Spawn", deadline.Token);
        var handle = await harness.HandleSource.Task.WaitAsync(deadline.Token);
        if (!harness.Service.TryGetRun(accepted.RunId, out var stream) || stream is null)
            throw new InvalidOperationException("The submitted run left no retained stream.");
        // Settled, not Completion: the run's completion task settles while the pump may
        // still be draining the last buffered events into the replay; Settled completes
        // strictly after the terminal frames are published, so the snapshot below is whole.
        await stream.Settled.WaitAsync(deadline.Token);

        // Subscribing after the run completed returns the whole replay as the snapshot.
        var (initialList, _) = stream.Subscribe(0);
        var initial = initialList.ToList();
        var trace = string.Join("|", initial.Select(static frame =>
            $"{frame.Type}@{frame.RunId?[..6]}"))
            + $"; settledNotifications={harness.Buffer.SettledNotifications}";

        var parentRunId = accepted.RunId;
        var childRunId = handle.RunId.Value.ToString("N");

        initial.Should().Contain(frame => frame.Type == "run.started" && frame.RunId == parentRunId);
        initial.Should().Contain(frame => frame.Type == "tool.finished" && frame.RunId == parentRunId);
        initial.Should().Contain(frame => frame.Type == "run.started" && frame.RunId == childRunId);
        initial.Should().Contain(frame => frame.Type == "text.delta" && frame.RunId == childRunId);
        initial.Should().Contain(frame => frame.Type == "run.completed" && frame.RunId == childRunId, "frames: {0}", trace);
        initial.Should().Contain(frame => frame.Type == "run.completed" && frame.RunId == parentRunId);

        // The child settled before the parent committed (join-before-complete), and the
        // wire order reflects it.
        initial.FindIndex(frame => frame.Type == "run.completed" && frame.RunId == childRunId)
            .Should().BeLessThan(initial.FindIndex(frame => frame.Type == "run.completed" && frame.RunId == parentRunId));

        // A resuming subscriber with a past-the-end cursor receives the child's frames
        // (best-effort replay) plus the parent's terminal frames, but no parent sequenced
        // frames behind its cursor.
        var (resumedList, _) = stream.Subscribe(long.MaxValue);
        var resumed = resumedList.ToList();
        resumed.Should().Contain(frame => frame.RunId == childRunId && frame.Type == "run.started");
        resumed.Should().Contain(frame => frame.Type == "run.completed" && frame.RunId == parentRunId);
        resumed.Should().NotContain(frame => frame.Type == "text.delta" && frame.RunId == parentRunId);
    }

    [Fact(Timeout = 30_000)]
    public async Task RunCancelResolvesALiveSubagentChild()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = CreateHarness(
            postToolParentResponse: "done",
            childResponse: (messages, token) => ChildHangAsync(childStarted, token));

        var accepted = await harness.Service.StartTurnAsync(
            harness.Manager.Id.ToString(), "Spawn", deadline.Token);
        var handle = await harness.HandleSource.Task.WaitAsync(deadline.Token);
        await childStarted.Task.WaitAsync(deadline.Token);
        if (!harness.Service.TryGetRun(accepted.RunId, out var stream) || stream is null)
            throw new InvalidOperationException("The submitted run left no retained stream.");

        await harness.Service.CancelRunAsync(handle.RunId.Value.ToString("N"), deadline.Token);

        var result = await handle.Completion.WaitAsync(deadline.Token);
        result.Status.Should().Be(AgentSubagentStatus.Cancelled);
        await stream.Settled.WaitAsync(deadline.Token);
    }

    private static Harness CreateHarness(
        string postToolParentResponse,
        Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? childResponse = null)
    {
        var buffer = new SubagentEventBuffer();
        var handleSource = new TaskCompletionSource<IAgentSubagentHandle>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask<JsonElement> SpawnAsync(AIFunctionArguments arguments, CancellationToken token)
        {
            var handle = await AgentSubagentContext.GetRequired(arguments)
                .StartChildAsync(new AgentSubagentSpec { Input = "child task" }, token)
                .ConfigureAwait(false);
            handleSource.TrySetResult(handle);
            return JsonSerializer.SerializeToElement(
                new SpawnValue("started"),
                SubagentFrameJsonContext.Default.SpawnValue);
        }

        var spawnTool = CreateSpawnTool("agent_spawn", SpawnAsync);

        IAsyncEnumerable<ChatResponseUpdate> Route(IReadOnlyList<ChatMessage> messages, CancellationToken token)
        {
            if (!messages.Any(message =>
                    message.Contents.Any(content => content is FunctionResultContent)))
                return childResponse is not null
                    ? childResponse(messages, token)
                    : StreamAsync("child report");

            return StreamAsync(postToolParentResponse);
        }

        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("spawn", "agent_spawn", ("input", "child task"))),
            Route,
            Route);
        var manager = new MaieuticsAgentSessionManager(
            new FixedProfileProvider(new AgentRunProfile(
                client,
                new AgentSessionOptions(),
                tools: [spawnTool])),
            familiesRoot: null,
            storeFactory: null,
            NullLogger<MaieuticsAgentSessionManager>.Instance,
            subagents: new AgentSubagentOptions { MaxDepth = 1, EventSink = buffer });
        var service = new FrontendSessionService(
            manager,
            new MaieuticsCommandExecutor(manager, null, null, null, null),
            new FrontendDenoReplPresentationRouter(),
            NullLogger<FrontendSessionService>.Instance,
            subagentEvents: buffer);
        return new Harness(manager, service, buffer, handleSource);
    }

    private sealed class Harness(
        MaieuticsAgentSessionManager manager,
        FrontendSessionService service,
        SubagentEventBuffer buffer,
        TaskCompletionSource<IAgentSubagentHandle> handleSource)
    {
        internal MaieuticsAgentSessionManager Manager { get; } = manager;

        internal FrontendSessionService Service { get; } = service;

        internal SubagentEventBuffer Buffer { get; } = buffer;

        internal TaskCompletionSource<IAgentSubagentHandle> HandleSource { get; } = handleSource;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ChildHangAsync(
        TaskCompletionSource started,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    private static AIFunction CreateSpawnTool(
        string name,
        Func<AIFunctionArguments, CancellationToken, ValueTask<JsonElement>> body)
    {
        ValueTask<JsonElement> Invoke(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            return body(arguments, cancellationToken);
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

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return deadline;
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
}

internal sealed record SpawnValue(string Status);

[JsonSerializable(typeof(SpawnValue))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SubagentFrameJsonContext : JsonSerializerContext;
