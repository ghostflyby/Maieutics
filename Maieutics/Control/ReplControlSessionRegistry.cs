using System.Collections.Concurrent;

namespace Maieutics.Control;

/// <summary>
///     Maps live Deno child process ids to their identity: the owning REPL session id for REPL
///     children, or the plugin host id for out-of-process plugin hosts. The process-wide control
///     channel attributes requests to either kind through peer process identity.
/// </summary>
internal sealed class ReplControlSessionRegistry
{
    private readonly Lock changeLock = new();
    private readonly ConcurrentDictionary<int, string> pluginHosts = new();
    private readonly ConcurrentDictionary<int, string> sessions = new();
    private readonly ConcurrentDictionary<int, string> ownerSessions = new();
    private TaskCompletionSource registryChanged = CreateSignal();

    public void Register(int processId, string sessionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        sessions[processId] = sessionId;
        SignalChange();
    }

    /// <summary>Records the owning agent session of a REPL child, beside the pid-verified
    /// hello identity: the handshake authorizes the REPL-local session id the client claims,
    /// while session-scoped gates (the IDE workspace bridge, ADR 0042 decision 12) key on
    /// the agent session the events announce uses — the pid mapping, not the client's claim,
    /// is the trustworthy translation between the two.</summary>
    public void RegisterOwner(int processId, string ownerSessionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSessionId);
        ownerSessions[processId] = ownerSessionId;
        SignalChange();
    }

    public void Unregister(int processId)
    {
        sessions.TryRemove(processId, out _);
        ownerSessions.TryRemove(processId, out _);
        SignalChange();
    }

    internal async Task<bool> WaitForIdentityAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        while (true)
        {
            if (sessions.ContainsKey(processId) || pluginHosts.ContainsKey(processId))
                return true;

            Task changed;
            lock (changeLock)
            {
                if (sessions.ContainsKey(processId) || pluginHosts.ContainsKey(processId))
                    return true;
                changed = registryChanged.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void SignalChange()
    {
        TaskCompletionSource previous;
        lock (changeLock)
        {
            previous = registryChanged;
            registryChanged = CreateSignal();
        }
        previous.TrySetResult();
    }

    private static TaskCompletionSource CreateSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public bool TryGetSession(int processId, out string sessionId)
    {
        if (sessions.TryGetValue(processId, out var value))
        {
            sessionId = value;
            return true;
        }

        sessionId = string.Empty;
        return false;
    }

    /// <summary>The owning agent session id of a REPL child, when one was registered.</summary>
    public bool TryGetOwnerSession(int processId, out string ownerSessionId)
    {
        if (ownerSessions.TryGetValue(processId, out var value))
        {
            ownerSessionId = value;
            return true;
        }

        ownerSessionId = string.Empty;
        return false;
    }

    public bool IsOwnedBy(int processId, string sessionId)
    {
        return sessions.TryGetValue(processId, out var owned) &&
               string.Equals(owned, sessionId, StringComparison.Ordinal);
    }

    public bool ContainsSession(string sessionId)
    {
        return sessions.Values.Contains(sessionId, StringComparer.Ordinal);
    }

    public void RegisterPluginHost(int processId, string hostId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        pluginHosts[processId] = hostId;
        SignalChange();
    }

    public void UnregisterPluginHost(int processId)
    {
        pluginHosts.TryRemove(processId, out _);
        SignalChange();
    }

    public bool TryGetPluginHost(int processId, out string hostId)
    {
        if (pluginHosts.TryGetValue(processId, out var value))
        {
            hostId = value;
            return true;
        }

        hostId = string.Empty;
        return false;
    }

    public bool IsPluginHostOwnedBy(int processId, string hostId)
    {
        return pluginHosts.TryGetValue(processId, out var owned) &&
               string.Equals(owned, hostId, StringComparison.Ordinal);
    }
}