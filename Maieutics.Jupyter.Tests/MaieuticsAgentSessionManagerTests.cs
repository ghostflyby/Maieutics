using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Persistence;
using Microsoft.Extensions.AI;

namespace Maieutics.Jupyter.Tests;

/// <summary>Manual recovery through the session manager: list, resume, start new, the
/// disabled-persistence guard, and object pruning. No automatic restore exists by design.</summary>
public sealed class MaieuticsAgentSessionManagerTests : IDisposable
{
    private readonly string databaseDirectory;
    private readonly string objectsRoot;

    public MaieuticsAgentSessionManagerTests()
    {
        databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "maieutics-session-manager-tests",
            Guid.NewGuid().ToString("N"));
        objectsRoot = Path.Combine(databaseDirectory, "objects");
    }

    public void Dispose()
    {
        // Windows can keep the database handle briefly alive after the last
        // connection closes (WAL teardown, antivirus scanning); retry so a
        // teardown lock never masks the test result.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(databaseDirectory))
                {
                    Directory.Delete(databaseDirectory, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(200);
            }
        }
    }

    [Fact]
    public void PruneObjectsRemovesOnlyUnreferencedObjectsPastTheGrace()
    {
        var sessionId = AgentSessionId.Create();
        var objectStore = new ObjectStore(objectsRoot);
        var keep = objectStore.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("keep")));
        var freshOrphan = objectStore.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("fresh")));
        var staleOrphan = objectStore.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("stale")));
        File.SetLastWriteTimeUtc(
            Path.Combine(objectsRoot, staleOrphan.Sha256[..2], staleOrphan.Sha256),
            DateTime.UtcNow - TimeSpan.FromHours(2));

        using (var store = new SqliteTranscriptStore(FamilyPath(sessionId)))
        {
            store.AppendTurn(sessionId, Turn(sessionId, "a", "Question", "Answer"), [keep.Sha256]);
        }

        using var manager = new MaieuticsAgentSessionManager(
            new FixedProfileProvider(),
            databaseDirectory,
            familyId => new SqliteTranscriptStore(FamilyPath(familyId)),
            reclaimer: objectStore);

        manager.PruneObjects(TimeSpan.FromMinutes(30)).Should().Be(1);
        objectStore.Exists(keep.Sha256).Should().BeTrue();
        objectStore.Exists(freshOrphan.Sha256).Should().BeTrue();
        objectStore.Exists(staleOrphan.Sha256).Should().BeFalse();

        // A zero grace period sweeps everything unreferenced.
        manager.PruneObjects(TimeSpan.Zero).Should().Be(1);
        objectStore.Exists(freshOrphan.Sha256).Should().BeFalse();
    }

    [Fact]
    public void DisabledPersistenceGuardsTheRecoverySurface()
    {
        using var manager = new MaieuticsAgentSessionManager(
            new FixedProfileProvider(), familiesRoot: null, storeFactory: null);

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
            store.AppendTurn(sessionId, Turn(sessionId, "a", "Question one", "Answer one"), []);;
            store.AppendTurn(sessionId, Turn(sessionId, "b", "Question two", "Answer two"), []);;
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
            first.AppendTurn(older, Turn(older, "a", "old question", "old answer"), []);;
        }

        using (var second = new SqliteTranscriptStore(FamilyPath(newer)))
        {
            second.AppendTurn(newer, Turn(newer, "b", "new question", "new answer"), []);;
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

    [Fact]
    public void ForkCreatesTheHeadInTheRootFamilyAndActivatesIt()
    {
        var source = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(source)))
        {
            store.AppendTurn(source, Turn(source, "a", "Question one", "Answer one"), []);
            store.AppendTurn(source, Turn(source, "b", "Question two", "Answer two"), []);
        }

        using var manager = CreateManager();
        manager.Resume(source);
        var forkId = manager.Fork(source, forkPointSeq: 1);

        // The fork is active and its history ends at the fork point.
        manager.Id.Should().Be(forkId);
        manager.GetTranscriptSnapshot().Turns.Should().HaveCount(1);
        manager.GetTranscriptSnapshot().Turns[0].Messages[0].Text.Should().Be("Question one");

        // The head row lives in the source's family database with lineage and an
        // auto-derived title.
        var head = manager.FindDescriptor(forkId);
        head.Should().NotBeNull();
        head?.ParentSessionId.Should().Be(source);
        head?.ForkPointSeq.Should().Be(1);
        head?.Title.Should().Be("Question one · branch @ turn 2");
        manager.LoadStoredTranscript(forkId)!.Turns.Should().HaveCount(1);

        // The source session is untouched and still loadable.
        manager.LoadStoredTranscript(source)!.Turns.Should().HaveCount(2);
    }

    [Fact]
    public void ForkOutOfRangePointsAreRejectedTyped()
    {
        var source = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(source)))
        {
            store.AppendTurn(source, Turn(source, "a", "Question one", "Answer one"), []);
        }

        using var manager = CreateManager();
        manager.Invoking(m => m.Fork(source, 2))
            .Should().Throw<ArgumentOutOfRangeException>();
        manager.Invoking(m => m.Fork(source, -1))
            .Should().Throw<ArgumentOutOfRangeException>();
        manager.Invoking(m => m.Fork(AgentSessionId.Create(), 0))
            .Should().Throw<AgentSessionNotFoundException>();

        // A rejected fork leaves no phantom head row behind.
        manager.ListStoredSessions().Should().ContainSingle();

        // Creating a fork head over a missing parent fails typed and writes nothing.
        using (var store = new SqliteTranscriptStore(FamilyPath(source)))
        {
            store.Invoking(s => s.CreateForkSession(
                    AgentSessionId.Create(), AgentSessionId.Create(), 0, null))
                .Should().Throw<InvalidOperationException>().WithMessage("*has no row*");
        }

        manager.ListStoredSessions().Should().ContainSingle();
    }

    [Fact(Timeout = 30_000)]
    public async Task EvictionSkipsSessionsWithRunsInFlight()
    {
        var stored = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(stored)))
        {
            store.AppendTurn(stored, Turn(stored, "a", "Question", "Answer"), []);
        }

        var neverComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = new MaieuticsAgentSessionManager(
            new FixedProfileProvider(new HangingChatClient(neverComplete.Task)),
            databaseDirectory,
            familyId => new SqliteTranscriptStore(FamilyPath(familyId)));

        var session = manager.Resolve(stored);
        var run = await session.StartTurnAsync(AgentTurn.FromText("hang"), CancellationToken.None);
        session.IsRunInProgress.Should().BeTrue();

        // Churn the foreground past the capacity: the running session is never
        // an eviction candidate (a second instance for the same identity would
        // break the single-run gate).
        for (var index = 0; index < MaieuticsAgentSessionManager.LiveSessionCapacity; index++)
        {
            manager.StartNew();
        }

        manager.IsLive(stored).Should().BeTrue();

        await run.DisposeAsync();
        session.IsRunInProgress.Should().BeFalse();
    }

    [Fact]
    public void ResolveEnforcesTheLiveCap()
    {
        var stored = new List<AgentSessionId>();
        for (var index = 0; index < MaieuticsAgentSessionManager.LiveSessionCapacity + 1; index++)
        {
            var id = AgentSessionId.Create();
            stored.Add(id);
            using (var store = new SqliteTranscriptStore(FamilyPath(id)))
            {
                store.AppendTurn(id, Turn(id, (index + 1).ToString(), "Question", "Answer"), []);
            }
        }

        using var manager = CreateManager();
        foreach (var id in stored)
        {
            manager.Resolve(id);
        }

        // The boot session is unresumable (zero turns) and stays, but every
        // resumable session beyond the cap is evicted.
        manager.LiveCount.Should().BeLessThanOrEqualTo(MaieuticsAgentSessionManager.LiveSessionCapacity + 1);
        // The most recently resolved session is still live and complete.
        manager.Resolve(stored[^1]).GetTranscriptSnapshot().Turns.Should().HaveCount(1);
    }

    [Fact]
    public void ResolveLazilyResumesStoredSessionsWithoutMovingTheForeground()
    {
        var stored = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(stored)))
        {
            store.AppendTurn(stored, Turn(stored, "a", "Question", "Answer"), []);
        }

        using var manager = CreateManager();
        var before = manager.Id;

        var resolved = manager.Resolve(stored);
        resolved.Id.Should().Be(stored);
        resolved.GetTranscriptSnapshot().Turns.Should().HaveCount(1);
        // Addressing a session never moves the foreground.
        manager.Id.Should().Be(before);
        manager.LiveCount.Should().Be(2);
    }

    [Fact]
    public void EvictionKeepsResumableSessionsRecoverable()
    {
        var stored = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(stored)))
        {
            store.AppendTurn(stored, Turn(stored, "a", "Question", "Answer"), []);
        }

        using var manager = CreateManager();
        manager.Resolve(stored);

        // Push the live set past its capacity with sessions that have no stored
        // state: they must never be evicted, but the resumable one may be.
        for (var index = 0; index < MaieuticsAgentSessionManager.LiveSessionCapacity; index++)
        {
            manager.StartNew();
        }

        var resolved = manager.Resolve(stored);
        resolved.GetTranscriptSnapshot().Turns.Should().HaveCount(1);
        // The cap is soft by design: the zero-turn siblings cannot be evicted
        // (their state exists nowhere else), so only the overage above the
        // unresumable set is tolerated.
        manager.LiveCount.Should().BeLessThanOrEqualTo(MaieuticsAgentSessionManager.LiveSessionCapacity + 2);
    }

    [Fact]
    public void ZeroTurnForksResumeThroughTheChain()
    {
        var source = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(source)))
        {
            store.AppendTurn(source, Turn(source, "a", "Question one", "Answer one"), []);
        }

        using var manager = CreateManager();
        var forkId = manager.Fork(source, forkPointSeq: 0);
        manager.Id.Should().Be(forkId);
        manager.GetTranscriptSnapshot().Turns.Should().BeEmpty();

        // A zero-turn fork stays resumable: the chain supplies the prefix.
        manager.StartNew();
        manager.Resume(forkId).Should().Be(forkId);
        manager.GetTranscriptSnapshot().Turns.Should().BeEmpty();

        // Resuming the fork's source still restores the full history.
        manager.Resume(source);
        manager.GetTranscriptSnapshot().Turns.Should().HaveCount(1);
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

    private sealed class FixedProfileProvider(IChatClient? chatClient = null) : IAgentRunProfileProvider
    {
        public Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IAgentRunProfileLease>(new Lease(
                new AgentRunProfile(chatClient ?? new StubChatClient(), new AgentSessionOptions())));
        }

        private sealed class Lease(AgentRunProfile profile) : IAgentRunProfileLease
        {
            public AgentRunProfile Profile { get; } = profile;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class HangingChatClient(Task neverComplete) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<ChatResponse>(new NotSupportedException());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await neverComplete.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
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
