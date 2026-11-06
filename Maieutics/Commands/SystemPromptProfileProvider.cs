using System.Text;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Plugins.Contributions;

namespace Maieutics.Commands;

/// <summary>Captures system instruction extensions once per conversation run without changing
/// configured profiles, canonical history or the provider-neutral Agent runtime.</summary>
internal sealed class SystemPromptProfileProvider(
    IAgentRunProfileProvider inner, ISystemPromptSource source) : IAgentRunProfileProvider
{
    public async Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var lease = await inner.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var contributions = await source.GetSystemPromptsAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (contributions.Count == 0) return lease;
            var profile = lease.Profile;
            var composed = new StringBuilder(profile.Options.SystemPrompt?.TrimEnd() ?? string.Empty);
            var characters = 0;
            foreach (var entry in contributions)
            {
                // Identity framing is kernel-authored; plugin-provided content is intentionally
                // system-level instruction under declaration approval, never promoted from tool output.
                var separator = composed.Length == 0 ? string.Empty : "\n\n";
                var section = $"{separator}### SystemPrompt {JsonEncodedText.Encode(entry.Source)}/{entry.Name}\n\n{entry.Content}";
                characters += section.Length;
                if (characters > SystemPromptContributionKind.MaximumTotalCharacters)
                    throw new InvalidOperationException("SystemPrompt contributions exceed the per-run character budget.");
                composed.Append(section);
            }
            var adjusted = new AgentRunProfile(
                profile.ChatClient,
                profile.Options with { SystemPrompt = composed.ToString() },
                profile.ModelIdentity,
                profile.Capabilities,
                profile.HostedCapabilities,
                profile.Tools,
                profile.HostedTools);
            return new Lease(lease, adjusted);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Lease(IAgentRunProfileLease inner, AgentRunProfile profile) : IAgentRunProfileLease
    {
        public AgentRunProfile Profile => profile;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
