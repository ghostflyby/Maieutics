using System.Collections.Concurrent;

namespace Maieutics.Control;

/// <summary>
/// Maps live Deno REPL child process ids to the owning REPL session id so the process-wide
/// control channel can attribute requests to a session.
/// </summary>
internal sealed class ReplControlSessionRegistry
{
    private readonly ConcurrentDictionary<int, string> sessions = new();

    public void Register(int processId, string sessionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        sessions[processId] = sessionId;
    }

    public void Unregister(int processId)
    {
        sessions.TryRemove(processId, out _);
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
}
