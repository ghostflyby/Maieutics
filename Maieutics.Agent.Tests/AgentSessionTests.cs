using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent.Tests;

public sealed class AgentSessionTests
{
    [Fact]
    public async Task StartTurnStartsProviderImmediatelyAndReservesSessionBeforeReturning()
    {
        using var deadline = CreateDeadline();
        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedChatClient(
            (_, token) => WaitForGateAsync(providerStarted, releaseProvider.Task, "first", token),
            (_, _) => StreamAsync("second"));
        var session = new AgentSession(client);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("first"), deadline.Token);

        await providerStarted.Task.WaitAsync(deadline.Token);
        run.Id.Value.Should().NotBe(Guid.Empty);
        run.SessionId.Should().Be(session.Id);
        await (Session: session, deadline.Token)
            .Awaiting(static state => state.Session.StartTurnAsync(AgentTurn.FromText("concurrent"), state.Token))
            .Should().ThrowAsync<AgentTurnInProgressException>();

        releaseProvider.SetResult();
        await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), deadline.Token);
        await ReadEventsAsync(next, deadline.Token);
        (await next.Completion.WaitAsync(deadline.Token)).AssistantMessage.Text.Should().Be("second");
    }

    [Fact]
    public async Task SuccessfulRunStreamsStableIdsSequencesAndCommittedTranscript()
    {
        using var deadline = CreateDeadline();
        var session = new AgentSession(new ScriptedChatClient((_, _) => StreamAsync("Hello", " world")));

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Question"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        var result = await run.Completion.WaitAsync(deadline.Token);

        events.Should().HaveCount(3);
        events.Select(agentEvent => agentEvent.Sequence).Should().Equal(1, 2, 3);
        var runId = run.Id;
        events.Should().OnlyContain(agentEvent => agentEvent.RunId == runId);
        var deltas = events.OfType<AgentTextDelta>().ToArray();
        deltas.Select(delta => delta.Text).Should().Equal("Hello", " world");
        var completed = events.OfType<AgentMessageCompleted>().Single();
        deltas.Select(delta => delta.MessageId).Should().OnlyContain(id => id == completed.AgentMessageId);
        completed.Message.Should().BeEquivalentTo(result.AssistantMessage);

        result.RunId.Should().Be(run.Id);
        result.UserMessage.Role.Should().Be(ChatRole.User);
        result.UserMessage.Text.Should().Be("Question");
        result.AssistantMessage.Role.Should().Be(ChatRole.Assistant);
        result.AssistantMessage.Text.Should().Be("Hello world");
        result.Transcript.SessionId.Should().Be(session.Id);
        result.Transcript.Version.Should().Be(1);
        result.Transcript.Turns.Should().ContainSingle();
        result.Transcript.Turns[0].RunId.Should().Be(run.Id);
        result.Transcript.Turns[0].Messages.Select(static message => (message.Role, message.Text)).Should().Equal(
            (result.UserMessage.Role, result.UserMessage.Text),
            (result.AssistantMessage.Role, result.AssistantMessage.Text));
        session.GetTranscriptSnapshot().Should().BeEquivalentTo(result.Transcript);
    }

    [Fact]
    public async Task ProviderFailureRollsBackPartialTurnAndReleasesSession()
    {
        using var deadline = CreateDeadline();
        var client = new ScriptedChatClient(
            (_, _) => FailAfterTextAsync("partial", new InvalidOperationException("provider failed")),
            (_, _) => StreamAsync("recovered"));
        var session = new AgentSession(client);

        await using var failedRun = await session.StartTurnAsync(AgentTurn.FromText("failure"), deadline.Token);
        var events = await ReadEventsAsync(failedRun, deadline.Token);
        events.OfType<AgentTextDelta>().Select(delta => delta.Text).Should().Equal("partial");
        var failure = (await failedRun.Completion.WaitAsync(deadline.Token)
            .Invoking(static task => task)
            .Should().ThrowAsync<AgentProviderException>()).Which;
        failure.InnerException.Should().BeOfType<InvalidOperationException>();
        session.GetTranscriptSnapshot().Turns.Should().BeEmpty();

        await using var recoveredRun = await session.StartTurnAsync(AgentTurn.FromText("retry"), deadline.Token);
        await ReadEventsAsync(recoveredRun, deadline.Token);
        (await recoveredRun.Completion.WaitAsync(deadline.Token)).AssistantMessage.Text.Should().Be("recovered");
    }

    [Fact]
    public async Task CancellationPreservesPartialEventsAndRollsBackTurn()
    {
        using var deadline = CreateDeadline();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedChatClient(
            (_, token) => WaitAfterTextAsync("partial", waiting, token),
            (_, _) => StreamAsync("next"));
        var session = new AgentSession(client);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("cancel"), deadline.Token);
        await waiting.Task.WaitAsync(deadline.Token);
        await run.CancelAsync(deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);

        events.OfType<AgentTextDelta>().Select(delta => delta.Text).Should().Equal("partial");
        await run.Completion
            .Invoking(static task => task)
            .Should().ThrowAsync<OperationCanceledException>();
        session.GetTranscriptSnapshot().Turns.Should().BeEmpty();

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), deadline.Token);
        await ReadEventsAsync(next, deadline.Token);
        await next.Completion.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task InputAndResponseLimitsRollBackWholeTurn()
    {
        using var deadline = CreateDeadline();
        var unusedClient = new ScriptedChatClient((_, _) => StreamAsync("unused"));
        var inputLimited = new AgentSession(unusedClient, new AgentSessionOptions { MaxInputCharacters = 3 });
        var inputFailure = (await (Session: inputLimited, deadline.Token)
            .Awaiting(static state => state.Session.StartTurnAsync(AgentTurn.FromText("four"), state.Token))
            .Should().ThrowAsync<AgentInputLimitExceededException>()).Which;
        inputFailure.ActualCharacters.Should().Be(4);
        unusedClient.Requests.Should().BeEmpty();
        inputLimited.GetTranscriptSnapshot().Turns.Should().BeEmpty();

        var responseLimited = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync("123", "456")),
            new AgentSessionOptions { MaxResponseCharacters = 5 });
        await using var run = await responseLimited.StartTurnAsync(AgentTurn.FromText("limit"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        events.OfType<AgentTextDelta>().Select(delta => delta.Text).Should().Equal("123");
        await run.Completion.WaitAsync(deadline.Token)
            .Invoking(static task => task)
            .Should().ThrowAsync<AgentResponseLimitExceededException>();
        responseLimited.GetTranscriptSnapshot().Turns.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyAndUnsupportedResponsesRollBackWholeTurn()
    {
        using var deadline = CreateDeadline();
        var emptySession = new AgentSession(new ScriptedChatClient((_, _) => StreamAsync()));
        await using var emptyRun = await emptySession.StartTurnAsync(AgentTurn.FromText("empty"), deadline.Token);
        (await ReadEventsAsync(emptyRun, deadline.Token)).Should().BeEmpty();
        await emptyRun.Completion.WaitAsync(deadline.Token)
            .Invoking(static task => task)
            .Should().ThrowAsync<AgentUnsupportedResponseException>();
        emptySession.GetTranscriptSnapshot().Turns.Should().BeEmpty();

        var unsupportedUpdate = new ChatResponseUpdate(
            ChatRole.Assistant,
            [new FunctionCallContent("call", "tool", new Dictionary<string, object?>())]);
        var unsupportedSession = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync(unsupportedUpdate)));
        await using var unsupportedRun = await unsupportedSession.StartTurnAsync(
            AgentTurn.FromText("unsupported"),
            deadline.Token);
        await ReadEventsAsync(unsupportedRun, deadline.Token);
        await unsupportedRun.Completion.WaitAsync(deadline.Token)
            .Invoking(static task => task)
            .Should().ThrowAsync<AgentToolArgumentsException>();
        unsupportedSession.GetTranscriptSnapshot().Turns.Should().BeEmpty();
    }

    [Fact]
    public async Task HistoryEvictionAlwaysRemovesCompleteOldestTurns()
    {
        using var deadline = CreateDeadline();
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync("one"),
            (_, _) => StreamAsync("two"),
            (_, _) => StreamAsync("three"));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            MaxRetainedTurns = 1,
            MaxHistoryBytes = 1_000
        });

        await CompleteTurnAsync(session, "a", deadline.Token);
        await CompleteTurnAsync(session, "bb", deadline.Token);
        await CompleteTurnAsync(session, "c", deadline.Token);

        var transcript = session.GetTranscriptSnapshot();
        transcript.Version.Should().Be(3);
        transcript.Turns.Should().ContainSingle();
        transcript.Turns[0].Messages.Select(message => message.Role).Should()
            .Equal(ChatRole.User, ChatRole.Assistant);
        transcript.Turns[0].Messages.Select(message => message.Text).Should().Equal("c", "three");
    }

    [Fact]
    public async Task EventsAllowOnlyOneConsumer()
    {
        using var deadline = CreateDeadline();
        var session = new AgentSession(new ScriptedChatClient((_, _) => StreamAsync("answer")));
        await using var run = await session.StartTurnAsync(AgentTurn.FromText("question"), deadline.Token);

        await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);
        await (Run: run, deadline.Token)
            .Awaiting(static state => ReadEventsAsync(state.Run, state.Token))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*only one consumer*");
    }

    [Fact]
    public async Task CancellationReleasesAProducerBlockedByEventBackpressure()
    {
        using var deadline = CreateDeadline();
        var secondUpdateProduced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedChatClient(
            (_, token) => SignalBeforeSecondUpdateAsync(secondUpdateProduced, token),
            (_, _) => StreamAsync("next"));
        var session = new AgentSession(client, new AgentSessionOptions { EventBufferCapacity = 1 });

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("blocked"), deadline.Token);
        await secondUpdateProduced.Task.WaitAsync(deadline.Token);
        run.Completion.IsCompleted.Should().BeFalse();

        await run.CancelAsync(deadline.Token);
        await run.Completion
            .Invoking(static task => task)
            .Should().ThrowAsync<OperationCanceledException>();
        session.GetTranscriptSnapshot().Turns.Should().BeEmpty();

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), deadline.Token);
        await ReadEventsAsync(next, deadline.Token);
        await next.Completion.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task CancellationAndDisposalAreIdempotentAndReleaseSession()
    {
        using var deadline = CreateDeadline();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedChatClient(
            (_, token) => WaitWithoutOutputAsync(waiting, token),
            (_, _) => StreamAsync("next"));
        var session = new AgentSession(client);
        var run = await session.StartTurnAsync(AgentTurn.FromText("cancel"), deadline.Token);
        await waiting.Task.WaitAsync(deadline.Token);

        await run.CancelAsync(deadline.Token);
        await run.CancelAsync(deadline.Token);
        await run.DisposeAsync();
        await run.DisposeAsync();

        await run.Completion
            .Invoking(static task => task)
            .Should().ThrowAsync<OperationCanceledException>();
        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), deadline.Token);
        await ReadEventsAsync(next, deadline.Token);
        await next.Completion.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task DirectHistoryRequestsContainSystemPromptAndCommittedTurnsOnly()
    {
        using var deadline = CreateDeadline();
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync("First answer"),
            (_, _) => StreamAsync("Second answer"));
        var session = new AgentSession(client, new AgentSessionOptions { SystemPrompt = "Be concise." });

        await CompleteTurnAsync(session, "First question", deadline.Token);
        await CompleteTurnAsync(session, "Second question", deadline.Token);

        client.Requests.Should().HaveCount(2);
        client.Instructions.Should().Equal("Be concise.", "Be concise.");
        client.Requests[0].Select(MessageTuple).Should().Equal((ChatRole.User, "First question"));
        client.Requests[1].Select(MessageTuple).Should().Equal(
            (ChatRole.User, "First question"),
            (ChatRole.Assistant, "First answer"),
            (ChatRole.User, "Second question"));
    }

    [Fact]
    public async Task ProviderConversationIdConflictRollsBackAndAllowsNextRun()
    {
        using var deadline = CreateDeadline();
        var conflicting = new ChatResponseUpdate(ChatRole.Assistant, "conflict")
        {
            ConversationId = "provider-owned-conversation"
        };
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(conflicting),
            (_, _) => StreamAsync("recovered"));
        var session = new AgentSession(client);

        await using var failed = await session.StartTurnAsync(AgentTurn.FromText("first"), deadline.Token);
        await ReadEventsAsync(failed, deadline.Token);
        await failed.Completion.WaitAsync(deadline.Token)
            .Invoking(static task => task)
            .Should().ThrowAsync<AgentUnsupportedResponseException>();
        session.GetTranscriptSnapshot().Turns.Should().BeEmpty();

        await using var recovered = await session.StartTurnAsync(AgentTurn.FromText("retry"), deadline.Token);
        await ReadEventsAsync(recovered, deadline.Token);
        (await recovered.Completion.WaitAsync(deadline.Token)).AssistantMessage.Text.Should().Be("recovered");
        client.Requests[1].Select(MessageTuple).Should().Equal((ChatRole.User, "retry"));
    }

    [Fact]
    public void ConstructorRejectsInvalidDuplicateAndNonObjectFunctionMetadata()
    {
        var client = new ScriptedChatClient((_, _) => StreamAsync("unused"));
        var valid = CreateTool(
            "echo",
            (_, _, _) => ValueTask.FromResult<JsonElement?>(null));

        FluentActions.Invoking(() => new AgentSession(
                client,
                tools: [new MetadataAIFunction(valid, "invalid name", valid.JsonSchema)]))
            .Should().Throw<ArgumentException>().WithMessage("*Tool names*");

        FluentActions.Invoking(() => new AgentSession(client, tools: [valid, valid]))
            .Should().Throw<ArgumentException>().WithMessage("*already registered*");

        FluentActions.Invoking(() => new AgentSession(
                client,
                tools: [new MetadataAIFunction(valid, valid.Name, ParseJson("[]"))]))
            .Should().Throw<ArgumentException>().WithMessage("*schema must describe a JSON object*");

        FluentActions.Invoking(() => new AgentSession(
                client,
                tools: [new MetadataAIFunction(valid, valid.Name, ParseJson("{\"type\":\"string\"}"))]))
            .Should().Throw<ArgumentException>().WithMessage("*schema must describe a JSON object*");
    }

    [Fact]
    public async Task EarlyEventDisposalFollowedByRunDisposalCancelsAndRollsBack()
    {
        using var deadline = CreateDeadline();
        var client = new ScriptedChatClient(
            (_, token) => StreamUntilCanceledAsync(token),
            (_, _) => StreamAsync("next"));
        var session = new AgentSession(client, new AgentSessionOptions { EventBufferCapacity = 1 });
        var run = await session.StartTurnAsync(AgentTurn.FromText("early stop"), deadline.Token);

        await using (var events = run.Events.GetAsyncEnumerator(deadline.Token))
        {
            (await events.MoveNextAsync()).Should().BeTrue();
            events.Current.Should().BeOfType<AgentTextDelta>();
        }

        await run.DisposeAsync();
        await run.Completion
            .Invoking(static task => task)
            .Should().ThrowAsync<OperationCanceledException>();
        session.GetTranscriptSnapshot().Turns.Should().BeEmpty();

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), deadline.Token);
        await ReadEventsAsync(next, deadline.Token);
        await next.Completion.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task ToolCallPublishesLifecycleCommitsCompleteTurnAndReplaysHistory()
    {
        using var deadline = CreateDeadline();
        var tool = CreateTool(
            "echo",
            async (context, arguments, cancellationToken) =>
            {
                var typed = arguments.Deserialize(AgentTestJsonContext.Default.EchoArguments);
                typed.Should().NotBe(null);
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

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Use echo"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        var result = await run.Completion.WaitAsync(deadline.Token);

        events.Select(agentEvent => agentEvent.Sequence).Should()
            .Equal(Enumerable.Range(1, events.Count).Select(static value => (long)value));
        events.Select(agentEvent => agentEvent.GetType()).Should().Equal(
            typeof(AgentMessageCompleted),
            typeof(AgentToolStarted),
            typeof(AgentToolProgress),
            typeof(AgentToolFinished),
            typeof(AgentTextDelta),
            typeof(AgentMessageCompleted));
        events.OfType<AgentMessageCompleted>().Should().HaveCount(2);
        events.OfType<AgentToolStarted>().Single().Arguments
            .Deserialize(AgentTestJsonContext.Default.EchoArguments)?.Text.Should().Be("hello");
        events.OfType<AgentToolProgress>().Single().Content.Should()
            .BeEquivalentTo(new TextContent("working"));

        result.Transcript.Turns.Should().ContainSingle();
        var messages = result.Transcript.Turns[0].Messages;
        messages.Select(message => message.Role).Should().Equal(
            ChatRole.User,
            ChatRole.Assistant,
            ChatRole.Tool,
            ChatRole.Assistant);
        var call = messages[1].Contents.OfType<FunctionCallContent>().Single();
        var toolResult = messages[2].Contents.OfType<FunctionResultContent>().Single();
        toolResult.CallId.Should().Be(call.CallId).And.Be("provider-call");
        events.OfType<AgentToolStarted>().Single().CallId.ToString().Should().NotBe(call.CallId);
        var resultEnvelope = toolResult.Result.Should().BeOfType<JsonElement>().Which;
        resultEnvelope.GetProperty("status").GetString().Should().Be("ok");
        resultEnvelope.GetProperty("value").GetProperty("text").GetString().Should().Be("hello");
        events.OfType<AgentToolFinished>().Single().Result.GetRawText().Should().Be(resultEnvelope.GetRawText());
        result.AssistantMessage.Should().BeEquivalentTo(messages[^1]);
        result.AssistantMessage.Text.Should().Be("The tool returned hello.");

        client.Requests.Should().HaveCount(2);
        var providerResult = client.Requests[1]
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single();
        providerResult.Result.Should().BeOfType<JsonElement>()
            .Which.GetProperty("status").GetString().Should().Be("ok");

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("Continue"), deadline.Token);
        await ReadEventsAsync(next, deadline.Token);
        await next.Completion.WaitAsync(deadline.Token);
        client.Requests[2].Select(message => message.Role).Should().Equal(
            ChatRole.User,
            ChatRole.Assistant,
            ChatRole.Tool,
            ChatRole.Assistant,
            ChatRole.User);
        client.Requests[2].SelectMany(static message => message.Contents)
            .OfType<FunctionCallContent>().Single().CallId.Should().Be("provider-call");
        client.Requests[2].SelectMany(static message => message.Contents)
            .OfType<FunctionResultContent>().Single().CallId.Should().Be("provider-call");
    }

    [Fact]
    public async Task ExpectedToolFailureReturnsStableEnvelopeAndAllowsModelRecovery()
    {
        using var deadline = CreateDeadline();
        var tool = CreateTool(
            "lookup",
            (_, _, _) => ValueTask.FromException<JsonElement?>(
                new AgentToolException("not_found", "No matching value was found.")));
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync(ToolCallUpdate("call", "lookup")),
            (messages, _) =>
            {
                var envelope = messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>()
                    .Single().Result.Should().BeOfType<JsonElement>().Which;
                envelope.GetProperty("status").GetString().Should().Be("error");
                envelope.GetProperty("code").GetString().Should().Be("not_found");
                return StreamAsync("I could not find it.");
            });
        var session = new AgentSession(client, tools: [tool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Find it"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        var result = await run.Completion.WaitAsync(deadline.Token);

        var finished = events.OfType<AgentToolFinished>().Single().Result;
        finished.GetProperty("status").GetString().Should().Be("error");
        finished.GetProperty("code").GetString().Should().Be("not_found");
        result.AssistantMessage.Text.Should().Be("I could not find it.");
        result.Transcript.Turns[0].Messages[2].Contents
            .OfType<FunctionResultContent>().Single().Result.Should().BeOfType<JsonElement>()
            .Which.GetProperty("status").GetString().Should().Be("error");
    }

    [Fact]
    public async Task MultipleToolCallsExecuteSeriallyInProviderOrder()
    {
        using var deadline = CreateDeadline();
        var active = 0;
        var maximumActive = 0;
        var order = new List<string>();
        var tool = CreateTool(
            "record",
            async (_, arguments, _) =>
            {
                var value = arguments.Deserialize(AgentTestJsonContext.Default.EchoArguments)?.Text;
                value.Should().NotBeNull();
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

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Record both"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        order.Should().Equal("one", "two");
        maximumActive.Should().Be(1);
        events.OfType<AgentToolStarted>().Select(started =>
                started.Arguments.Deserialize(AgentTestJsonContext.Default.EchoArguments)?.Text)
            .Should().Equal("one", "two");
    }

    [Fact]
    public async Task UnexpectedToolExceptionPublishesFailureAndRollsBackWholeTurn()
    {
        using var deadline = CreateDeadline();
        var tool = CreateTool(
            "explode",
            (_, _, _) => ValueTask.FromException<JsonElement?>(new InvalidOperationException("secret detail")));
        var session = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync(ToolCallUpdate("call", "explode"))),
            tools: [tool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Explode"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        var failure = (await run.Completion.WaitAsync(deadline.Token)
            .Invoking(static task => task)
            .Should().ThrowAsync<AgentToolInvocationException>()).Which;

        failure.ToolName.Should().Be("explode");
        failure.InnerException.Should().BeOfType<InvalidOperationException>();
        var finished = events.OfType<AgentToolFinished>().Single().Result;
        finished.GetProperty("status").GetString().Should().Be("error");
        finished.GetProperty("message").GetString().Should().Be("The tool failed unexpectedly.");
        session.GetTranscriptSnapshot().Turns.Should().BeEmpty();
    }

    [Fact]
    public async Task NonJsonFunctionResultFailsDeterministicallyAndRollsBackWholeTurn()
    {
        using var deadline = CreateDeadline();
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

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Return raw"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        var failure = (await run.Completion.WaitAsync(deadline.Token)
            .Invoking(static task => task)
            .Should().ThrowAsync<AgentToolInvocationException>()).Which;

        failure.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("JsonElement or null");
        events.OfType<AgentToolStarted>().Should().ContainSingle();
        events.OfType<AgentToolFinished>().Single().Result.GetProperty("status").GetString()
            .Should().Be("error");
        session.GetTranscriptSnapshot().Turns.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolArgumentLimitTerminatesRunBeforeInvocation()
    {
        using var deadline = CreateDeadline();
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

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Limit"), deadline.Token);
        (await ReadEventsAsync(run, deadline.Token)).Should().BeEmpty();
        var failure = (await run.Completion.WaitAsync(deadline.Token)
            .Invoking(static task => task)
            .Should().ThrowAsync<AgentToolLimitExceededException>()).Which;

        failure.LimitName.Should().Be(nameof(AgentSessionOptions.MaxToolArgumentsBytes));
        invoked.Should().BeFalse();
        session.GetTranscriptSnapshot().Turns.Should().BeEmpty();
    }

    [Fact]
    public async Task CancellationStopsActiveToolAndRollsBackTurn()
    {
        using var deadline = CreateDeadline();
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
                    if (cancellationToken.IsCancellationRequested)
                    {
                        canceled.TrySetResult();
                    }
                }
            });
        var session = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync(ToolCallUpdate("call", "wait"))),
            tools: [tool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Wait"), deadline.Token);
        await started.Task.WaitAsync(deadline.Token);
        await run.CancelAsync(deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);

        await canceled.Task.WaitAsync(deadline.Token);
        events.OfType<AgentToolStarted>().Should().ContainSingle();
        await run.Completion
            .Invoking(static task => task)
            .Should().ThrowAsync<OperationCanceledException>();
        session.GetTranscriptSnapshot().Turns.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolResultAndProgressLimitsRollBackWholeTurn()
    {
        using var deadline = CreateDeadline();
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
        await using (var run = await resultLimited.StartTurnAsync(AgentTurn.FromText("Large"), deadline.Token))
        {
            await ReadEventsAsync(run, deadline.Token);
            var failure = (await run.Completion.WaitAsync(deadline.Token)
                .Invoking(static task => task)
                .Should().ThrowAsync<AgentToolLimitExceededException>()).Which;
            failure.LimitName.Should().Be(nameof(AgentSessionOptions.MaxToolResultBytes));
        }

        resultLimited.GetTranscriptSnapshot().Turns.Should().BeEmpty();

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
        await using (var run = await progressLimited.StartTurnAsync(AgentTurn.FromText("Progress"), deadline.Token))
        {
            var events = await ReadEventsAsync(run, deadline.Token);
            events.OfType<AgentToolProgress>().Select(progress => ((TextContent)progress.Content).Text)
                .Should().Equal("one");
            var failure = (await run.Completion.WaitAsync(deadline.Token)
                .Invoking(static task => task)
                .Should().ThrowAsync<AgentToolLimitExceededException>()).Which;
            failure.LimitName.Should().Be(nameof(AgentSessionOptions.MaxToolProgressEventsPerCall));
        }

        progressLimited.GetTranscriptSnapshot().Turns.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallAndModelIterationLimitsRollBackWholeTurn()
    {
        using var deadline = CreateDeadline();
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
        await using (var run = await callLimited.StartTurnAsync(AgentTurn.FromText("Count"), deadline.Token))
        {
            (await ReadEventsAsync(run, deadline.Token)).Should().BeEmpty();
            var failure = (await run.Completion.WaitAsync(deadline.Token)
                .Invoking(static task => task)
                .Should().ThrowAsync<AgentToolLimitExceededException>()).Which;
            failure.LimitName.Should().Be(nameof(AgentSessionOptions.MaxToolCallsPerTurn));
        }

        invoked.Should().Be(0);
        callLimited.GetTranscriptSnapshot().Turns.Should().BeEmpty();

        var iterationLimited = new AgentSession(
            new ScriptedChatClient(
                (_, _) => StreamAsync(ToolCallUpdate("one", "again")),
                (_, _) => StreamAsync(ToolCallUpdate("two", "again"))),
            new AgentSessionOptions { MaxModelIterationsPerTurn = 2 },
            [
                CreateTool(
                    "again",
                    (_, _, _) => ValueTask.FromResult<JsonElement?>(null))
            ]);
        await using (var run = await iterationLimited.StartTurnAsync(AgentTurn.FromText("Again"), deadline.Token))
        {
            await ReadEventsAsync(run, deadline.Token);
            var failure = (await run.Completion.WaitAsync(deadline.Token)
                .Invoking(static task => task)
                .Should().ThrowAsync<AgentModelIterationLimitExceededException>()).Which;
            failure.MaximumIterations.Should().Be(2);
        }

        iterationLimited.GetTranscriptSnapshot().Turns.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolBearingHistoryEvictionRemovesWholeOldestTurn()
    {
        using var deadline = CreateDeadline();
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

        await CompleteTurnAsync(session, "tool turn", deadline.Token);
        await CompleteTurnAsync(session, "plain turn", deadline.Token);

        var transcript = session.GetTranscriptSnapshot();
        transcript.Version.Should().Be(2);
        transcript.Turns.Should().ContainSingle();
        transcript.Turns[0].Messages.Select(message => message.Text).Should().Equal("plain turn", "second");
        transcript.Turns[0].Messages
            .SelectMany(message => message.Contents)
            .Where(content => content is FunctionCallContent or FunctionResultContent)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task TextBeforeToolCallAndFinalTextUseTheirCompletedMessageIds()
    {
        using var deadline = CreateDeadline();
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

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("Check"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        var deltas = events.OfType<AgentTextDelta>().ToArray();
        var completed = events.OfType<AgentMessageCompleted>().ToArray();
        deltas.Select(delta => delta.Text).Should().Equal("checking", "done");
        deltas[0].MessageId.Should().Be(completed[0].AgentMessageId);
        deltas[1].MessageId.Should().Be(completed[^1].AgentMessageId);
        completed[0].AgentMessageId.Should().NotBe(completed[^1].AgentMessageId);
    }

    [Fact]
    public void PublicResultModelsRejectDefaultIdentifiersAndInvalidSequences()
    {
        var runId = AgentRunId.Create();
        var messageId = AgentMessageId.Create();
        var message = new ChatMessage(ChatRole.Assistant, "answer");

        FluentActions.Invoking(() => new AgentTranscript(default, 0, [])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new AgentTextDelta(runId, 0, messageId, "answer"))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new AgentMessageCompleted(default, 1, messageId, message))
            .Should().Throw<ArgumentException>();
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
        await foreach (var agentEvent in run.Events.WithCancellation(cancellationToken))
        {
            events.Add(agentEvent);
        }

        return events;
    }

    private static (ChatRole Role, string Text) MessageTuple(ChatMessage message) =>
        (message.Role, message.Text);

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
        params (string Name, object? Value)[] arguments) =>
        new(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    callId,
                    name,
                    arguments.ToDictionary(static argument => argument.Name, static argument => argument.Value))
            ]);

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
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
        foreach (var update in updates)
        {
            yield return update;
        }
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

    private static CancellationTokenSource CreateDeadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
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

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public List<string?> Instructions { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(new NotSupportedException());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var request = messages.Select(message => message.Clone()).ToArray();
            Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> response;
            lock (gate)
            {
                Requests.Add(request);
                Instructions.Add(options?.Instructions);
                response = responses.Dequeue();
            }

            return response(request, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
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