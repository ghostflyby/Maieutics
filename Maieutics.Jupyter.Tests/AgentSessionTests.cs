using System.Runtime.CompilerServices;
using FluentAssertions;
using Maieutics.Agent;
using Microsoft.Extensions.AI;

namespace Maieutics.Jupyter.Tests;

public sealed class AgentSessionTests
{
    [Fact]
    public async Task SuccessfulTurnsStreamTextAndCommitTransactionalHistory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = new FakeChatClient(
            _ => StreamAsync("Hello", " world"),
            _ => StreamAsync("Second"));
        var session = new AgentSession(client, new AgentSessionOptions { SystemPrompt = "Be concise." });

        var first = await ReadEventsAsync(session.ExecuteTurnAsync(new AgentTurn("First"), cancellationToken));
        var second = await ReadEventsAsync(session.ExecuteTurnAsync(new AgentTurn("Next"), cancellationToken));

        first.OfType<AgentTextDelta>().Select(delta => delta.Text).Should().Equal("Hello", " world");
        first.OfType<AgentTurnCompleted>().Single().Assistant.Text.Should().Be("Hello world");
        second.OfType<AgentTurnCompleted>().Single().Assistant.Text.Should().Be("Second");
        session.GetHistorySnapshot().Should().Equal(
            new AgentMessage(AgentMessageRole.User, "First"),
            new AgentMessage(AgentMessageRole.Assistant, "Hello world"),
            new AgentMessage(AgentMessageRole.User, "Next"),
            new AgentMessage(AgentMessageRole.Assistant, "Second"));
        client.Requests[0].Select(message => (message.Role, message.Text)).Should().Equal(
            (ChatRole.System, "Be concise."),
            (ChatRole.User, "First"));
        client.Requests[1].Select(message => (message.Role, message.Text)).Should().Equal(
            (ChatRole.System, "Be concise."),
            (ChatRole.User, "First"),
            (ChatRole.Assistant, "Hello world"),
            (ChatRole.User, "Next"));
    }

    [Fact]
    public async Task ProviderFailureCancellationAndResponseLimitRollBackTurn()
    {
        var failureClient =
            new FakeChatClient(_ => FailAfterTextAsync("partial", new InvalidOperationException("boom")));
        var failureSession = new AgentSession(failureClient);
        var failure = () => ReadEventsAsync(failureSession.ExecuteTurnAsync(new AgentTurn("failure")));
        (await failure.Should().ThrowAsync<AgentProviderException>()).Which.InnerException.Should()
            .BeOfType<InvalidOperationException>();
        failureSession.GetHistorySnapshot().Should().BeEmpty();

        using var cancellation = new CancellationTokenSource();
        var cancellationClient = new FakeChatClient(token => WaitAfterTextAsync("partial", token));
        var cancellationSession = new AgentSession(cancellationClient);
        await using var enumerator = cancellationSession.ExecuteTurnAsync(new AgentTurn("cancel"), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        cancellation.Cancel();
        var canceled = async () => await enumerator.MoveNextAsync();
        await canceled.Should().ThrowAsync<OperationCanceledException>();
        cancellationSession.GetHistorySnapshot().Should().BeEmpty();

        var limitedClient = new FakeChatClient(_ => StreamAsync("123", "456"));
        var limitedSession = new AgentSession(limitedClient, new AgentSessionOptions { MaxResponseCharacters = 5 });
        var limited = () => ReadEventsAsync(limitedSession.ExecuteTurnAsync(new AgentTurn("limit")));
        await limited.Should().ThrowAsync<AgentResponseLimitExceededException>();
        limitedSession.GetHistorySnapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyAndToolCallResponsesAreRejectedWithoutHistory()
    {
        var emptySession = new AgentSession(new FakeChatClient(_ => StreamAsync()));
        var empty = () => ReadEventsAsync(emptySession.ExecuteTurnAsync(new AgentTurn("empty")));
        await empty.Should().ThrowAsync<AgentUnsupportedResponseException>();
        emptySession.GetHistorySnapshot().Should().BeEmpty();

        var update = new ChatResponseUpdate(
            ChatRole.Assistant,
            [new FunctionCallContent("call", "tool", new Dictionary<string, object?>())]);
        var toolSession = new AgentSession(new FakeChatClient(_ => StreamAsync(update)));
        var tool = () => ReadEventsAsync(toolSession.ExecuteTurnAsync(new AgentTurn("tool")));
        await tool.Should().ThrowAsync<AgentUnsupportedResponseException>();
        toolSession.GetHistorySnapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task InputLimitRejectsBeforeProviderAndReasoningMetadataIsNotEmitted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var limitedClient = new FakeChatClient(_ => StreamAsync("unused"));
        var limitedSession = new AgentSession(limitedClient, new AgentSessionOptions { MaxInputCharacters = 3 });
        var limited = () => ReadEventsAsync(
            limitedSession.ExecuteTurnAsync(new AgentTurn("four"), cancellationToken));

        await limited.Should().ThrowAsync<AgentInputLimitExceededException>();
        limitedClient.Requests.Should().BeEmpty();
        limitedSession.GetHistorySnapshot().Should().BeEmpty();

        var update = new ChatResponseUpdate(
            ChatRole.Assistant,
            [new TextReasoningContent("private reasoning"), new TextContent("answer"), new UsageContent()]);
        var session = new AgentSession(new FakeChatClient(_ => StreamAsync(update)));
        var events = await ReadEventsAsync(session.ExecuteTurnAsync(new AgentTurn("question"), cancellationToken));

        events.OfType<AgentTextDelta>().Single().Text.Should().Be("answer");
        events.OfType<AgentTextDelta>().Select(delta => delta.Text)
            .Concat(events.OfType<AgentTurnCompleted>().Select(completed => completed.Assistant.Text))
            .Should().NotContain(text => text.Contains("private reasoning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HistoryEvictionRemovesOnlyCompleteOldestTurns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = new FakeChatClient(
            _ => StreamAsync("one"),
            _ => StreamAsync("two"),
            _ => StreamAsync("three"));
        var session = new AgentSession(client, new AgentSessionOptions
        {
            MaxRetainedTurns = 2,
            MaxHistoryCharacters = 10
        });

        await ReadEventsAsync(session.ExecuteTurnAsync(new AgentTurn("a"), cancellationToken));
        await ReadEventsAsync(session.ExecuteTurnAsync(new AgentTurn("bb"), cancellationToken));
        await ReadEventsAsync(session.ExecuteTurnAsync(new AgentTurn("c"), cancellationToken));

        session.GetHistorySnapshot().Should().Equal(
            new AgentMessage(AgentMessageRole.User, "c"),
            new AgentMessage(AgentMessageRole.Assistant, "three"));
    }

    [Fact]
    public async Task ConcurrentTurnIsRejectedUntilFirstEnumerationCompletes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeChatClient(
            token => WaitForGateAsync(gate.Task, token),
            _ => StreamAsync("next"));
        var session = new AgentSession(client);
        await using var first = session.ExecuteTurnAsync(new AgentTurn("first"), cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        var firstMove = first.MoveNextAsync().AsTask();

        var concurrent = () => ReadEventsAsync(
            session.ExecuteTurnAsync(new AgentTurn("concurrent"), cancellationToken));
        await concurrent.Should().ThrowAsync<AgentTurnInProgressException>();

        gate.SetResult();
        (await firstMove).Should().BeTrue();
        while (await first.MoveNextAsync())
        {
        }

        var next = await ReadEventsAsync(session.ExecuteTurnAsync(new AgentTurn("next"), cancellationToken));
        next.OfType<AgentTurnCompleted>().Single().Assistant.Text.Should().Be("next");
    }

    private static async Task<IReadOnlyList<AgentEvent>> ReadEventsAsync(IAsyncEnumerable<AgentEvent> source)
    {
        var events = new List<AgentEvent>();
        await foreach (var agentEvent in source.WithCancellation(TestContext.Current.CancellationToken))
        {
            events.Add(agentEvent);
        }

        return events;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        params string[] text)
    {
        foreach (var value in text)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, value);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        ChatResponseUpdate update)
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

    private static async IAsyncEnumerable<ChatResponseUpdate> WaitAfterTextAsync(
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WaitForGateAsync(
        Task gate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, "first");
    }

    private sealed class FakeChatClient(
        params Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>[] responses) : IChatClient
    {
        private readonly Queue<Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>> responses =
            new(responses);

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

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
            Requests.Add(messages.Select(message => message.Clone()).ToArray());
            return responses.Dequeue()(cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}