using Maieutics.Agent;
using Maieutics.Permissions;
using Microsoft.Extensions.Logging;

namespace Maieutics.Execution;

/// <summary>Owns the terminal sessions of every Agent session. Sessions survive across turns and die with
/// their Agent session or the process. Each session captures the effective policy of its owning Agent
/// session at reserve time (ADR 0018 §7); the policy never changes mid-operation.</summary>
internal sealed class TerminalRegistry(Workspace workspace, TerminalOptions options,
    ITerminalProcessFactory factory, ILogger<TerminalSession> logger, PermissionPolicyAcquirer acquirer) : IAsyncDisposable
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

    internal async Task<TerminalRunResult> RunOnceAsync(
        AgentSessionId ownerSessionId,
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout,
        TerminalSnapshotRequest snapshotRequest,
        CancellationToken cancellationToken)
    {
        // Without a timeout this starts a persistent session and returns immediately (the terminal_run
        // creation mode); with a timeout it runs a one-shot command through the deadline path. One-shot
        // sessions get a Guid id so they never occupy the lazy default slot; persistent sessions from
        // terminal_run also get a Guid id and are listed like any explicitly created session.
        var kind = timeout.HasValue ? TerminalSessionKind.OneShot : TerminalSessionKind.Persistent;
        var session = Reserve(
            ownerSessionId,
            Guid.NewGuid().ToString("N"),
            false,
            kind,
            executable,
            arguments);
        if (timeout is { } deadline)
        {
            TerminalRunResult result;
            try
            {
                result = await session.RunOnceAsync(deadline, snapshotRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A cancelled or failed one-shot is dead (its child was already terminated on the
                // cancellation path): release the slot and dispose outside the registry lock.
                await RemoveFinishedOneShotAsync(ownerSessionId, session).ConfigureAwait(false);
                throw;
            }

            // A timed-out one-shot stays registered: the result carries the session id as the
            // pollable handle and the task URI as its readable snapshot (ADR 0028). A settled
            // one-shot has exited and captured its result, so nothing else can ever use the
            // session again — remove it instead of leaking a dead PTY against
            // MaxSessionsPerAgent.
            if (result.Settled)
            {
                await RemoveFinishedOneShotAsync(ownerSessionId, session).ConfigureAwait(false);
            }
            else
            {
                result = result with
                {
                    TaskUri = TerminalTaskResourceSource.ComposeUri(ownerSessionId, session.SessionId)
                };
            }

            return result;
        }

        await session.StartAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = session.GetSnapshot();
        return new TerminalRunResult(
            snapshot.SessionId,
            snapshot.State,
            null,
            true,
            session.Snapshot(snapshotRequest).Frame);
    }

    /// <summary>Lists every Agent session's timed-out one-shots as task-resource handles
    /// (ADR 0028): these stay readable at their task URI until closed.</summary>
    internal TerminalTaskHandle[] ListOneShotTasks()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return sessions
                .SelectMany(static owned => owned.Value.Values.Select(session => (owned.Key, Session: session)))
                .Where(static entry => entry.Session.Kind == TerminalSessionKind.OneShot)
                .Select(static entry => ToTaskHandle(entry.Key, entry.Session))
                .ToArray();
        }
    }

    /// <summary>Reads one one-shot's live state for its task snapshot (ADR 0028); null when
    /// the ids name no registered one-shot.</summary>
    internal TerminalTaskHandle? TryGetOneShotTask(AgentSessionId ownerSessionId, string sessionId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (sessions.TryGetValue(ownerSessionId, out var owned) &&
                owned.TryGetValue(sessionId, out var session) &&
                session.Kind == TerminalSessionKind.OneShot)
                return ToTaskHandle(ownerSessionId, session);

            return null;
        }
    }

    /// <summary>Waits until one one-shot's child process exits and returns its settled task
    /// handle — the wait half of the task plane's control contract (ADR 0030 decision 5).
    /// Waiting on an already-exited one-shot returns immediately; on a removed one-shot this
    /// fails the caller with the registry's typed miss.</summary>
    /// <exception cref="ArgumentException">No registered one-shot matches the ids.</exception>
    /// <exception cref="TimeoutException">The child stayed alive past the timeout.</exception>
    internal async Task<TerminalTaskHandle> WaitOneShotAsync(
        AgentSessionId ownerSessionId,
        string sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var session = GetRegisteredOneShot(ownerSessionId, sessionId);
        // The bounded wait layers a timeout over the session's exit signal; the underlying
        // wait outlives a timeout on CancellationToken.None and settles at the process's real
        // exit (or the close path), so no cancellation side state is left behind.
        var exited = session.WaitExitedAsync(CancellationToken.None);
        if (timeout != Timeout.InfiniteTimeSpan)
            exited = exited.WaitAsync(timeout, cancellationToken);
        await exited.ConfigureAwait(false);

        return TryGetOneShotTask(ownerSessionId, sessionId) ??
               throw new ArgumentException(
                   $"No registered one-shot terminal session matches '{sessionId}'.",
                   nameof(sessionId));
    }

    /// <summary>Cancels one one-shot by closing its session and returns the terminal task
    /// handle — the cancel half of the task plane's control contract (ADR 0030 decision 5).
    /// An already-terminal one-shot returns unchanged, so the operation is idempotent; the
    /// close removes a live one-shot from the registry, matching terminal_close semantics.</summary>
    /// <exception cref="ArgumentException">No registered one-shot matches the ids.</exception>
    internal async Task<TerminalTaskHandle> CancelOneShotAsync(
        AgentSessionId ownerSessionId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var handle = TryGetOneShotTask(ownerSessionId, sessionId) ??
                     throw new ArgumentException(
                         $"No registered one-shot terminal session matches '{sessionId}'.",
                         nameof(sessionId));
        if (TerminalTaskResourceSource.MapStatus(handle) != "working")
            return handle;

        await CloseAsync(ownerSessionId, sessionId, cancellationToken).ConfigureAwait(false);
        return handle with { State = "closed" };
    }

    private TerminalSession GetRegisteredOneShot(AgentSessionId ownerSessionId, string sessionId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (sessions.TryGetValue(ownerSessionId, out var owned) &&
                owned.TryGetValue(sessionId, out var session) &&
                session.Kind == TerminalSessionKind.OneShot)
                return session;
        }

        throw new ArgumentException(
            $"No registered one-shot terminal session matches '{sessionId}'.",
            nameof(sessionId));
    }

    private static TerminalTaskHandle ToTaskHandle(AgentSessionId ownerSessionId, TerminalSession session)
    {
        var snapshot = session.GetSnapshot();
        return new TerminalTaskHandle(ownerSessionId, snapshot.SessionId, snapshot.State, snapshot.ExitCode);
    }

    internal TerminalInfo[] List(AgentSessionId ownerSessionId)
    {
        TerminalSession[] snapshot;
        lock (gate)
        {
            ThrowIfDisposed();
            if (!sessions.TryGetValue(ownerSessionId, out var owned)) return [];
            snapshot = owned.Values.ToArray();
        }

        return snapshot
            .OrderByDescending(static session => session.IsDefault)
            .ThenBy(static session => session.SessionId, StringComparer.Ordinal)
            .Select(static session => session.GetSnapshot())
            .ToArray();
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

        return new TerminalCloseResult();
    }

    /// <summary>Releases a finished one-shot session: removed under the lock, then disposed
    /// outside it (the same order CloseAsync uses). A completed run's PTY is dead, so keeping it
    /// registered would permanently consume one of the agent's session slots.</summary>
    private async Task RemoveFinishedOneShotAsync(AgentSessionId ownerSessionId, TerminalSession session)
    {
        Remove(ownerSessionId, session.SessionId, session);
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not dispose finished one-shot terminal session {SessionId}.",
                session.SessionId);
        }
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

        return Reserve(ownerSessionId, DefaultSessionId, true, TerminalSessionKind.Persistent, options.Shell, options.Arguments);
    }

    private TerminalSession Reserve(
        AgentSessionId ownerSessionId,
        string sessionId,
        bool isDefault,
        TerminalSessionKind kind,
        string executable,
        IReadOnlyList<string> launchArguments)
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
                kind,
                executable,
                launchArguments,
                acquirer.Acquire(PermissionLayer.Empty, ownerSessionId),
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
