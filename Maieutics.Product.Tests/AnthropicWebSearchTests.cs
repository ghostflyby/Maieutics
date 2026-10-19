using Maieutics.Agent;
using Maieutics.Providers.Anthropic;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>Drives the Anthropic Messages adapter directly against the fake server so the exact
/// request body and the provider-neutral content it produces can be asserted.</summary>
public sealed class AnthropicWebSearchTests
{
    [Fact(Timeout = 30_000)]
    public async Task HostedWebSearchIsDeclaredAsAServerToolAndResultsAreParsed()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(20));
        await using var provider = new FakeAnthropicServer(
            "claude-test", "unused", webSearchFlow: true, requestCount: 1);
        using var client = new AnthropicMessagesChatClient("claude-test", "test-key", provider.Endpoint);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "search zhipu ai")],
                           new ChatOptions { Tools = [new HostedWebSearchTool()] },
                           deadline.Token))
            updates.Add(update);

        // The request declares Anthropic's versioned server tool, not a function with a schema.
        var tools = provider.RequestBody.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("web_search_20250305", tools[0].GetProperty("type").GetString());
        Assert.Equal("web_search", tools[0].GetProperty("name").GetString());
        Assert.False(tools[0].TryGetProperty("input_schema", out _));
        // No limit is configured, so the wire block carries no cap.
        Assert.False(tools[0].TryGetProperty("max_uses", out _));

        var contents = updates.SelectMany(static update => update.Contents).ToArray();
        var searchCall = contents.OfType<WebSearchToolCallContent>().Single();
        Assert.Equal(["zhipu ai"], searchCall.Queries);
        Assert.Equal("srvtoolu_test", searchCall.CallId);

        var searchResult = contents.OfType<WebSearchToolResultContent>().Single();
        Assert.Equal(searchCall.CallId, searchResult.CallId);

        // The answer text streams as deltas; the citations ride a trailing content item whose
        // annotations the aggregated response preserves.
        var texts = contents.OfType<TextContent>().ToArray();
        Assert.Contains("Zhipu AI is a Chinese AI company.", string.Concat(texts.Select(static text => text.Text)));
        var cited = texts.Single(text => text.Annotations is { Count: > 0 });
        Assert.Equal(new Uri("https://example.com/zhipu"), Assert.IsType<CitationAnnotation>(cited.Annotations![0]).Url);
    }

    [Fact(Timeout = 30_000)]
    public async Task ConfiguredLimitIsDeclaredAsTheWireMaxUses()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(20));
        await using var provider = new FakeAnthropicServer(
            "claude-test", "unused", webSearchFlow: true, requestCount: 1);
        using var client = new AnthropicMessagesChatClient("claude-test", "test-key", provider.Endpoint);

        var options = new ChatOptions { Tools = [new HostedWebSearchTool()] };
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[AgentChatOptionKeys.MaxBuiltinToolCalls] = 4;

        await foreach (var _ in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "search zhipu ai")],
                           options,
                           deadline.Token))
        {
        }

        // The endpoint limit rides the wire as the server tool's own per-request cap.
        Assert.Equal(4, provider.RequestBody.GetProperty("tools")[0]
            .GetProperty("max_uses").GetInt32());
    }

    [Fact(Timeout = 30_000)]
    public async Task SearchResultsReplayVerbatimIncludingEncryptedContent()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(20));
        await using var provider = new FakeAnthropicServer("claude-test", "unused", webSearchFlow: true);
        using var client = new AnthropicMessagesChatClient("claude-test", "test-key", provider.Endpoint);

        // First turn: the search runs and the assistant turn is captured.
        var turn = new List<AIContent>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "search zhipu ai")],
                           new ChatOptions { Tools = [new HostedWebSearchTool()] },
                           deadline.Token))
            turn.AddRange(update.Contents);

        // Second turn: the captured assistant turn is replayed. Anthropic validates the blocks
        // it produced, so the request must carry them unchanged, encrypted_content included.
        var requestBody = await CaptureSecondRequestAsync(
            provider,
            [
                new ChatMessage(ChatRole.User, "search zhipu ai"),
                new ChatMessage(ChatRole.Assistant, turn),
                new ChatMessage(ChatRole.User, "thanks")
            ],
            client,
            deadline.Token);

        Assert.Contains("\"type\":\"server_tool_use\"", requestBody);
        Assert.Contains("\"type\":\"web_search_tool_result\"", requestBody);
        Assert.Contains("ENCRYPTED_PAYLOAD", requestBody);
        Assert.Contains("\"tool_use_id\":\"srvtoolu_test\"", requestBody);
        // Citations replay with their opaque encrypted_index intact.
        Assert.Contains("ENCRYPTED_INDEX", requestBody);
        Assert.Contains("Zhipu AI is a Chinese AI company.", requestBody);
    }

    [Fact(Timeout = 30_000)]
    public async Task WebSearchCallWithoutItsWireBlockIsRejectedRatherThanMangled()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(20));
        await using var provider = new FakeAnthropicServer("claude-test", "answer");
        using var client = new AnthropicMessagesChatClient("claude-test", "test-key", provider.Endpoint);

        // A constructed call with no preserved wire block cannot be faithfully replayed.
        var fabricated = new WebSearchToolCallContent("srvtoolu_x") { Queries = new List<string> { "q" } };
        await Assert.ThrowsAnyAsync<NotSupportedException>(async () => { await ConsumeAsync(client, fabricated, deadline.Token); });
    }

    private static async Task ConsumeAsync(IChatClient client, AIContent content, CancellationToken cancellationToken)
    {
        await foreach (var _ in client.GetStreamingResponseAsync(
                           [
                               new ChatMessage(ChatRole.User, "hi"),
                               new ChatMessage(ChatRole.Assistant, [content])
                           ],
                           null,
                           cancellationToken))
        {
        }
    }

    /// <summary>Replays a captured turn and returns the second request's body text.</summary>
    private static async Task<string> CaptureSecondRequestAsync(
        FakeAnthropicServer provider,
        IReadOnlyList<ChatMessage> messages,
        IChatClient client,
        CancellationToken cancellationToken)
    {
        // The fake server answers every request; the replayed turn does not need its reply.
        await foreach (var _ in client.GetStreamingResponseAsync(messages, null, cancellationToken))
        {
        }

        var request = provider.RequestBodies.Last();
        return request.GetRawText();
    }

    private static CancellationTokenSource CreateDeadline(
        CancellationToken cancellationToken,
        TimeSpan timeout) => TestDeadlines.Create(cancellationToken, timeout);
}
