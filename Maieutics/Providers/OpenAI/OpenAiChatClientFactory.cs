using System.ClientModel;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace Maieutics.Providers.OpenAI;

internal sealed class OpenAiChatClientFactory : IConfiguredChatClientFactory
{
    public string ProviderName => "OpenAI";

    public object GetConfigurationKey(MaieuticsOptions options)
    {
        Validate(options);
        var openAi = options.Providers.OpenAI;
        return new ConfigurationKey(
            options.Model.Name,
            openAi.ApiFlavor,
            openAi.ApiKey,
            openAi.Endpoint?.AbsoluteUri);
    }

    public IChatClient Create(MaieuticsOptions options)
    {
        Validate(options);
        var openAi = options.Providers.OpenAI;
        var credential = new ApiKeyCredential(openAi.ApiKey);
        var openAiClient = openAi.Endpoint is null
            ? new OpenAIClient(credential)
            : new OpenAIClient(credential, new OpenAIClientOptions { Endpoint = openAi.Endpoint });

#pragma warning disable OPENAI001 // The OpenAI .NET Responses surface is currently marked experimental.
        var client = openAi.ApiFlavor switch
        {
            OpenAiApiFlavor.Responses => openAiClient.GetResponsesClient().AsIChatClient(options.Model.Name),
            OpenAiApiFlavor.ChatCompletions => openAiClient.GetChatClient(options.Model.Name).AsIChatClient(),
            _ => throw new UnreachableException()
        };

        return new ConfigureOptionsChatClient(client, chatOptions =>
        {
            chatOptions.RawRepresentationFactory = _ => openAi.ApiFlavor switch
            {
                OpenAiApiFlavor.Responses => new CreateResponseOptions { StoredOutputEnabled = false },
                OpenAiApiFlavor.ChatCompletions => new ChatCompletionOptions { StoredOutputEnabled = false },
                _ => throw new UnreachableException()
            };
        });
#pragma warning restore OPENAI001
    }

    private static void Validate(MaieuticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model.Name);
        var openAi = options.Providers.OpenAI;
        ArgumentException.ThrowIfNullOrWhiteSpace(openAi.ApiKey);
        if (!Enum.IsDefined(openAi.ApiFlavor))
        {
            throw new ArgumentOutOfRangeException(nameof(options), openAi.ApiFlavor,
                "Unsupported OpenAI API flavor.");
        }

        if (openAi.Endpoint is not null &&
            (!openAi.Endpoint.IsAbsoluteUri || openAi.Endpoint.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("The OpenAI endpoint must be an absolute HTTP or HTTPS URI.", nameof(options));
        }
    }

    private sealed record ConfigurationKey(
        string Model,
        OpenAiApiFlavor ApiFlavor,
        string ApiKey,
        string? Endpoint);
}