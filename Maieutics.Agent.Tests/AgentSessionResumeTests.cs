using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent.Tests;

public sealed class AgentSessionResumeTests
{
    [Fact(Timeout = 30_000)]
    public async Task ResumeRestoresCommittedHistoryAndContinuesAppendingToTheStore()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var sessionId = AgentSessionId.Create();
        var store = new InMemoryTranscriptStore();
        var firstProvider = new FixedProfileProvider(
            new ScriptedChatClient((_, _) => StreamAsync("first")));
        var original = new AgentSession(firstProvider, transcriptStore: store, sessionId: sessionId);

        await using var run = await original.StartTurnAsync(AgentTurn.FromText("one"), deadline.Token);
        await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        var resumedProvider = new FixedProfileProvider(
            new ScriptedChatClient((_, _) => StreamAsync("second")));
        var resumed = AgentSession.Resume(resumedProvider, store, sessionId);

        resumed.Id.Should().Be(sessionId);
        var snapshot = resumed.GetTranscriptSnapshot();
        snapshot.Turns.Should().HaveCount(1);
        snapshot.Turns[0].Messages[0].Text.Should().Be("one");
        snapshot.Turns[0].Messages[^1].Text.Should().Be("first");

        await using var next = await resumed.StartTurnAsync(AgentTurn.FromText("two"), deadline.Token);
        await ReadEventsAsync(next, deadline.Token);
        (await next.Completion.WaitAsync(deadline.Token)).AssistantMessage.Text.Should().Be("second");

        if (store.LoadTranscript(sessionId) is not { } persisted)
        {
            throw new InvalidOperationException("The resumed session's turns were not persisted.");
        }

        persisted.Turns.Should().HaveCount(2);
        persisted.Turns[1].Messages[0].Text.Should().Be("two");
    }

    [Fact]
    public void ResumeUnknownSessionThrowsTyped()
    {
        var provider = new FixedProfileProvider(new ScriptedChatClient());
        var store = new InMemoryTranscriptStore();

        var resume = () => AgentSession.Resume(provider, store, AgentSessionId.Create());
        resume.Should().Throw<AgentSessionNotFoundException>();
    }

    private sealed class FixedProfileProvider(IChatClient client) : IAgentRunProfileProvider
    {
        public Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IAgentRunProfileLease>(
                new Lease(new AgentRunProfile(client, new AgentSessionOptions())));
        }

        private sealed class Lease(AgentRunProfile profile) : IAgentRunProfileLease
        {
            public AgentRunProfile Profile { get; } = profile;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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

    private sealed class InMemoryTranscriptStore : IAgentTranscriptStore
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, List<AgentTranscriptTurn>> sessions = new(StringComparer.Ordinal);

        public void AppendTurn(AgentSessionId sessionId, AgentTranscriptTurn turn)
        {
            lock (gate)
            {
                if (!sessions.TryGetValue(sessionId.Value.ToString("N"), out var turns))
                {
                    turns = [];
                    sessions[sessionId.Value.ToString("N")] = turns;
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
            lock (gate)
            {
                if (!sessions.TryGetValue(sessionId.Value.ToString("N"), out var turns)) return null;

                return new AgentTranscript(sessionId, turns.Count, turns.ToImmutableArray());
            }
        }

        public IReadOnlyList<AgentSessionDescriptor> ListSessions()
        {
            lock (gate)
            {
                return sessions
                    .Select(pair => new AgentSessionDescriptor(
                        new AgentSessionId(Guid.ParseExact(pair.Key, "N")),
                        DateTimeOffset.MinValue,
                        DateTimeOffset.MinValue,
                        pair.Value.Count))
                    .ToArray();
            }
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