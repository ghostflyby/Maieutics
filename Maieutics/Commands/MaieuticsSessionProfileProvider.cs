using Maieutics.Agent;
using Maieutics.Configuration;

namespace Maieutics.Commands;

/// <summary>
///     One live session's model-profile source. Resolves the session's own override first
///     (set by a session-addressed <c>%model use</c> cell or a fork's <c>profileId</c>),
///     then falls back to the process selection. An override that no longer resolves —
///     removed by a configuration reload — falls back too instead of failing the turn.
///     Without a runtime configuration (persistence-less hosts, tests) the provider is
///     absent and sessions share the manager's process provider.
/// </summary>
internal sealed class MaieuticsSessionProfileProvider(IMaieuticsRuntimeConfiguration runtime) : IAgentRunProfileProvider
{
    private readonly IMaieuticsRuntimeConfiguration _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    private readonly Lock _gate = new();
    private string? _overrideProfileId;

    /// <summary>Gets the session's current override, or <see langword="null" /> when the
    /// session follows the process selection.</summary>
    public string? Override
    {
        get
        {
            lock (_gate)
            {
                return _overrideProfileId;
            }
        }
    }

    /// <summary>Sets or clears the session's profile override (a configured profile id).</summary>
    public void SetOverride(string? profileId)
    {
        lock (_gate)
        {
            _overrideProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId;
        }
    }

    public async Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        string? overrideId;
        lock (_gate)
        {
            overrideId = _overrideProfileId;
        }

        if (overrideId is not null &&
            await _runtime.AcquireProfileAsync(overrideId, cancellationToken).ConfigureAwait(false)
                is { } overrideLease)
        {
            return overrideLease;
        }

        return await _runtime.AcquireAsync(cancellationToken).ConfigureAwait(false);
    }
}
