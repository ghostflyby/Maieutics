using Maieutics.Agent;
using Maieutics.Providers.Anthropic;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>Anthropic carries token usage inside the message stream: input tokens on
/// <c>message_start</c> and the message's final output count on <c>message_delta</c>. The
/// Messages adapter must surface that usage, otherwise an Agent run result records none.</summary>
public sealed class AnthropicUsageTests
{
    [Fact(Timeout = 30_000)]
    public async Task AdapterEmitsTheStreamedMessageUsageAsOneUsageContent()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken);
        await using var server = new GatedModelProvider(ModelApiFlavor.Anthropic, ProviderGateMode.Scripted, model: "claude-test", answer: "Hello from Claude");
        using var client = new AnthropicMessagesChatClient("claude-test", "configured-key", server.Endpoint);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           "Say hello",
                           cancellationToken: deadline.Token))
            updates.Add(update);

        var contents = updates.SelectMany(static update => update.Contents).ToArray();
        var usage = Assert.Single(contents.OfType<UsageContent>());
        // The fixture reports 1 input token on message_start and 1 output token on
        // message_delta; the total is derived because Anthropic reports none.
        Assert.Equal(1L, usage.Details.InputTokenCount);
        Assert.Equal(1L, usage.Details.OutputTokenCount);
        Assert.Equal(2L, usage.Details.TotalTokenCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task RunResultRecordsTheUsageTheAnthropicStreamReported()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken);
        await using var server = new GatedModelProvider(ModelApiFlavor.Anthropic, ProviderGateMode.Scripted, model: "claude-test", answer: "Hello from Claude");
        using var client = AnthropicChatClientFactory.Create(
            "claude-test",
            new AnthropicSourceOptions("configured-key", server.Endpoint));
        var session = new AgentSession(client);

        await using var run = await session.StartTurnAsync(
            AgentTurn.FromText("Say hello"),
            deadline.Token);
        await ReadEventsAsync(run, deadline.Token);
        var result = await run.Completion.WaitAsync(deadline.Token);

        await server.Completion.WaitAsync(deadline.Token);
        Assert.Single(server.RequestBodies);

        // A run result whose usage stayed null is the defect: the stream reported 1 input
        // and 1 output token, and the run result must record them.
        var usage = result.Usage ??
                    throw new InvalidOperationException("The run result recorded no token usage.");
        Assert.Equal(1L, usage.InputTokenCount);
        Assert.Equal(1L, usage.OutputTokenCount);
        Assert.Equal(2L, usage.TotalTokenCount);
        Assert.Equal("Hello from Claude", result.AssistantMessage.Text);
    }

    private static async Task<List<AgentEvent>> ReadEventsAsync(
        IAgentRun run,
        CancellationToken cancellationToken)
    {
        var events = new List<AgentEvent>();
        await foreach (var agentEvent in run.Events.WithCancellation(cancellationToken))
            events.Add(agentEvent);

        return events;
    }
}
