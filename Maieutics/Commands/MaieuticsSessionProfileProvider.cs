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
internal sealed class MaieuticsSessionProfileProvider : IAgentRunProfileProvider
{
    private readonly IMaieuticsRuntimeConfiguration runtime;
    private readonly Lock gate = new();
    private string? overrideProfileId;

    public MaieuticsSessionProfileProvider(IMaieuticsRuntimeConfiguration runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>Gets the session's current override, or <see langword="null" /> when the
    /// session follows the process selection.</summary>
    public string? Override
    {
        get
        {
            lock (gate)
            {
                return overrideProfileId;
            }
        }
    }

    /// <summary>Sets or clears the session's profile override (a configured profile id).</summary>
    public void SetOverride(string? profileId)
    {
        lock (gate)
        {
            overrideProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId;
        }
    }

    public async Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        string? overrideId;
        lock (gate)
        {
            overrideId = overrideProfileId;
        }

        if (overrideId is not null &&
            await runtime.AcquireProfileAsync(overrideId, cancellationToken).ConfigureAwait(false)
                is { } overrideLease)
        {
            return overrideLease;
        }

        return await runtime.AcquireAsync(cancellationToken).ConfigureAwait(false);
    }
}
