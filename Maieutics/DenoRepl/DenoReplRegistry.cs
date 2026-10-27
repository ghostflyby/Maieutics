using Maieutics.Agent;
using Maieutics.Execution;
using Microsoft.Extensions.Logging;

namespace Maieutics.DenoRepl;

internal sealed class DenoReplRegistry(
    Workspace workspace,
    DenoReplOptions options,
    IDenoReplSessionFactory factory,
    IDenoReplPresentationRouter presentationRouter,
    ILogger<DenoReplSession> logger,
    IReplDisplayObjectStore? displayObjectStore = null,
    ReplOwnerPolicyCache? ownerPolicyCache = null)
    : IAsyncDisposable
{
    private const string DefaultSessionId = "default";

    private readonly TaskCompletionSource _disposalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Lock _gate = new();
    private readonly Dictionary<AgentSessionId, Dictionary<string, DenoReplSession>> _sessions = [];
    private readonly IReplDisplayObjectStore? _displayObjectStore = displayObjectStore;
    private readonly ReplOwnerPolicyCache? _ownerPolicyCache = ownerPolicyCache;
    private int _disposeState;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            await _disposalCompletion.Task.ConfigureAwait(false);
            return;
        }

        Exception? failure = null;
        DenoReplSession[] snapshot;
        lock (_gate)
        {
            snapshot = _sessions.Values.SelectMany(static value => value.Values).ToArray();
            _sessions.Clear();
        }

        _ownerPolicyCache?.ClearAll();
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
            _disposalCompletion.TrySetResult();
        else
            _disposalCompletion.TrySetException(failure);

        await _disposalCompletion.Task.ConfigureAwait(false);
    }

    internal async Task<DenoReplSessionResult> CreateAsync(
        AgentSessionId ownerSessionId,
        CancellationToken cancellationToken)
    {
        var session = Reserve(ownerSessionId, Guid.NewGuid().ToString("N"), false);
        await session.StartAsync(cancellationToken).ConfigureAwait(false);
        return session.GetSnapshot();
    }

    internal async Task<DenoReplExecutionResult> ExecuteAsync(
        AgentToolContext toolContext,
        string code,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var session = GetOrReserveDefault(toolContext.SessionId, sessionId);
        return await session.ExecuteAsync(code, toolContext.CallId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Ensures the default Deno REPL session for an Agent session is created and started, and
    ///     returns it. Used by the comm relay to deliver frontend comm messages to the REPL child.
    /// </summary>
    internal async Task<DenoReplSession> EnsureDefaultAsync(
        AgentSessionId ownerSessionId,
        CancellationToken cancellationToken)
    {
        var session = GetOrReserveDefault(ownerSessionId, null);
        await session.StartAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    /// <summary>Resolves the owning Agent session of one REPL session id, or null when no
    /// live REPL session matches. The model-orchestration surface uses this to scope a Deno
    /// caller's spawns to its owning session (ADR 0031).</summary>
    internal AgentSessionId? TryGetOwnerSessionId(string replSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replSessionId);
        lock (_gate)
        {
            foreach (var (owner, byId) in _sessions)
                if (byId.ContainsKey(replSessionId))
                    return owner;
        }

        return null;
    }

    internal DenoReplListResult List(AgentSessionId ownerSessionId)
    {
        DenoReplSession[] snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_sessions.TryGetValue(ownerSessionId, out var owned)) return new DenoReplListResult([]);
            snapshot = owned.Values.ToArray();
        }

        return new DenoReplListResult(snapshot
            .Select(static session => session.GetSnapshot())
            .OrderByDescending(static session => session.IsDefault)
            .ThenBy(static session => session.SessionId, StringComparer.Ordinal)
            .ToArray());
    }

    internal async Task<DenoReplSessionResult> RestartAsync(
        AgentSessionId ownerSessionId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var session = GetExisting(ownerSessionId, ResolveSessionId(sessionId));
        return await session.RestartAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<DenoReplCloseResult> CloseAsync(
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
            logger.LogWarning(exception, "Could not close Deno REPL session {SessionId} cleanly.", resolvedSessionId);
            throw new AgentToolException(
                "repl_close_failed",
                $"Deno REPL session '{resolvedSessionId}' could not be closed cleanly.");
        }
        finally
        {
            Remove(ownerSessionId, resolvedSessionId, session);
        }

        return new DenoReplCloseResult(resolvedSessionId, true);
    }

    private DenoReplSession GetOrReserveDefault(AgentSessionId ownerSessionId, string? sessionId)
    {
        var resolvedSessionId = ResolveSessionId(sessionId);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_sessions.TryGetValue(ownerSessionId, out var owned) &&
                owned.TryGetValue(resolvedSessionId, out var existing))
                return existing;

            if (!string.Equals(resolvedSessionId, DefaultSessionId, StringComparison.Ordinal))
                throw NotFound(resolvedSessionId);
        }

        return Reserve(ownerSessionId, DefaultSessionId, true);
    }

    private DenoReplSession Reserve(AgentSessionId ownerSessionId, string sessionId, bool isDefault)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_sessions.TryGetValue(ownerSessionId, out var owned))
            {
                owned = new Dictionary<string, DenoReplSession>(StringComparer.Ordinal);
                _sessions.Add(ownerSessionId, owned);
            }

            if (owned.TryGetValue(sessionId, out var existing)) return existing;

            if (owned.Count >= options.MaxSessionsPerAgent)
                throw new AgentToolException(
                    "repl_session_limit",
                    $"An Agent session may own at most {options.MaxSessionsPerAgent} Deno REPL sessions.");

            var created = new DenoReplSession(
                ownerSessionId,
                sessionId,
                isDefault,
                workspace.Capture().RootPath,
                options,
                factory,
                presentationRouter,
                logger,
                _displayObjectStore);
            owned.Add(sessionId, created);
            return created;
        }
    }

    private DenoReplSession GetExisting(AgentSessionId ownerSessionId, string sessionId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_sessions.TryGetValue(ownerSessionId, out var owned) &&
                owned.TryGetValue(sessionId, out var session))
                return session;
        }

        throw NotFound(sessionId);
    }

    private void Remove(
        AgentSessionId ownerSessionId,
        string sessionId,
        DenoReplSession expected)
    {
        bool ownerHasNoSessions;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(ownerSessionId, out var owned) ||
                !owned.TryGetValue(sessionId, out var current) ||
                !ReferenceEquals(current, expected))
                return;

            owned.Remove(sessionId);
            ownerHasNoSessions = owned.Count == 0;
            if (ownerHasNoSessions) _sessions.Remove(ownerSessionId);
        }

        // The owner-keyed policy snapshot leaves only with the owner's LAST session: an owner
        // may keep several live REPLs (each re-caches on start), and clearing the entry while a
        // sibling stays live would drop the bridge back to the wider model-tool baseline.
        if (ownerHasNoSessions)
            _ownerPolicyCache?.Clear(ownerSessionId.Value.ToString("N"));
    }

    private static string ResolveSessionId(string? sessionId)
    {
        if (sessionId is null) return DefaultSessionId;

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new AgentToolException("repl_invalid_arguments", "sessionId cannot be empty.");

        return sessionId;
    }

    private static AgentToolException NotFound(string sessionId)
    {
        return new AgentToolException(
            "repl_session_not_found",
            $"Deno REPL session '{sessionId}' does not exist.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    }
}
