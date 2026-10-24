using System.Collections.Concurrent;
using Maieutics.Permissions;

namespace Maieutics.DenoRepl;

/// <summary>
///     Owner-keyed cache of the effective REPL policy: session-scoped kernel gates (the IDE
///     workspace bridge, ADR 0042 decision 12) judge a script call against exactly the snapshot
///     the permission broker holds for that session's REPL child, so the bridged path and the
///     direct Deno path cannot diverge. Keyed by the owning agent session id — the identity the
///     control bus stamps on script invocations through the pid-verified owner mapping — cached
///     when the session's REPL starts (beside the broker pre-cache) and cleared when the session
///     closes. A session's most recently started REPL wins; the map is bounded because owner
///     sessions need no join callback at this layer.
/// </summary>
internal sealed class ReplOwnerPolicyCache
{
    private const int Capacity = 512;

    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, EffectivePolicy> _policies = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public void Cache(string ownerSessionId, EffectivePolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSessionId);
        ArgumentNullException.ThrowIfNull(policy);

        lock (_gate)
        {
            if (!_policies.ContainsKey(ownerSessionId)) _order.Enqueue(ownerSessionId);
            _policies[ownerSessionId] = policy;
            while (_order.Count > Capacity) _policies.TryRemove(_order.Dequeue(), out _);
        }
    }

    public void Clear(string ownerSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSessionId);
        lock (_gate)
        {
            _policies.TryRemove(ownerSessionId, out _);
        }
    }

    public void ClearAll()
    {
        lock (_gate)
        {
            _policies.Clear();
            _order.Clear();
        }
    }

    public EffectivePolicy? TryGet(string ownerSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSessionId);
        return _policies.TryGetValue(ownerSessionId, out var policy) ? policy : null;
    }
}
