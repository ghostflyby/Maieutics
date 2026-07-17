using System.ClientModel;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace Maieutics.Providers.OpenAI;

internal static class OpenAiChatClientFactory
{
    public static IChatClient Create(string model, MaieuticsOpenAIOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        if (!Enum.IsDefined(options.ApiFlavor))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ApiFlavor, "Unsupported OpenAI API flavor.");
        }

        if (options.Endpoint is not null &&
            (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("The OpenAI endpoint must be an absolute HTTP or HTTPS URI.", nameof(options));
        }

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