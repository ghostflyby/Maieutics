using System.Collections.Immutable;
using Maieutics.Agent;
using Microsoft.Data.Sqlite;

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
    private const int CurrentSchemaVersion = 2;

    /// <summary>One database file per fork family; the family directory is keyed by the family
    /// root session id so derived scanners and backups can glob <c>families/*/history.db</c>.</summary>
    internal static string FamilyDatabasePath(string familiesRoot, AgentSessionId familyId)
    {
        return Path.Combine(familiesRoot, familyId.Value.ToString("N"), "history.db");
    }

    private readonly Lock gate = new();
    private readonly SqliteConnection connection;
    private bool disposed;

    public SqliteTranscriptStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        // Pooling disabled: each family store holds exactly one connection for its lifetime, and
        // pooled handles would keep deleted family directories locked on Windows.
        connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
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
            session.CommandText = """
                INSERT INTO sessions(id, created_at, last_activity_at, turn_count)
                VALUES($id, $now, $now, 0)
                ON CONFLICT(id) DO NOTHING;
                """;
            session.Parameters.AddWithValue("$id", familyId);
            session.Parameters.AddWithValue("$now", now);
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
                var turnSeq = Convert.ToInt64(seq.ExecuteScalar()!);

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

    public AgentTranscript? LoadTranscript(AgentSessionId sessionId)
    {
        var id = sessionId.Value.ToString("N");
        lock (gate)
        {
            using var turns = connection.CreateCommand();
            turns.CommandText = """
                SELECT seq, run_id, truncated, profile_id, model_provider, model_name, messages
                FROM turns
                WHERE session_id = $id
                ORDER BY seq;
                """;
            turns.Parameters.AddWithValue("$id", id);
            using var reader = turns.ExecuteReader();
            if (!reader.HasRows) return null;

            var builder = ImmutableArray.CreateBuilder<AgentTranscriptTurn>();
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

            return new AgentTranscript(sessionId, builder.Count, builder.ToImmutable());
        }
    }

    public IReadOnlyList<AgentSessionDescriptor> ListSessions()
    {        lock (gate)
        {
            using var query = connection.CreateCommand();
            query.CommandText = """
                SELECT id, created_at, last_activity_at, turn_count
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
                    reader.GetInt32(3)));
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
            // The full idempotent script: every step uses IF NOT EXISTS, so a fresh database
            // and any older version both converge to the current schema in one transaction.
            migrate.CommandText = """
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

                PRAGMA user_version=2;
                """;
            migrate.ExecuteNonQuery();
            transaction.Commit();
        }
    }
}
