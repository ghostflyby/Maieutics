using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>Product tests for the SQLite Agent transcript store: durable round trips across
/// reopen, session metadata, and schema-version refusal (ADR 0009 metadata store, v1).</summary>
public sealed class SqliteTranscriptStoreTests : IDisposable
{
    private readonly string databaseDirectory;
    private readonly string databasePath;

    public SqliteTranscriptStoreTests()
    {
        databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "maieutics-transcript-store-tests",
            Guid.NewGuid().ToString("N"));
        databasePath = Path.Combine(databaseDirectory, "history.db");
    }

    public void Dispose()
    {
        if (Directory.Exists(databaseDirectory))
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public void AppendedObjectReferencesFeedGarbageCollection()
    {
        var sessionId = AgentSessionId.Create();
        var turn = Turn(sessionId, "a", identity: null, truncated: false,
            ("user", "Question"), ("assistant", "Answer"));
        var referenced = new string('b', 64);
        var alsoReferenced = new string('c', 64);

        using (var store = new SqliteTranscriptStore(databasePath))
        {
            store.AppendTurn(sessionId, turn, [referenced, alsoReferenced, referenced]);
            store.AppendTurn(sessionId, turn, []);
        }

        using var reopened = new SqliteTranscriptStore(databasePath);
        var references = reopened.GetReferencedObjectIds();
        references.Should().BeEquivalentTo([referenced, alsoReferenced]);
    }

    [Fact]
    public void MigratesVersionOneDatabasesToTheCurrentSchema()
    {
        Directory.CreateDirectory(databaseDirectory);
        using (var legacy = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
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

        using var store = new SqliteTranscriptStore(databasePath);
        store.ListSessions().Should().ContainSingle();
        using var check = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        check.Open();
        using var version = check.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Convert.ToInt32(version.ExecuteScalar()).Should().Be(4);
    }

    [Fact]
    public void MigratesVersionThreeDatabasesWithoutLosingMetadata()
    {
        Directory.CreateDirectory(databaseDirectory);
        var sessionId = AgentSessionId.Create();
        using (var v3 = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
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

        using var store = new SqliteTranscriptStore(databasePath);
        var descriptor = store.ListSessions().Should().ContainSingle().Subject;
        descriptor.Id.Should().Be(sessionId);
        descriptor.Title.Should().Be("v3 title");
        descriptor.WorkspaceRoot.Should().Be("/repos/v3");
        descriptor.ParentSessionId.Should().BeNull();
        descriptor.ForkPointSeq.Should().BeNull();
    }

    [Fact]
    public void MigratesVersionTwoDatabasesWithoutLosingMetadata()
    {
        Directory.CreateDirectory(databaseDirectory);
        var sessionId = AgentSessionId.Create();
        using (var v2 = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
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

        using var store = new SqliteTranscriptStore(databasePath);
        var descriptor = store.ListSessions().Should().ContainSingle().Subject;
        descriptor.Id.Should().Be(sessionId);
        descriptor.TurnCount.Should().Be(1);
        descriptor.Title.Should().BeNull();
        descriptor.Preview.Should().BeNull();
        descriptor.WorkspaceRoot.Should().BeNull();
    }

    [Fact]
    public void FirstTurnStampsPreviewAndWorkspaceRootWriteOnce()
    {
        var sessionId = AgentSessionId.Create();
        var identity = new AgentModelIdentity(new AgentModelProfileId("default"), "OpenAI", "gpt-test");
        // The accessor moves between calls: a rewrite of the write-once stamp on
        // the second turn would surface as the second root winning.
        var calls = 0;
        using var store = new SqliteTranscriptStore(databasePath, () => calls++ == 0 ? "/repos/alpha" : "/repos/beta");

        store.AppendTurn(sessionId, Turn(sessionId, "a", identity, truncated: false,
            ("user", "First\nquestion\r\nspans lines"), ("assistant", "Answer")), []);
        store.AppendTurn(sessionId, Turn(sessionId, "b", identity: null, truncated: false,
            ("user", "Second question"), ("assistant", "Answer two")), []);

        var descriptor = store.ListSessions().Should().ContainSingle().Subject;
        descriptor.Preview.Should().Be("First question spans lines");
        descriptor.WorkspaceRoot.Should().Be("/repos/alpha");
    }

    [Fact]
    public void PreviewIsTruncatedToTheDisplayCap()
    {
        var sessionId = AgentSessionId.Create();
        using var store = new SqliteTranscriptStore(databasePath);
        store.AppendTurn(sessionId, Turn(sessionId, "a", identity: null, truncated: false,
            ("user", new string('x', 500)), ("assistant", "Answer")), []);

        var descriptor = store.ListSessions().Should().ContainSingle().Subject;
        descriptor.Preview.Should().NotBeNull().And.HaveLength(SqliteTranscriptStore.MaxDisplayTextLength);
        descriptor.Preview.Should().EndWith("…");
    }

    [Fact]
    public void SetTitleUpdatesExistingSessionsAndCreatesZeroTurnRowsForUnknownOnes()
    {
        var committed = AgentSessionId.Create();
        var unknown = AgentSessionId.Create();
        using var store = new SqliteTranscriptStore(databasePath, () => "/repos/alpha");
        store.AppendTurn(committed, Turn(committed, "a", identity: null, truncated: false,
            ("user", "Question"), ("assistant", "Answer")), []);

        store.SetTitle(committed, "Refactor vfs lenses");
        store.SetTitle(unknown, "Named before first turn");

        var descriptors = store.ListSessions();
        descriptors.Should().HaveCount(2);
        descriptors.Single(session => session.Id == committed).Title.Should().Be("Refactor vfs lenses");
        descriptors.Single(session => session.Id == committed).Preview.Should().Be("Question");
        var named = descriptors.Single(session => session.Id == unknown);
        named.TurnCount.Should().Be(0);
        named.Title.Should().Be("Named before first turn");
        named.Preview.Should().BeNull();
        named.WorkspaceRoot.Should().Be("/repos/alpha");
        named.CreatedAt.Should().BeOnOrBefore(named.LastActivityAt);

        // Clearing stores null.
        store.SetTitle(committed, null);
        store.ListSessions().Single(session => session.Id == committed).Title.Should().BeNull();
    }

    [Fact]
    public void FamilyDatabasePathJoinsTheFamilyDirectory()
    {
        var familyId = AgentSessionId.Create();
        var path = SqliteTranscriptStore.FamilyDatabasePath("/data/agent/families", familyId);
        path.Replace('\\', '/').Should().Be($"/data/agent/families/{familyId.Value.ToString("N")}/history.db");
    }

    [Fact]
    public void AppendedTurnsSurviveReopenWithCanonicalContent()
    {
        var sessionId = AgentSessionId.Create();
        var identity = new AgentModelIdentity(new AgentModelProfileId("default"), "OpenAI", "gpt-test");
        var first = Turn(
            sessionId,
            "a",
            identity,
            truncated: false,
            ("user", "Question one"),
            ("assistant", "Answer one"));
        var second = Turn(
            sessionId,
            "b",
            identity: null,
            truncated: true,
            ("user", "Question two"),
            ("assistant", "Answer two"));

        using (var store = new SqliteTranscriptStore(databasePath))
        {
            store.AppendTurn(sessionId, first, []);
            store.AppendTurn(sessionId, second, []);
        }

        using var reopened = new SqliteTranscriptStore(databasePath);
        var transcript = reopened.LoadTranscript(sessionId);
        transcript.Should().NotBeNull();
        transcript!.SessionId.Should().Be(sessionId);
        transcript.Turns.Should().HaveCount(2);
        transcript.Turns[0].RunId.Should().Be(first.RunId);
        transcript.Turns[0].ModelIdentity.Should().Be(identity);
        transcript.Turns[0].Truncated.Should().BeFalse();
        transcript.Turns[0].Messages.Select(message => message.Text)
            .Should().Equal("Question one", "Answer one");
        transcript.Turns[1].ModelIdentity.Should().BeNull();
        transcript.Turns[1].Truncated.Should().BeTrue();
        transcript.Turns[1].Messages.Select(message => message.Text)
            .Should().Equal("Question two", "Answer two");

        var sessions = reopened.ListSessions();
        sessions.Should().ContainSingle();
        sessions[0].Id.Should().Be(sessionId);
        sessions[0].TurnCount.Should().Be(2);
        sessions[0].LastActivityAt.Should().BeOnOrAfter(sessions[0].CreatedAt);
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

        using var store = new SqliteTranscriptStore(databasePath);
        store.AppendTurn(sessionId, turn, []);
        var loaded = store.LoadTranscript(sessionId);

        loaded.Should().NotBeNull();
        var contents = loaded!.Turns[0].Messages[^1].Contents;
        contents.OfType<TextReasoningContent>().Should().ContainSingle().Which.Text.Should().Be("private reasoning summary");
        contents.OfType<TextContent>().Should().ContainSingle().Which.Text.Should().Be("public answer");
    }

    [Fact]
    public void UnknownSessionsLoadAsAbsent()
    {
        using var store = new SqliteTranscriptStore(databasePath);
        store.LoadTranscript(AgentSessionId.Create()).Should().BeNull();
        store.ListSessions().Should().BeEmpty();
    }

    [Fact]
    public void CreateForkSessionPersistsLineageAndLoadWalksTheChain()
    {
        var root = AgentSessionId.Create();
        var fork = AgentSessionId.Create();
        var identity = new AgentModelIdentity(new AgentModelProfileId("default"), "OpenAI", "gpt-test");
        using var store = new SqliteTranscriptStore(databasePath, () => "/repos/alpha");
        store.AppendTurn(root, Turn(root, "a", identity, truncated: false,
            ("user", "Question one"), ("assistant", "Answer one")), []);
        store.AppendTurn(root, Turn(root, "b", identity: null, truncated: false,
            ("user", "Question two"), ("assistant", "Answer two")), []);
        store.AppendTurn(root, Turn(root, "c", identity: null, truncated: false,
            ("user", "Question three"), ("assistant", "Answer three")), []);

        store.CreateForkSession(fork, root, forkPointSeq: 2, title: "root · branch @ turn 3");

        // The fork sees the prefix; the root keeps its whole history.
        var forkTranscript = store.LoadTranscript(fork);
        forkTranscript.Should().NotBeNull();
        forkTranscript!.SessionId.Should().Be(fork);
        forkTranscript.Turns.Select(turn => turn.RunId).Should().HaveCount(2);
        forkTranscript.Turns[0].Messages[0].Text.Should().Be("Question one");
        forkTranscript.Turns[1].Messages[^1].Text.Should().Be("Answer two");
        store.LoadTranscript(root)!.Turns.Should().HaveCount(3);

        // The fork's own turns append after the prefix.
        store.AppendTurn(fork, Turn(fork, "d", identity: null, truncated: false,
            ("user", "Fork question"), ("assistant", "Fork answer")), []);
        var grown = store.LoadTranscript(fork)!.Turns;
        grown.Should().HaveCount(3);
        grown[2].Messages[0].Text.Should().Be("Fork question");

        var descriptor = store.ListSessions().Single(session => session.Id == fork);
        descriptor.ParentSessionId.Should().Be(root);
        descriptor.ForkPointSeq.Should().Be(2);
        descriptor.Title.Should().Be("root · branch @ turn 3");
        descriptor.TurnCount.Should().Be(1);
    }

    [Fact]
    public void ForkOfForkLoadsTheDeepChain()
    {
        var root = AgentSessionId.Create();
        var forkA = AgentSessionId.Create();
        var forkB = AgentSessionId.Create();
        using var store = new SqliteTranscriptStore(databasePath);
        foreach (var suffix in new[] { "a", "b", "c" })
        {
            store.AppendTurn(root, Turn(root, suffix, identity: null, truncated: false,
                ("user", $"Question {suffix}"), ("assistant", $"Answer {suffix}")), []);
        }

        store.CreateForkSession(forkA, root, forkPointSeq: 2, title: null);
        store.AppendTurn(forkA, Turn(forkA, "d", identity: null, truncated: false,
            ("user", "Question d"), ("assistant", "Answer d")), []);
        // forkB keeps all three of forkA's visible turns.
        store.CreateForkSession(forkB, forkA, forkPointSeq: 3, title: null);

        var loaded = store.LoadTranscript(forkB)!.Turns;
        loaded.Should().HaveCount(3);
        loaded.Select(turn => turn.Messages[0].Text).Should().Equal(
            "Question a", "Question b", "Question d");
        store.ListSessions().Single(session => session.Id == forkB)
            .ParentSessionId.Should().Be(forkA);
    }

    [Fact]
    public void ZeroTurnRowsLoadAsEmptyTranscripts()
    {
        var renamed = AgentSessionId.Create();
        var freshFork = AgentSessionId.Create();
        using var store = new SqliteTranscriptStore(databasePath);
        store.SetTitle(renamed, "Named before first turn");
        store.CreateForkSession(freshFork, renamed, forkPointSeq: 0, title: null);

        var forkTranscript = store.LoadTranscript(freshFork);
        forkTranscript.Should().NotBeNull();
        forkTranscript!.SessionId.Should().Be(freshFork);
        forkTranscript.Turns.Should().BeEmpty();
        store.LoadTranscript(renamed)!.Turns.Should().BeEmpty();
    }

    [Fact]
    public void CyclicForkParentLinksFailTyped()
    {
        // CreateForkSession cannot produce a cycle (the parent row must exist
        // first), so this simulates external corruption of the parent links.
        var a = AgentSessionId.Create();
        var b = AgentSessionId.Create();
        using var store = new SqliteTranscriptStore(databasePath);
        using (var raw = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
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

        var load = () => store.LoadTranscript(a);
        load.Should().Throw<InvalidOperationException>().WithMessage("*exceeds 64*");
    }

    [Fact]
    public void DanglingForkParentFailsTyped()
    {
        var missing = AgentSessionId.Create().Value.ToString("N");
        var fork = AgentSessionId.Create();
        using var store = new SqliteTranscriptStore(databasePath);
        using (var raw = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            raw.Open();
            using var insert = raw.CreateCommand();
            insert.CommandText =
                $"INSERT INTO sessions(id, created_at, last_activity_at, turn_count, parent_session_id, fork_point_seq) " +
                $"VALUES('{fork.Value.ToString("N")}', '2026-01-01T00:00:00.0000000+00:00', " +
                $"'2026-01-01T00:00:00.0000000+00:00', 0, '{missing}', 1);";
            insert.ExecuteNonQuery();
        }

        var load = () => store.LoadTranscript(fork);
        load.Should().Throw<InvalidOperationException>().WithMessage("*has no session row*");
    }

    [Fact]
    public void ForkPreviewArrivesWithTheFirstOwnTurn()
    {
        var root = AgentSessionId.Create();
        var fork = AgentSessionId.Create();
        using var store = new SqliteTranscriptStore(databasePath, () => "/repos/alpha");
        store.AppendTurn(root, Turn(root, "a", identity: null, truncated: false,
            ("user", "Original question"), ("assistant", "Answer")), []);
        store.CreateForkSession(fork, root, forkPointSeq: 0, title: null);

        store.ListSessions().Single(session => session.Id == fork).Preview.Should().BeNull();
        store.AppendTurn(fork, Turn(fork, "b", identity: null, truncated: false,
            ("user", "Fork question"), ("assistant", "Fork answer")), []);

        var descriptor = store.ListSessions().Single(session => session.Id == fork);
        descriptor.Preview.Should().Be("Fork question");
        descriptor.WorkspaceRoot.Should().Be("/repos/alpha");
    }

    [Fact]
    public void RefusesDatabasesWrittenByANewerSchema()
    {
        Directory.CreateDirectory(databaseDirectory);
        using (var newer = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            newer.Open();
            using var bump = newer.CreateCommand();
            bump.CommandText = "PRAGMA user_version=99;";
            bump.ExecuteNonQuery();
        }

        var open = () => new SqliteTranscriptStore(databasePath);
        open.Should().Throw<InvalidOperationException>()
            .WithMessage("*newer build*");
    }

    private static AgentTranscriptTurn Turn(
        AgentSessionId sessionId,
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
