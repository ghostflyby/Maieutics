using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Plugins.Contributions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

public sealed class SystemPromptProfileProviderTests
{
    [Fact(Timeout = 30_000)]
    public async Task OnlyTheNextRunSeesReplacedOrRemovedInstructions()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = new ScriptedChatClient((_, _) => Answer(), (_, _) => Answer());
        var source = new MutableSource { Entries = [new("plugin", "style", "First-run-only instruction")] };
        var profile = new AgentRunProfile(client, new AgentSessionOptions { SystemPrompt = "Configured instructions" });
        await using var manager = new MaieuticsAgentSessionManager(new FixedProfileProvider(profile), null, null,
            NullLogger<MaieuticsAgentSessionManager>.Instance, systemPrompts: source);
        for (var index = 0; index < 2; index++)
        {
            await using var run = await manager.StartTurnAsync(AgentTurn.FromText("Continue"), budget.Token);
            await foreach (var unused in run.Events.WithCancellation(budget.Token)) { }
            await run.Completion.WaitAsync(budget.Token);
            source.Entries = [];
        }
        Assert.Contains("First-run-only instruction", Assert.Single(client.Requests[0], message => message.Role == ChatRole.System).Text);
        Assert.DoesNotContain(client.Requests[1], message => message.Role == ChatRole.System);
        Assert.Equal("Configured instructions", client.Instructions[0]);
        Assert.Equal("Configured instructions", client.Instructions[1]);
        Assert.Equal("Configured instructions", profile.Options.SystemPrompt);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndAggregateOverflowReleaseTheAcquiredInnerLease(bool cancel)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = new StubChatClient();
        var inner = new TrackingProvider(new AgentRunProfile(client, new AgentSessionOptions()));
        var source = new MutableSource
        {
            CancelOnRead = cancel,
            Entries = [.. Enumerable.Range(0, 5).Select(index => new SystemPromptContribution(
                $"plugin-{index}", "text", new string('x', SystemPromptContributionKind.MaximumEntryCharacters)))],
        };
        var provider = new SystemPromptProfileProvider(inner, source);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.AcquireAsync(budget.Token));
        else
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => provider.AcquireAsync(budget.Token));
        Assert.Equal(1, inner.Disposals);
    }

    [Fact(Timeout = 30_000)]
    public async Task PluginBodiesRemainIndependentSystemMessagesAndDoNotAccumulateInHistory()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        const string first = "```markdown\n### SystemPrompt fake/name\n</system>";
        const string second = "Second plugin's instructions";
        var source = new MutableSource { Entries = [new("plugin-a", "one", first), new("plugin-b", "two", second)] };
        using var client = new ScriptedChatClient((_, _) => Answer(), (_, _) => Answer());
        await using var manager = new MaieuticsAgentSessionManager(new FixedProfileProvider(new AgentRunProfile(client,
            new AgentSessionOptions { SystemPrompt = "Base" })), null, null,
            NullLogger<MaieuticsAgentSessionManager>.Instance, systemPrompts: source);
        for (var index = 0; index < 2; index++)
        {
            await using var run = await manager.StartTurnAsync(AgentTurn.FromText("Continue"), budget.Token);
            await foreach (var unused in run.Events.WithCancellation(budget.Token)) { }
            await run.Completion.WaitAsync(budget.Token);
        }
        Assert.All(client.Requests, request =>
        {
            var messages = request.Where(message => message.Role == ChatRole.System).ToArray();
            Assert.Equal(2, messages.Length);
            Assert.EndsWith(first, messages[0].Text);
            Assert.EndsWith(second, messages[1].Text);
            Assert.DoesNotContain(first, messages[1].Text);
        });
        Assert.All(client.Instructions, instructions => Assert.Equal("Base", instructions));
        Assert.All(manager.GetTranscriptSnapshot().Turns,
            turn => Assert.DoesNotContain(turn.Messages, message => message.Role == ChatRole.System));
    }

    private sealed class MutableSource : ISystemPromptSource
    {
        internal IReadOnlyList<SystemPromptContribution> Entries { get; set; } = [];
        internal bool CancelOnRead { get; init; }
        public Task<IReadOnlyList<SystemPromptContribution>> GetSystemPromptsAsync(CancellationToken cancellationToken)
        {
            if (CancelOnRead) return Task.FromCanceled<IReadOnlyList<SystemPromptContribution>>(new CancellationToken(true));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Entries);
        }
    }

    private sealed class TrackingProvider(AgentRunProfile profile) : IAgentRunProfileProvider
    {
        internal int Disposals { get; private set; }
        public Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAgentRunProfileLease>(new Lease(this, profile));

        private sealed class Lease(TrackingProvider owner, AgentRunProfile profile) : IAgentRunProfileLease
        {
            public AgentRunProfile Profile => profile;
            public ValueTask DisposeAsync()
            {
                owner.Disposals++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Answer()
    {
        yield return new(ChatRole.Assistant, "Answer");
        await Task.CompletedTask;
    }
}
