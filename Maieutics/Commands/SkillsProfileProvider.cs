using Maieutics.Agent;
using Maieutics.Skills;

namespace Maieutics.Commands;

/// <summary>
///     Decorates every acquired run profile with the live skill catalog section (ADR 0039):
///     the system prompt of the <em>next</em> run carries the catalog snapshot current at
///     acquisition. This is the whole propagation mechanism — an in-flight run keeps the
///     instructions it captured (invariant: active loops retain their captured prompt), and
///     the appended section never enters configuration-reload identity because it composes
///     after the lease is acquired. An empty catalog passes the lease through untouched.
/// </summary>
internal sealed class SkillsProfileProvider(
    IAgentRunProfileProvider inner,
    SkillCatalog catalog) : IAgentRunProfileProvider
{
    public async Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var lease = await inner.AcquireAsync(cancellationToken).ConfigureAwait(false) ??
                    throw new InvalidOperationException("The Agent run profile provider returned a null lease.");
        var composed = SkillPromptComposer.Compose(lease.Profile.Options.SystemPrompt, catalog.Current);
        if (composed is null) return lease;

        var profile = lease.Profile;
        var adjusted = new AgentRunProfile(
            profile.ChatClient,
            profile.Options with { SystemPrompt = composed },
            profile.ModelIdentity,
            profile.Capabilities,
            profile.HostedCapabilities,
            profile.Tools,
            profile.HostedTools);
        return new Lease(lease, adjusted);
    }

    private sealed class Lease(IAgentRunProfileLease inner, AgentRunProfile profile) : IAgentRunProfileLease
    {
        public AgentRunProfile Profile { get; } = profile;

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
