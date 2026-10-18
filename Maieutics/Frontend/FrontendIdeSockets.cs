using System.Collections.Concurrent;
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
///     ADR 0042 decision 12 adds the workspace bridge's arbitration: every
///     announcement carries a monotonic registration sequence, and the workspace
///     file system is served from the *earliest live announcement* of the calling
///     session — the elected authority — through <see cref="TryGetAuthority" /> and
///     <see cref="TrySendToAsync" />. Workspace fs requests are pushed to that one
///     socket only (another announcer never sees the frame, so the broadcast's
///     first-answer-wins race cannot answer a read with a different window's
///     buffer); a withdraw re-elects the next-earliest deterministically.
/// </summary>
internal sealed class FrontendIdeSockets : IIdeFrontendGate
{
    /// <summary>The IDE capability version this build speaks; announced versions must
    /// match exactly (older frontends never announce; newer ones are not understood).</summary>
    internal const int Version = 1;

    private sealed record Registration(string SessionId, long Sequence, Func<FrontendEventFrame, CancellationToken, Task> SendAsync, SemaphoreSlim SendGate);

    private readonly ConcurrentDictionary<Guid, Registration> registrations = new();

    private readonly ConcurrentDictionary<string, int> counts = new(StringComparer.Ordinal);

    private long sequence;

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
        registrations[id] = new Registration(sessionId, Interlocked.Increment(ref sequence), sendAsync, new SemaphoreSlim(1, 1));
        return id;
    }

    /// <summary>Drops one announcing socket; the session leaves the gate at zero.
    /// If the withdrawn socket was the session's (or the process-wide) authority,
    /// the next-earliest live announcement takes over at the next acquire.</summary>
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

    /// <summary>Elects the workspace bridge's authority (ADR 0042 decision 12): the
    /// earliest live announcement of <paramref name="sessionId" /> — or of the whole
    /// process when the caller has no session (context-less script relays). Deterministic
    /// under any number of attached IDEs and IDE kinds; null when none is live.</summary>
    public IdeAnnouncerHandle? TryGetAuthority(string? sessionId)
    {
        Registration? best = null;
        var bestId = Guid.Empty;
        foreach (var (id, registration) in registrations)
        {
            if (sessionId is not null &&
                !string.Equals(registration.SessionId, sessionId, StringComparison.Ordinal))
                continue;

            if (best is null || registration.Sequence < best.Sequence)
            {
                best = registration;
                bestId = id;
            }
        }

        return best is null ? null : new IdeAnnouncerHandle(best.SessionId, bestId, best.SendAsync);
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
                InvalidOperationException or IOException)
            {
                // The socket died mid-hand-off; its own drain loop withdraws the
                // registration. One dead announcer must not block the others.
                Withdraw(registration.SessionId, id);
            }
        }

        return sent;
    }

    /// <summary>Pushes one frame to exactly the named registration — the workspace
    /// bridge's targeted delivery. A failed send withdraws the registration and
    /// returns false; the caller surfaces the lost authority as a typed failure
    /// instead of silently re-asking a different announcer.</summary>
    public async Task<bool> TrySendToAsync(Guid registrationId, FrontendEventFrame frame, CancellationToken cancellationToken)
    {
        if (!registrations.TryGetValue(registrationId, out var registration)) return false;

        try
        {
            await registration.SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await registration.SendAsync(frame, cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                registration.SendGate.Release();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            Withdraw(registration.SessionId, registrationId);
            return false;
        }
    }
}

/// <summary>The elected authority's handle for one bridge acquire: enough to push
/// an <c>ide.request</c> frame to that announcer's socket and nothing more — the
/// registration itself stays private to the sockets registry.</summary>
internal sealed record IdeAnnouncerHandle(
    string SessionId,
    Guid RegistrationId,
    Func<FrontendEventFrame, CancellationToken, Task> SendAsync);
