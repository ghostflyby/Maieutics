using System.Collections.Immutable;
using Maieutics.Agent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Maieutics.Persistence;

/// <summary>
///     Persists the canonical Agent transcript in one SQLite database: immutable turn rows plus
///     a per-session head, one transaction per committed turn (ADR 0009 metadata store). The
///     store owns a single connection and serializes all access behind one lock; the session
///     commit path is the only writer and reads are rare, so WAL's concurrency is not needed yet.
///     Durability follows the plugin-store precedent (ADR 0022): WAL with
///     <c>synchronous=NORMAL</c>, so an application crash preserves every committed turn while a
///     power loss may drop the most recent commits. Schema changes are versioned through
///     <c>PRAGMA user_version</c>; a database written by a newer build is refused.
/// </summary>
internal sealed class SqliteTranscriptStore : IAgentTranscriptStore, IDisposable
{
    private const int CurrentSchemaVersion = 4;

    /// <summary>Bounded display metadata: the preview (first user text) and the title cap at
    /// this many characters.</summary>
    internal const int MaxDisplayTextLength = 200;

    /// <summary>Fork-chain walks stop here so a corrupted (cyclic) parent link fails with a
    /// typed error instead of looping forever. Real chains are a few links deep.</summary>
    private const int MaxForkChainDepth = 64;

    /// <summary>One database file per fork family; the family directory is keyed by the family
    /// root session id so derived scanners and backups can glob <c>families/*/history.db</c>.</summary>
    internal static string FamilyDatabasePath(string familiesRoot, AgentSessionId familyId)
    {
        return Path.Combine(familiesRoot, familyId.Value.ToString("N"), "history.db");
    }

    private readonly Lock gate = new();
    private readonly SqliteConnection connection;
    private readonly Func<string?>? workspaceRootAccessor;
    private bool disposed;

