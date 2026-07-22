using System.ClientModel;
using System.Diagnostics;
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

    private sealed class OpenAiSource(OpenAiSourceOptions options) : IConfiguredChatClientSource
    {
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
    }

    private sealed class SourceGenerationKey(
        OpenAiApiFlavor apiFlavor,
        string apiKey,
        string? endpoint) : IEquatable<SourceGenerationKey>
    {
        private readonly OpenAiApiFlavor _apiFlavor = apiFlavor;
        private readonly string _apiKey = apiKey;
        private readonly string? _endpoint = endpoint;

        public bool Equals(SourceGenerationKey? other) =>
            other is not null &&
            _apiFlavor == other._apiFlavor &&
            string.Equals(_apiKey, other._apiKey, StringComparison.Ordinal) &&
            string.Equals(_endpoint, other._endpoint, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as SourceGenerationKey);

        public override int GetHashCode() => HashCode.Combine(
            _apiFlavor,
            StringComparer.Ordinal.GetHashCode(_apiKey),
            _endpoint is null ? 0 : StringComparer.Ordinal.GetHashCode(_endpoint));

        public override string ToString() =>
            $"SourceGenerationKey {{ ApiFlavor = {_apiFlavor}, ApiKey = <redacted>, Endpoint = {_endpoint ?? "<default>"} }}";
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