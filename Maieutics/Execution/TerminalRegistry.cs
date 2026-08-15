using Maieutics.Agent;
using Microsoft.Extensions.Logging;

namespace Maieutics.Execution;

/// <summary>Owns the terminal sessions of every Agent session. Sessions survive across turns and die with
/// their Agent session or the process.</summary>
internal sealed class TerminalRegistry(Workspace workspace, TerminalOptions options,
    ITerminalProcessFactory factory, ILogger<TerminalSession> logger) : IAsyncDisposable
{
    private const string DefaultSessionId = "default";

    private readonly TaskCompletionSource disposalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Lock gate = new();
    private readonly Dictionary<AgentSessionId, Dictionary<string, TerminalSession>> sessions = [];
    private int disposeState;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref disposeState, 1, 0) != 0)
        {
            await disposalCompletion.Task.ConfigureAwait(false);
            return;
        }

        Exception? failure = null;
        TerminalSession[] snapshot;
        lock (gate)
        {
            snapshot = sessions.Values.SelectMany(static value => value.Values).ToArray();
            sessions.Clear();
        }

        try
        {
            await Task.WhenAll(snapshot.Select(static session => session.DisposeAsync().AsTask()))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is null)
            disposalCompletion.TrySetResult();
        else
            disposalCompletion.TrySetException(failure);

        await disposalCompletion.Task.ConfigureAwait(false);
    }

    internal async Task<TerminalInfo> CreateAsync(
        AgentSessionId ownerSessionId,
        CancellationToken cancellationToken)
    {
        var session = Reserve(ownerSessionId, Guid.NewGuid().ToString("N"), false);
        await session.StartAsync(cancellationToken).ConfigureAwait(false);
        return session.GetSnapshot();
    }

    internal TerminalListResult List(AgentSessionId ownerSessionId)
    {
        TerminalSession[] snapshot;
        lock (gate)
        {
            ThrowIfDisposed();
            if (!sessions.TryGetValue(ownerSessionId, out var owned)) return new TerminalListResult([]);
            snapshot = owned.Values.ToArray();
        }

        return new TerminalListResult(snapshot
            .Select(static session => session.GetSnapshot())
            .OrderByDescending(static session => session.IsDefault)
            .ThenBy(static session => session.SessionId, StringComparer.Ordinal)
            .ToArray());
    }

    internal async Task<TerminalInputResult> ExecuteAsync(
        AgentToolContext toolContext,
        TerminalInputBatch batch,
        TerminalSnapshotRequest snapshotRequest,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var session = GetOrReserveDefault(toolContext.SessionId, sessionId);
        return await session.ExecuteInputAsync(batch, snapshotRequest, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TerminalPasteResult> PasteAsync(
        AgentToolContext toolContext,
        string text,
        TerminalSnapshotRequest snapshotRequest,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var session = GetOrReserveDefault(toolContext.SessionId, sessionId);
        return await session.PasteAsync(text, snapshotRequest, cancellationToken).ConfigureAwait(false);
    }

    internal TerminalSnapshotResult Snapshot(
        AgentSessionId ownerSessionId,
        TerminalSnapshotRequest snapshotRequest,
        string? sessionId)
    {
        // A read never starts the shell: only a write (execute, paste) lazily creates the default session.
        var session = GetExisting(ownerSessionId, ResolveSessionId(sessionId));
        return session.Snapshot(snapshotRequest);
    }

    internal async Task<TerminalInterruptResult> InterruptAsync(
        AgentSessionId ownerSessionId,
        TerminalSnapshotRequest snapshotRequest,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        // A control call never starts the shell: only a write (execute, paste) lazily creates the default session.
        var session = GetExisting(ownerSessionId, ResolveSessionId(sessionId));
        return await session.InterruptAsync(snapshotRequest, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TerminalCloseResult> CloseAsync(
        AgentSessionId ownerSessionId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var resolvedSessionId = ResolveSessionId(sessionId);
        var session = GetExisting(ownerSessionId, resolvedSessionId);

        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not close terminal session {SessionId} cleanly.", resolvedSessionId);
            throw new AgentToolException(
                "terminal_close_failed",
                $"The terminal session '{resolvedSessionId}' could not be closed cleanly.");
        }
        finally
        {
            Remove(ownerSessionId, resolvedSessionId, session);
        }

        return new TerminalCloseResult(resolvedSessionId, true);
    }

    private TerminalSession GetOrReserveDefault(AgentSessionId ownerSessionId, string? sessionId)
    {
        var resolvedSessionId = ResolveSessionId(sessionId);
        lock (gate)
        {
            ThrowIfDisposed();
            if (sessions.TryGetValue(ownerSessionId, out var owned) &&
                owned.TryGetValue(resolvedSessionId, out var existing))
                return existing;

            if (!string.Equals(resolvedSessionId, DefaultSessionId, StringComparison.Ordinal))
                throw NotFound(resolvedSessionId);
        }

        return Reserve(ownerSessionId, DefaultSessionId, true);
    }

    private TerminalSession Reserve(AgentSessionId ownerSessionId, string sessionId, bool isDefault)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!sessions.TryGetValue(ownerSessionId, out var owned))
            {
                owned = new Dictionary<string, TerminalSession>(StringComparer.Ordinal);
                sessions.Add(ownerSessionId, owned);
            }

            if (owned.TryGetValue(sessionId, out var existing)) return existing;

            if (owned.Count >= options.MaxSessionsPerAgent)
                throw new AgentToolException(
                    "terminal_session_limit",
                    $"An Agent session may own at most {options.MaxSessionsPerAgent} terminal sessions.");

            var created = new TerminalSession(
                ownerSessionId,
                sessionId,
                isDefault,
                workspace.Capture().RootPath,
                options,
                factory,
                logger);
            owned.Add(sessionId, created);
            return created;
        }
    }

    private TerminalSession GetExisting(AgentSessionId ownerSessionId, string sessionId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (sessions.TryGetValue(ownerSessionId, out var owned) &&
                owned.TryGetValue(sessionId, out var session))
                return session;
        }

        throw NotFound(sessionId);
    }

    private void Remove(AgentSessionId ownerSessionId, string sessionId, TerminalSession expected)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(ownerSessionId, out var owned) ||
                !owned.TryGetValue(sessionId, out var current) ||
                !ReferenceEquals(current, expected))
                return;

            owned.Remove(sessionId);
            if (owned.Count == 0) sessions.Remove(ownerSessionId);
        }
    }

    private static string ResolveSessionId(string? sessionId)
    {
        if (sessionId is null) return DefaultSessionId;

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new AgentToolException("terminal_invalid_arguments", "sessionId cannot be empty.");

        return sessionId;
    }

    private static AgentToolException NotFound(string sessionId)
    {
        return new AgentToolException(
            "terminal_session_not_found",
            $"The terminal session '{sessionId}' does not exist.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
    }
}
