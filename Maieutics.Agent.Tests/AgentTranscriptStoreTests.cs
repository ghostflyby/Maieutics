using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent.Tests;

public sealed class AgentTranscriptStoreTests
{
    [Fact(Timeout = 30_000)]
    public async Task CommittedTurnsAreAppendedInOrderWithCanonicalContent()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var store = new RecordingTranscriptStore();
        var session = new AgentSession(
            new ScriptedChatClient(
                (_, _) => StreamAsync("Hello", " world"),
                (_, _) => StreamAsync("Again", "!")),
            transcriptStore: store);

        await using var first = await session.StartTurnAsync(AgentTurn.FromText("one"), deadline.Token);
        await ReadEventsAsync(first, deadline.Token);
        await first.Completion.WaitAsync(deadline.Token);

        await using var second = await session.StartTurnAsync(AgentTurn.FromText("two"), deadline.Token);
        await ReadEventsAsync(second, deadline.Token);
        await second.Completion.WaitAsync(deadline.Token);

        store.Turns.Should().HaveCount(2);
        store.Turns[0].RunId.Should().Be(first.Id);
        store.Turns[1].RunId.Should().Be(second.Id);
        store.Turns[0].Messages[0].Role.Should().Be(ChatRole.User);
        store.Turns[0].Messages[0].Text.Should().Be("one");
        store.Turns[0].Messages[^1].Role.Should().Be(ChatRole.Assistant);
        store.Turns[0].Messages[^1].Text.Should().Be("Hello world");
        store.Turns[1].Messages.Should().HaveCount(2);
        store.Turns[1].Messages[0].Text.Should().Be("two");
        store.Turns[1].Messages[^1].Text.Should().Be("Again!");
    }

    [Fact(Timeout = 30_000)]
    public async Task StoreFailureRollsBackTheTurnAndKeepsTheSessionUsable()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var store = new RecordingTranscriptStore { FailOnAppend = true };
        var session = new AgentSession(
            new ScriptedChatClient(
                (_, _) => StreamAsync("first"),
                (_, _) => StreamAsync("second")),
            transcriptStore: store);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("question"), deadline.Token);
        await ReadEventsAsync(run, deadline.Token);
        await run.Completion.Awaiting(static completion => completion.WaitAsync(TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidOperationException>();
        store.FailOnAppend = false;

        session.GetTranscriptSnapshot().Turns.Should().BeEmpty();

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("again"), deadline.Token);
        await ReadEventsAsync(next, deadline.Token);
        (await next.Completion.WaitAsync(deadline.Token)).AssistantMessage.Text.Should().Be("second");

        store.Turns.Should().ContainSingle();
        store.Turns[0].Messages[0].Text.Should().Be("again");
    }

    [Fact(Timeout = 30_000)]
    public async Task SessionReusesAnInjectedIdentityForTheStore()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var store = new RecordingTranscriptStore();
        var sessionId = AgentSessionId.Create();
        var session = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync("ok")),
            transcriptStore: store,
            sessionId: sessionId);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("question"), deadline.Token);
        await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        session.Id.Should().Be(sessionId);
        store.SessionIds.Should().ContainSingle().Which.Should().Be(sessionId);
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken testToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        return deadline;
    }

    private static async Task<List<AgentEvent>> ReadEventsAsync(
        IAgentRun run,
        CancellationToken cancellationToken)
    {
        var events = new List<AgentEvent>();
        await foreach (var agentEvent in run.Events.WithCancellation(cancellationToken))
            events.Add(agentEvent);

        return events;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(params string[] deltas)
    {
        foreach (var delta in deltas)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, delta);
        }
    }

    private sealed class RecordingTranscriptStore : IAgentTranscriptStore
    {
        private readonly Lock gate = new();

        public List<AgentTranscriptTurn> Turns { get; } = [];

        public List<AgentSessionId> SessionIds { get; } = [];

        public List<IReadOnlyList<string>> References { get; } = [];

        public bool FailOnAppend { get; set; }

        public void AppendTurn(AgentSessionId sessionId, AgentTranscriptTurn turn, IReadOnlyList<string> objectReferences)
        {
            lock (gate)
            {
                if (FailOnAppend) throw new InvalidOperationException("The transcript store is unavailable.");
                SessionIds.Add(sessionId);
                References.Add(objectReferences);
                Turns.Add(new AgentTranscriptTurn(
                    turn.RunId,
                    turn.Messages.Select(message => message.Clone()).ToArray(),
                    turn.ModelIdentity,
                    turn.Truncated));
            }
        }

        public AgentTranscript? LoadTranscript(AgentSessionId sessionId)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<AgentSessionDescriptor> ListSessions()
        {
            throw new NotSupportedException();
        }
    }

    private sealed class ScriptedChatClient(
        params Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>[] responses)
        : IChatClient
    {
        private readonly Queue<Func<IReadOnlyList<ChatMessage>, CancellationToken,
            IAsyncEnumerable<ChatResponseUpdate>>> responses = new(responses);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return responses.Dequeue()(messages.ToArray(), cancellationToken);
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<ChatResponse>(new NotSupportedException());
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
