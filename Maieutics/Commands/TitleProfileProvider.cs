using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;
using Microsoft.Extensions.AI;

namespace Maieutics.Commands;

/// <summary>Adds the title tool and bounded display context to one immutable run profile.</summary>
internal sealed class TitleProfileProvider(IAgentRunProfileProvider inner, SessionTitleService titles,
    AgentSessionId sessionId) : IAgentRunProfileProvider
{
    public async Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var lease = await inner.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new Lease(lease, titles.Decorate(lease.Profile, sessionId));
        }
        catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private sealed class Lease(IAgentRunProfileLease inner, AgentRunProfile profile) : IAgentRunProfileLease
    {
        public AgentRunProfile Profile => profile;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TitleProposalResult))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class TitleToolJsonContext : JsonSerializerContext;
