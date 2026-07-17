using System.Runtime.CompilerServices;
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
        var concurrent = () => session.StartTurnAsync(AgentTurn.FromText("concurrent"), deadline.Token);
        await concurrent.Should().ThrowAsync<AgentTurnInProgressException>();

        releaseProvider.SetResult();
        await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), deadline.Token);
        await ReadEventsAsync(next, deadline.Token);
        (await next.Completion.WaitAsync(deadline.Token)).AssistantMessage.Text().Should().Be("second");
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
        events.Should().OnlyContain(agentEvent => agentEvent.RunId == run.Id);
        var deltas = events.OfType<AgentTextDelta>().ToArray();
        deltas.Select(delta => delta.Text).Should().Equal("Hello", " world");
        deltas.Select(delta => delta.MessageId).Should().OnlyContain(id => id == result.AssistantMessage.Id);
        var completed = events.OfType<AgentMessageCompleted>().Single();
        completed.Message.Should().Be(result.AssistantMessage);

        result.RunId.Should().Be(run.Id);
        result.UserMessage.Id.Value.Should().NotBe(Guid.Empty);
        result.UserMessage.Role.Should().Be(AgentMessageRole.User);
        result.UserMessage.Text().Should().Be("Question");
        result.AssistantMessage.Id.Value.Should().NotBe(Guid.Empty);
        result.AssistantMessage.Role.Should().Be(AgentMessageRole.Assistant);
        result.AssistantMessage.Text().Should().Be("Hello world");
        result.Transcript.SessionId.Should().Be(session.Id);
        result.Transcript.Version.Should().Be(1);
        result.Transcript.Messages.Should().Equal(result.UserMessage, result.AssistantMessage);
        session.GetTranscriptSnapshot().Should().Be(result.Transcript);
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
        var completion = () => failedRun.Completion.WaitAsync(deadline.Token);

        events.OfType<AgentTextDelta>().Select(delta => delta.Text).Should().Equal("partial");
        var failure = (await completion.Should().ThrowAsync<AgentProviderException>()).Which;
        failure.InnerException.Should().BeOfType<InvalidOperationException>();
        session.GetTranscriptSnapshot().Messages.Should().BeEmpty();

        await using var recoveredRun = await session.StartTurnAsync(AgentTurn.FromText("retry"), deadline.Token);
        await ReadEventsAsync(recoveredRun, deadline.Token);
        (await recoveredRun.Completion.WaitAsync(deadline.Token)).AssistantMessage.Text().Should().Be("recovered");
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
        var completion = () => run.Completion;
        await completion.Should().ThrowAsync<OperationCanceledException>();
        session.GetTranscriptSnapshot().Messages.Should().BeEmpty();

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
        var rejected = () => inputLimited.StartTurnAsync(AgentTurn.FromText("four"), deadline.Token);

        var inputFailure = (await rejected.Should().ThrowAsync<AgentInputLimitExceededException>()).Which;
        inputFailure.ActualCharacters.Should().Be(4);
        unusedClient.Requests.Should().BeEmpty();
        inputLimited.GetTranscriptSnapshot().Messages.Should().BeEmpty();

        var responseLimited = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync("123", "456")),
            new AgentSessionOptions { MaxResponseCharacters = 5 });
        await using var run = await responseLimited.StartTurnAsync(AgentTurn.FromText("limit"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        var completion = () => run.Completion.WaitAsync(deadline.Token);

        events.OfType<AgentTextDelta>().Select(delta => delta.Text).Should().Equal("123");
        await completion.Should().ThrowAsync<AgentResponseLimitExceededException>();
        responseLimited.GetTranscriptSnapshot().Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyAndUnsupportedResponsesRollBackWholeTurn()
    {
        using var deadline = CreateDeadline();
        var emptySession = new AgentSession(new ScriptedChatClient((_, _) => StreamAsync()));
        await using var emptyRun = await emptySession.StartTurnAsync(AgentTurn.FromText("empty"), deadline.Token);
        (await ReadEventsAsync(emptyRun, deadline.Token)).Should().BeEmpty();
        var emptyCompletion = () => emptyRun.Completion.WaitAsync(deadline.Token);
        await emptyCompletion.Should().ThrowAsync<AgentUnsupportedResponseException>();
        emptySession.GetTranscriptSnapshot().Messages.Should().BeEmpty();

        var unsupportedUpdate = new ChatResponseUpdate(
            ChatRole.Assistant,
            [new FunctionCallContent("call", "tool", new Dictionary<string, object?>())]);
        var unsupportedSession = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync(unsupportedUpdate)));
        await using var unsupportedRun = await unsupportedSession.StartTurnAsync(
            AgentTurn.FromText("unsupported"),
            deadline.Token);
        await ReadEventsAsync(unsupportedRun, deadline.Token);
        var unsupportedCompletion = () => unsupportedRun.Completion.WaitAsync(deadline.Token);
        await unsupportedCompletion.Should().ThrowAsync<AgentUnsupportedResponseException>();
        unsupportedSession.GetTranscriptSnapshot().Messages.Should().BeEmpty();
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
            MaxRetainedTurns = 2,
            MaxHistoryCharacters = 10
        });

        await CompleteTurnAsync(session, "a", deadline.Token);
        await CompleteTurnAsync(session, "bb", deadline.Token);
        await CompleteTurnAsync(session, "c", deadline.Token);

        var transcript = session.GetTranscriptSnapshot();
        transcript.Version.Should().Be(3);
        transcript.Messages.Select(message => message.Role).Should()
            .Equal(AgentMessageRole.User, AgentMessageRole.Assistant);
        transcript.Messages.Select(message => message.Text()).Should().Equal("c", "three");
    }

    [Fact]
    public async Task EventsAllowOnlyOneConsumer()
    {
        using var deadline = CreateDeadline();
        var session = new AgentSession(new ScriptedChatClient((_, _) => StreamAsync("answer")));
        await using var run = await session.StartTurnAsync(AgentTurn.FromText("question"), deadline.Token);

        await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);
        var secondConsumer = () => ReadEventsAsync(run, deadline.Token);

        await secondConsumer.Should().ThrowAsync<InvalidOperationException>()
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
        var completion = () => run.Completion;
        await completion.Should().ThrowAsync<OperationCanceledException>();
        session.GetTranscriptSnapshot().Messages.Should().BeEmpty();

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

        var completion = () => run.Completion;
        await completion.Should().ThrowAsync<OperationCanceledException>();
        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), deadline.Token);
        await ReadEventsAsync(next, deadline.Token);
        await next.Completion.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task FrameworkHistoryRequestsContainSystemPromptAndCommittedTurnsOnly()
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
    public async Task ProviderConversationIdConflictRollsBackAndRecreatesFrameworkSession()
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
        var failure = () => failed.Completion.WaitAsync(deadline.Token);
        await failure.Should().ThrowAsync<AgentProviderException>();
        session.GetTranscriptSnapshot().Messages.Should().BeEmpty();

        await using var recovered = await session.StartTurnAsync(AgentTurn.FromText("retry"), deadline.Token);
        await ReadEventsAsync(recovered, deadline.Token);
        (await recovered.Completion.WaitAsync(deadline.Token)).AssistantMessage.Text().Should().Be("recovered");
        client.Requests[1].Select(MessageTuple).Should().Equal((ChatRole.User, "retry"));
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
        var completion = () => run.Completion;
        await completion.Should().ThrowAsync<OperationCanceledException>();
        session.GetTranscriptSnapshot().Messages.Should().BeEmpty();

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("next"), deadline.Token);
        await ReadEventsAsync(next, deadline.Token);
        await next.Completion.WaitAsync(deadline.Token);
    }

    [Fact]
    public void PublicResultModelsRejectDefaultIdentifiersAndInvalidSequences()
    {
        var runId = AgentRunId.Create();
        var messageId = AgentMessageId.Create();
        var message = new AgentMessage(
            messageId,
            AgentMessageRole.Assistant,
            [new AgentTextContent("answer")]);

        var transcript = () => new AgentTranscript(default, 0, []);
        transcript.Should().Throw<ArgumentException>();
        var delta = () => new AgentTextDelta(runId, 0, messageId, "answer");
        delta.Should().Throw<ArgumentOutOfRangeException>();
        var completed = () => new AgentMessageCompleted(default, 1, message);
        completed.Should().Throw<ArgumentException>();
    }

    private static async Task<AgentRunResult> CompleteTurnAsync(
        AgentSession session,
        string input,
        CancellationToken cancellationToken)
    {
        await using var run = await session.StartTurnAsync(AgentTurn.FromText(input), cancellationToken);
        await ReadEventsAsync(run, cancellationToken);
        return await run.Completion.WaitAsync(cancellationToken);
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

file static class AgentMessageAssertions
{
    internal static string Text(this AgentMessage message) =>
        string.Concat(message.Contents.OfType<AgentTextContent>().Select(content => content.Text));
}