using System.Text;
using System.Text.Json;
using FluentAssertions;
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
        tools.GetArrayLength().Should().Be(1);
        tools[0].GetProperty("type").GetString().Should().Be("web_search_20250305");
        tools[0].GetProperty("name").GetString().Should().Be("web_search");
        tools[0].TryGetProperty("input_schema", out _).Should().BeFalse();

        var contents = updates.SelectMany(static update => update.Contents).ToArray();
        var searchCall = contents.OfType<WebSearchToolCallContent>().Single();
        searchCall.Queries.Should().Equal("zhipu ai");
        searchCall.CallId.Should().Be("srvtoolu_test");

        var searchResult = contents.OfType<WebSearchToolResultContent>().Single();
        searchResult.CallId.Should().Be(searchCall.CallId);

        // The answer text streams as deltas; the citations ride a trailing content item whose
        // annotations the aggregated response preserves.
        var texts = contents.OfType<TextContent>().ToArray();
        string.Concat(texts.Select(static text => text.Text))
            .Should().Contain("Zhipu AI is a Chinese AI company.");
        var cited = texts.Single(text => text.Annotations is { Count: > 0 });
        cited.Annotations![0].Should().BeOfType<CitationAnnotation>()
            .Which.Url.Should().Be(new Uri("https://example.com/zhipu"));
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

        requestBody.Should().Contain("\"type\":\"server_tool_use\"");
        requestBody.Should().Contain("\"type\":\"web_search_tool_result\"");
        requestBody.Should().Contain("ENCRYPTED_PAYLOAD");
        requestBody.Should().Contain("\"tool_use_id\":\"srvtoolu_test\"");
        // Citations replay with their opaque encrypted_index intact.
        requestBody.Should().Contain("ENCRYPTED_INDEX");
        requestBody.Should().Contain("Zhipu AI is a Chinese AI company.");
    }

    [Fact(Timeout = 30_000)]
    public async Task WebSearchCallWithoutItsWireBlockIsRejectedRatherThanMangled()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(20));
        await using var provider = new FakeAnthropicServer("claude-test", "answer");
        using var client = new AnthropicMessagesChatClient("claude-test", "test-key", provider.Endpoint);

        // A constructed call with no preserved wire block cannot be faithfully replayed.
        var fabricated = new WebSearchToolCallContent("srvtoolu_x") { Queries = new List<string> { "q" } };
        await client.Awaiting(c => ConsumeAsync(c, fabricated, deadline.Token))
            .Should().ThrowAsync<NotSupportedException>();
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
        TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }
}
