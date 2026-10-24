using System.Runtime.CompilerServices;
using System.Text;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

/// <summary>Manual recovery through the session manager: list, resume, start new, the
/// disabled-persistence guard, and object pruning. No automatic restore exists by design.</summary>
public sealed class MaieuticsAgentSessionManagerTests : IAsyncDisposable
{
    private readonly string _databaseDirectory;
    private readonly string _objectsRoot;

    public MaieuticsAgentSessionManagerTests()
    {
        _databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "maieutics-session-manager-tests",
            Guid.NewGuid().ToString("N"));
        _objectsRoot = Path.Combine(_databaseDirectory, "objects");
    }

    public async ValueTask DisposeAsync()
    {
        // WAL teardown and external scanners (antivirus, indexer) can hold the database
        // handle past the last close on Windows; the shared helper retries with an
        // asynchronous backoff so a teardown lock never masks the test result.
        await TempArea.DeleteAsync(_databaseDirectory, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void PruneObjectsRemovesOnlyUnreferencedObjectsPastTheGrace()
    {
        var sessionId = AgentSessionId.Create();
        var objectStore = new ObjectStore(_objectsRoot);
        var keep = objectStore.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("keep")));
        var freshOrphan = objectStore.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("fresh")));
        var staleOrphan = objectStore.Ingest(new MemoryStream(Encoding.UTF8.GetBytes("stale")));
        File.SetLastWriteTimeUtc(
            Path.Combine(_objectsRoot, staleOrphan.Sha256[..2], staleOrphan.Sha256),
            DateTime.UtcNow - TimeSpan.FromHours(2));

        using (var store = new SqliteTranscriptStore(FamilyPath(sessionId)))
        {
            store.AppendTurn(sessionId, Turn("a", "Question", "Answer"), [keep.Sha256]);
        }

        using var manager = new MaieuticsAgentSessionManager(
            new FixedProfileProvider(),
            _databaseDirectory,
            familyId => new SqliteTranscriptStore(FamilyPath(familyId)),
            NullLogger<MaieuticsAgentSessionManager>.Instance,
            reclaimer: objectStore);

        Assert.Equal(1, manager.PruneObjects(TimeSpan.FromMinutes(30)));
        Assert.True(objectStore.Exists(keep.Sha256));
        Assert.True(objectStore.Exists(freshOrphan.Sha256));
        Assert.False(objectStore.Exists(staleOrphan.Sha256));

        // A zero grace period sweeps everything unreferenced.
        Assert.Equal(1, manager.PruneObjects(TimeSpan.Zero));
        Assert.False(objectStore.Exists(freshOrphan.Sha256));
    }

    [Fact]
    public void DisabledPersistenceGuardsTheRecoverySurface()
    {
        using var manager = new MaieuticsAgentSessionManager(
            new FixedProfileProvider(), familiesRoot: null, storeFactory: null,
            NullLogger<MaieuticsAgentSessionManager>.Instance);

        Assert.False(manager.PersistenceEnabled);
        Assert.Empty(manager.ListStoredSessions());
        var assertion13 = Assert.ThrowsAny<ArgumentException>(() => manager.Resume(AgentSessionId.Create()));
        Assert.Matches("(?is)^.*persistence\\ is\\ disabled.*$", assertion13.Message);

        var before = manager.Id;
        Assert.NotEqual(before, manager.StartNew());
        Assert.NotEqual(before, manager.Id);
    }

    [Fact]
    public void ResumeReplacesTheActiveSessionWithTheStoredHistory()
    {
        var sessionId = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(sessionId)))
        {
            store.AppendTurn(sessionId, Turn("a", "Question one", "Answer one"), []);
            store.AppendTurn(sessionId, Turn("b", "Question two", "Answer two"), []);
        }

        using var manager = CreateManager();
        Assert.NotEqual(sessionId, manager.Id);

        Assert.Equal(sessionId, manager.Resume(sessionId));
        Assert.Equal(sessionId, manager.Id);
        Assert.Equal(2, manager.GetTranscriptSnapshot().Turns.Count());
        Assert.Equal("Question one", manager.GetTranscriptSnapshot().Turns[0].Messages[0].Text);
        Assert.Equal("Answer two", manager.GetTranscriptSnapshot().Turns[1].Messages[^1].Text);

        var sessions = manager.ListStoredSessions();
        Assert.Single(sessions);
        Assert.Equal(sessionId, sessions[0].Id);
        Assert.Equal(2, sessions[0].TurnCount);

        var afterNew = manager.StartNew();
        Assert.NotEqual(sessionId, afterNew);
        Assert.Empty(manager.GetTranscriptSnapshot().Turns);
        // A fresh session has no committed turns, so it does not appear in the list.
        Assert.Single(manager.ListStoredSessions());

        manager.Resume(sessionId);
        Assert.Equal(sessionId, manager.Id);
        // Resuming the already active identity is a no-op.
        Assert.Equal(sessionId, manager.Resume(sessionId));
    }

    [Fact]
    public void ListsSessionsAcrossFamilyDatabasesMostRecentFirst()
    {
        var older = AgentSessionId.Create();
        var newer = AgentSessionId.Create();
        using (var first = new SqliteTranscriptStore(FamilyPath(older)))
        {
            first.AppendTurn(older, Turn("a", "old question", "old answer"), []);
        }

        using (var second = new SqliteTranscriptStore(FamilyPath(newer)))
        {
            second.AppendTurn(newer, Turn("b", "new question", "new answer"), []);
        }

        using var manager = CreateManager();
        var sessions = manager.ListStoredSessions();
        Assert.All([older, newer], item => Assert.Contains(item, sessions.Select(session => session.Id)));
        Assert.True(sessions.First().LastActivityAt >= sessions.Last().LastActivityAt);
    }

    [Fact]
    public void ResumeUnknownSessionThrowsTyped()
    {
        using var manager = CreateManager();

        Assert.ThrowsAny<AgentSessionNotFoundException>(() => manager.Resume(AgentSessionId.Create()));
    }

    [Fact]
    public void ForkCreatesTheHeadInTheRootFamilyAndActivatesIt()
    {
        var source = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(source)))
        {
            store.AppendTurn(source, Turn("a", "Question one", "Answer one"), []);
            store.AppendTurn(source, Turn("b", "Question two", "Answer two"), []);
        }

        using var manager = CreateManager();
        manager.Resume(source);
        var forkId = manager.Fork(source, forkPointSeq: 1);

        // The fork is active and its history ends at the fork point.
        Assert.Equal(forkId, manager.Id);
        Assert.Single(manager.GetTranscriptSnapshot().Turns);
        Assert.Equal("Question one", manager.GetTranscriptSnapshot().Turns[0].Messages[0].Text);

        // The head row lives in the source's family database with lineage and an
        // auto-derived title.
        var head = manager.FindDescriptor(forkId);
        Assert.NotNull(head);
        Assert.Equal(source, head.ParentSessionId);
        Assert.Equal(1, head.ForkPointSeq);
        Assert.Equal("Question one · branch @ turn 2", head.Title);
        Assert.Single(manager.LoadStoredTranscript(forkId)!.Turns);

        // The source session is untouched and still loadable.
        Assert.Equal(2, manager.LoadStoredTranscript(source)!.Turns.Count());
    }

    [Fact]
    public void ForkOutOfRangePointsAreRejectedTyped()
    {
        var source = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(source)))
        {
            store.AppendTurn(source, Turn("a", "Question one", "Answer one"), []);
        }

        using var manager = CreateManager();
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => manager.Fork(source, 2));
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => manager.Fork(source, -1));
        Assert.ThrowsAny<AgentSessionNotFoundException>(() => manager.Fork(AgentSessionId.Create(), 0));

        // A rejected fork leaves no phantom head row behind.
        Assert.Single(manager.ListStoredSessions());

        // Creating a fork head over a missing parent fails typed and writes nothing.
        using (var store = new SqliteTranscriptStore(FamilyPath(source)))
        {
            var assertion14 = Assert.ThrowsAny<InvalidOperationException>(() => store.CreateForkSession(
                AgentSessionId.Create(), AgentSessionId.Create(), 0, null));
            Assert.Matches("(?is)^.*has\\ no\\ row.*$", assertion14.Message);
        }

        Assert.Single(manager.ListStoredSessions());
    }

    [Fact(Timeout = 30_000)]
    public async Task EvictionSkipsSessionsWithRunsInFlight()
    {
        var stored = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(stored)))
        {
            store.AppendTurn(stored, Turn("a", "Question", "Answer"), []);
        }

        var neverComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = new MaieuticsAgentSessionManager(
            new FixedProfileProvider(new HangingChatClient(neverComplete.Task)),
            _databaseDirectory,
            familyId => new SqliteTranscriptStore(FamilyPath(familyId)),
            NullLogger<MaieuticsAgentSessionManager>.Instance);

        var session = manager.Resolve(stored);
        var run = await session
            .StartTurnAsync(AgentTurn.FromText("hang"), TestContext.Current.CancellationToken);
        Assert.True(session.IsRunInProgress);

        // Churn the foreground past the capacity: the running session is never
        // an eviction candidate (a second instance for the same identity would
        // break the single-run gate).
        for (var index = 0; index < MaieuticsAgentSessionManager.LiveSessionCapacity; index++)
        {
            manager.StartNew();
        }

        Assert.True(manager.IsLive(stored));

        await run.DisposeAsync();
        Assert.False(session.IsRunInProgress);
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
                store.AppendTurn(id, Turn((index + 1).ToString(), "Question", "Answer"), []);
            }
        }

        using var manager = CreateManager();
        foreach (var id in stored)
        {
            manager.Resolve(id);
        }

        // The boot session is unresumable (zero turns) and stays, but every
        // resumable session beyond the cap is evicted.
        Assert.True(manager.LiveCount <= MaieuticsAgentSessionManager.LiveSessionCapacity + 1);
        // The most recently resolved session is still live and complete.
        Assert.Single(manager.Resolve(stored[^1]).GetTranscriptSnapshot().Turns);
    }

    [Fact]
    public void ResolveLazilyResumesStoredSessionsWithoutMovingTheForeground()
    {
        var stored = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(stored)))
        {
            store.AppendTurn(stored, Turn("a", "Question", "Answer"), []);
        }

        using var manager = CreateManager();
        var before = manager.Id;

        var resolved = manager.Resolve(stored);
        Assert.Equal(stored, resolved.Id);
        Assert.Single(resolved.GetTranscriptSnapshot().Turns);
        // Addressing a session never moves the foreground.
        Assert.Equal(before, manager.Id);
        Assert.Equal(2, manager.LiveCount);
    }

    [Fact]
    public void EvictionKeepsResumableSessionsRecoverable()
    {
        var stored = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(stored)))
        {
            store.AppendTurn(stored, Turn("a", "Question", "Answer"), []);
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
        Assert.Single(resolved.GetTranscriptSnapshot().Turns);
        // The cap is soft by design: the zero-turn siblings cannot be evicted
        // (their state exists nowhere else), so only the overage above the
        // unresumable set is tolerated.
        Assert.True(manager.LiveCount <= MaieuticsAgentSessionManager.LiveSessionCapacity + 2);
    }

    [Fact]
    public void ZeroTurnForksResumeThroughTheChain()
    {
        var source = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(source)))
        {
            store.AppendTurn(source, Turn("a", "Question one", "Answer one"), []);
        }

        using var manager = CreateManager();
        var forkId = manager.Fork(source, forkPointSeq: 0);
        Assert.Equal(forkId, manager.Id);
        Assert.Empty(manager.GetTranscriptSnapshot().Turns);

        // A zero-turn fork stays resumable: the chain supplies the prefix.
        manager.StartNew();
        Assert.Equal(forkId, manager.Resume(forkId));
        Assert.Empty(manager.GetTranscriptSnapshot().Turns);

        // Resuming the fork's source still restores the full history.
        manager.Resume(source);
        Assert.Single(manager.GetTranscriptSnapshot().Turns);
    }

    [Fact]
    public void PinnedSessionsAreNotEvictedUntilTheLeaseIsReleased()
    {
        var stored = AgentSessionId.Create();
        using (var store = new SqliteTranscriptStore(FamilyPath(stored)))
        {
            store.AppendTurn(stored, Turn("a", "Question", "Answer"), []);
        }

        using var manager = CreateManager();
        manager.Resolve(stored);

        // An unknown session has no live entry to pin.
        Assert.Null(manager.TryPinSession(AgentSessionId.Create()));

        // A queued session holds one cooperative pin lease (ADR 0025): eviction skips the
        // pinned session both at candidacy and at the removal re-check.
        var lease = manager.TryPinSession(stored);
        Assert.NotNull(lease);
        for (var index = 0; index < MaieuticsAgentSessionManager.LiveSessionCapacity; index++)
        {
            manager.StartNew();
        }

        Assert.True(manager.IsLive(stored));

        // Disposing the lease twice releases the pin exactly once; the next eviction pass
        // may evict the session again (the lease is capacity management, not a lifetime
        // guarantee).
        if (lease is { } pin)
        {
            pin.Dispose();
            pin.Dispose();
        }

        for (var index = 0; index < MaieuticsAgentSessionManager.LiveSessionCapacity; index++)
        {
            manager.StartNew();
        }

        Assert.False(manager.IsLive(stored));
    }

    private MaieuticsAgentSessionManager CreateManager()
    {
        return new MaieuticsAgentSessionManager(
            new FixedProfileProvider(),
            _databaseDirectory,
            familyId => new SqliteTranscriptStore(FamilyPath(familyId)),
            NullLogger<MaieuticsAgentSessionManager>.Instance);
    }

    private string FamilyPath(AgentSessionId familyId) =>
        SqliteTranscriptStore.FamilyDatabasePath(_databaseDirectory, familyId);

    private static AgentTranscriptTurn Turn(
        string runIdSuffix,
        string userText,
        string assistantText)
    {
        return new AgentTranscriptTurn(
            new AgentRunId(Guid.Parse($"00000000-0000-0000-0000-{runIdSuffix.PadLeft(12, '0')}")),
            [new ChatMessage(ChatRole.User, userText), new ChatMessage(ChatRole.Assistant, assistantText)]);
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
}
