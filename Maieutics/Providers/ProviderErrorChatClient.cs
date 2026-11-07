using System.Runtime.CompilerServices;
using Maieutics.Agent;
using Maieutics.Providers.OpenAI;
using Microsoft.Extensions.AI;

namespace Maieutics.Providers;

/// <summary>Keeps SDK and transport exceptions inside the provider boundary.</summary>
internal sealed class ProviderErrorChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            OpenAiResponseErrors.ThrowIfFailed(response);
            return response;
        }
        catch (Exception exception) when (ShouldNormalize(exception, cancellationToken))
        {
            throw ProviderErrors.Normalize(exception);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<ChatResponseUpdate> updates;
        try
        {
            updates = base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception exception) when (ShouldNormalize(exception, cancellationToken))
        {
            throw ProviderErrors.Normalize(exception);
        }

        await using (updates.ConfigureAwait(false))
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await updates.MoveNextAsync().ConfigureAwait(false);
                    if (hasNext) OpenAiResponseErrors.ThrowIfFailed(updates.Current);
                }
                catch (Exception exception) when (ShouldNormalize(exception, cancellationToken))
                {
                    throw ProviderErrors.Normalize(exception);
                }
                if (!hasNext) yield break;
                yield return updates.Current;
            }
        }
    }

    private static bool ShouldNormalize(Exception exception, CancellationToken cancellationToken) =>
        exception is not AgentException &&
        !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);
}
