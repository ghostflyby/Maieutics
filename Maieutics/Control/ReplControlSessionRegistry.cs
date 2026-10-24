using System.Collections.Concurrent;

namespace Maieutics.Control;

/// <summary>
///     Maps live Deno child process ids to their identity: the owning REPL session id for REPL
///     children, or the plugin host id for out-of-process plugin hosts. The process-wide control
///     channel attributes requests to either kind through peer process identity.
/// </summary>
internal sealed class ReplControlSessionRegistry
{
    private readonly Lock _changeLock = new();
    private readonly ConcurrentDictionary<int, string> _pluginHosts = new();
    private readonly ConcurrentDictionary<int, string> _sessions = new();
    private readonly ConcurrentDictionary<int, string> _ownerSessions = new();
    private TaskCompletionSource _registryChanged = CreateSignal();

    public void Register(int processId, string sessionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _sessions[processId] = sessionId;
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
        _ownerSessions[processId] = ownerSessionId;
        SignalChange();
    }

    public void Unregister(int processId)
    {
        _sessions.TryRemove(processId, out _);
        _ownerSessions.TryRemove(processId, out _);
        SignalChange();
    }

    internal async Task<bool> WaitForIdentityAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        while (true)
        {
            if (_sessions.ContainsKey(processId) || _pluginHosts.ContainsKey(processId))
                return true;

            Task changed;
            lock (_changeLock)
            {
                if (_sessions.ContainsKey(processId) || _pluginHosts.ContainsKey(processId))
                    return true;
                changed = _registryChanged.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void SignalChange()
    {
        TaskCompletionSource previous;
        lock (_changeLock)
        {
            previous = _registryChanged;
            _registryChanged = CreateSignal();
        }
        previous.TrySetResult();
    }

    private static TaskCompletionSource CreateSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public bool TryGetSession(int processId, out string sessionId)
    {
        if (_sessions.TryGetValue(processId, out var value))
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
        if (_ownerSessions.TryGetValue(processId, out var value))
        {
            ownerSessionId = value;
            return true;
        }

        ownerSessionId = string.Empty;
        return false;
    }

    public bool IsOwnedBy(int processId, string sessionId)
    {
        return _sessions.TryGetValue(processId, out var owned) &&
               string.Equals(owned, sessionId, StringComparison.Ordinal);
    }

    public bool ContainsSession(string sessionId)
    {
        return _sessions.Values.Contains(sessionId, StringComparer.Ordinal);
    }

    public void RegisterPluginHost(int processId, string hostId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        _pluginHosts[processId] = hostId;
        SignalChange();
    }

    public void UnregisterPluginHost(int processId)
    {
        _pluginHosts.TryRemove(processId, out _);
        SignalChange();
    }

    public bool TryGetPluginHost(int processId, out string hostId)
    {
        if (_pluginHosts.TryGetValue(processId, out var value))
        {
            hostId = value;
            return true;
        }

        hostId = string.Empty;
        return false;
    }

    public bool IsPluginHostOwnedBy(int processId, string hostId)
    {
        return _pluginHosts.TryGetValue(processId, out var owned) &&
               string.Equals(owned, hostId, StringComparison.Ordinal);
    }
}
