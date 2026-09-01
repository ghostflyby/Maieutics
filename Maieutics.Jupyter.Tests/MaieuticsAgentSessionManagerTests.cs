using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Jupyter;
using Maieutics.Persistence;
using Microsoft.Extensions.AI;

namespace Maieutics.Jupyter.Tests;

/// <summary>Manual recovery through the session manager: list, resume, start new, and the
/// disabled-persistence guard. No automatic restore exists by design.</summary>
public sealed class MaieuticsAgentSessionManagerTests : IDisposable
{
    private readonly string databaseDirectory;

    public MaieuticsAgentSessionManagerTests()
    {
        databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "maieutics-session-manager-tests",
            Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(databaseDirectory))
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public void DisabledPersistenceGuardsTheRecoverySurface()
    {
        using var manager = new MaieuticsAgentSessionManager(
            new FixedProfileProvider(), sessionsRoot: null, storeFactory: null);

        manager.PersistenceEnabled.Should().BeFalse();
        manager.ListStoredSessions().Should().BeEmpty();
        manager.Invoking(m => m.Resume(AgentSessionId.Create()))
            .Should().Throw<ArgumentException>().WithMessage("*persistence is disabled*");

        var before = manager.Id;
        manager.StartNew().Should().NotBe(before);
        manager.Id.Should().NotBe(before);
    }

    [Fact]
    public void ResumeReplacesTheActiveSessionWithTheStoredHistory()
    {
        var sessionId = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(sessionId)))
        {
            store.AppendTurn(sessionId, Turn(sessionId, "a", "Question one", "Answer one"));
            store.AppendTurn(sessionId, Turn(sessionId, "b", "Question two", "Answer two"));
        }

        using var manager = CreateManager();
        manager.Id.Should().NotBe(sessionId);

        manager.Resume(sessionId).Should().Be(sessionId);
        manager.Id.Should().Be(sessionId);
        manager.GetTranscriptSnapshot().Turns.Should().HaveCount(2);
        manager.GetTranscriptSnapshot().Turns[0].Messages[0].Text.Should().Be("Question one");
        manager.GetTranscriptSnapshot().Turns[1].Messages[^1].Text.Should().Be("Answer two");

        var sessions = manager.ListStoredSessions();
        sessions.Should().ContainSingle();
        sessions[0].Id.Should().Be(sessionId);
        sessions[0].TurnCount.Should().Be(2);

        var afterNew = manager.StartNew();
        afterNew.Should().NotBe(sessionId);
        manager.GetTranscriptSnapshot().Turns.Should().BeEmpty();
        // A fresh session has no committed turns, so it does not appear in the list.
        manager.ListStoredSessions().Should().ContainSingle();

        manager.Resume(sessionId);
        manager.Id.Should().Be(sessionId);
        // Resuming the already active identity is a no-op.
        manager.Resume(sessionId).Should().Be(sessionId);
    }

    [Fact]
    public void ListsSessionsAcrossFamilyDatabasesMostRecentFirst()
    {
        var older = AgentSessionId.Create();
        var newer = AgentSessionId.Create();
        using (var first = new SqliteTranscriptStore(FamilyPath(older)))
        {
            first.AppendTurn(older, Turn(older, "a", "old question", "old answer"));
        }

        using (var second = new SqliteTranscriptStore(FamilyPath(newer)))
        {
            second.AppendTurn(newer, Turn(newer, "b", "new question", "new answer"));
        }

        using var manager = CreateManager();
        var sessions = manager.ListStoredSessions();
        sessions.Select(session => session.Id).Should().Contain([older, newer]);
        sessions.First().LastActivityAt.Should().BeOnOrAfter(sessions.Last().LastActivityAt);
    }

    [Fact]
    public void ResumeUnknownSessionThrowsTyped()
    {
        using var manager = CreateManager();

        manager.Invoking(m => m.Resume(AgentSessionId.Create()))
            .Should().Throw<AgentSessionNotFoundException>();
    }

    private MaieuticsAgentSessionManager CreateManager()
    {
        return new MaieuticsAgentSessionManager(
            new FixedProfileProvider(),
            databaseDirectory,
            familyId => new SqliteTranscriptStore(FamilyPath(familyId)));
    }

    private string FamilyPath(AgentSessionId familyId) =>
        SqliteTranscriptStore.FamilyDatabasePath(databaseDirectory, familyId);

    private static AgentTranscriptTurn Turn(
        AgentSessionId sessionId,
        string runIdSuffix,
        string userText,
        string assistantText)
    {
        return new AgentTranscriptTurn(
            new AgentRunId(Guid.Parse($"00000000-0000-0000-0000-{runIdSuffix.PadLeft(12, '0')}")),
            [new ChatMessage(ChatRole.User, userText), new ChatMessage(ChatRole.Assistant, assistantText)]);
    }

    private sealed class FixedProfileProvider : IAgentRunProfileProvider
    {
        public Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IAgentRunProfileLease>(new Lease(new AgentRunProfile(new StubChatClient(), new AgentSessionOptions())));
        }

        private sealed class Lease(AgentRunProfile profile) : IAgentRunProfileLease
        {
            public AgentRunProfile Profile { get; } = profile;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class StubChatClient : IChatClient
    {
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
            throw new NotSupportedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
