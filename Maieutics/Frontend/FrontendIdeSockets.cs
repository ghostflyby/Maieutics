using System.Collections.Concurrent;
using Maieutics.Configuration;

namespace Maieutics.Frontend;

/// <summary>
///     Tracks which sessions have a live events socket that announced IDE capability
///     (ADR 0042 decision 2): the announcement rides the events upgrade request's
///     <c>ide</c> query parameter, is per socket, and is cleared on disconnect — this
///     registry counts announcing sockets per session so the gate answers both the
///     profile-level (any socket) and invocation-level (this session's socket) checks.
///     Sessions are keyed in the wire's N-form.
/// </summary>
internal sealed class FrontendIdeSockets : IIdeFrontendGate
{
    /// <summary>The IDE capability version this build speaks; announced versions must
    /// match exactly (older frontends never announce; newer ones are not understood).</summary>
    internal const int Version = 1;

    private readonly ConcurrentDictionary<string, int> counts = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool HasIdeFrontend => !counts.IsEmpty;

    /// <inheritdoc />
    public bool IsIdeFrontendConnected(string sessionId)
    {
        return counts.TryGetValue(sessionId, out var count) && count > 0;
    }

    /// <summary>Records one announcing socket for the session (refcounted: a session may
    /// hold several sockets).</summary>
    public void Announce(string sessionId)
    {
        counts.AddOrUpdate(sessionId, 1, static (_, count) => count + 1);
    }

    /// <summary>Drops one announcing socket; the session leaves the gate at zero.</summary>
    public void Withdraw(string sessionId)
    {
        while (true)
        {
            if (!counts.TryGetValue(sessionId, out var count)) return;

            if (count <= 1)
            {
                if (((ICollection<KeyValuePair<string, int>>)counts).Remove(
                        new KeyValuePair<string, int>(sessionId, count)))
                    return;
            }
            else if (counts.TryUpdate(sessionId, count - 1, count))
            {
                return;
            }
        }
    }
}
