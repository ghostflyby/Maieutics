using Maieutics.Agent;
using Maieutics.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>Product tests for the SQLite Agent transcript store: durable round trips across
/// reopen, session metadata, and schema-version refusal (ADR 0009 metadata store, v1).</summary>
public sealed class SqliteTranscriptStoreTests : IAsyncDisposable
{
    private readonly string _databaseDirectory;
    private readonly string _databasePath;

    public SqliteTranscriptStoreTests()
    {
        _databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "maieutics-transcript-store-tests",
            Guid.NewGuid().ToString("N"));
        _databasePath = Path.Combine(_databaseDirectory, "history.db");
    }

    /// <summary>A WAL database's last handle can outlive the store's disposal by a moment
    /// (Windows releases the file asynchronously, and a scanner may still hold it), so the
    /// directory is removed through the shared retrying cleanup instead of a bare recursive
    /// delete that turns that window into a cleanup failure.</summary>
    public async ValueTask DisposeAsync()
    {
        await TempArea.DeleteAsync(_databaseDirectory, CancellationToken.None);
    }

    [Fact]
    public void AppendedObjectReferencesFeedGarbageCollection()
    {
        var sessionId = AgentSessionId.Create();
        var turn = Turn("a", identity: null, truncated: false,
            ("user", "Question"), ("assistant", "Answer"));
        var referenced = new string('b', 64);
        var alsoReferenced = new string('c', 64);

        using (var store = new SqliteTranscriptStore(_databasePath))
        {
            store.AppendTurn(sessionId, turn, [referenced, alsoReferenced, referenced]);
            store.AppendTurn(sessionId, turn, []);
        }

        using var reopened = new SqliteTranscriptStore(_databasePath);
        var references = reopened.GetReferencedObjectIds();
        Assert.Equivalent(new[] { referenced, alsoReferenced }, references);
    }

    [Fact]
    public void MigratesVersionOneDatabasesToTheCurrentSchema()
    {
        Directory.CreateDirectory(_databaseDirectory);
        using (var legacy = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
        {
            legacy.Open();
            using var script = legacy.CreateCommand();
            script.CommandText = """
                CREATE TABLE sessions (id TEXT PRIMARY KEY, created_at TEXT NOT NULL,
                    last_activity_at TEXT NOT NULL, turn_count INTEGER NOT NULL);
                CREATE TABLE turns (session_id TEXT NOT NULL REFERENCES sessions(id), seq INTEGER NOT NULL,
                    run_id TEXT NOT NULL, truncated INTEGER NOT NULL, profile_id TEXT, model_provider TEXT,
                    model_name TEXT, messages BLOB NOT NULL, byte_count INTEGER NOT NULL,
                    created_at TEXT NOT NULL, PRIMARY KEY (session_id, seq));
                INSERT INTO sessions VALUES('00000000000000000000000000000001', '2026-01-01T00:00:00.0000000+00:00',
                    '2026-01-01T00:00:00.0000000+00:00', 0);
                PRAGMA user_version=1;
                """;
            script.ExecuteNonQuery();
        }

        using var store = new SqliteTranscriptStore(_databasePath);
        Assert.Single(store.ListSessions());
        using var check = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        check.Open();
        using var version = check.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(4, Convert.ToInt32(version.ExecuteScalar()));
    }

    [Fact]
    public void MigratesVersionThreeDatabasesWithoutLosingMetadata()
    {
        Directory.CreateDirectory(_databaseDirectory);
        var sessionId = AgentSessionId.Create();
        using (var v3 = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
        {
            v3.Open();
            using var script = v3.CreateCommand();
            script.CommandText = $"""
                CREATE TABLE sessions (id TEXT PRIMARY KEY, created_at TEXT NOT NULL,
                    last_activity_at TEXT NOT NULL, turn_count INTEGER NOT NULL, title TEXT,
                    preview TEXT, workspace_root TEXT);
                CREATE TABLE turns (session_id TEXT NOT NULL REFERENCES sessions(id), seq INTEGER NOT NULL,
                    run_id TEXT NOT NULL, truncated INTEGER NOT NULL, profile_id TEXT, model_provider TEXT,
                    model_name TEXT, messages BLOB NOT NULL, byte_count INTEGER NOT NULL,
                    created_at TEXT NOT NULL, PRIMARY KEY (session_id, seq));
                CREATE TABLE blob_refs (session_id TEXT NOT NULL REFERENCES sessions(id), seq INTEGER NOT NULL,
                    sha256 TEXT NOT NULL, PRIMARY KEY (session_id, seq, sha256));
                INSERT INTO sessions VALUES('{sessionId.Value.ToString("N")}', '2026-01-01T00:00:00.0000000+00:00',
                    '2026-01-02T00:00:00.0000000+00:00', 0, 'v3 title', NULL, '/repos/v3');
                PRAGMA user_version=3;
                """;
            script.ExecuteNonQuery();
        }

        using var store = new SqliteTranscriptStore(_databasePath);
        var descriptor = Assert.Single(store.ListSessions());
        Assert.Equal(sessionId, descriptor.Id);
        Assert.Equal("v3 title", descriptor.Title);
        Assert.Equal("/repos/v3", descriptor.WorkspaceRoot);
        Assert.Null(descriptor.ParentSessionId);
        Assert.Null(descriptor.ForkPointSeq);
    }

    [Fact]
    public void MigratesVersionTwoDatabasesWithoutLosingMetadata()
    {
        Directory.CreateDirectory(_databaseDirectory);
        var sessionId = AgentSessionId.Create();
        using (var v2 = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
        {
            v2.Open();
            using var script = v2.CreateCommand();
            script.CommandText = $"""
                CREATE TABLE sessions (id TEXT PRIMARY KEY, created_at TEXT NOT NULL,
                    last_activity_at TEXT NOT NULL, turn_count INTEGER NOT NULL);
                CREATE TABLE turns (session_id TEXT NOT NULL REFERENCES sessions(id), seq INTEGER NOT NULL,
                    run_id TEXT NOT NULL, truncated INTEGER NOT NULL, profile_id TEXT, model_provider TEXT,
                    model_name TEXT, messages BLOB NOT NULL, byte_count INTEGER NOT NULL,
                    created_at TEXT NOT NULL, PRIMARY KEY (session_id, seq));
                CREATE TABLE blob_refs (session_id TEXT NOT NULL REFERENCES sessions(id), seq INTEGER NOT NULL,
                    sha256 TEXT NOT NULL, PRIMARY KEY (session_id, seq, sha256));
                INSERT INTO sessions VALUES('{sessionId.Value.ToString("N")}', '2026-01-01T00:00:00.0000000+00:00',
                    '2026-01-02T00:00:00.0000000+00:00', 1);
                PRAGMA user_version=2;
                """;
            script.ExecuteNonQuery();
        }

        using var store = new SqliteTranscriptStore(_databasePath);
        var descriptor = Assert.Single(store.ListSessions());
        Assert.Equal(sessionId, descriptor.Id);
        Assert.Equal(1, descriptor.TurnCount);
        Assert.Null(descriptor.Title);
        Assert.Null(descriptor.Preview);
        Assert.Null(descriptor.WorkspaceRoot);
    }

    [Fact]
    public void FirstTurnStampsPreviewAndWorkspaceRootWriteOnce()
    {
        var sessionId = AgentSessionId.Create();
        var identity = new AgentModelIdentity(new AgentModelProfileId("default"), "OpenAI", "gpt-test");
        // The accessor moves between calls: a rewrite of the write-once stamp on
        // the second turn would surface as the second root winning.
        var calls = 0;
        using var store = new SqliteTranscriptStore(_databasePath, () => calls++ == 0 ? "/repos/alpha" : "/repos/beta");

        store.AppendTurn(sessionId, Turn("a", identity, truncated: false,
            ("user", "First\nquestion\r\nspans lines"), ("assistant", "Answer")), []);
        store.AppendTurn(sessionId, Turn("b", identity: null, truncated: false,
            ("user", "Second question"), ("assistant", "Answer two")), []);

        var descriptor = Assert.Single(store.ListSessions());
        Assert.Equal("First question spans lines", descriptor.Preview);
        Assert.Equal("/repos/alpha", descriptor.WorkspaceRoot);
    }

    [Fact]
    public void PreviewIsTruncatedToTheDisplayCap()
    {
        var sessionId = AgentSessionId.Create();
        using var store = new SqliteTranscriptStore(_databasePath);
        store.AppendTurn(sessionId, Turn("a", identity: null, truncated: false,
            ("user", new string('x', 500)), ("assistant", "Answer")), []);

        var descriptor = Assert.Single(store.ListSessions());
        Assert.NotNull(descriptor.Preview);
        Assert.Equal(SqliteTranscriptStore.MaxDisplayTextLength, (descriptor.Preview ?? throw new InvalidOperationException("the assertion subject is null")).Length);
        Assert.EndsWith("…", descriptor.Preview);
    }

    [Fact]
    public void SetTitleUpdatesExistingSessionsAndCreatesZeroTurnRowsForUnknownOnes()
    {
        var committed = AgentSessionId.Create();
        var unknown = AgentSessionId.Create();
        using var store = new SqliteTranscriptStore(_databasePath, () => "/repos/alpha");
        store.AppendTurn(committed, Turn("a", identity: null, truncated: false,
            ("user", "Question"), ("assistant", "Answer")), []);

        store.SetTitle(committed, "Refactor vfs lenses");
        store.SetTitle(unknown, "Named before first turn");

        var descriptors = store.ListSessions();
        Assert.Equal(2, descriptors.Count());
        Assert.Equal("Refactor vfs lenses", descriptors.Single(session => session.Id == committed).Title);
        Assert.Equal("Question", descriptors.Single(session => session.Id == committed).Preview);
        var named = descriptors.Single(session => session.Id == unknown);
        Assert.Equal(0, named.TurnCount);
        Assert.Equal("Named before first turn", named.Title);
        Assert.Null(named.Preview);
        Assert.Equal("/repos/alpha", named.WorkspaceRoot);
        Assert.True(named.CreatedAt <= named.LastActivityAt);

        // Clearing stores null.
        store.SetTitle(committed, null);
        Assert.Null(store.ListSessions().Single(session => session.Id == committed).Title);
    }

    [Fact]
    public void FamilyDatabasePathJoinsTheFamilyDirectory()
    {
        var familyId = AgentSessionId.Create();
        var path = SqliteTranscriptStore.FamilyDatabasePath("/data/agent/families", familyId);
        Assert.Equal($"/data/agent/families/{familyId.Value.ToString("N")}/history.db", path.Replace('\\', '/'));
    }

    [Fact]
    public void AppendedTurnsSurviveReopenWithCanonicalContent()
    {
        var sessionId = AgentSessionId.Create();
        var identity = new AgentModelIdentity(new AgentModelProfileId("default"), "OpenAI", "gpt-test");
        var first = Turn(
            "a",
            identity,
            truncated: false,
            ("user", "Question one"),
            ("assistant", "Answer one"));
        var second = Turn(
            "b",
            identity: null,
            truncated: true,
            ("user", "Question two"),
            ("assistant", "Answer two"));

        using (var store = new SqliteTranscriptStore(_databasePath))
        {
            store.AppendTurn(sessionId, first, []);
            store.AppendTurn(sessionId, second, []);
        }

        using var reopened = new SqliteTranscriptStore(_databasePath);
        var transcript = reopened.LoadTranscript(sessionId);
        Assert.NotNull(transcript);
        Assert.Equal(sessionId, transcript.SessionId);
        Assert.Equal(2, transcript.Turns.Count());
        Assert.Equal(first.RunId, transcript.Turns[0].RunId);
        Assert.Equal(identity, transcript.Turns[0].ModelIdentity);
        Assert.False(transcript.Turns[0].Truncated);
        Assert.Equal(["Question one", "Answer one"], transcript.Turns[0].Messages.Select(message => message.Text));
        Assert.Null(transcript.Turns[1].ModelIdentity);
        Assert.True(transcript.Turns[1].Truncated);
        Assert.Equal(["Question two", "Answer two"], transcript.Turns[1].Messages.Select(message => message.Text));

        var sessions = reopened.ListSessions();
        var item = Assert.Single(sessions);
        Assert.Equal(sessionId, item.Id);
        Assert.Equal(2, sessions[0].TurnCount);
        Assert.True(sessions[0].LastActivityAt >= sessions[0].CreatedAt);
    }

    [Fact]
    public void RetainsProviderReasoningContentAcrossTheStore()
    {
        var sessionId = AgentSessionId.Create();
        var assistant = new ChatMessage(ChatRole.Assistant, [
            new TextReasoningContent("private reasoning summary"),
            new TextContent("public answer"),
        ]);
        var turn = new AgentTranscriptTurn(
            AgentRunId.Create(),
            [new ChatMessage(ChatRole.User, "ask"), assistant],
            modelIdentity: null);

        using var store = new SqliteTranscriptStore(_databasePath);
        store.AppendTurn(sessionId, turn, []);
        var loaded = store.LoadTranscript(sessionId);

        Assert.NotNull(loaded);
        var contents = loaded.Turns[0].Messages[^1].Contents;
        Assert.Equal("private reasoning summary", Assert.Single(contents.OfType<TextReasoningContent>()).Text);
        Assert.Equal("public answer", Assert.Single(contents.OfType<TextContent>()).Text);
    }

    [Fact]
    public void UnknownSessionsLoadAsAbsent()
    {
        using var store = new SqliteTranscriptStore(_databasePath);
        Assert.Null(store.LoadTranscript(AgentSessionId.Create()));
        Assert.Empty(store.ListSessions());
    }

    [Fact]
    public void CreateForkSessionPersistsLineageAndLoadWalksTheChain()
    {
        var root = AgentSessionId.Create();
        var fork = AgentSessionId.Create();
        var identity = new AgentModelIdentity(new AgentModelProfileId("default"), "OpenAI", "gpt-test");
        using var store = new SqliteTranscriptStore(_databasePath, () => "/repos/alpha");
        store.AppendTurn(root, Turn("a", identity, truncated: false,
            ("user", "Question one"), ("assistant", "Answer one")), []);
        store.AppendTurn(root, Turn("b", identity: null, truncated: false,
            ("user", "Question two"), ("assistant", "Answer two")), []);
        store.AppendTurn(root, Turn("c", identity: null, truncated: false,
            ("user", "Question three"), ("assistant", "Answer three")), []);

        store.CreateForkSession(fork, root, forkPointSeq: 2, title: "root · branch @ turn 3");

        // The fork sees the prefix; the root keeps its whole history.
        var forkTranscript = store.LoadTranscript(fork);
        Assert.NotNull(forkTranscript);
        Assert.Equal(fork, forkTranscript.SessionId);
        Assert.Equal(2, forkTranscript.Turns.Select(turn => turn.RunId).Count());
        Assert.Equal("Question one", forkTranscript.Turns[0].Messages[0].Text);
        Assert.Equal("Answer two", forkTranscript.Turns[1].Messages[^1].Text);
        Assert.Equal(3, store.LoadTranscript(root)!.Turns.Count());

        // The fork's own turns append after the prefix.
        store.AppendTurn(fork, Turn("d", identity: null, truncated: false,
            ("user", "Fork question"), ("assistant", "Fork answer")), []);
        var grown = store.LoadTranscript(fork)!.Turns;
        Assert.Equal(3, grown.Count());
        Assert.Equal("Fork question", grown[2].Messages[0].Text);

        var descriptor = store.ListSessions().Single(session => session.Id == fork);
        Assert.Equal(root, descriptor.ParentSessionId);
        Assert.Equal(2, descriptor.ForkPointSeq);
        Assert.Equal("root · branch @ turn 3", descriptor.Title);
        Assert.Equal(1, descriptor.TurnCount);
    }

    [Fact]
    public void ForkOfForkLoadsTheDeepChain()
    {
        var root = AgentSessionId.Create();
        var forkA = AgentSessionId.Create();
        var forkB = AgentSessionId.Create();
        using var store = new SqliteTranscriptStore(_databasePath);
        foreach (var suffix in new[] { "a", "b", "c" })
        {
            store.AppendTurn(root, Turn(suffix, identity: null, truncated: false,
                ("user", $"Question {suffix}"), ("assistant", $"Answer {suffix}")), []);
        }

        store.CreateForkSession(forkA, root, forkPointSeq: 2, title: null);
        store.AppendTurn(forkA, Turn("d", identity: null, truncated: false,
            ("user", "Question d"), ("assistant", "Answer d")), []);
        // forkB keeps all three of forkA's visible turns.
        store.CreateForkSession(forkB, forkA, forkPointSeq: 3, title: null);

        var loaded = store.LoadTranscript(forkB)!.Turns;
        Assert.Equal(3, loaded.Count());
        Assert.Equal(["Question a", "Question b", "Question d"], loaded.Select(turn => turn.Messages[0].Text));
        Assert.Equal(forkA, store.ListSessions().Single(session => session.Id == forkB)
            .ParentSessionId);
    }

    [Fact]
    public void ZeroTurnRowsLoadAsEmptyTranscripts()
    {
        var renamed = AgentSessionId.Create();
        var freshFork = AgentSessionId.Create();
        using var store = new SqliteTranscriptStore(_databasePath);
        store.SetTitle(renamed, "Named before first turn");
        store.CreateForkSession(freshFork, renamed, forkPointSeq: 0, title: null);

        var forkTranscript = store.LoadTranscript(freshFork);
        Assert.NotNull(forkTranscript);
        Assert.Equal(freshFork, forkTranscript.SessionId);
        Assert.Empty(forkTranscript.Turns);
        Assert.Empty(store.LoadTranscript(renamed)!.Turns);
    }

    [Fact]
    public void CyclicForkParentLinksFailTyped()
    {
        // CreateForkSession cannot produce a cycle (the parent row must exist
        // first), so this simulates external corruption of the parent links.
        var a = AgentSessionId.Create();
        var b = AgentSessionId.Create();
        using var store = new SqliteTranscriptStore(_databasePath);
        using (var raw = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
        {
            raw.Open();
            using var insert = raw.CreateCommand();
            insert.CommandText = $"""
                INSERT INTO sessions(id, created_at, last_activity_at, turn_count, parent_session_id, fork_point_seq)
                VALUES('{a.Value.ToString("N")}', '2026-01-01T00:00:00.0000000+00:00',
                    '2026-01-01T00:00:00.0000000+00:00', 0, '{b.Value.ToString("N")}', 1);
                INSERT INTO sessions(id, created_at, last_activity_at, turn_count, parent_session_id, fork_point_seq)
                VALUES('{b.Value.ToString("N")}', '2026-01-01T00:00:00.0000000+00:00',
                    '2026-01-01T00:00:00.0000000+00:00', 0, '{a.Value.ToString("N")}', 1);
                """;
            insert.ExecuteNonQuery();
        }

        var assertion33 = Assert.ThrowsAny<InvalidOperationException>(() => store.LoadTranscript(a));
        Assert.Matches("(?is)^.*exceeds\\ 64.*$", assertion33.Message);
    }

    [Fact]
    public void DanglingForkParentFailsTyped()
    {
        var missing = AgentSessionId.Create().Value.ToString("N");
        var fork = AgentSessionId.Create();
        using var store = new SqliteTranscriptStore(_databasePath);
        using (var raw = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
        {
            raw.Open();
            using var insert = raw.CreateCommand();
            insert.CommandText =
                $"INSERT INTO sessions(id, created_at, last_activity_at, turn_count, parent_session_id, fork_point_seq) " +
                $"VALUES('{fork.Value:N}', '2026-01-01T00:00:00.0000000+00:00', " +
                $"'2026-01-01T00:00:00.0000000+00:00', 0, '{missing}', 1);";
            insert.ExecuteNonQuery();
        }

        var assertion34 = Assert.ThrowsAny<InvalidOperationException>(() => store.LoadTranscript(fork));
        Assert.Matches("(?is)^.*has\\ no\\ session\\ row.*$", assertion34.Message);
    }

    [Fact]
    public void ForkPreviewArrivesWithTheFirstOwnTurn()
    {
        var root = AgentSessionId.Create();
        var fork = AgentSessionId.Create();
        using var store = new SqliteTranscriptStore(_databasePath, () => "/repos/alpha");
        store.AppendTurn(root, Turn("a", identity: null, truncated: false,
            ("user", "Original question"), ("assistant", "Answer")), []);
        store.CreateForkSession(fork, root, forkPointSeq: 0, title: null);

        Assert.Null(store.ListSessions().Single(session => session.Id == fork).Preview);
        store.AppendTurn(fork, Turn("b", identity: null, truncated: false,
            ("user", "Fork question"), ("assistant", "Fork answer")), []);

        var descriptor = store.ListSessions().Single(session => session.Id == fork);
        Assert.Equal("Fork question", descriptor.Preview);
        Assert.Equal("/repos/alpha", descriptor.WorkspaceRoot);
    }

    [Fact]
    public void RefusesDatabasesWrittenByANewerSchema()
    {
        Directory.CreateDirectory(_databaseDirectory);
        using (var newer = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
        {
            newer.Open();
            using var bump = newer.CreateCommand();
            bump.CommandText = "PRAGMA user_version=99;";
            bump.ExecuteNonQuery();
        }

        var open = () => new SqliteTranscriptStore(_databasePath);
        var assertion35 = Assert.ThrowsAny<InvalidOperationException>(() => open.Invoke());
        Assert.Matches("(?is)^.*newer\\ build.*$", assertion35.Message);
    }

    private static AgentTranscriptTurn Turn(
        string runIdSuffix,
        AgentModelIdentity? identity,
        bool truncated,
        params (string Role, string Text)[] messages)
    {
        var chatMessages = messages.Select(pair => new ChatMessage(
            pair.Role == "user" ? ChatRole.User : ChatRole.Assistant,
            pair.Text)).ToArray();
        return new AgentTranscriptTurn(
            new AgentRunId(RunGuid(runIdSuffix)),
            chatMessages,
            identity,
            truncated);
    }

    private static Guid RunGuid(string suffix) =>
        Guid.Parse($"00000000-0000-0000-0000-{suffix.PadLeft(12, '0')}");
}
