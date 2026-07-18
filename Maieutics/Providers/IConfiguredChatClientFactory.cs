using Microsoft.Extensions.AI;

namespace Maieutics.Providers;

internal interface IConfiguredChatClientFactory
{
    string ProviderName { get; }

    object GetConfigurationKey(MaieuticsOptions options);

    IChatClient Create(MaieuticsOptions options);
}