using Maieutics.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Maieutics.Providers.Anthropic;

internal sealed class AnthropicChatClientFactory : IConfiguredChatClientFactory
{
    public string ProviderName => "Anthropic";

    public IConfiguredChatClientSource BindSource(string sourceId, IConfigurationSection configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new AnthropicSourceOptions();
        configuration.Bind(options, static binder => binder.ErrorOnUnknownConfiguration = true);
        options.Validate();
        return new AnthropicSource(this, options);
    }

    public IChatClient Create(string model, AnthropicSourceOptions source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(source);
        source.Validate();
        return new AnthropicMessagesChatClient(model, source.ApiKey, source.Endpoint);
    }

    private sealed class AnthropicSource(
        AnthropicChatClientFactory factory,
        AnthropicSourceOptions options) : IConfiguredChatClientSource
    {
        public string ProviderName => "Anthropic";

        public object ConfigurationKey { get; } = new SourceKey(options.ApiKey, options.Endpoint?.AbsoluteUri);

        public AgentModelCapabilities Capabilities =>
            AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

        public IChatClient Create(string model) => factory.Create(model, options);
    }

    private sealed record SourceKey(string ApiKey, string? Endpoint);
}