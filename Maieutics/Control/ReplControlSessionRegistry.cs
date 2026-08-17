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
    private TaskCompletionSource registryChanged = CreateSignal();

    public void Register(int processId, string sessionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        sessions[processId] = sessionId;
        SignalChange();
    }

    public void Unregister(int processId)
    {
        sessions.TryRemove(processId, out _);
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