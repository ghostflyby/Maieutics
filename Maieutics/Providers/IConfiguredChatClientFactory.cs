using Maieutics.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Maieutics.Providers;

internal interface IConfiguredChatClientFactory
{
    string ProviderName { get; }

    IConfiguredChatClientSource BindSource(string sourceId, IConfigurationSection configuration);
}

internal interface IConfiguredChatClientSource
{
    string ProviderName { get; }

    object ClientGenerationKey { get; }

    AgentModelCapabilities Capabilities { get; }

    /// <summary>Gets the provider API endpoint, when one is configured.</summary>
    Uri? EndpointUri { get; }

    /// <summary>Gets the vendor identity owning this source, when one is configured.</summary>
    string? Vendor { get; }

    /// <summary>
    ///     Gets the provider-neutral capability names the source's API format can express. An
    ///     unknown or unsupported format returns an empty list.
    /// </summary>
    IReadOnlyList<string> FormatCapabilities { get; }

    IChatClient Create(string model);
}
