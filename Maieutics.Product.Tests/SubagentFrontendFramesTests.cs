using System.Text.Json;
using System.Text.Json.Serialization;
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
        var harness = CreateHarness(postToolParentResponse: "done", useTaskWait: true);

        var accepted = await harness.Service.StartTurnAsync(
            harness.Manager.Id.ToString(), "Spawn", deadline.Token);
        var handle = await harness.HandleSource.Task.WaitAsync(deadline.Token);
        if (!harness.Service.TryGetRun(accepted.RunId, out var stream) || stream is null)
            throw new InvalidOperationException("The submitted run left no retained stream.");
        // Settled, not Completion: the run's completion task settles while the pump may
        // still be draining the last buffered events into the replay; Settled completes
        // strictly after the terminal frames are published, so the snapshot below is whole.
        await stream.Settled.WaitAsync(deadline.Token);

        // The child frames ride the parent stream through a display tap whose publishes
        // are synchronous on the child's pump: OnSubagentStartedAsync publishes the
        // child run.started BEFORE StartChildAsync returns (the pump's first await is
        // inside that callback's call site), so every child frame strictly precedes the
        // spawn tool's result — which the parent renders as its tool.finished frame.
        // The parent's tool.finished is therefore the strict successor proving the child
        // frames were offered, and Settled (awaited above) orders the parent's whole
        // frame sequence after it. Assert synchronously on the settled snapshot.
        // Subscribing after the run completed returns the whole replay as the snapshot.
        var (initialList, _) = stream.Subscribe(0);
        var initial = initialList.ToList();
        var trace = string.Join("|", initial.Select(static frame =>
            $"{frame.Type}@{frame.RunId?[..6]}"))
            + $"; settledNotifications={harness.Buffer.SettledNotifications}";

        var parentRunId = accepted.RunId;
        var childRunId = handle.RunId.Value.ToString("N");

        Assert.Contains(initial, frame => frame.Type == "run.started" && frame.RunId == parentRunId);
        Assert.Contains(initial, frame => frame.Type == "tool.finished" && frame.RunId == parentRunId);
        Assert.Contains(initial, frame => frame.Type == "run.started" && frame.RunId == childRunId);
        Assert.Contains(initial, frame => frame.Type == "text.delta" && frame.RunId == childRunId);
        Assert.Contains(initial, frame => frame.Type == "run.completed" && frame.RunId == childRunId);
        Assert.Contains(initial, frame => frame.Type == "run.completed" && frame.RunId == parentRunId);

        // The child settled before the parent committed (join-before-complete), and the
        // wire order reflects it.
        Assert.True(initial.FindIndex(frame => frame.Type == "run.completed" && frame.RunId == childRunId) < initial.FindIndex(frame => frame.Type == "run.completed" && frame.RunId == parentRunId));

        // A resuming subscriber with a past-the-end cursor receives the child's frames
        // (best-effort replay) plus the parent's terminal frames, but no parent sequenced
        // frames behind its cursor.
        var (resumedList, _) = stream.Subscribe(long.MaxValue);
        var resumed = resumedList.ToList();
        Assert.Contains(resumed, frame => frame.RunId == childRunId && frame.Type == "run.started");
        Assert.Contains(resumed, frame => frame.Type == "run.completed" && frame.RunId == parentRunId);
        Assert.DoesNotContain(resumed, frame => frame.Type == "text.delta" && frame.RunId == parentRunId);
    }

    [Fact(Timeout = 30_000)]
    public async Task RunCancelResolvesALiveSubagentChild()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The parent's post-tool response hangs until the test releases it, ensuring
        // CancelRunAsync operates on a live child (the parent run is still in-flight).
        var parentReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = CreateHarness(
            postToolParentResponse: "done",
            childResponse: (messages, token) => ChildHangAsync(childStarted, token),
            parentRelease: parentReleased.Task);

        var accepted = await harness.Service.StartTurnAsync(
            harness.Manager.Id.ToString(), "Spawn", deadline.Token);
        var handle = await harness.HandleSource.Task.WaitAsync(deadline.Token);
        await childStarted.Task.WaitAsync(deadline.Token);
        if (!harness.Service.TryGetRun(accepted.RunId, out var stream) || stream is null)
            throw new InvalidOperationException("The submitted run left no retained stream.");

        // Cancel the child while the parent run is still in-flight (the parent model is
        // parked on parentReleased). The child settles as cancelled; the parent tool call
        // sees the cancellation and the model finishes normally.
        await harness.Service.CancelRunAsync(handle.RunId.Value.ToString("N"), deadline.Token);

        var result = await handle.Completion.WaitAsync(deadline.Token);
        Assert.Equal(AgentSubagentStatus.Cancelled, result.Status);

        // Release the parent; it returns "done" and the run completes normally.
        parentReleased.TrySetResult();
        await stream.Settled.WaitAsync(deadline.Token);
    }

    private static Harness CreateHarness(
        string postToolParentResponse,
        Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? childResponse = null,
        bool useTaskWait = false,
        Task? parentRelease = null)
    {
        var buffer = new SubagentEventBuffer();
        var handleSource = new TaskCompletionSource<IAgentSubagentHandle>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var taskWaitCalls = 0;
        CreateSpawnTool("agent_probe", (arguments, token) =>
        {
            var handle = AgentSubagentContext.GetRequired(arguments)
                .StartChildAsync(new AgentSubagentSpec { Input = "child task" }, token)
                .AsTask();
            handle.ContinueWith(
                static (completed, state) => ((TaskCompletionSource<IAgentSubagentHandle>)state!).TrySetResult(completed.Result),
                handleSource,
                TaskContinuationOptions.ExecuteSynchronously);
            return new ValueTask<JsonElement>(JsonSerializer.SerializeToElement(
                new SpawnValue("started"),
                SubagentFrameJsonContext.Default.SpawnValue));
        });

        async ValueTask<JsonElement> ProbeWaitAsync(AIFunctionArguments arguments, CancellationToken token)
        {
            var handle = await handleSource.Task.WaitAsync(token).ConfigureAwait(false);
            var result = await AgentSubagentContext.GetRequired(arguments)
                .WaitChildAsync(handle.RunId, Timeout.InfiniteTimeSpan, token)
                .ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(
                new SpawnValue(result.Status.ToString()),
                SubagentFrameJsonContext.Default.SpawnValue);
        }

        var probeWait = CreateSpawnTool("agent_probe_wait", ProbeWaitAsync);

        IAsyncEnumerable<ChatResponseUpdate> Route(IReadOnlyList<ChatMessage> messages, CancellationToken token)
        {
            if (!messages.Any(message =>
                    message.Contents.Any(content => content is FunctionResultContent)))
                return childResponse is not null
                    ? childResponse(messages, token)
                    : StreamAsync("child report");

            // The first post-tool request continues the scripted flow; with useTaskWait the
            // parent waits on the child in-turn, so the child settles before the turn ends
            // and join-before-complete never cancels a still-running child.
            if (Interlocked.Increment(ref taskWaitCalls) == 1 && useTaskWait)
                return StreamAsync(ToolCallUpdate("wait", "agent_probe_wait"));

            // When parentRelease is set, park the parent model so CancelRunAsync can run
            // while the parent run is still in-flight (the child is still alive).
            if (parentRelease is not null)
                return WaitAndStreamAsync(parentRelease, postToolParentResponse, token);

            return StreamAsync(postToolParentResponse);
        }

        var spawnTool = CreateSpawnTool("agent_spawn", (arguments, token) =>
        {
            var handle = AgentSubagentContext.GetRequired(arguments)
                .StartChildAsync(new AgentSubagentSpec { Input = "child task" }, token)
                .AsTask();
            handle.ContinueWith(
                static (completed, state) => ((TaskCompletionSource<IAgentSubagentHandle>)state!).TrySetResult(completed.Result),
                handleSource,
                TaskContinuationOptions.ExecuteSynchronously);
            return new ValueTask<JsonElement>(JsonSerializer.SerializeToElement(
                new SpawnValue("started"),
                SubagentFrameJsonContext.Default.SpawnValue));
        });
        var tools = useTaskWait ? new[] { spawnTool, probeWait } : new[] { spawnTool };

        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("spawn", "agent_spawn", ("input", "child task"))),
            Route,
            Route,
            Route);
        var manager = new MaieuticsAgentSessionManager(
            new FixedProfileProvider(new AgentRunProfile(
                client,
                new AgentSessionOptions(),
                tools: tools)),
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

    private static async IAsyncEnumerable<ChatResponseUpdate> WaitAndStreamAsync(
        Task release, string text,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await release;
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
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

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken) =>
        TestDeadlines.Create(cancellationToken);


}

internal sealed record SpawnValue(string Status);

[JsonSerializable(typeof(SpawnValue))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SubagentFrameJsonContext : JsonSerializerContext;
