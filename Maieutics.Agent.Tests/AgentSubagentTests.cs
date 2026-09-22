using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent.Tests;

public sealed class AgentSubagentTests
{
    [Fact(Timeout = 30_000)]
    public async Task BlockingSpawnReturnsChildReportAndParentTranscriptStaysTurnShaped()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var collector = new EventCollector();
        var childResults = new TaskCompletionSource<AgentSubagentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawnTool = CreateSpawnTool("agent_spawn", async (arguments, token) =>
        {
            var handle = await AgentSubagentContext.GetRequired(arguments)
                .StartChildAsync(new AgentSubagentSpec { Input = "child task" }, token)
                .ConfigureAwait(false);
            var result = await handle.Completion.WaitAsync(token).ConfigureAwait(false);
            childResults.TrySetResult(result);
            return SpawnValue("completed", result.Report, null);
        });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "agent_spawn")),
            (_, _) => StreamAsync("child report"),
            (_, _) => StreamAsync("done"));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = collector }
        }, [spawnTool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        var result = await run.Completion.WaitAsync(deadline.Token);
        var childResult = await childResults.Task.WaitAsync(deadline.Token);

        result.AssistantMessage.Text.Should().Be("done");
        childResult.Status.Should().Be(AgentSubagentStatus.Completed);
        childResult.Report.Should().Be("child report");
        childResult.SessionId.Should().NotBe(session.Id);
        var finished = events.OfType<AgentToolFinished>().Single();
        finished.Result.GetProperty("status").GetString().Should().Be("ok");
        var transcript = session.GetTranscriptSnapshot();
        transcript.Turns.Should().ContainSingle();
        transcript.Turns[0].Messages[^1].Text.Should().Be("done");
        collector.Events.Should().Contain(pair => pair.Event is AgentTextDelta);
        collector.Events.Select(pair => pair.SessionId).Should().OnlyContain(id => id == childResult.SessionId);
    }

    [Fact(Timeout = 30_000)]
    public async Task ParentCancellationCancelsRunningChildAndRollsBackTurn()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handleSource = new TaskCompletionSource<IAgentSubagentHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawnTool = CreateSpawnTool("agent_spawn", async (arguments, token) =>
        {
            var handle = await AgentSubagentContext.GetRequired(arguments)
                .StartChildAsync(new AgentSubagentSpec { Input = "child task" }, token)
                .ConfigureAwait(false);
            handleSource.TrySetResult(handle);
            var result = await handle.Completion.WaitAsync(token).ConfigureAwait(false);
            return SpawnValue(result.Status.ToString(), null, null);
        });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "agent_spawn")),
            (_, token) => WaitAfterTextAsync("child started", childStarted, token));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = new EventCollector() }
        }, [spawnTool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        await childStarted.Task.WaitAsync(deadline.Token);
        await run.CancelAsync(deadline.Token);

        await run.Completion.WaitAsync(deadline.Token)
            .Invoking(static task => task)
            .Should().ThrowAsync<OperationCanceledException>();
        var handle = await handleSource.Task.WaitAsync(deadline.Token);
        var childResult = await handle.Completion.WaitAsync(deadline.Token);
        childResult.Status.Should().Be(AgentSubagentStatus.Cancelled);
        session.GetTranscriptSnapshot().Turns.Should().BeEmpty();
    }

    [Fact(Timeout = 30_000)]
    public async Task HighVolumeChildEventsStayConsumedAndDoNotStallTheChild()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var collector = new EventCollector();
        var spawnTool = CreateSpawnTool("agent_spawn", async (arguments, token) =>
        {
            var handle = await AgentSubagentContext.GetRequired(arguments)
                .StartChildAsync(new AgentSubagentSpec { Input = "child task" }, token)
                .ConfigureAwait(false);
            var result = await handle.Completion.WaitAsync(token).ConfigureAwait(false);
            return SpawnValue(result.Status.ToString(), result.Report, null);
        });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "agent_spawn")),
            (_, _) => StreamTextsAsync(300),
            (_, _) => StreamAsync("done"));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            EventBufferCapacity = 8,
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = collector }
        }, [spawnTool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        events.OfType<AgentToolFinished>().Should().ContainSingle();
        collector.Events.Select(pair => pair.Event).OfType<AgentTextDelta>()
            .Should().HaveCountGreaterThanOrEqualTo(300);
    }

    [Fact(Timeout = 30_000)]
    public async Task DepthBudgetExhaustedLeavesChildWithoutSpawnContext()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var childResults = new TaskCompletionSource<AgentSubagentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawnTool = CreateSpawnTool("agent_spawn", async (arguments, token) =>
        {
            var handle = await AgentSubagentContext.GetRequired(arguments)
                .StartChildAsync(
                    new AgentSubagentSpec { Input = "child task", Tools = ["child_probe"] },
                    token)
                .ConfigureAwait(false);
            var result = await handle.Completion.WaitAsync(token).ConfigureAwait(false);
            childResults.TrySetResult(result);
            return result.Status == AgentSubagentStatus.Completed
                ? SpawnValue("completed", result.Report, null)
                : SpawnValue(result.Status.ToString(), null, result.Failure?.GetType().Name);
        });
        var childProbe = CreateSpawnTool("child_probe", async (arguments, _) => SpawnValue(
            AgentSubagentContext.GetRequired(arguments).SessionId.ToString(), null, null));
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "agent_spawn")),
            (_, _) => StreamAsync(ToolCallUpdate("grandchild", "child_probe")),
            (_, _) => StreamAsync("done"));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = new EventCollector() }
        }, [spawnTool, childProbe]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);
        var childResult = await childResults.Task.WaitAsync(deadline.Token);

        childResult.Status.Should().Be(AgentSubagentStatus.Failed);
        childResult.Failure.Should().BeOfType<AgentToolInvocationException>();
        events.OfType<AgentToolFinished>().Should().ContainSingle();
        events.OfType<AgentToolFinished>().Single().Result.GetProperty("value")
            .GetProperty("failureType").GetString().Should().Be(nameof(AgentToolInvocationException));
        session.GetTranscriptSnapshot().Turns.Should().ContainSingle();
    }

    [Fact(Timeout = 30_000)]
    public async Task PerTurnChildBudgetExhaustionFailsTheSpawnTyped()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var spawnCalls = 0;
        var spawnTool = CreateSpawnTool("agent_spawn", async (arguments, token) =>
        {
            if (Interlocked.Increment(ref spawnCalls) > 1)
            {
                try
                {
                    await AgentSubagentContext.GetRequired(arguments)
                        .StartChildAsync(new AgentSubagentSpec { Input = "second child" }, token)
                        .ConfigureAwait(false);
                    return SpawnValue("completed", null, null);
                }
                catch (AgentSubagentBudgetExceededException exception)
                {
                    return SpawnValue("budget_exhausted", null, exception.LimitName);
                }
            }

            var handle = await AgentSubagentContext.GetRequired(arguments)
                .StartChildAsync(new AgentSubagentSpec { Input = "first child" }, token)
                .ConfigureAwait(false);
            var result = await handle.Completion.WaitAsync(token).ConfigureAwait(false);
            return SpawnValue(result.Status.ToString(), result.Report, null);
        });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("first", "agent_spawn")),
            (_, _) => StreamAsync("report one"),
            (_, _) => StreamAsync(ToolCallUpdate("second", "agent_spawn")),
            (_, _) => StreamAsync("done"));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions
            {
                MaxDepth = 1,
                MaxChildrenPerTurn = 1,
                EventSink = new EventCollector()
            }
        }, [spawnTool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        events.OfType<AgentToolFinished>().Should().HaveCount(2);
        events.OfType<AgentToolFinished>().Last().Result.GetProperty("value")
            .GetProperty("failureType").GetString().Should().Be(nameof(AgentSubagentOptions.MaxChildrenPerTurn));
        session.GetTranscriptSnapshot().Turns.Should().ContainSingle();
    }

    [Fact(Timeout = 30_000)]
    public async Task JoinTerminatesUnsettledChildAtRunEndWithoutStallingCommit()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handleSource = new TaskCompletionSource<IAgentSubagentHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawnTool = CreateSpawnTool("agent_spawn", async (arguments, token) =>
        {
            var handle = await AgentSubagentContext.GetRequired(arguments)
                .StartChildAsync(new AgentSubagentSpec { Input = "child task" }, token)
                .ConfigureAwait(false);
            handleSource.TrySetResult(handle);
            return SpawnValue("started", null, null);
        });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "agent_spawn")),
            // Route by request shape, never by arrival order: the spawn tool returns before
            // the child's first request, so the parent's next request and the child's only
            // request race for the same scripted slot. The parent's post-tool request carries
            // the function result; the child's first request is a single user message. Both
            // slots share the router because either side may dequeue first.
            (messages, token) => messages.Any(message =>
                message.Contents.Any(content => content is FunctionResultContent))
                ? StreamAsync("done")
                : WaitWithoutOutputAsync(childStarted, token),
            (messages, token) => messages.Any(message =>
                message.Contents.Any(content => content is FunctionResultContent))
                ? StreamAsync("done")
                : WaitWithoutOutputAsync(childStarted, token));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = new EventCollector() }
        }, [spawnTool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        var handle = await handleSource.Task.WaitAsync(deadline.Token);
        await childStarted.Task.WaitAsync(deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        var result = await run.Completion.WaitAsync(deadline.Token);

        result.AssistantMessage.Text.Should().Be("done");
        session.GetTranscriptSnapshot().Turns.Should().ContainSingle();
        events.OfType<AgentToolFinished>().Single().Result.GetProperty("status").GetString().Should().Be("ok");
        var childResult = await handle.Completion.WaitAsync(deadline.Token);
        childResult.Status.Should().Be(AgentSubagentStatus.Cancelled);
    }

    [Fact(Timeout = 30_000)]
    public async Task SinkFailureTerminatesChildInsteadOfStallingIt()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var collector = new EventCollector { FailAfter = 2 };
        var childResults = new TaskCompletionSource<AgentSubagentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawnTool = CreateSpawnTool("agent_spawn", async (arguments, token) =>
        {
            var handle = await AgentSubagentContext.GetRequired(arguments)
                .StartChildAsync(new AgentSubagentSpec { Input = "child task" }, token)
                .ConfigureAwait(false);
            var result = await handle.Completion.WaitAsync(token).ConfigureAwait(false);
            childResults.TrySetResult(result);
            return SpawnValue(result.Status.ToString(), null, null);
        });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "agent_spawn")),
            (_, token) => StreamUntilCanceledAsync(token),
            (_, _) => StreamAsync("done"));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = collector }
        }, [spawnTool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);
        var childResult = await childResults.Task.WaitAsync(deadline.Token);

        childResult.Status.Should().Be(AgentSubagentStatus.Cancelled);
        collector.Events.Should().HaveCount(2);
        events.OfType<AgentToolFinished>().Single().Result.GetProperty("value")
            .GetProperty("status").GetString().Should().Be(nameof(AgentSubagentStatus.Cancelled));
    }

    [Fact]
    public void PositiveDepthWithoutEventSinkFailsConfiguration()
    {
        var client = new ScriptedChatClient((_, _) => StreamAsync("done"));
        var construct = () => new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1 }
        });

        construct.Should().Throw<InvalidOperationException>();
    }

    [Fact(Timeout = 30_000)]
    public async Task UnknownAllowlistToolNameRejectsTheSpec()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var session = new AgentSession(new ScriptedChatClient((_, _) => StreamAsync("done")));
        var tool = AIFunctionFactory.Create(
            (string text) => text,
            new AIFunctionFactoryOptions { Name = "known" });
        var tools = ImmutableDictionary.Create<string, AIFunction>(StringComparer.Ordinal).Add("known", tool);
        var spawner = session.SubagentHost.CreateSpawner(
            AgentRunId.Create(),
            tools,
            new AgentSessionOptions
            {
                Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = new EventCollector() }
            });

        await (Spawner: spawner, Token: deadline.Token)
            .Awaiting(static state => state.Spawner.StartChildAsync(
                new AgentSubagentSpec { Input = "x", Tools = ["nope"] }, state.Token))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact(Timeout = 30_000)]
    public async Task AsyncSpawnThenWaitReturnsTheChildReportWhenTheChildCompletesMidTurn()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var collector = new EventCollector();
        IAgentSubagentHandle? spawned = null;
        var postToolCalls = 0;
        var spawnTool = CreateSpawnTool("agent_spawn", async (arguments, token) =>
        {
            spawned = await AgentSubagentContext.GetRequired(arguments)
                .StartChildAsync(new AgentSubagentSpec { Input = "child task" }, token)
                .ConfigureAwait(false);
            return SpawnValue("started", spawned.RunId.Value.ToString("N"), null);
        });
        var waitTool = CreateSpawnTool("agent_wait", async (arguments, token) =>
        {
            var result = await AgentSubagentContext.GetRequired(arguments)
                .WaitChildAsync(spawned!.RunId, Timeout.InfiniteTimeSpan, token)
                .ConfigureAwait(false);
            return SpawnValue(result.Status.ToString(), result.Report, null);
        });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "agent_spawn")),
            // Every slot after the first routes by shape: a single user message is a child
            // request, any request carrying a function result is a parent post-tool request.
            // Never mix explicit mid-slots with a racing child.
            Route, Route, Route);
        var session = new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = collector }
        }, [spawnTool, waitTool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        var result = await run.Completion.WaitAsync(deadline.Token);

        result.AssistantMessage.Text.Should().Be("done");
        spawned.Should().NotBeNull();
        events.OfType<AgentToolFinished>().Last().Result.GetProperty("value")
            .GetProperty("report").GetString().Should().Be("async report");
        collector.Events.Should().Contain(pair => pair.Event is AgentTextDelta);

        IAsyncEnumerable<ChatResponseUpdate> Route(IReadOnlyList<ChatMessage> messages, CancellationToken token)
        {
            return messages.Any(message =>
                       message.Contents.Any(content => content is FunctionResultContent))
                ? Interlocked.Increment(ref postToolCalls) == 1
                    ? StreamAsync(ToolCallUpdate("wait", "agent_wait"))
                    : StreamAsync("done")
                : StreamAsync("async report");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task WaitTimesOutTypedWhileTheChildKeepsRunningUntilTheTurnJoinsIt()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handleSource = new TaskCompletionSource<IAgentSubagentHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawnTool = CreateSpawnTool("agent_spawn", async (arguments, token) =>
        {
            var spawner = AgentSubagentContext.GetRequired(arguments);
            var handle = await spawner
                .StartChildAsync(new AgentSubagentSpec { Input = "child task" }, token)
                .ConfigureAwait(false);
            handleSource.TrySetResult(handle);
            try
            {
                await spawner.WaitChildAsync(handle.RunId, TimeSpan.FromMilliseconds(500), token)
                    .ConfigureAwait(false);
                return SpawnValue("completed", null, null);
            }
            catch (AgentSubagentWaitTimeoutException)
            {
                return SpawnValue("timeout", null, null);
            }
        });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "agent_spawn")),
            (_, token) => WaitWithoutOutputAsync(childStarted, token),
            (_, _) => StreamAsync("done"));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = new EventCollector() }
        }, [spawnTool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        events.OfType<AgentToolFinished>().Single().Result.GetProperty("value")
            .GetProperty("status").GetString().Should().Be("timeout");
        var handle = await handleSource.Task.WaitAsync(deadline.Token);
        var childResult = await handle.Completion.WaitAsync(deadline.Token);
        childResult.Status.Should().Be(AgentSubagentStatus.Cancelled);
        session.GetTranscriptSnapshot().Turns.Should().ContainSingle();
    }

    [Fact(Timeout = 30_000)]
    public async Task CancelChildTerminatesTheRunningChildAndReturnsItsResult()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handleSource = new TaskCompletionSource<IAgentSubagentHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawnTool = CreateSpawnTool("agent_spawn", async (arguments, token) =>
        {
            var spawner = AgentSubagentContext.GetRequired(arguments);
            var handle = await spawner
                .StartChildAsync(new AgentSubagentSpec { Input = "child task" }, token)
                .ConfigureAwait(false);
            handleSource.TrySetResult(handle);
            var result = await spawner.CancelChildAsync(handle.RunId, token).ConfigureAwait(false);
            return SpawnValue(result.Status.ToString(), null, null);
        });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "agent_spawn")),
            (_, token) => WaitWithoutOutputAsync(childStarted, token),
            (_, _) => StreamAsync("done"));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = new EventCollector() }
        }, [spawnTool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        events.OfType<AgentToolFinished>().Single().Result.GetProperty("value")
            .GetProperty("status").GetString().Should().Be(nameof(AgentSubagentStatus.Cancelled));
        var handle = await handleSource.Task.WaitAsync(deadline.Token);
        var childResult = await handle.Completion.WaitAsync(deadline.Token);
        childResult.Status.Should().Be(AgentSubagentStatus.Cancelled);
    }

    [Fact(Timeout = 30_000)]
    public async Task CancelOnATerminalChildReturnsItsResultUnchanged()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var postToolCalls = 0;
        IAgentSubagentHandle? spawned = null;
        var spawnTool = CreateSpawnTool("agent_spawn", async (arguments, token) =>
        {
            spawned = await AgentSubagentContext.GetRequired(arguments)
                .StartChildAsync(new AgentSubagentSpec { Input = "child task" }, token)
                .ConfigureAwait(false);
            var result = await spawned.Completion.WaitAsync(token).ConfigureAwait(false);
            return SpawnValue(result.Status.ToString(), result.Report, null);
        });
        var cancelTool = CreateSpawnTool("agent_cancel", async (arguments, token) =>
        {
            var result = await AgentSubagentContext.GetRequired(arguments)
                .CancelChildAsync(spawned!.RunId, token).ConfigureAwait(false);
            return SpawnValue(result.Status.ToString(), result.Report, null);
        });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("spawn", "agent_spawn")),
            Route, Route, Route);
        var session = new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = new EventCollector() }
        }, [spawnTool, cancelTool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        var finishes = events.OfType<AgentToolFinished>().ToList();
        finishes.Should().HaveCount(2);
        finishes[0].Result.GetProperty("value").GetProperty("report").GetString().Should().Be("b report");
        finishes[1].Result.GetProperty("value").GetProperty("status").GetString()
            .Should().Be(nameof(AgentSubagentStatus.Completed));
        finishes[1].Result.GetProperty("value").GetProperty("report").GetString().Should().Be("b report");

        IAsyncEnumerable<ChatResponseUpdate> Route(IReadOnlyList<ChatMessage> messages, CancellationToken token)
        {
            return messages.Any(message =>
                       message.Contents.Any(content => content is FunctionResultContent))
                ? Interlocked.Increment(ref postToolCalls) == 1
                    ? StreamAsync(ToolCallUpdate("call", "agent_cancel"))
                    : StreamAsync("done")
                : StreamAsync("b report");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task UnknownChildRunIdFailsTypedOnWaitAndCancel()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var unknown = AgentRunId.Create();
        var probeTool = CreateSpawnTool("agent_wait", async (arguments, token) =>
        {
            var spawner = AgentSubagentContext.GetRequired(arguments);
            try
            {
                await spawner.WaitChildAsync(unknown, TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                return SpawnValue("waited", null, null);
            }
            catch (AgentSubagentNotFoundException)
            {
                try
                {
                    await spawner.CancelChildAsync(unknown, token).ConfigureAwait(false);
                    return SpawnValue("cancelled", null, null);
                }
                catch (AgentSubagentNotFoundException)
                {
                    return SpawnValue("not_found", null, null);
                }
            }
        });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "agent_wait")),
            (_, _) => StreamAsync("done"));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = new EventCollector() }
        }, [probeTool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Probe"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        events.OfType<AgentToolFinished>().Single().Result.GetProperty("value")
            .GetProperty("status").GetString().Should().Be("not_found");
    }

    [Fact(Timeout = 30_000)]
    public async Task OverlappingChildrenReportOnlyAfterBothHaveStarted()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var collector = new EventCollector();
        var startedSignals = new ConcurrentQueue<TaskCompletionSource>(
        [
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        ]);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childrenStarted = 0;
        IAgentSubagentHandle? first = null;
        IAgentSubagentHandle? second = null;
        var spawnTool = CreateSpawnTool("agent_spawn", async (arguments, token) =>
        {
            var spawner = AgentSubagentContext.GetRequired(arguments);
            first = await spawner.StartChildAsync(new AgentSubagentSpec { Input = "child a" }, token)
                .ConfigureAwait(false);
            second = await spawner.StartChildAsync(new AgentSubagentSpec { Input = "child b" }, token)
                .ConfigureAwait(false);
            return SpawnValue("spawned", null, null);
        });
        var waitTool = CreateSpawnTool("agent_wait", async (arguments, token) =>
        {
            var spawner = AgentSubagentContext.GetRequired(arguments);
            var firstResult = await spawner.WaitChildAsync(first!.RunId, Timeout.InfiniteTimeSpan, token)
                .ConfigureAwait(false);
            var secondResult = await spawner.WaitChildAsync(second!.RunId, Timeout.InfiniteTimeSpan, token)
                .ConfigureAwait(false);
            return SpawnPair(
                firstResult.Status.ToString(),
                secondResult.Status.ToString(),
                firstResult.Report,
                secondResult.Report);
        });
        var postToolCalls = 0;
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "agent_spawn")),
            // Every slot after the first routes by shape. A child signals its own start and
            // then holds until BOTH children started, so a sequential runtime deadlocks into
            // the test deadline instead of passing.
            Route, Route, Route, Route);
        var session = new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = collector }
        }, [spawnTool, waitTool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        var finishes = events.OfType<AgentToolFinished>().ToList();
        finishes.Should().HaveCount(2);
        finishes[1].Result.GetProperty("value").GetProperty("reportA").GetString().Should().Be("overlap report");
        finishes[1].Result.GetProperty("value").GetProperty("reportB").GetString().Should().Be("overlap report");
        finishes[1].Result.GetProperty("value").GetProperty("statusA").GetString()
            .Should().Be(nameof(AgentSubagentStatus.Completed));
        finishes[1].Result.GetProperty("value").GetProperty("statusB").GetString()
            .Should().Be(nameof(AgentSubagentStatus.Completed));
        collector.Events.Select(pair => pair.SessionId).Distinct().Should().HaveCount(2);

        IAsyncEnumerable<ChatResponseUpdate> Route(IReadOnlyList<ChatMessage> messages, CancellationToken token)
        {
            return messages.Any(message =>
                       message.Contents.Any(content => content is FunctionResultContent))
                ? Interlocked.Increment(ref postToolCalls) == 1
                    ? StreamAsync(ToolCallUpdate("wait", "agent_wait"))
                    : StreamAsync("done")
                : WaitBothStartedAsync(startedSignals, bothStarted, () => Interlocked.Increment(ref childrenStarted), token);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task LifecycleSinkSeesStartedBeforeEventsAndSettledAfterTheChildDrains()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var collector = new OrderedLifecycleSink();
        var spawnTool = CreateSpawnTool("agent_spawn", async (arguments, token) =>
        {
            var handle = await AgentSubagentContext.GetRequired(arguments)
                .StartChildAsync(new AgentSubagentSpec { Input = "child task" }, token)
                .ConfigureAwait(false);
            var result = await handle.Completion.WaitAsync(token).ConfigureAwait(false);
            return SpawnValue(result.Status.ToString(), result.Report, null);
        });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "agent_spawn")),
            (_, _) => StreamAsync("child report"),
            (_, _) => StreamAsync("done"));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = collector }
        }, [spawnTool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);
        await collector.Settled.Task.WaitAsync(deadline.Token);

        collector.Order.Should().Equal("started", "event", "settled");
        collector.SettledStatus.Should().Be(AgentSubagentStatus.Completed);
    }

    [Fact(Timeout = 30_000)]
    public async Task DetachedChildRunsSessionScopedAndStaysAddressable()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var collector = new EventCollector();
        var session = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync("detached report")),
            new AgentSessionOptions
            {
                Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = collector }
            });
        var tools = ImmutableDictionary.Create<string, AIFunction>(StringComparer.Ordinal);
        var spawnerOptions = new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = collector }
        };
        var spawner = session.SubagentHost.CreateSpawner(AgentRunId.Create(), tools, spawnerOptions);
        _ = spawner;

        var handle = await session.SubagentHost.StartDetachedChildAsync(
            new AgentSubagentSpec { Input = "detached task" },
            spawnerOptions,
            [],
            deadline.Token);

        var result = await session.SubagentHost
            .WaitDetachedAsync(handle.RunId, Timeout.InfiniteTimeSpan, deadline.Token);
        result.Status.Should().Be(AgentSubagentStatus.Completed);
        result.Report.Should().Be("detached report");
        session.SubagentHost.FindChild(handle.RunId).Should().NotBeNull();
        session.SubagentHost.ListChildren().Should().ContainSingle();
        collector.Events.Should().NotBeEmpty();

        var cancelled = await session.SubagentHost
            .CancelDetachedAsync(handle.RunId, deadline.Token);
        cancelled.Status.Should().Be(AgentSubagentStatus.Completed);
    }

    [Fact(Timeout = 30_000)]
    public async Task DetachedChildrenAreCappedAndUnknownRunsFailTyped()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var session = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync("done")),
            new AgentSessionOptions
            {
                Subagents = new AgentSubagentOptions { MaxDepth = 1, MaxDetachedChildren = 1, EventSink = new EventCollector() }
            });
        var spawnerOptions = new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, MaxDetachedChildren = 1, EventSink = new EventCollector() }
        };

        await session.SubagentHost.StartDetachedChildAsync(
            new AgentSubagentSpec { Input = "first" }, spawnerOptions, [], deadline.Token);

        var second = () => session.SubagentHost.StartDetachedChildAsync(
            new AgentSubagentSpec { Input = "second" }, spawnerOptions, [], deadline.Token);
        (await second.Should().ThrowAsync<AgentSubagentBudgetExceededException>())
            .Which.LimitName.Should().Be(nameof(AgentSubagentOptions.MaxDetachedChildren));

        await session.SubagentHost
            .Invoking(static host => host.WaitDetachedAsync(
                AgentRunId.Create(), TimeSpan.FromSeconds(1), CancellationToken.None))
            .Should().ThrowAsync<AgentSubagentNotFoundException>();
    }

    [Fact(Timeout = 30_000)]
    public async Task ActiveRunSpawnerResolvesDuringTheTurnAndJoinsItsChildren()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var collector = new EventCollector();
        var sessionRef = new SessionRef();
        AgentSubagentStatus orchestrationStatus = default;

        var orchestrateTool = CreateSpawnTool("orchestrate", async (arguments, token) =>
        {
            // Inside the tool call the session's run is in flight: the orchestration surface
            // resolves to the run-owned spawner, and the child it spawns is joined before the
            // turn commits.
            if (sessionRef.Session is not { } owner)
                return SpawnValue("no-session", null, null);
            var spawner = owner.TryCreateActiveRunSpawner();
            if (spawner is null)
                return SpawnValue("no-active-run", null, null);
            var handle = await spawner
                .StartChildAsync(new AgentSubagentSpec { Input = "child task" }, token)
                .ConfigureAwait(false);
            var result = await handle.Completion.WaitAsync(token).ConfigureAwait(false);
            orchestrationStatus = result.Status;
            return SpawnValue(result.Status.ToString(), result.Report, null);
        });

        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "orchestrate")),
            (_, _) => StreamAsync("child report"),
            (_, _) => StreamAsync("done"));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            Subagents = new AgentSubagentOptions { MaxDepth = 1, EventSink = collector }
        }, [orchestrateTool]);
        sessionRef.Session = session;

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Spawn"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        orchestrationStatus.Should().Be(AgentSubagentStatus.Completed);
        events.OfType<AgentToolFinished>().Single().Result.GetProperty("value")
            .GetProperty("report").GetString().Should().Be("child report");
        session.TryCreateActiveRunSpawner().Should().BeNull();
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return deadline;
    }

    private static AIFunction CreateSpawnTool(
        string name,
        Func<AIFunctionArguments, CancellationToken, Task<JsonElement?>> body)
    {
        ValueTask<JsonElement?> Invoke(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            return new ValueTask<JsonElement?>(body(arguments, cancellationToken));
        }

        return AIFunctionFactory.Create(
            (Func<AIFunctionArguments, CancellationToken, ValueTask<JsonElement?>>)Invoke,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = $"Test spawn tool {name}.",
                SerializerOptions = AgentTestJsonContext.Default.Options,
                ExcludeResultSchema = true
            });
    }

    private static JsonElement SpawnValue(string status, string? report, string? failureType)
    {
        return JsonSerializer.SerializeToElement(
            new SpawnToolValue(status, report, failureType),
            SubagentTestJsonContext.Default.SpawnToolValue);
    }

    private static JsonElement SpawnPair(string statusA, string statusB, string? reportA, string? reportB)
    {
        return JsonSerializer.SerializeToElement(
            new SpawnPairValue(statusA, statusB, reportA, reportB),
            SubagentTestJsonContext.Default.SpawnPairValue);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WaitBothStartedAsync(
        ConcurrentQueue<TaskCompletionSource> startedSignals,
        TaskCompletionSource bothStarted,
        Func<int> countStarted,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!startedSignals.TryDequeue(out var started))
            throw new InvalidOperationException("The test scripted more child requests than children.");
        started.TrySetResult();
        if (countStarted() == 2)
            bothStarted.TrySetResult();
        await bothStarted.Task.WaitAsync(cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, "overlap report");
    }

    private static async Task<List<AgentEvent>> ReadEventsAsync(IAgentRun run, CancellationToken cancellationToken)
    {
        var events = new List<AgentEvent>();
        await foreach (var agentEvent in run.Events.WithCancellation(cancellationToken))
            events.Add(agentEvent);
        return events;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(ChatResponseUpdate update)
    {
        await Task.Yield();
        yield return update;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(string text)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamTextsAsync(int count)
    {
        for (var index = 0; index < count; index++)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"t{index}");
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamUntilCanceledAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var index = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"c{index++}");
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WaitAfterTextAsync(
        string text,
        TaskCompletionSource waiting,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        waiting.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WaitWithoutOutputAsync(
        TaskCompletionSource waiting,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        waiting.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
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

    private sealed class EventCollector : IAgentSubagentEventSink
    {
        private readonly Lock gate = new();

        public List<(AgentSessionId SessionId, AgentEvent Event)> Events { get; } = [];

        public int FailAfter { get; init; } = int.MaxValue;

        public ValueTask OnSubagentEventAsync(
            AgentSessionId childSessionId,
            AgentEvent agentEvent,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                Events.Add((childSessionId, agentEvent));
                if (Events.Count >= FailAfter)
                    throw new InvalidOperationException("The test sink failed on purpose.");
            }

            return ValueTask.CompletedTask;
        }
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

    private sealed class SessionRef
    {
        public AgentSession? Session { get; set; }
    }

    private sealed class OrderedLifecycleSink : IAgentSubagentEventSink, IAgentSubagentLifecycleSink
    {
        private readonly Lock gate = new();

        public List<string> Order { get; } = [];

        public AgentSubagentStatus SettledStatus { get; private set; }

        public TaskCompletionSource Settled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private bool seenEvent;

        public ValueTask OnSubagentStartedAsync(
            AgentSessionId childSessionId,
            AgentRunId childRunId,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                Order.Add("started");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask OnSubagentEventAsync(
            AgentSessionId childSessionId,
            AgentEvent agentEvent,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (!seenEvent)
                {
                    seenEvent = true;
                    Order.Add("event");
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask OnSubagentSettledAsync(
            AgentSessionId childSessionId,
            AgentRunId childRunId,
            AgentSubagentStatus status,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                Order.Add("settled");
                SettledStatus = status;
                Settled.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }
}

internal sealed record SpawnToolValue(string Status, string? Report, string? FailureType);

internal sealed record SpawnPairValue(string StatusA, string StatusB, string? ReportA, string? ReportB);

[JsonSerializable(typeof(SpawnToolValue))]
[JsonSerializable(typeof(SpawnPairValue))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SubagentTestJsonContext : JsonSerializerContext;
