using System.ClientModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Maieutics.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace Maieutics.Providers.OpenAI;

internal sealed class OpenAiChatClientFactory : IConfiguredChatClientFactory
{
    public string ProviderName => "OpenAI";

    public IConfiguredChatClientSource BindSource(string sourceId, IConfigurationSection configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new OpenAiSourceOptions();
        configuration.Bind(options, static binder => binder.ErrorOnUnknownConfiguration = true);
        options.Validate();
        return new OpenAiSource(options);
    }

    private sealed class OpenAiSource(OpenAiSourceOptions options) : IConfiguredChatClientSource, IModelDiscoverySource
    {
        private static readonly HttpClient DiscoveryHttpClient = new();

        public string ProviderName => "OpenAI";

        public object ClientGenerationKey { get; } = new SourceGenerationKey(
            options.ApiFlavor,
            options.ApiKey,
            options.Endpoint?.AbsoluteUri);

        public AgentModelCapabilities Capabilities =>
            AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

        public IChatClient Create(string model)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            var credential = new ApiKeyCredential(options.ApiKey);
            var openAiClient = options.Endpoint is null
                ? new OpenAIClient(credential)
                : new OpenAIClient(credential, new OpenAIClientOptions { Endpoint = options.Endpoint });

#pragma warning disable OPENAI001 // The OpenAI .NET Responses surface is currently marked experimental.
            var client = options.ApiFlavor switch
            {
                OpenAiApiFlavor.Responses => openAiClient.GetResponsesClient().AsIChatClient(model),
                OpenAiApiFlavor.ChatCompletions => openAiClient.GetChatClient(model).AsIChatClient(),
                _ => throw new UnreachableException()
            };

            return new ConfigureOptionsChatClient(client, chatOptions =>
            {
                chatOptions.RawRepresentationFactory = _ => options.ApiFlavor switch
                {
                    OpenAiApiFlavor.Responses => new CreateResponseOptions { StoredOutputEnabled = false },
                    OpenAiApiFlavor.ChatCompletions => new ChatCompletionOptions { StoredOutputEnabled = false },
                    _ => throw new UnreachableException()
                };
            });
#pragma warning restore OPENAI001
        }

        public async ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
        {
            var endpoint = options.Endpoint is null
                ? new Uri("https://api.openai.com/v1/models")
                : new Uri(options.Endpoint, "models");

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", options.ApiKey);

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

                var ownedBy = model.TryGetProperty("owned_by", out var ownedByElement)
                    ? ownedByElement.GetString()
                    : null;

                DateTime? createdAt = null;
                if (model.TryGetProperty("created", out var createdElement) &&
                    createdElement.TryGetInt64(out var createdUnix))
                {
                    createdAt = DateTimeOffset.FromUnixTimeSeconds(createdUnix).UtcDateTime;
                }

                models.Add(new AgentModelDescriptor(id, "OpenAI", ownedBy, createdAt));
            }

            return models;
        }
    }

    private sealed class SourceGenerationKey(
        OpenAiApiFlavor apiFlavor,
        string apiKey,
        string? endpoint) : IEquatable<SourceGenerationKey>
    {
        private readonly OpenAiApiFlavor apiFlavor = apiFlavor;
        private readonly string apiKey = apiKey;
        private readonly string? endpoint = endpoint;

        public bool Equals(SourceGenerationKey? other) =>
            other is not null &&
            apiFlavor == other.apiFlavor &&
            string.Equals(apiKey, other.apiKey, StringComparison.Ordinal) &&
            string.Equals(endpoint, other.endpoint, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as SourceGenerationKey);

        public override int GetHashCode() => HashCode.Combine(
            apiFlavor,
            StringComparer.Ordinal.GetHashCode(apiKey),
            endpoint is null ? 0 : StringComparer.Ordinal.GetHashCode(endpoint));

        public override string ToString() =>
            $"SourceGenerationKey {{ ApiFlavor = {apiFlavor}, ApiKey = <redacted>, Endpoint = {endpoint ?? "<default>"} }}";
    }
}

internal sealed class OpenAiSourceOptions
{
    public string? Provider { get; set; }

    public OpenAiApiFlavor ApiFlavor { get; set; } = OpenAiApiFlavor.Responses;

    public string ApiKey { get; set; } = string.Empty;

    public Uri? Endpoint { get; set; }

    internal void Validate()
    {
        if (Provider is not null && !string.Equals(Provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The OpenAI source Provider must be 'OpenAI'.", nameof(Provider));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ApiKey);
        if (!Enum.IsDefined(ApiFlavor))
        {
            throw new ArgumentOutOfRangeException(nameof(ApiFlavor), ApiFlavor, "Unsupported OpenAI API flavor.");
        }

        if (Endpoint is not null &&
            (!Endpoint.IsAbsoluteUri || Endpoint.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("The OpenAI endpoint must be an absolute HTTP or HTTPS URI.",
                nameof(Endpoint));
        }
    }
}