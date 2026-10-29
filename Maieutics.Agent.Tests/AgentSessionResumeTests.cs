using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using ScriptedChatClient = Maieutics.Testing.ScriptedChatClient;

namespace Maieutics.Agent.Tests;

public sealed class AgentSessionResumeTests
{
    [Fact(Timeout = 30_000)]
    public async Task ResumeRestoresCommittedHistoryAndContinuesAppendingToTheStore()
    {
        var sessionId = AgentSessionId.Create();
        var store = new InMemoryTranscriptStore();
        var firstProvider = new FixedProfileProvider(
            new ScriptedChatClient((_, _) => StreamAsync("first")));
        var original = new AgentSession(firstProvider, transcriptStore: store, sessionId: sessionId);

        await using var run = await original.StartTurnAsync(AgentTurn.FromText("one"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        await run.Completion.WaitAsync(TestContext.Current.CancellationToken);

        var resumedProvider = new FixedProfileProvider(
            new ScriptedChatClient((_, _) => StreamAsync("second")));
        var resumed = AgentSession.Resume(resumedProvider, store, sessionId);

        Assert.Equal(sessionId, resumed.Id);
        var snapshot = resumed.GetTranscriptSnapshot();
        var item = Assert.Single(snapshot.Turns);
        Assert.Equal("one", item.Messages[0].Text);
        Assert.Equal("first", snapshot.Turns[0].Messages[^1].Text);

        await using var next = await resumed.StartTurnAsync(AgentTurn.FromText("two"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(next, TestContext.Current.CancellationToken);
        Assert.Equal("second", (await next.Completion.WaitAsync(TestContext.Current.CancellationToken)).AssistantMessage.Text);

        if (store.LoadTranscript(sessionId) is not { } persisted)
        {
            throw new InvalidOperationException("The resumed session's turns were not persisted.");
        }

        Assert.Equal(2, persisted.Turns.Count());
        Assert.Equal("two", persisted.Turns[1].Messages[0].Text);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedTurnIsPersistedRestoredAndReplayed(bool hasPartialResponse)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryTranscriptStore();
        var original = new AgentSession(new ScriptedChatClient(
            (_, token) => InterruptedResponseAsync(hasPartialResponse, waiting, token)), transcriptStore: store);

        await using var run = await original.StartTurnAsync(AgentTurn.FromText("interrupted question"), deadline.Token);
        var events = ReadEventsAsync(run, deadline.Token);
        await waiting.Task.WaitAsync(deadline.Token);
        await run.CancelAsync(deadline.Token);
        await events;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Completion);

        var client = new ScriptedChatClient((_, _) => StreamAsync("continued"));
        var resumed = AgentSession.Resume(new FixedProfileProvider(client), store, original.Id);
        var turn = Assert.Single(resumed.GetTranscriptSnapshot().Turns);
        Assert.Equal(run.Id, turn.RunId);
        Assert.True(turn.Truncated);
        Assert.Equal(hasPartialResponse ? 2 : 1, turn.Messages.Count);
        Assert.Equal("interrupted question", turn.Messages[0].Text);
        Assert.Equal(1, resumed.GetTranscriptSnapshot().Version);

        await using var next = await resumed.StartTurnAsync(AgentTurn.FromText("continue"), deadline.Token);
        await ReadEventsAsync(next, deadline.Token);
        await next.Completion.WaitAsync(deadline.Token);
        Assert.Equal(hasPartialResponse
                ? new[] { "interrupted question", "partial", "continue" }
                : new[] { "interrupted question", "continue" },
            Assert.Single(client.Requests).Select(message => message.Text));
        Assert.Equal(2, store.LoadTranscript(original.Id)?.Version);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> InterruptedResponseAsync(
        bool hasPartialResponse,
        TaskCompletionSource waiting,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (hasPartialResponse) yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
        waiting.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    [Fact]
    public void ResumeUnknownSessionThrowsTyped()
    {
        var provider = new FixedProfileProvider(new ScriptedChatClient());
        var store = new InMemoryTranscriptStore();

        Assert.ThrowsAny<AgentSessionNotFoundException>(() => AgentSession.Resume(provider, store, AgentSessionId.Create()));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoredHistoryIsWindowedBeforeTheFirstProviderRequest(bool fork)
    {
        var id = AgentSessionId.Create();
        var store = new InMemoryTranscriptStore();
        foreach (var text in new[] { "old", "recent" })
            store.AppendTurn(id, new AgentTranscriptTurn(AgentRunId.Create(),
                [new ChatMessage(ChatRole.User, text), new ChatMessage(ChatRole.Assistant, "answer")]), []);

        var client = new ScriptedChatClient((_, _) => StreamAsync("next"));
        var provider = new FixedProfileProvider(new AgentRunProfile(client,
            new AgentSessionOptions { MaxRetainedTurns = 1 }));
        var restored = fork
            ? AgentSession.Fork(provider, store, id, AgentSessionId.Create(), 2)
            : AgentSession.Resume(provider, store, id);
        var before = restored.GetTranscriptSnapshot();

        await using var run = await restored.StartTurnAsync(AgentTurn.FromText("new"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        await run.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "recent", "answer", "new" }, Assert.Single(client.Requests).Select(message => message.Text));
        Assert.Equal(2, before.Turns.Length);
        Assert.Equal(fork ? 2 : 3, store.LoadTranscript(id)?.Version);
    }

    [Fact(Timeout = 30_000)]
    public async Task RestoredHistoryHonorsTheByteBudgetBeforeProviderInvocation()
    {
        var id = AgentSessionId.Create();
        var store = new InMemoryTranscriptStore();
        ChatMessage[] recent = [new(ChatRole.User, "recent"), new(ChatRole.Assistant, "answer")];
        store.AppendTurn(id, new AgentTranscriptTurn(AgentRunId.Create(),
            [new ChatMessage(ChatRole.User, new string('x', 2_000)), new ChatMessage(ChatRole.Assistant, "old")]), []);
        store.AppendTurn(id, new AgentTranscriptTurn(AgentRunId.Create(), recent), []);
        var client = new ScriptedChatClient((_, _) => StreamAsync("next"));
        var provider = new FixedProfileProvider(new AgentRunProfile(client,
            new AgentSessionOptions { MaxHistoryBytes = AgentTranscriptEncoding.Encode(recent).Length }));
        var restored = AgentSession.Resume(provider, store, id);

        await using var run = await restored.StartTurnAsync(AgentTurn.FromText("new"), TestContext.Current.CancellationToken);
        await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        await run.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "recent", "answer", "new" }, Assert.Single(client.Requests).Select(message => message.Text));
    }

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

    private sealed class InMemoryTranscriptStore : IAgentTranscriptStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, List<AgentTranscriptTurn>> _sessions = new(StringComparer.Ordinal);

        public void AppendTurn(AgentSessionId sessionId, AgentTranscriptTurn turn, IReadOnlyList<string> objectReferences)
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(sessionId.Value.ToString("N"), out var turns))
                {
                    turns = [];
                    _sessions[sessionId.Value.ToString("N")] = turns;
                }

                turns.Add(new AgentTranscriptTurn(
                    turn.RunId,
                    turn.Messages.Select(message => message.Clone()).ToArray(),
                    turn.ModelIdentity,
                    turn.Truncated));
            }
        }

        public AgentTranscript? LoadTranscript(AgentSessionId sessionId)
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(sessionId.Value.ToString("N"), out var turns)) return null;

                return new AgentTranscript(sessionId, turns.Count, turns.ToImmutableArray());
            }
        }

        public IReadOnlyList<AgentSessionDescriptor> ListSessions()
        {
            lock (_gate)
            {
                return _sessions
                    .Select(pair => new AgentSessionDescriptor(
                        new AgentSessionId(Guid.ParseExact(pair.Key, "N")),
                        DateTimeOffset.MinValue,
                        DateTimeOffset.MinValue,
                        pair.Value.Count))
                    .ToArray();
            }
        }
    }
}