    public SqliteTranscriptStore(
        string databasePath,
        Func<string?>? workspaceRootAccessor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.workspaceRootAccessor = workspaceRootAccessor;
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        // Pooling disabled: each family store holds exactly one connection for its lifetime, and
        // pooled handles would keep deleted family directories locked on Windows.
        connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        try
        {
            connection.Open();
            using var pragmas = connection.CreateCommand();
            // Journal mode is queried back to fail loudly on filesystems (network shares) that
            // cannot provide WAL, instead of silently degrading to rollback-journal locking.
            pragmas.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA foreign_keys=ON;
                """;
            pragmas.ExecuteNonQuery();
            Migrate();
        }
        catch
        {
            // A rejected store must not leak its open handle: on Windows it would keep the
            // database file locked after the constructor failed.
            connection.Dispose();
            throw;
        }
    }

    public void AppendTurn(AgentSessionId sessionId, AgentTranscriptTurn turn, IReadOnlyList<string> objectReferences)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(objectReferences);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var messages = AgentTranscriptEncoding.Encode(turn.Messages);
        var familyId = sessionId.Value.ToString("N");
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            var session = connection.CreateCommand();
            session.Transaction = transaction;
            // Rows that already exist (fork heads, sessions named before their first commit)
            // keep every stored column and only gain the still-missing preview from their
            // first committing turn; rows with a preview keep it (write-once).
            session.CommandText = """
                INSERT INTO sessions(id, created_at, last_activity_at, turn_count, preview, workspace_root)
                VALUES($id, $now, $now, 0, $preview, $workspace)
                ON CONFLICT(id) DO UPDATE SET preview = COALESCE(sessions.preview, excluded.preview);
                """;
            session.Parameters.AddWithValue("$id", familyId);
            session.Parameters.AddWithValue("$now", now);
            session.Parameters.AddWithValue("$preview", (object?)ComputePreview(turn) ?? DBNull.Value);
            session.Parameters.AddWithValue("$workspace", (object?)workspaceRootAccessor?.Invoke() ?? DBNull.Value);
            session.ExecuteNonQuery();

            var append = connection.CreateCommand();
            append.Transaction = transaction;
            append.CommandText = """
                INSERT INTO turns(session_id, seq, run_id, truncated, profile_id, model_provider,
                                  model_name, messages, byte_count, created_at)
                VALUES(
                    $id,
                    (SELECT COALESCE(MAX(seq), -1) + 1 FROM turns WHERE session_id = $id),
                    $runId,
                    $truncated,
                    $profileId,
                    $modelProvider,
                    $modelName,
                    $messages,
                    $byteCount,
                    $now);
                UPDATE sessions
                SET last_activity_at = $now, turn_count = turn_count + 1
                WHERE id = $id;
                """;
            append.Parameters.AddWithValue("$id", familyId);
            append.Parameters.AddWithValue("$runId", turn.RunId.Value.ToString("N"));
            append.Parameters.AddWithValue("$truncated", turn.Truncated ? 1L : 0L);
            append.Parameters.AddWithValue("$profileId", turn.ModelIdentity?.ProfileId.Value as object ?? DBNull.Value);
            append.Parameters.AddWithValue("$modelProvider", (object?)turn.ModelIdentity?.Provider ?? DBNull.Value);
            append.Parameters.AddWithValue("$modelName", (object?)turn.ModelIdentity?.Model ?? DBNull.Value);
            append.Parameters.AddWithValue("$messages", messages);
            append.Parameters.AddWithValue("$byteCount", messages.LongLength);
            append.Parameters.AddWithValue("$now", now);
            append.ExecuteNonQuery();

            if (objectReferences.Count > 0)
            {
                var seq = connection.CreateCommand();
                seq.Transaction = transaction;
                seq.CommandText = "SELECT COALESCE(MAX(seq), -1) FROM turns WHERE session_id = $id;";
                seq.Parameters.AddWithValue("$id", familyId);
                if (seq.ExecuteScalar() is not { } rawTurnSeq)
                    throw new InvalidOperationException(
                        $"The turn sequence lookup for session '{familyId}' returned no row.");

                var turnSeq = Convert.ToInt64(rawTurnSeq);

                foreach (var sha256 in objectReferences.Distinct(StringComparer.Ordinal))
                {
                    var reference = connection.CreateCommand();
                    reference.Transaction = transaction;
                    reference.CommandText = """
                        INSERT OR IGNORE INTO blob_refs(session_id, seq, sha256)
                        VALUES($id, $seq, $sha256);
                        """;
                    reference.Parameters.AddWithValue("$id", familyId);
                    reference.Parameters.AddWithValue("$seq", turnSeq);
                    reference.Parameters.AddWithValue("$sha256", sha256);
                    reference.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
    }

    /// <summary>Sets or clears one session's title. An absent row is created (zero turns) so an
    /// explicitly named session is listed before its first committed turn; preview stays absent
    /// until a turn commits and the workspace follows the same creation accessor as
    /// <see cref="AppendTurn" />.</summary>
    public void SetTitle(AgentSessionId sessionId, string? title)
    {
        var id = sessionId.Value.ToString("N");
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sessions(id, created_at, last_activity_at, turn_count, title, preview, workspace_root)
                VALUES($id, $now, $now, 0, $title, NULL, $workspace)
                ON CONFLICT(id) DO UPDATE SET title = $title;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
            command.Parameters.AddWithValue("$workspace", (object?)workspaceRootAccessor?.Invoke() ?? DBNull.Value);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    /// <summary>Creates a fork head: a zero-turn session row whose history is
    /// <c>parent_session_id</c>'s turns with <c>seq &lt; fork_point_seq</c>, followed by its own.
    /// Constant cost per ADR 0009 — turns are never copied; the row lives in the family
    /// database this store owns (the root ancestor's).</summary>
    public void CreateForkSession(
        AgentSessionId forkId,
        AgentSessionId parentSessionId,
        int forkPointSeq,
        string? title)
    {
        if (forkPointSeq < 0)
            throw new ArgumentOutOfRangeException(nameof(forkPointSeq), forkPointSeq,
                "The fork point cannot be negative.");

        lock (gate)
        {
            // parent_session_id carries no foreign key (an ALTER TABLE cannot add
            // one), so the parent's presence is enforced explicitly at creation
            // time; a parent that vanishes later still fails the chain walk.
            if (ReadSessionRow(parentSessionId.Value.ToString("N")) is null)
            {
                throw new InvalidOperationException(
                    $"The fork parent '{parentSessionId.Value.ToString("N")}' has no row in this family database.");
            }

            using var transaction = connection.BeginTransaction();
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sessions(
                    id, created_at, last_activity_at, turn_count, title, preview, workspace_root,
                    parent_session_id, fork_point_seq)
                VALUES($id, $now, $now, 0, $title, NULL, $workspace, $parent, $forkPoint);
                """;
            command.Parameters.AddWithValue("$id", forkId.Value.ToString("N"));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
            command.Parameters.AddWithValue("$workspace", (object?)workspaceRootAccessor?.Invoke() ?? DBNull.Value);
            command.Parameters.AddWithValue("$parent", parentSessionId.Value.ToString("N"));
            command.Parameters.AddWithValue("$forkPoint", forkPointSeq);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    /// <summary>Loads the committed transcript of one session, walking the fork parent chain:
    /// each ancestor contributes turns with <c>seq &lt; fork_point_seq</c> of the child that
    /// forked from it, then the session's own turns. A stored session with zero turns
    /// (renamed ahead of its first commit, or a fresh fork head) yields an empty transcript
    /// rather than "absent" — resuming it stays meaningful because the chain supplies the
    /// prefix.</summary>
    public AgentTranscript? LoadTranscript(AgentSessionId sessionId)
    {
        var id = sessionId.Value.ToString("N");
        lock (gate)
        {
            // Resolve the parent chain from the target back to its root before reading any
            // turns, so a broken link fails loudly instead of silently truncating history.
            var chain = new List<(string Id, long? ForkPoint)>();
            var cursor = id;
            while (true)
            {
                var row = ReadSessionRow(cursor);
                if (row is null)
                {
                    if (chain.Count == 0) return null;
                    throw new InvalidOperationException(
                        $"The fork parent '{cursor}' of '{chain[^1].Id}' has no session row in this family database.");
                }

                chain.Add((cursor, row.Value.ForkPoint));
                if (row.Value.Parent is null) break;

                if (chain.Count >= MaxForkChainDepth)
                {
                    throw new InvalidOperationException(
                        $"The fork chain of session '{id}' exceeds {MaxForkChainDepth} links; the parent links are corrupt.");
                }

                cursor = row.Value.Parent;
            }

            var builder = ImmutableArray.CreateBuilder<AgentTranscriptTurn>();
            // Walk root → target: an ancestor contributes the turns its child forked at.
            for (var index = chain.Count - 1; index >= 0; index--)
            {
                var limit = index > 0 ? chain[index - 1].ForkPoint : null;
                AppendTurns(chain[index].Id, limit, builder);
            }

            return new AgentTranscript(sessionId, builder.Count, builder.ToImmutable());
        }
    }

    /// <summary>Reads one session row's lineage link, or <see langword="null" /> when the row
    /// is absent.</summary>
    private (string? Parent, long? ForkPoint)? ReadSessionRow(string id)
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT parent_session_id, fork_point_seq FROM sessions WHERE id = $id;";
        query.Parameters.AddWithValue("$id", id);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return null;

        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    /// <summary>Appends one session's stored turns, optionally bounded to
    /// <c>seq &lt; limit</c>, in commit order.</summary>
    private void AppendTurns(string id, long? limit, ImmutableArray<AgentTranscriptTurn>.Builder builder)
    {
        using var turns = connection.CreateCommand();
        turns.CommandText = """
            SELECT seq, run_id, truncated, profile_id, model_provider, model_name, messages
            FROM turns
            WHERE session_id = $id AND ($limit IS NULL OR seq < $limit)
            ORDER BY seq;
            """;
        turns.Parameters.AddWithValue("$id", id);
        turns.Parameters.AddWithValue("$limit", (object?)limit ?? DBNull.Value);
        using var reader = turns.ExecuteReader();
        while (reader.Read())
        {
            var runId = new AgentRunId(Guid.ParseExact(reader.GetString(1), "N"));
            var truncated = reader.GetInt64(2) != 0;
            var messages = AgentTranscriptEncoding.Decode((byte[])reader.GetValue(6));
            AgentModelIdentity? identity = reader.IsDBNull(3)
                ? null
                : new AgentModelIdentity(
                    new AgentModelProfileId(reader.GetString(3)),
                    reader.GetString(4),
                    reader.GetString(5));
            builder.Add(new AgentTranscriptTurn(runId, messages, identity, truncated));
        }
    }

    public IReadOnlyList<AgentSessionDescriptor> ListSessions()
    {        lock (gate)
        {
            using var query = connection.CreateCommand();
            query.CommandText = """
                SELECT id, created_at, last_activity_at, turn_count, title, preview, workspace_root,
                       parent_session_id, fork_point_seq
                FROM sessions
                ORDER BY last_activity_at DESC;
                """;
            using var reader = query.ExecuteReader();
            var sessions = new List<AgentSessionDescriptor>();
            while (reader.Read())
            {
                sessions.Add(new AgentSessionDescriptor(
                    new AgentSessionId(Guid.ParseExact(reader.GetString(0), "N")),
                    DateTimeOffset.Parse(reader.GetString(1)),
                    DateTimeOffset.Parse(reader.GetString(2)),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7)
                        ? null
                        : new AgentSessionId(Guid.ParseExact(reader.GetString(7), "N")),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8)));
            }

            return sessions;
        }
    }

