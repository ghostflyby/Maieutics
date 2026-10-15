using System.Collections.Concurrent;
using System.Text.Json;
using Maieutics.Configuration;

namespace Maieutics.Frontend;

/// <summary>
///     Registry of the events sockets that announced IDE capability (ADR 0042,
///     decisions 2 and 10): it backs the gate (which sessions have a live
///     announcing socket) **and** the script/plugin request channel — an
///     <c>ide.request</c> raised outside an agent run (REPL <c>tools.invoke</c>,
///     plugin <c>capability.invoke</c>) has no run stream to ride, so it is
///     pushed directly to every registered announcing socket (unordered, not
///     replayed, private to announcers). Answers still travel the REST endpoint,
///     whose 404 idempotence tolerates the missing replay.
/// </summary>
internal sealed class FrontendIdeSockets : IIdeFrontendGate
{
    /// <summary>The IDE capability version this build speaks; announced versions must
    /// match exactly (older frontends never announce; newer ones are not understood).</summary>
    internal const int Version = 1;

    private sealed record Registration(string SessionId, Func<FrontendEventFrame, CancellationToken, Task> SendAsync, SemaphoreSlim SendGate);

    private readonly ConcurrentDictionary<Guid, Registration> registrations = new();

    private readonly ConcurrentDictionary<string, int> counts = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool HasIdeFrontend => !counts.IsEmpty;

    /// <inheritdoc />
    public bool IsIdeFrontendConnected(string sessionId)
    {
        return counts.TryGetValue(sessionId, out var count) && count > 0;
    }

    /// <summary>Registers one announcing socket (refcounted per session: a session
    /// may hold several sockets). Returns the registration id to withdraw with.</summary>
    public Guid Announce(string sessionId, Func<FrontendEventFrame, CancellationToken, Task> sendAsync)
    {
        counts.AddOrUpdate(sessionId, 1, static (_, count) => count + 1);
        var id = Guid.NewGuid();
        registrations[id] = new Registration(sessionId, sendAsync, new SemaphoreSlim(1, 1));
        return id;
    }

    /// <summary>Drops one announcing socket; the session leaves the gate at zero.</summary>
    public void Withdraw(string sessionId, Guid registrationId)
    {
        if (registrations.TryRemove(registrationId, out _))
        {
            WithdrawCount(sessionId);
        }
    }

    private void WithdrawCount(string sessionId)
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

    /// <summary>Pushes one frame to every live announcing socket. A socket that
    /// fails to send is dropped (its disconnect path withdraws it again; both
    /// are idempotent). Returns false when no socket was reachable — the
    /// caller surfaces that as the typed unavailable family.</summary>
    public async Task<bool> TrySendToAnnouncingAsync(FrontendEventFrame frame, CancellationToken cancellationToken)
    {
        var sent = false;
        foreach (var (id, registration) in registrations)
        {
            try
            {
                await registration.SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await registration.SendAsync(frame, cancellationToken).ConfigureAwait(false);
                    sent = true;
                }
                finally
                {
                    registration.SendGate.Release();
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or
                InvalidOperationException or System.IO.IOException)
            {
                // The socket died mid-hand-off; its own drain loop withdraws the
                // registration. One dead announcer must not block the others.
                Withdraw(registration.SessionId, id);
            }
        }

        return sent;
    }
}
