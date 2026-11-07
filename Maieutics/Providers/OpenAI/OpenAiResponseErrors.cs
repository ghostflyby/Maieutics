using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace Maieutics.Providers.OpenAI;

#pragma warning disable OPENAI001 // The OpenAI .NET Responses surface is currently marked experimental.
#pragma warning disable SCME0001 // The pinned SDK preserves Chat error objects in its NativeAOT-safe JsonPatch API.

/// <summary>The SDK projects Responses failures as updates/content rather than throwing.</summary>
internal static class OpenAiResponseErrors
{
    internal static void ThrowIfFailed(ChatResponseUpdate update)
    {
        switch (update.RawRepresentation)
        {
            case StreamingChatCompletionUpdate chat when chat.Patch.Contains("$.error"u8):
                chat.Patch.TryGetValue("$.error.code"u8, out string? chatCode);
                if (string.IsNullOrEmpty(chatCode))
                    chat.Patch.TryGetValue("$.error.type"u8, out chatCode);
                Throw(chatCode);
                break;
            case StreamingResponseErrorUpdate:
                // The MEAI adapter also reads nested error.code from compatible endpoints.
                Throw(update.Contents.OfType<ErrorContent>().FirstOrDefault()?.ErrorCode);
                break;
            case StreamingResponseFailedUpdate failed:
                Throw(failed.Response.Error?.Code.ToString());
                break;
        }
    }

    internal static void ThrowIfFailed(ChatResponse response)
    {
        if (response.RawRepresentation is ChatCompletion chat && chat.Patch.Contains("$.error"u8))
        {
            chat.Patch.TryGetValue("$.error.code"u8, out string? code);
            if (string.IsNullOrEmpty(code)) chat.Patch.TryGetValue("$.error.type"u8, out code);
            Throw(code);
        }
        if (response.RawRepresentation is ResponseResult { Error: { } error })
            Throw(error.Code.ToString());
    }

    private static void Throw(string? code) => throw ProviderErrors.FromResponse(null, code,
        new InvalidDataException("The provider reported a response failure."));
}

#pragma warning restore OPENAI001
#pragma warning restore SCME0001
