using Maieutics.Agent;
using Maieutics.Control;
using Microsoft.Extensions.Logging;

namespace Maieutics.Execution;

internal sealed class DenoReplRegistry(
    Workspace workspace,
    DenoReplOptions options,
    IDenoReplSessionFactory factory,
    IDenoReplPresentationRouter presentationRouter,
    ReplControlSessionRegistry controlRegistry,
    ILogger<DenoReplSession> logger)
    : IAsyncDisposable
{
    private const string DefaultSessionId = "default";

    private readonly TaskCompletionSource disposalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Lock gate = new();
    private readonly Dictionary<AgentSessionId, Dictionary<string, DenoReplSession>> sessions = [];
    private int disposeState;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref disposeState, 1, 0) != 0)
        {
            await disposalCompletion.Task.ConfigureAwait(false);
            return;
        }

        Exception? failure = null;
        DenoReplSession[] snapshot;
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

    internal DenoReplListResult List(AgentSessionId ownerSessionId)
    {
        DenoReplSession[] snapshot;
        lock (gate)
        {
            ThrowIfDisposed();
            if (!sessions.TryGetValue(ownerSessionId, out var owned)) return new DenoReplListResult([]);
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

    private DenoReplSession Reserve(AgentSessionId ownerSessionId, string sessionId, bool isDefault)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!sessions.TryGetValue(ownerSessionId, out var owned))
            {
                owned = new Dictionary<string, DenoReplSession>(StringComparer.Ordinal);
                sessions.Add(ownerSessionId, owned);
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
                controlRegistry,
                logger);
            owned.Add(sessionId, created);
            return created;
        }
    }

    private DenoReplSession GetExisting(AgentSessionId ownerSessionId, string sessionId)
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

    private void Remove(
        AgentSessionId ownerSessionId,
        string sessionId,
        DenoReplSession expected)
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
    }
}
