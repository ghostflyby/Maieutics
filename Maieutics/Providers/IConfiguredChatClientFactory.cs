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

    object ConfigurationKey { get; }

    AgentModelCapabilities Capabilities { get; }

    IChatClient Create(string model);
}