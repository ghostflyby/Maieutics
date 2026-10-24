using Microsoft.Extensions.AI;
using ScriptedChatClient = Maieutics.Testing.ScriptedChatClient;

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

        Assert.Equal(2, store.Turns.Count());
        Assert.Equal(first.Id, store.Turns[0].RunId);
        Assert.Equal(second.Id, store.Turns[1].RunId);
        Assert.Equal(ChatRole.User, store.Turns[0].Messages[0].Role);
        Assert.Equal("one", store.Turns[0].Messages[0].Text);
        Assert.Equal(ChatRole.Assistant, store.Turns[0].Messages[^1].Role);
        Assert.Equal("Hello world", store.Turns[0].Messages[^1].Text);
        Assert.Equal(2, store.Turns[1].Messages.Count());
        Assert.Equal("two", store.Turns[1].Messages[0].Text);
        Assert.Equal("Again!", store.Turns[1].Messages[^1].Text);
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
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => { await run.Completion.WaitAsync(TestContext.Current.CancellationToken); });
        store.FailOnAppend = false;

        Assert.Empty(session.GetTranscriptSnapshot().Turns);

        await using var next = await session.StartTurnAsync(AgentTurn.FromText("again"), deadline.Token);
        await ReadEventsAsync(next, deadline.Token);
        Assert.Equal("second", (await next.Completion.WaitAsync(deadline.Token)).AssistantMessage.Text);

        Assert.Single(store.Turns);
        Assert.Equal("again", store.Turns[0].Messages[0].Text);
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

        Assert.Equal(sessionId, session.Id);
        Assert.Equal(sessionId, Assert.Single(store.SessionIds));
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken testToken)
        => TestDeadlines.Create(testToken, TimeSpan.FromSeconds(25));

    private static async Task ReadEventsAsync(
        IAgentRun run,
        CancellationToken cancellationToken)
    {
        await foreach (var _ in run.Events.WithCancellation(cancellationToken))
        {
        }
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

        public bool FailOnAppend { get; set; }

        public void AppendTurn(AgentSessionId sessionId, AgentTranscriptTurn turn, IReadOnlyList<string> objectReferences)
        {
            lock (gate)
            {
                if (FailOnAppend) throw new InvalidOperationException("The transcript store is unavailable.");
                SessionIds.Add(sessionId);
                Turns.Add(new AgentTranscriptTurn(
                    turn.RunId,
                    turn.Messages.Select(message => message.Clone()).ToArray(),
                    turn.ModelIdentity,
                    turn.Truncated));
            }
        }

        public AgentTranscript LoadTranscript(AgentSessionId sessionId)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<AgentSessionDescriptor> ListSessions()
        {
            throw new NotSupportedException();
        }
    }
}
