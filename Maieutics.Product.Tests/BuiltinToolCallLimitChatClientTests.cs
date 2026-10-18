using Maieutics.Agent;
using Maieutics.Providers.OpenAI;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>Drives the built-in tool call ceiling wrapper directly: providers without a wire
/// cap must fail a request with a typed error once the response surfaces more built-in calls
/// than the configured limit, and pass everything through when no limit is configured.</summary>
public sealed class BuiltinToolCallLimitChatClientTests
{
    [Fact]
    public async Task WithoutALimitEveryUpdateIsPassedThrough()
    {
        var inner = new StubChatClient(WebSearchUpdates(calls: 5));
        using var client = new BuiltinToolCallLimitChatClient(inner);
        var options = new ChatOptions();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "search")], options, TestContext.Current.CancellationToken))
            updates.Add(update);

        Assert.Equal(5, updates.Count());
        Assert.Equal(5, updates.SelectMany(static update => update.Contents)
            .OfType<WebSearchToolCallContent>().Count());
    }

    [Fact]
    public async Task CallsWithinTheLimitStreamFully()
    {
        var inner = new StubChatClient(WebSearchUpdates(calls: 2));
        using var client = new BuiltinToolCallLimitChatClient(inner);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "search")],
                           OptionsWithLimit(2),
                           TestContext.Current.CancellationToken))
            updates.Add(update);

        Assert.Equal(2, updates.SelectMany(static update => update.Contents)
            .OfType<WebSearchToolCallContent>().Count());
    }

    [Fact]
    public async Task ExceedingTheLimitFailsTheRequestWithATypedError()
    {
        var inner = new StubChatClient(WebSearchUpdates(calls: 3));
        using var client = new BuiltinToolCallLimitChatClient(inner);

        var exception = await Assert.ThrowsAnyAsync<AgentToolLimitExceededException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                               [new ChatMessage(ChatRole.User, "search")],
                               OptionsWithLimit(2),
                               TestContext.Current.CancellationToken))
            {
            }
        });
        Assert.Equal("MaxBuiltinToolCalls", exception.LimitName);
        Assert.Equal(2, exception.Maximum);
    }

    [Fact]
    public async Task NonStreamingResponseExceedingTheLimitFailsWithTheSameError()
    {
        var updates = WebSearchUpdates(calls: 2).ToArray();
        var response = new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            updates.SelectMany(static update => update.Contents).ToArray()));
        var inner = new StubChatClient([], response);
        using var client = new BuiltinToolCallLimitChatClient(inner);

        var act = () => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "search")],
            OptionsWithLimit(1),
            TestContext.Current.CancellationToken);

        Assert.Equal("MaxBuiltinToolCalls", (await Assert.ThrowsAnyAsync<AgentToolLimitExceededException>(async () => { await act.Invoke(); })).LimitName);
    }

    private static ChatOptions OptionsWithLimit(int limit)
    {
        return new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [AgentChatOptionKeys.MaxBuiltinToolCalls] = limit
            }
        };
    }

    private static IEnumerable<ChatResponseUpdate> WebSearchUpdates(int calls)
    {
        for (var index = 0; index < calls; index++)
        {
            yield return new ChatResponseUpdate
            {
                Contents = [new WebSearchToolCallContent($"ws_{index}")]
            };
        }
    }

    private sealed class StubChatClient(
        IEnumerable<ChatResponseUpdate> updates,
        ChatResponse? response = null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(response ?? throw new InvalidOperationException("No response configured."));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        void IDisposable.Dispose()
        {
        }
    }
}
