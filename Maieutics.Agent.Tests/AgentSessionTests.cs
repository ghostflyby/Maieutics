using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using ScriptedChatClient = Maieutics.Testing.ScriptedChatClient;

namespace Maieutics.Agent.Tests;

public sealed class AgentSessionTests
{
    [Fact(Timeout = 30_000)]
    public async Task StartTurnStartsProviderImmediatelyAndReservesSessionBeforeReturning()
    {
        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedChatClient(
            (_, token) => WaitForGateAsync(providerStarted, releaseProvider.Task, "first", token),
            (_, _) => StreamAsync("second"));
        var session = new AgentSession(client);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("first"), TestContext.Current.CancellationToken);

        await providerStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(Guid.Empty, run.Id.Value);
        Assert.Equal(session.Id, run.SessionId);
        await Assert.ThrowsAnyAsync<AgentTurnInProgressException>(async () => { await session.StartTurnAsync(AgentTurn.FromText("concurrent"), TestContext.Current.CancellationToken); });

        releaseProvider.SetResult();
        await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        await run.Completion.WaitAsync(TestContext.Current.CancellationToken);

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(next, TestContext.Current.CancellationToken);
        Assert.Equal("second", (await next.Completion.WaitAsync(TestContext.Current.CancellationToken)).AssistantMessage.Text);
    }

    [Fact(Timeout = 30_000)]
    public async Task SuccessfulRunStreamsStableIdsSequencesAndCommittedTranscript()
    {
        var session = new AgentSession(new ScriptedChatClient((_, _) => StreamAsync("Hello", " world")));

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Question"), TestContext.Current.CancellationToken);
        var events = await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        var result = await run.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, events.Count());
        Assert.Equal([1, 2, 3], events.Select(agentEvent => agentEvent.Sequence));
        var runId = run.Id;
        Assert.All(events, agentEvent => Assert.True(agentEvent.RunId == runId));
        var deltas = events.OfType<AgentTextDelta>().ToArray();
        Assert.Equal(["Hello", " world"], deltas.Select(delta => delta.Text));
        var completed = events.OfType<AgentMessageCompleted>().Single();
        Assert.All(deltas.Select(delta => delta.MessageId), id => Assert.True(id == completed.AgentMessageId));
        Assert.Equivalent(result.AssistantMessage, completed.Message);

        Assert.Equal(run.Id, result.RunId);
        Assert.Equal(ChatRole.User, result.UserMessage.Role);
        Assert.Equal("Question", result.UserMessage.Text);
        Assert.Equal(ChatRole.Assistant, result.AssistantMessage.Role);
        Assert.Equal("Hello world", result.AssistantMessage.Text);
        Assert.Equal(session.Id, result.Transcript.SessionId);
        Assert.Equal(1, result.Transcript.Version);
        var item = Assert.Single(result.Transcript.Turns);
        Assert.Equal(run.Id, item.RunId);
        Assert.Equal([
            (result.UserMessage.Role, result.UserMessage.Text), (result.AssistantMessage.Role, result.AssistantMessage.Text),
        ], result.Transcript.Turns[0].Messages.Select(static message => (message.Role, message.Text)));
        Assert.Equivalent(result.Transcript, session.GetTranscriptSnapshot());
    }

    [Fact(Timeout = 30_000)]
    public async Task ProviderFailureRollsBackPartialTurnAndReleasesSession()
    {
        var client = new ScriptedChatClient(
            (_, _) => FailAfterTextAsync("partial", new InvalidOperationException("provider failed")),
            (_, _) => StreamAsync("recovered"));
        var session = new AgentSession(client);

        await using var failedRun = await session.StartTurnAsync(AgentTurn.FromText("failure"), TestContext.Current.CancellationToken);
        var events = await ReadEventsAsync(failedRun, TestContext.Current.CancellationToken);
        Assert.Equal(["partial"], events.OfType<AgentTextDelta>().Select(delta => delta.Text));
        var failure = await Assert.ThrowsAnyAsync<AgentProviderException>(async () => { await failedRun.Completion.WaitAsync(TestContext.Current.CancellationToken); });
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Empty(session.GetTranscriptSnapshot().Turns);

        await using var recoveredRun = await session.StartTurnAsync(AgentTurn.FromText("retry"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(recoveredRun, TestContext.Current.CancellationToken);
        Assert.Equal("recovered", (await recoveredRun.Completion.WaitAsync(TestContext.Current.CancellationToken)).AssistantMessage.Text);
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellationPreservesPartialEventsAndCommitsTurn()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedChatClient(
            (_, token) => WaitAfterTextAsync("partial", waiting, token),
            (_, _) => StreamAsync("next"));
        var session = new AgentSession(client);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("cancel"), TestContext.Current.CancellationToken);
        await waiting.Task.WaitAsync(TestContext.Current.CancellationToken);
        await run.CancelAsync(TestContext.Current.CancellationToken);
        var events = await ReadEventsAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal(["partial"], events.OfType<AgentTextDelta>().Select(delta => delta.Text));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await run.Completion; });
        var turn = Assert.Single(session.GetTranscriptSnapshot().Turns);
        Assert.Equal(run.Id, turn.RunId);
        Assert.True(turn.Truncated);
        Assert.Equal(new[] { "cancel", "partial" }, turn.Messages.Select(message => message.Text));

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(next, TestContext.Current.CancellationToken);
        await next.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "cancel", "partial", "next" }, client.Requests[1].Select(message => message.Text));
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellationRejectsNonAssistantResponseRolesAndLeavesHistoryUnchanged()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync("previous answer"),
            (_, token) => WaitAfterUpdateAsync(new ChatResponseUpdate(ChatRole.User, "injected user message"), waiting, token),
            (_, _) => StreamAsync("recovered"));
        var session = new AgentSession(client);
        await CompleteTurnAsync(session, "previous question", deadline.Token);
        var before = session.GetTranscriptSnapshot();

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("question"), deadline.Token);
        var events = ReadEventsAsync(run, deadline.Token);
        await waiting.Task.WaitAsync(deadline.Token);
        await run.CancelAsync(deadline.Token);
        await events;
        await Assert.ThrowsAnyAsync<AgentUnsupportedResponseException>(() => run.Completion);
        Assert.Equivalent(before, session.GetTranscriptSnapshot());

        await CompleteTurnAsync(session, "retry", deadline.Token);
        Assert.Equal(new[] { "previous question", "previous answer", "retry" },
            client.Requests[2].Select(message => message.Text));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationOmitsToolCallsWithoutAnIdentityAndPreservesOtherContent(
        bool hasPartialText,
        bool hasIdentifiedCall)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<AIContent> contents = [new FunctionCallContent(string.Empty, "wait")];
        if (hasPartialText) contents.Insert(0, new TextContent("partial"));
        if (hasIdentifiedCall) contents.Add(new FunctionCallContent("identified", "wait"));
        var client = new ScriptedChatClient(
            (_, token) => WaitAfterUpdateAsync(new ChatResponseUpdate(ChatRole.Assistant, contents), waiting, token),
            (_, _) => StreamAsync("continued"));
        var session = new AgentSession(client);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("question"), deadline.Token);
        var events = ReadEventsAsync(run, deadline.Token);
        await waiting.Task.WaitAsync(deadline.Token);
        await run.CancelAsync(deadline.Token);
        Assert.DoesNotContain(await events, agentEvent => agentEvent is AgentToolStarted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Completion);

        var turn = Assert.Single(session.GetTranscriptSnapshot().Turns);
        Assert.True(turn.Truncated);
        Assert.Equal("question", turn.Messages[0].Text);
        Assert.Equal(hasIdentifiedCall ? 3 : hasPartialText ? 2 : 1, turn.Messages.Count);
        if (hasPartialText) Assert.Equal("partial", turn.Messages[1].Text);
        Assert.Equal(hasIdentifiedCall ? new[] { "identified" } : [], turn.Messages
            .SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.CallId));
        var results = turn.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
        Assert.Equal(hasIdentifiedCall ? new[] { "identified" } : [], results.Select(result => result.CallId));
        Assert.All(results, result => Assert.Equal("cancelled",
            Assert.IsType<JsonElement>(result.Result).GetProperty("status").GetString()));

        await CompleteTurnAsync(session, "continue", deadline.Token);
        Assert.Equal(turn.Messages.Select(message => message.Text).Append("continue"),
            client.Requests[1].Select(message => message.Text));
    }

    [Fact(Timeout = 30_000)]
    public async Task InputAndResponseLimitsRollBackWholeTurn()
    {
        var unusedClient = new ScriptedChatClient((_, _) => StreamAsync("unused"));
        var inputLimited = new AgentSession(unusedClient, new AgentSessionOptions { MaxInputCharacters = 3 });
        var inputFailure = await Assert.ThrowsAnyAsync<AgentInputLimitExceededException>(async () => { await inputLimited.StartTurnAsync(AgentTurn.FromText("four"), TestContext.Current.CancellationToken); });
        Assert.Equal(4, inputFailure.ActualCharacters);
        Assert.Empty(unusedClient.Requests);
        Assert.Empty(inputLimited.GetTranscriptSnapshot().Turns);

        var responseLimited = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync("123", "456")),
            new AgentSessionOptions { MaxResponseCharacters = 5 });
        await using var run = await responseLimited.StartTurnAsync(AgentTurn.FromText("limit"), TestContext.Current.CancellationToken);
        var events = await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        Assert.Equal(["123"], events.OfType<AgentTextDelta>().Select(delta => delta.Text));
        await Assert.ThrowsAnyAsync<AgentResponseLimitExceededException>(async () => { await run.Completion.WaitAsync(TestContext.Current.CancellationToken); });
        Assert.Empty(responseLimited.GetTranscriptSnapshot().Turns);
    }

    [Fact(Timeout = 30_000)]
    public async Task EmptyAndUnsupportedResponsesRollBackWholeTurn()
    {
        var emptySession = new AgentSession(new ScriptedChatClient((_, _) => StreamAsync()));
        await using var emptyRun = await emptySession.StartTurnAsync(AgentTurn.FromText("empty"), TestContext.Current.CancellationToken);
        Assert.Empty(await ReadEventsAsync(emptyRun, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<AgentUnsupportedResponseException>(async () => { await emptyRun.Completion.WaitAsync(TestContext.Current.CancellationToken); });
        Assert.Empty(emptySession.GetTranscriptSnapshot().Turns);

        var unsupportedUpdate = new ChatResponseUpdate(
            ChatRole.Assistant,
            [new FunctionCallContent("call", "tool", new Dictionary<string, object?>())]);
        var unsupportedSession = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync(unsupportedUpdate)));
        await using var unsupportedRun = await unsupportedSession.StartTurnAsync(
            AgentTurn.FromText("unsupported"),
            TestContext.Current.CancellationToken);
        await ReadEventsAsync(unsupportedRun, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<AgentToolArgumentsException>(async () => { await unsupportedRun.Completion.WaitAsync(TestContext.Current.CancellationToken); });
        Assert.Empty(unsupportedSession.GetTranscriptSnapshot().Turns);
    }

    [Fact(Timeout = 30_000)]
    public async Task HistoryEvictionAlwaysRemovesCompleteOldestTurns()
    {
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync("one"),
            (_, _) => StreamAsync("two"),
            (_, _) => StreamAsync("three"));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            MaxRetainedTurns = 1,
            MaxHistoryBytes = 1_000
        });

        await CompleteTurnAsync(session, "a", TestContext.Current.CancellationToken);
        await CompleteTurnAsync(session, "bb", TestContext.Current.CancellationToken);
        await CompleteTurnAsync(session, "c", TestContext.Current.CancellationToken);

        var transcript = session.GetTranscriptSnapshot();
        Assert.Equal(3, transcript.Version);
        var item = Assert.Single(transcript.Turns);
        Assert.Equal([ChatRole.User, ChatRole.Assistant], item.Messages.Select(message => message.Role));
        Assert.Equal(["c", "three"], transcript.Turns[0].Messages.Select(message => message.Text));
    }

    [Fact(Timeout = 30_000)]
    public async Task EventsAllowOnlyOneConsumer()
    {
        var session = new AgentSession(new ScriptedChatClient((_, _) => StreamAsync("answer")));
        await using var run = await session.StartTurnAsync(AgentTurn.FromText("question"), TestContext.Current.CancellationToken);

        await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        await run.Completion.WaitAsync(TestContext.Current.CancellationToken);
        var assertion2 = await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => { await ReadEventsAsync(run, TestContext.Current.CancellationToken); });
        Assert.Matches("(?is)^.*only\\ one\\ consumer.*$", assertion2.Message);
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellationReleasesAProducerBlockedByEventBackpressure()
    {
        var secondUpdateProduced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedChatClient(
            (_, token) => SignalBeforeSecondUpdateAsync(secondUpdateProduced, token),
            (_, _) => StreamAsync("next"));
        var session = new AgentSession(client, new AgentSessionOptions { EventBufferCapacity = 1 });

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("blocked"), TestContext.Current.CancellationToken);
        await secondUpdateProduced.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(run.Completion.IsCompleted);

        await run.CancelAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await run.Completion; });
        var turn = Assert.Single(session.GetTranscriptSnapshot().Turns);
        Assert.True(turn.Truncated);
        Assert.Equal(run.Id, turn.RunId);
        Assert.StartsWith("one", turn.Messages[1].Text);

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(next, TestContext.Current.CancellationToken);
        await next.Completion.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellationAndDisposalAreIdempotentAndReleaseSession()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedChatClient(
            (_, token) => WaitWithoutOutputAsync(waiting, token),
            (_, _) => StreamAsync("next"));
        var session = new AgentSession(client);
        var run = await session.StartTurnAsync(AgentTurn.FromText("cancel"), TestContext.Current.CancellationToken);
        await waiting.Task.WaitAsync(TestContext.Current.CancellationToken);

        await run.CancelAsync(TestContext.Current.CancellationToken);
        await run.CancelAsync(TestContext.Current.CancellationToken);
        await run.DisposeAsync();
        await run.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await run.Completion; });
        var turn = Assert.Single(session.GetTranscriptSnapshot().Turns);
        Assert.Equal("cancel", Assert.Single(turn.Messages).Text);
        Assert.True(turn.Truncated);
        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(next, TestContext.Current.CancellationToken);
        await next.Completion.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task DirectHistoryRequestsContainSystemPromptAndCommittedTurnsOnly()
    {
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync("First answer"),
            (_, _) => StreamAsync("Second answer"));
        var session = new AgentSession(client, new AgentSessionOptions { SystemPrompt = "Be concise." });

        await CompleteTurnAsync(session, "First question", TestContext.Current.CancellationToken);
        await CompleteTurnAsync(session, "Second question", TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Requests.Count());
        Assert.Equal(new[] { "Be concise.", "Be concise." }, client.Instructions);
        Assert.Equal(new (ChatRole Role, string Text)[] { (ChatRole.User, "First question") }, client.Requests[0].Select(MessageTuple));
        Assert.Equal(new (ChatRole Role, string Text)[] { (ChatRole.User, "First question"), (ChatRole.Assistant, "First answer"), (ChatRole.User, "Second question") }, client.Requests[1].Select(MessageTuple));
    }

    [Fact(Timeout = 30_000)]
    public async Task ProviderConversationIdConflictRollsBackAndAllowsNextRun()
    {
        var conflicting = new ChatResponseUpdate(ChatRole.Assistant, "conflict")
        {
            ConversationId = "provider-owned-conversation"
        };
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(conflicting),
            (_, _) => StreamAsync("recovered"));
        var session = new AgentSession(client);

        await using var failed = await session.StartTurnAsync(AgentTurn.FromText("first"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(failed, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<AgentUnsupportedResponseException>(async () => { await failed.Completion.WaitAsync(TestContext.Current.CancellationToken); });
        Assert.Empty(session.GetTranscriptSnapshot().Turns);

        await using var recovered = await session.StartTurnAsync(AgentTurn.FromText("retry"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(recovered, TestContext.Current.CancellationToken);
        Assert.Equal("recovered", (await recovered.Completion.WaitAsync(TestContext.Current.CancellationToken)).AssistantMessage.Text);
        Assert.Equal(new (ChatRole Role, string Text)[] { (ChatRole.User, "retry") }, client.Requests[1].Select(MessageTuple));
    }

    [Fact]
    public void ConstructorRejectsInvalidDuplicateAndNonObjectFunctionMetadata()
    {
        var client = new ScriptedChatClient((_, _) => StreamAsync("unused"));
        var valid = CreateTool(
            "echo",
            (_, _, _) => ValueTask.FromResult<JsonElement?>(null));

        var assertion3 = Assert.ThrowsAny<ArgumentException>(() => new AgentSession(
            client,
            tools: [new MetadataAIFunction(valid, "invalid name", valid.JsonSchema)]));
        Assert.Matches("(?is)^.*Tool\\ names.*$", assertion3.Message);

        var assertion4 = Assert.ThrowsAny<ArgumentException>(() => new AgentSession(client, tools: [valid, valid]));
        Assert.Matches("(?is)^.*already\\ registered.*$", assertion4.Message);

        var assertion5 = Assert.ThrowsAny<ArgumentException>(() => new AgentSession(
            client,
            tools: [new MetadataAIFunction(valid, valid.Name, ParseJson("[]"))]));
        Assert.Matches("(?is)^.*schema\\ must\\ describe\\ a\\ JSON\\ object.*$", assertion5.Message);

        var assertion6 = Assert.ThrowsAny<ArgumentException>(() => new AgentSession(
            client,
            tools: [new MetadataAIFunction(valid, valid.Name, ParseJson("{\"type\":\"string\"}"))]));
        Assert.Matches("(?is)^.*schema\\ must\\ describe\\ a\\ JSON\\ object.*$", assertion6.Message);
    }

    [Fact(Timeout = 30_000)]
    public async Task EarlyEventDisposalFollowedByRunDisposalPreservesHistory()
    {
        var client = new ScriptedChatClient(
            (_, token) => StreamUntilCanceledAsync(token),
            (_, _) => StreamAsync("next"));
        var session = new AgentSession(client, new AgentSessionOptions { EventBufferCapacity = 1 });
        var run = await session.StartTurnAsync(AgentTurn.FromText("early stop"), TestContext.Current.CancellationToken);

        await using (var events = run.Events.GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await events.MoveNextAsync());
            Assert.IsType<AgentTextDelta>(events.Current);
        }

        await run.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await run.Completion; });
        var turn = Assert.Single(session.GetTranscriptSnapshot().Turns);
        Assert.True(turn.Truncated);
        Assert.Equal(run.Id, turn.RunId);

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(next, TestContext.Current.CancellationToken);
        await next.Completion.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task ToolCallPublishesLifecycleCommitsCompleteTurnAndReplaysHistory()
    {
        var tool = CreateTool(
            "echo",
            async (context, arguments, cancellationToken) =>
            {
                var typed = arguments.Deserialize(AgentTestJsonContext.Default.EchoArguments);
                Assert.NotNull(typed);
                await context.ReportProgressAsync(
                    new TextContent("working"),
                    cancellationToken);
                return JsonSerializer.SerializeToElement(
                    new EchoResult(typed.Text, 1),
                    AgentTestJsonContext.Default.EchoResult);
            });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("provider-call", "echo", ("text", "hello"))),
            (_, _) => StreamAsync("The tool returned hello."),
            (_, _) => StreamAsync("History retained."));
        var session = new AgentSession(client, tools: [tool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Use echo"), TestContext.Current.CancellationToken);
        var events = await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        var result = await run.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(1, events.Count).Select(static value => (long)value), events.Select(agentEvent => agentEvent.Sequence));
        Assert.Equal([
            typeof(AgentMessageCompleted), typeof(AgentToolStarted), typeof(AgentToolProgress), typeof(AgentToolFinished), typeof(AgentTextDelta), typeof(AgentMessageCompleted),
        ], events.Select(agentEvent => agentEvent.GetType()));
        Assert.Equal(2, events.OfType<AgentMessageCompleted>().Count());
        Assert.Equal("hello", events.OfType<AgentToolStarted>().Single().Arguments
            .Deserialize(AgentTestJsonContext.Default.EchoArguments)?.Text);
        Assert.Equivalent(new TextContent("working"), events.OfType<AgentToolProgress>().Single().Content);

        var item = Assert.Single(result.Transcript.Turns);
        var messages = item.Messages;
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant], messages.Select(message => message.Role));
        var call = messages[1].Contents.OfType<FunctionCallContent>().Single();
        var toolResult = messages[2].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal(call.CallId, toolResult.CallId);
        Assert.Equal("provider-call", toolResult.CallId);
        Assert.NotEqual(call.CallId, events.OfType<AgentToolStarted>().Single().CallId.ToString());
        var resultEnvelope = Assert.IsType<JsonElement>(toolResult.Result);
        Assert.Equal("ok", resultEnvelope.GetProperty("status").GetString());
        Assert.Equal("hello", resultEnvelope.GetProperty("value").GetProperty("text").GetString());
        Assert.Equal(resultEnvelope.GetRawText(), events.OfType<AgentToolFinished>().Single().Result.GetRawText());
        Assert.Equivalent(messages[^1], result.AssistantMessage);
        Assert.Equal("The tool returned hello.", result.AssistantMessage.Text);

        Assert.Equal(2, client.Requests.Count());
        var providerResult = client.Requests[1]
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single();
        Assert.Equal("ok", Assert.IsType<JsonElement>(providerResult.Result).GetProperty("status").GetString());

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("Continue"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(next, TestContext.Current.CancellationToken);
        await next.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.User], client.Requests[2].Select(message => message.Role));
        Assert.Equal("provider-call", client.Requests[2].SelectMany(static message => message.Contents)
            .OfType<FunctionCallContent>().Single().CallId);
        Assert.Equal("provider-call", client.Requests[2].SelectMany(static message => message.Contents)
            .OfType<FunctionResultContent>().Single().CallId);
    }

    [Fact(Timeout = 30_000)]
    public async Task ExpectedToolFailureReturnsStableEnvelopeAndAllowsModelRecovery()
    {
        var tool = CreateTool(
            "lookup",
            (_, _, _) => ValueTask.FromException<JsonElement?>(
                new AgentToolException("not_found", "No matching value was found.")));
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "lookup")),
            (messages, _) =>
            {
                var envelope = Assert.IsType<JsonElement>(messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>()
                    .Single().Result);
                Assert.Equal("error", envelope.GetProperty("status").GetString());
                Assert.Equal("not_found", envelope.GetProperty("code").GetString());
                return StreamAsync("I could not find it.");
            });
        var session = new AgentSession(client, tools: [tool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Find it"), TestContext.Current.CancellationToken);
        var events = await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        var result = await run.Completion.WaitAsync(TestContext.Current.CancellationToken);

        var finished = events.OfType<AgentToolFinished>().Single().Result;
        Assert.Equal("error", finished.GetProperty("status").GetString());
        Assert.Equal("not_found", finished.GetProperty("code").GetString());
        Assert.Equal("I could not find it.", result.AssistantMessage.Text);
        Assert.Equal("error", Assert.IsType<JsonElement>(result.Transcript.Turns[0].Messages[2].Contents
            .OfType<FunctionResultContent>().Single().Result).GetProperty("status").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task MultipleToolCallsExecuteSeriallyInProviderOrder()
    {
        var active = 0;
        var maximumActive = 0;
        var order = new List<string>();
        var tool = CreateTool(
            "record",
            async (_, arguments, _) =>
            {
                var value = arguments.Deserialize(AgentTestJsonContext.Default.EchoArguments)?.Text;
                Assert.NotNull(value);
                order.Add(value);
                var current = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, current);
                await Task.Yield();
                Interlocked.Decrement(ref active);
                return JsonSerializer.SerializeToElement(value, AgentTestJsonContext.Default.String);
            });
        var calls = new ChatResponseUpdate(
            ChatRole.Assistant,
            [
                new FunctionCallContent("first", "record", new Dictionary<string, object?> { ["text"] = "one" }),
                new FunctionCallContent("second", "record", new Dictionary<string, object?> { ["text"] = "two" })
            ]);
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(calls),
            (_, _) => StreamAsync("done"));
        var session = new AgentSession(client, tools: [tool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Record both"), TestContext.Current.CancellationToken);
        var events = await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        await run.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "one", "two" }, order);
        Assert.Equal(1, maximumActive);
        Assert.Equal(["one", "two"], events.OfType<AgentToolStarted>().Select(started =>
            started.Arguments.Deserialize(AgentTestJsonContext.Default.EchoArguments)?.Text));
    }

    [Fact(Timeout = 30_000)]
    public async Task UnexpectedToolExceptionPublishesFailureAndRollsBackWholeTurn()
    {
        var tool = CreateTool(
            "explode",
            (_, _, _) => ValueTask.FromException<JsonElement?>(new InvalidOperationException("secret detail")));
        var session = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync(ToolCallUpdate("call", "explode"))),
            tools: [tool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Explode"), TestContext.Current.CancellationToken);
        var events = await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAnyAsync<AgentToolInvocationException>(async () => { await run.Completion.WaitAsync(TestContext.Current.CancellationToken); });

        Assert.Equal("explode", failure.ToolName);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        var finished = events.OfType<AgentToolFinished>().Single().Result;
        Assert.Equal("error", finished.GetProperty("status").GetString());
        Assert.Equal("The tool failed unexpectedly.", finished.GetProperty("message").GetString());
        Assert.Empty(session.GetTranscriptSnapshot().Turns);
    }

    [Fact(Timeout = 30_000)]
    public async Task NonJsonFunctionResultFailsDeterministicallyAndRollsBackWholeTurn()
    {
        var tool = AIFunctionFactory.Create(
            (string value) => value,
            new AIFunctionFactoryOptions
            {
                Name = "raw_result",
                SerializerOptions = AgentTestJsonContext.Default.Options,
                ExcludeResultSchema = true,
                MarshalResult = static (result, _, _) => ValueTask.FromResult(result)
            });
        var session = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync(ToolCallUpdate("call", "raw_result", ("value", "raw")))),
            tools: [tool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Return raw"), TestContext.Current.CancellationToken);
        var events = await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAnyAsync<AgentToolInvocationException>(async () => { await run.Completion.WaitAsync(TestContext.Current.CancellationToken); });

        Assert.Contains("JsonElement or null", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
        Assert.Single(events.OfType<AgentToolStarted>());
        Assert.Equal("error", events.OfType<AgentToolFinished>().Single().Result.GetProperty("status").GetString());
        Assert.Empty(session.GetTranscriptSnapshot().Turns);
    }

    [Fact(Timeout = 30_000)]
    public async Task ToolArgumentLimitTerminatesRunBeforeInvocation()
    {
        var invoked = false;
        var tool = CreateTool(
            "echo",
            (_, _, _) =>
            {
                invoked = true;
                return ValueTask.FromResult<JsonElement?>(null);
            });
        var session = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync(ToolCallUpdate("call", "echo", ("text", "too large")))),
            new AgentSessionOptions { MaxToolArgumentsBytes = 4 },
            [tool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Limit"), TestContext.Current.CancellationToken);
        Assert.Empty(await ReadEventsAsync(run, TestContext.Current.CancellationToken));
        var failure = await Assert.ThrowsAnyAsync<AgentToolLimitExceededException>(async () => { await run.Completion.WaitAsync(TestContext.Current.CancellationToken); });

        Assert.Equal(nameof(AgentSessionOptions.MaxToolArgumentsBytes), failure.LimitName);
        Assert.False(invoked);
        Assert.Empty(session.GetTranscriptSnapshot().Turns);
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellationStopsActiveToolAndPreservesHistory()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = CreateTool(
            "wait",
            async (_, _, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("The canceled tool unexpectedly resumed.");
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested) canceled.TrySetResult();
                }
            });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("completed", "echo"),
                 new FunctionCallContent("call", "wait"),
                 new FunctionCallContent("pending", "wait")])),
            (_, _) => StreamAsync("continued"));
        var echo = CreateTool("echo", (_, _, _) => ValueTask.FromResult<JsonElement?>(ParseJson("42")));
        var session = new AgentSession(client, tools: [echo, tool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Wait"), TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await run.CancelAsync(TestContext.Current.CancellationToken);
        var events = await ReadEventsAsync(run, TestContext.Current.CancellationToken);

        await canceled.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, events.OfType<AgentToolStarted>().Count());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await run.Completion; });
        var turn = Assert.Single(session.GetTranscriptSnapshot().Turns);
        Assert.True(turn.Truncated);
        var results = turn.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
        Assert.Equal(new[] { "completed", "call", "pending" }, results.Select(result => result.CallId));
        Assert.Equal("ok", Assert.IsType<JsonElement>(results[0].Result).GetProperty("status").GetString());
        Assert.All(results[1..], result =>
            Assert.Equal("cancelled", Assert.IsType<JsonElement>(result.Result).GetProperty("status").GetString()));

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("Continue"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(next, TestContext.Current.CancellationToken);
        await next.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(turn.Messages.Count + 1, client.Requests[1].Count);
        Assert.Equal(new[] { "completed", "call", "pending" }, client.Requests[1]
            .SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.CallId));
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellationDuringLaterProviderIterationRetainsCompletedToolRound()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("completed", "echo")),
            (_, token) => WaitAfterTextAsync("partial answer", waiting, token),
            (_, _) => StreamAsync("continued"));
        var tool = CreateTool("echo", (_, _, _) => ValueTask.FromResult<JsonElement?>(ParseJson("42")));
        var session = new AgentSession(client, tools: [tool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("question"), deadline.Token);
        var events = ReadEventsAsync(run, deadline.Token);
        await waiting.Task.WaitAsync(deadline.Token);
        await run.CancelAsync(deadline.Token);
        await events;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Completion);

        var turn = Assert.Single(session.GetTranscriptSnapshot().Turns);
        Assert.True(turn.Truncated);
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant },
            turn.Messages.Select(message => message.Role));
        Assert.Equal("partial answer", turn.Messages[^1].Text);
        Assert.Equal("ok", Assert.IsType<JsonElement>(Assert.Single(turn.Messages[2].Contents
            .OfType<FunctionResultContent>()).Result).GetProperty("status").GetString());

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("continue"), deadline.Token);
        await ReadEventsAsync(next, deadline.Token);
        await next.Completion.WaitAsync(deadline.Token);
        Assert.Equal(5, client.Requests[2].Count);
        Assert.Equal("partial answer", client.Requests[2][3].Text);
    }

    [Fact(Timeout = 30_000)]
    public async Task ToolResultAndProgressLimitsRollBackWholeTurn()
    {
        var resultLimited = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync(ToolCallUpdate("call", "large"))),
            new AgentSessionOptions { MaxToolResultBytes = 16 },
            [
                CreateTool(
                    "large",
                    (_, _, _) => ValueTask.FromResult<JsonElement?>(
                        JsonSerializer.SerializeToElement(
                            new string('x', 100),
                            AgentTestJsonContext.Default.String)))
            ]);
        await using (var run = await resultLimited.StartTurnAsync(AgentTurn.FromText("Large"), TestContext.Current.CancellationToken))
        {
            await ReadEventsAsync(run, TestContext.Current.CancellationToken);
            var failure = await Assert.ThrowsAnyAsync<AgentToolLimitExceededException>(async () => { await run.Completion.WaitAsync(TestContext.Current.CancellationToken); });
            Assert.Equal(nameof(AgentSessionOptions.MaxToolResultBytes), failure.LimitName);
        }

        Assert.Empty(resultLimited.GetTranscriptSnapshot().Turns);

        var progressLimited = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync(ToolCallUpdate("call", "progress"))),
            new AgentSessionOptions { MaxToolProgressEventsPerCall = 1 },
            [
                CreateTool(
                    "progress",
                    async (context, _, cancellationToken) =>
                    {
                        await context.ReportProgressAsync(new TextContent("one"), cancellationToken);
                        await context.ReportProgressAsync(new TextContent("two"), cancellationToken);
                        return null;
                    })
            ]);
        await using (var run = await progressLimited.StartTurnAsync(AgentTurn.FromText("Progress"), TestContext.Current.CancellationToken))
        {
            var events = await ReadEventsAsync(run, TestContext.Current.CancellationToken);
            Assert.Equal(["one"], events.OfType<AgentToolProgress>().Select(progress => ((TextContent)progress.Content).Text));
            var failure = await Assert.ThrowsAnyAsync<AgentToolLimitExceededException>(async () => { await run.Completion.WaitAsync(TestContext.Current.CancellationToken); });
            Assert.Equal(nameof(AgentSessionOptions.MaxToolProgressEventsPerCall), failure.LimitName);
        }

        Assert.Empty(progressLimited.GetTranscriptSnapshot().Turns);
    }

    [Fact(Timeout = 30_000)]
    public async Task ToolCallAndModelIterationLimitsRollBackWholeTurn()
    {
        var invoked = 0;
        var twoCalls = new ChatResponseUpdate(
            ChatRole.Assistant,
            [
                new FunctionCallContent("one", "count", new Dictionary<string, object?>()),
                new FunctionCallContent("two", "count", new Dictionary<string, object?>())
            ]);
        var callLimited = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync(twoCalls)),
            new AgentSessionOptions { MaxToolCallsPerTurn = 1 },
            [
                CreateTool(
                    "count",
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref invoked);
                        return ValueTask.FromResult<JsonElement?>(null);
                    })
            ]);
        await using (var run = await callLimited.StartTurnAsync(AgentTurn.FromText("Count"), TestContext.Current.CancellationToken))
        {
            Assert.Empty(await ReadEventsAsync(run, TestContext.Current.CancellationToken));
            var failure = await Assert.ThrowsAnyAsync<AgentToolLimitExceededException>(async () => { await run.Completion.WaitAsync(TestContext.Current.CancellationToken); });
            Assert.Equal(nameof(AgentSessionOptions.MaxToolCallsPerTurn), failure.LimitName);
        }

        Assert.Equal(0, invoked);
        Assert.Empty(callLimited.GetTranscriptSnapshot().Turns);

        var iterationClient = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("one", "again")),
            (_, _) => StreamAsync(ToolCallUpdate("two", "again")),
            (_, _) => StreamAsync("continued"));
        var iterationLimited = new AgentSession(
            iterationClient,
            new AgentSessionOptions { MaxModelIterationsPerTurn = 2 },
            [
                CreateTool(
                    "again",
                    (_, _, _) => ValueTask.FromResult<JsonElement?>(null))
            ]);
        await using (var run = await iterationLimited.StartTurnAsync(AgentTurn.FromText("Again"), TestContext.Current.CancellationToken))
        {
            var events = await ReadEventsAsync(run, TestContext.Current.CancellationToken);
            var result = await run.Completion.WaitAsync(TestContext.Current.CancellationToken);

            Assert.True(result.Truncated);
            Assert.Single(events.OfType<AgentTurnTruncated>());
            var turn = Assert.Single(result.Transcript.Turns);
            Assert.True(turn.Truncated);
            Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant], turn.Messages.Select(static message => message.Role));
            Assert.Single(turn.Messages[^1].Contents.OfType<FunctionCallContent>());
        }

        // The next turn replays the committed partial history without the unanswered tool call.
        await using (var continued =
                     await iterationLimited.StartTurnAsync(AgentTurn.FromText("Continue"), TestContext.Current.CancellationToken))
        {
            await ReadEventsAsync(continued, TestContext.Current.CancellationToken);
            Assert.Equal("continued", (await continued.Completion.WaitAsync(TestContext.Current.CancellationToken)).AssistantMessage.Text);
        }

        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User], iterationClient.Requests[2].Select(static message => message.Role));
        Assert.Equal("Continue", iterationClient.Requests[2][^1].Text);
    }

    [Fact(Timeout = 30_000)]
    public async Task ToolUseWithinBudgetCommitsCompleteUntruncatedTurn()
    {
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("one", "echo")),
            (_, _) => StreamAsync("done"));
        var session = new AgentSession(
            client,
            new AgentSessionOptions { MaxModelIterationsPerTurn = 2 },
            [
                CreateTool(
                    "echo",
                    (_, _, _) => ValueTask.FromResult<JsonElement?>(null))
            ]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Go"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        var result = await run.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Truncated);
        Assert.Equal("done", result.AssistantMessage.Text);
        Assert.False(session.GetTranscriptSnapshot().Turns.Single().Truncated);
    }

    [Fact(Timeout = 30_000)]
    public async Task TurnDurationExpiryFailsTurnRollsBackAndReleasesSession()
    {
        var client = new ScriptedChatClient(
            (_, token) => WaitUntilCanceledAsync(token),
            (_, _) => StreamAsync("next"));
        var session = new AgentSession(
            client,
            new AgentSessionOptions { MaxTurnDuration = TimeSpan.FromMilliseconds(100) });

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("slow"), TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAnyAsync<AgentTurnDurationExceededException>(async () => { await run.Completion.WaitAsync(TestContext.Current.CancellationToken); });
        Assert.Equal(TimeSpan.FromMilliseconds(100), failure.MaximumDuration);
        Assert.Empty(session.GetTranscriptSnapshot().Turns);

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(next, TestContext.Current.CancellationToken);
        Assert.Equal("next", (await next.Completion.WaitAsync(TestContext.Current.CancellationToken)).AssistantMessage.Text);
    }

    [Fact]
    public void OptionsRejectNegativeTurnDuration()
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new AgentSessionOptions { MaxTurnDuration = TimeSpan.FromSeconds(-1) }.Validate());
    }

    [Fact(Timeout = 30_000)]
    public async Task ToolBearingHistoryEvictionRemovesWholeOldestTurn()
    {
        var tool = CreateTool(
            "echo",
            (_, _, _) => ValueTask.FromResult<JsonElement?>(
                JsonSerializer.SerializeToElement("value", AgentTestJsonContext.Default.String)));
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "echo")),
            (_, _) => StreamAsync("first"),
            (_, _) => StreamAsync("second"));
        var session = new AgentSession(
            client,
            new AgentSessionOptions { MaxRetainedTurns = 1 },
            [tool]);

        await CompleteTurnAsync(session, "tool turn", TestContext.Current.CancellationToken);
        await CompleteTurnAsync(session, "plain turn", TestContext.Current.CancellationToken);

        var transcript = session.GetTranscriptSnapshot();
        Assert.Equal(2, transcript.Version);
        var item = Assert.Single(transcript.Turns);
        Assert.Equal(["plain turn", "second"], item.Messages.Select(message => message.Text));
        Assert.DoesNotContain(transcript.Turns[0].Messages
            .SelectMany(message => message.Contents), content => content is FunctionCallContent or FunctionResultContent);
    }

    [Fact(Timeout = 30_000)]
    public async Task TextBeforeToolCallAndFinalTextUseTheirCompletedMessageIds()
    {
        var preamble = new ChatResponseUpdate(ChatRole.Assistant, "checking") { MessageId = "before-tool" };
        var call = ToolCallUpdate("call", "echo");
        call.MessageId = "before-tool";
        var final = new ChatResponseUpdate(ChatRole.Assistant, "done") { MessageId = "final" };
        var session = new AgentSession(
            new ScriptedChatClient(
                (_, _) => StreamUpdatesAsync(preamble, call),
                (_, _) => StreamAsync(final)),
            tools:
            [
                CreateTool(
                    "echo",
                    (_, _, _) => ValueTask.FromResult<JsonElement?>(null))
            ]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Check"), TestContext.Current.CancellationToken);
        var events = await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        await run.Completion.WaitAsync(TestContext.Current.CancellationToken);

        var deltas = events.OfType<AgentTextDelta>().ToArray();
        var completed = events.OfType<AgentMessageCompleted>().ToArray();
        Assert.Equal(["checking", "done"], deltas.Select(delta => delta.Text));
        Assert.Equal(completed[0].AgentMessageId, deltas[0].MessageId);
        Assert.Equal(completed[^1].AgentMessageId, deltas[1].MessageId);
        Assert.NotEqual(completed[^1].AgentMessageId, completed[0].AgentMessageId);
    }

    [Fact(Timeout = 30_000)]
    public async Task ConcurrentToolProgressKeepsWireOrderEqualToSequenceOrder()
    {
        const int reporterCount = 8;
        var tool = CreateTool(
            "burst",
            async (context, _, cancellationToken) =>
            {
                var reporters = Enumerable.Range(0, reporterCount).Select(index => Task.Run(
                    async () => await context.ReportProgressAsync(new TextContent($"p{index}"), cancellationToken),
                    cancellationToken));
                await Task.WhenAll(reporters);
                return JsonSerializer.SerializeToElement("done", AgentTestJsonContext.Default.String);
            });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "burst")),
            (_, _) => StreamAsync("completed"));
        var session = new AgentSession(client, tools: [tool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Burst"), TestContext.Current.CancellationToken);
        var events = await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        await run.Completion.WaitAsync(TestContext.Current.CancellationToken);

        var progresses = events.OfType<AgentToolProgress>().ToArray();
        Assert.Equal(reporterCount, progresses.Count());
        Assert.Equal(progresses.Select(progress => progress.Content).Count(), progresses.Select(progress => progress.Content).Distinct().Count());
        // Sequence numbers are allocated and enqueued atomically, so the wire order is exactly
        // the strictly increasing sequence order across all producers.
        Assert.Equal(Enumerable.Range(1, events.Count).Select(static value => (long)value), events.Select(agentEvent => agentEvent.Sequence));
    }

    [Fact(Timeout = 30_000)]
    public async Task ProgressReportedAfterRunCompletionFailsWithTypeSignal()
    {
        var contexts = new List<AgentToolContext>();
        var tool = CreateTool(
            "late",
            (context, _, _) =>
            {
                contexts.Add(context);
                return ValueTask.FromResult<JsonElement?>(null);
            });
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "late")),
            (_, _) => StreamAsync("done"),
            (_, _) => StreamAsync("next"));
        var session = new AgentSession(client, tools: [tool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Late"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        await run.Completion.WaitAsync(TestContext.Current.CancellationToken);
        var item = Assert.Single(contexts);
        var context = item;

        // The run completed and closed its event writer; a straggler reporter observes the
        // typed recoverable failure instead of ChannelClosedException.
        await Assert.ThrowsAnyAsync<AgentRunCompletedException>(async () =>
        {
            await context
                .ReportProgressAsync(new TextContent("late"), TestContext.Current.CancellationToken).AsTask();
        });

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(next, TestContext.Current.CancellationToken);
        Assert.Equal("next", (await next.Completion.WaitAsync(TestContext.Current.CancellationToken)).AssistantMessage.Text);
    }

    [Fact]
    public void PublicResultModelsRejectDefaultIdentifiersAndInvalidSequences()
    {
        var runId = AgentRunId.Create();
        var messageId = AgentMessageId.Create();
        var message = new ChatMessage(ChatRole.Assistant, "answer");

        Assert.ThrowsAny<ArgumentException>(() => new AgentTranscript(default, 0, []));
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new AgentTextDelta(runId, 0, messageId, "answer"));
        Assert.ThrowsAny<ArgumentException>(() => new AgentMessageCompleted(default, 1, messageId, message));
    }

    private static async Task CompleteTurnAsync(AgentSession session,
        string input,
        CancellationToken cancellationToken)
    {
        await using var run = await session.StartTurnAsync(AgentTurn.FromText(input), cancellationToken);
        await ReadEventsAsync(run, cancellationToken);
        await run.Completion.WaitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<AgentEvent>> ReadEventsAsync(
        IAgentRun run,
        CancellationToken cancellationToken)
    {
        var events = new List<AgentEvent>();
        await foreach (var agentEvent in run.Events.WithCancellation(cancellationToken)) events.Add(agentEvent);

        return events;
    }

    private static (ChatRole Role, string Text) MessageTuple(ChatMessage message)
    {
        return (message.Role, message.Text);
    }

    private static AIFunction CreateTool(
        string name,
        Func<AgentToolContext, JsonElement, CancellationToken, ValueTask<JsonElement?>> invoke)
    {
        async ValueTask<JsonElement?> InvokeAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var detachedArguments = JsonSerializer.SerializeToElement(
                arguments.ToDictionary(static pair => pair.Key, static pair => pair.Value),
                AgentTestJsonContext.Default.DictionaryStringObject);
            return await invoke(
                    AgentToolContext.GetRequired(arguments),
                    detachedArguments,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return AIFunctionFactory.Create(
            (Func<AIFunctionArguments, CancellationToken, ValueTask<JsonElement?>>)InvokeAsync,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = $"Test tool {name}.",
                SerializerOptions = AgentTestJsonContext.Default.Options,
                ExcludeResultSchema = true
            });
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

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(params string[] text)
    {
        foreach (var value in text)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, value);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(ChatResponseUpdate update)
    {
        await Task.Yield();
        yield return update;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamUpdatesAsync(
        params ChatResponseUpdate[] updates)
    {
        await Task.Yield();
        foreach (var update in updates) yield return update;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FailAfterTextAsync(
        string text,
        Exception exception)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        await Task.Yield();
        throw exception;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WaitForGateAsync(
        TaskCompletionSource providerStarted,
        Task gate,
        string response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        providerStarted.TrySetResult();
        await gate.WaitAsync(cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response);
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

    private static async IAsyncEnumerable<ChatResponseUpdate> WaitAfterUpdateAsync(
        ChatResponseUpdate update,
        TaskCompletionSource waiting,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return update;
        waiting.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> SignalBeforeSecondUpdateAsync(
        TaskCompletionSource secondUpdateProduced,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "one");
        cancellationToken.ThrowIfCancellationRequested();
        secondUpdateProduced.TrySetResult();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "two");
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamUntilCanceledAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var index = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"{index++}");
            await Task.Yield();
        }
        // ReSharper disable once IteratorNeverReturns
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WaitUntilCanceledAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    // ReSharper disable once InconsistentNaming
    private sealed class MetadataAIFunction(
        AIFunction innerFunction,
        string name,
        JsonElement jsonSchema) : DelegatingAIFunction(innerFunction)
    {
        public override string Name { get; } = name;

        public override JsonElement JsonSchema { get; } = jsonSchema.Clone();
    }
}

internal sealed record EchoArguments([property: JsonPropertyName("text")] string Text);

internal sealed record EchoResult(string Text, int Count);

[JsonSerializable(typeof(EchoArguments))]
[JsonSerializable(typeof(EchoResult))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class AgentTestJsonContext : JsonSerializerContext;
