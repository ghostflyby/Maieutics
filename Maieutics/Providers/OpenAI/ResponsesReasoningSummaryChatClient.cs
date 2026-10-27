using System.Runtime.CompilerServices;
using Maieutics.Agent;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace Maieutics.Providers.OpenAI;

#pragma warning disable OPENAI001 // The OpenAI .NET Responses surface is currently marked experimental.

/// <summary>Explicitly permits only the Responses summary-text delta for display. Raw reasoning
/// text, signatures, encrypted content, and completed-item replay state remain private.</summary>
internal sealed class ResponsesReasoningSummaryChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var summaryParts = new HashSet<(string ItemId, int SummaryIndex)>();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (update.RawRepresentation is StreamingResponseReasoningSummaryTextDeltaUpdate summary)
            {
                var separator = summaryParts.Add((summary.ItemId, summary.SummaryIndex)) && summaryParts.Count > 1
                    ? "\n\n"
                    : string.Empty;
                update.AdditionalProperties ??= [];
                update.AdditionalProperties[AgentReasoningSummaryDelta.UpdatePropertyName] = separator + summary.Delta;
            }
            yield return update;
        }
    }
}

#pragma warning restore OPENAI001
