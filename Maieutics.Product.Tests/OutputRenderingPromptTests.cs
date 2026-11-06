using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

public sealed class OutputRenderingPromptTests
{
    [Theory(Timeout = 30_000)]
    [InlineData(null, false)]
    [InlineData("Configured user instructions", true)]
    public async Task ConversationRequestsCarryOutputInstructionsWithoutChangingProfilesOrHistory(
        string? basePrompt, bool withSkills)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        await using var area = TempArea.Create("output-prompt");
        if (withSkills)
        {
            var directory = Path.Combine(area.Path, "example");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"),
                "---\nname: example\ndescription: Example task procedure\n---\nRead on demand.\n", budget.Token);
        }
        await using var catalog = SkillCatalog.Create(
            [(SkillSource.Workspace, area.Path)], TimeProvider.System, NullLogger<SkillCatalog>.Instance);
        using var client = new ScriptedChatClient((_, _) => Answer(), (_, _) => Answer());
        var profile = new AgentRunProfile(client, new AgentSessionOptions { SystemPrompt = basePrompt });
        await using var manager = new MaieuticsAgentSessionManager(new FixedProfileProvider(profile),
            familiesRoot: null, storeFactory: null, NullLogger<MaieuticsAgentSessionManager>.Instance,
            skillCatalog: withSkills ? catalog : null);

        for (var index = 0; index < 2; index++)
        {
            var session = manager.Resolve(manager.Id);
            await using var run = await session.StartTurnAsync(AgentTurn.FromText("Show an example"), budget.Token);
            await foreach (var unused in run.Events.WithCancellation(budget.Token)) { }
            await run.Completion.WaitAsync(budget.Token);
        }

        Assert.Equal(2, client.Instructions.Count);
        Assert.All(client.Instructions, instructions =>
        {
            var prompt = Assert.IsType<string>(instructions);
            Assert.Contains("VitePress-based Markdown", prompt);
            Assert.Contains("only after successful completion", prompt);
            Assert.Equal(2, prompt.Split("## Output rendering", StringSplitOptions.None).Length);
            if (basePrompt is not null) Assert.StartsWith(basePrompt, prompt);
            if (withSkills) Assert.Contains("skill://example", prompt);
        });
        Assert.Equal(basePrompt, profile.Options.SystemPrompt);
        await using var metadata = await manager.AcquireTitleProfileAsync(manager.Id, budget.Token);
        Assert.Equal(basePrompt, metadata.Profile.Options.SystemPrompt);
        Assert.All(manager.GetTranscriptSnapshot().Turns, turn =>
        {
            Assert.Equal([ChatRole.User, ChatRole.Assistant], turn.Messages.Select(message => message.Role));
            Assert.Equal("Answer", turn.Messages[^1].Text);
        });
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Answer()
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Answer");
        await Task.CompletedTask;
    }
}
