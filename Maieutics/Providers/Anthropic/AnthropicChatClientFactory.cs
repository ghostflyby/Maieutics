using System.Text.Json;
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
        return new AnthropicSource(options);
    }

    public static IChatClient Create(string model, AnthropicSourceOptions source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(source);
        source.Validate();
        return new AnthropicMessagesChatClient(model, source.ApiKey, source.Endpoint);
    }

    private sealed class AnthropicSource(AnthropicSourceOptions options) : IConfiguredChatClientSource, IModelDiscoverySource
    {
        private static readonly HttpClient DiscoveryHttpClient = new();

        public string ProviderName => "Anthropic";

        public object ClientGenerationKey { get; } =
            new SourceGenerationKey(options.ApiKey, options.Endpoint?.AbsoluteUri);

        public AgentModelCapabilities Capabilities =>
            AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

        public IChatClient Create(string model) => AnthropicChatClientFactory.Create(model, options);

        public async ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
        {
            var endpoint = options.Endpoint is null
                ? new Uri("https://api.anthropic.com/v1/models")
                : new Uri(options.Endpoint, "models");

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Add("x-api-key", options.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            using var response = await DiscoveryHttpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var modelsElement = document.RootElement.GetProperty("data");

            var models = new List<AgentModelDescriptor>(modelsElement.GetArrayLength());
            foreach (var model in modelsElement.EnumerateArray())
            {
                var id = model.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                DateTime? createdAt = null;
                if (model.TryGetProperty("created_at", out var createdElement) &&
                    createdElement.TryGetDateTime(out var createdDt))
                {
                    createdAt = createdDt;
                }

                models.Add(new AgentModelDescriptor(id, "Anthropic", "Anthropic", createdAt));
            }

            return models;
        }
    }

    private sealed class SourceGenerationKey(string apiKey, string? endpoint) : IEquatable<SourceGenerationKey>
    {
        private readonly string apiKey = apiKey;
        private readonly string? endpoint = endpoint;

        public bool Equals(SourceGenerationKey? other) =>
            other is not null &&
            string.Equals(apiKey, other.apiKey, StringComparison.Ordinal) &&
            string.Equals(endpoint, other.endpoint, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as SourceGenerationKey);

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(apiKey),
            endpoint is null ? 0 : StringComparer.Ordinal.GetHashCode(endpoint));

        public override string ToString() =>
            $"SourceGenerationKey {{ ApiKey = <redacted>, Endpoint = {endpoint ?? "<default>"} }}";
    }
}