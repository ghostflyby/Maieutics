using Maieutics.Agent;
using Microsoft.Extensions.AI;

namespace Maieutics.Providers.OpenAI;

/// <summary>
///     Enforces the configured per-request ceiling on provider-side built-in tool calls
///     (<see cref="AgentChatOptionKeys.MaxBuiltinToolCalls" />) for providers whose wire has no
///     native cap parameter. The provider executes built-in tools like web search server-side,
///     so the ceiling cannot be pushed onto the request; instead the client counts the built-in
///     tool calls the response surfaces and fails the request with a typed error once the
///     ceiling is exceeded. With no configured limit this wrapper is pass-through.
/// </summary>
internal sealed class BuiltinToolCallLimitChatClient(IChatClient innerClient) : IChatClient
{
    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await innerClient
            .GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);
        AssertWithinLimit(response.Messages.SelectMany(static message => message.Contents), options);
        return response;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seen = 0;
        var limit = ReadMaxBuiltinToolCalls(options);
        await foreach (var update in innerClient.GetStreamingResponseAsync(messages, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (limit is { } ceiling)
            {
                seen += update.Contents.OfType<WebSearchToolCallContent>().Count();
                if (seen > ceiling)
                    throw new AgentToolLimitExceededException("MaxBuiltinToolCalls", ceiling);
            }

            yield return update;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        innerClient.GetService(serviceType, serviceKey);

    /// <inheritdoc />
    void IDisposable.Dispose() => innerClient.Dispose();

    private static int? ReadMaxBuiltinToolCalls(ChatOptions? options)
    {
        return options?.AdditionalProperties?.TryGetValue(AgentChatOptionKeys.MaxBuiltinToolCalls, out var value) == true &&
               value is int limit
            ? limit
            : null;
    }

    private static void AssertWithinLimit(IEnumerable<AIContent> contents, ChatOptions? options)
    {
        if (ReadMaxBuiltinToolCalls(options) is not { } ceiling) return;

        var seen = contents.OfType<WebSearchToolCallContent>().Count();
        if (seen > ceiling) throw new AgentToolLimitExceededException("MaxBuiltinToolCalls", ceiling);
    }
}