    /// <summary>Collects the distinct object addresses this family's turns reference; the union
    /// across family databases is the live set for object garbage collection.</summary>
    internal IReadOnlyCollection<string> GetReferencedObjectIds()
    {
        lock (gate)
        {
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT DISTINCT sha256 FROM blob_refs;";
            using var reader = query.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0));

            return ids;
        }
    }

    /// <summary>Collects this family's (session, object address) pairs in commit order; the
    /// derived inspection view is rebuilt from these pairs.</summary>
    internal IReadOnlyList<(AgentSessionId Session, string Sha256)> GetSessionObjectReferences()
    {
        lock (gate)
        {
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT session_id, sha256 FROM blob_refs ORDER BY session_id, seq;";
            using var reader = query.ExecuteReader();
            var references = new List<(AgentSessionId, string)>();
            while (reader.Read())
            {
                references.Add((
                    new AgentSessionId(Guid.ParseExact(reader.GetString(0), "N")),
                    reader.GetString(1)));
            }

            return references;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, true)) return;
        lock (gate)
        {
            connection.Dispose();
        }
    }

    private void Migrate()
    {
        lock (gate)
        {
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            var current = Convert.ToInt32(version.ExecuteScalar());

            if (current > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The Agent transcript store was written by a newer build (schema version {current} > {CurrentSchemaVersion}).");
            }

            if (current == CurrentSchemaVersion) return;

            using var transaction = connection.BeginTransaction();
            var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            // The table script is idempotent (IF NOT EXISTS), so a fresh database and any
            // older version converge here. The ALTER steps are not idempotent, so each is
            // gated on the version that introduced its columns and runs at most once per
            // database, inside this one transaction.
            var script = """
                CREATE TABLE IF NOT EXISTS sessions (
                    id TEXT PRIMARY KEY,
                    created_at TEXT NOT NULL,
                    last_activity_at TEXT NOT NULL,
                    turn_count INTEGER NOT NULL);

                CREATE TABLE IF NOT EXISTS turns (
                    session_id TEXT NOT NULL REFERENCES sessions(id),
                    seq INTEGER NOT NULL,
                    run_id TEXT NOT NULL,
                    truncated INTEGER NOT NULL,
                    profile_id TEXT,
                    model_provider TEXT,
                    model_name TEXT,
                    messages BLOB NOT NULL,
                    byte_count INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY (session_id, seq));

                CREATE TABLE IF NOT EXISTS blob_refs (
                    session_id TEXT NOT NULL REFERENCES sessions(id),
                    seq INTEGER NOT NULL,
                    sha256 TEXT NOT NULL,
                    PRIMARY KEY (session_id, seq, sha256));
                """;
            if (current < 3)
            {
                script += """
                    ALTER TABLE sessions ADD COLUMN title TEXT;
                    ALTER TABLE sessions ADD COLUMN preview TEXT;
                    ALTER TABLE sessions ADD COLUMN workspace_root TEXT;
                    """;
            }

            if (current < 4)
            {
                script += """
                    ALTER TABLE sessions ADD COLUMN parent_session_id TEXT;
                    ALTER TABLE sessions ADD COLUMN fork_point_seq INTEGER;
                    """;
            }

            script += $"PRAGMA user_version={CurrentSchemaVersion};";
            migrate.CommandText = script;
            migrate.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    /// <summary>Derives the session preview from the turn that creates the row: the first user
    /// message's text, newlines collapsed for one-line display and truncated. Write-once —
    /// later turns never change it.</summary>
    private static string? ComputePreview(AgentTranscriptTurn turn)
    {
        var text = turn.Messages.FirstOrDefault(message => message.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var preview = string.Join(' ', lines).Trim();
        if (preview.Length == 0) return null;
        return preview.Length <= MaxDisplayTextLength
            ? preview
            : preview[..(MaxDisplayTextLength - 1)] + "…";
    }
}
