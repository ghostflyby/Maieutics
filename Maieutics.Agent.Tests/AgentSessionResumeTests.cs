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

    [Fact]
    public void ResumeUnknownSessionThrowsTyped()
    {
        var provider = new FixedProfileProvider(new ScriptedChatClient());
        var store = new InMemoryTranscriptStore();

        Assert.ThrowsAny<AgentSessionNotFoundException>(() => AgentSession.Resume(provider, store, AgentSessionId.Create()));
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
