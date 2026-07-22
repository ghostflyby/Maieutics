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

        public object ClientGenerationKey { get; } =
            new SourceGenerationKey(options.ApiKey, options.Endpoint?.AbsoluteUri);

        public AgentModelCapabilities Capabilities =>
            AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

        public IChatClient Create(string model) => factory.Create(model, options);
    }

    private sealed class SourceGenerationKey(string apiKey, string? endpoint) : IEquatable<SourceGenerationKey>
    {
        private readonly string _apiKey = apiKey;
        private readonly string? _endpoint = endpoint;

        public bool Equals(SourceGenerationKey? other) =>
            other is not null &&
            string.Equals(_apiKey, other._apiKey, StringComparison.Ordinal) &&
            string.Equals(_endpoint, other._endpoint, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as SourceGenerationKey);

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(_apiKey),
            _endpoint is null ? 0 : StringComparer.Ordinal.GetHashCode(_endpoint));

        public override string ToString() =>
            $"SourceGenerationKey {{ ApiKey = <redacted>, Endpoint = {_endpoint ?? "<default>"} }}";
    }
}