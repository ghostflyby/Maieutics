using System.Runtime.CompilerServices;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

public sealed class SessionTitleGeneratorTests
{
    [Theory(Timeout = 30_000)]
    [InlineData(false, "auto")]
    [InlineData(true, "auto")]
    [InlineData(false, "initial")]
    [InlineData(true, "initial")]
    public async Task FirstTitleDoesNotBlockCompletionOrAnotherTurnAndRunsOnlyOnce(bool persistent, string mode)
    {
        using var budget = Budget(TestContext.Current.CancellationToken);
        await using var area = TempArea.Create("initial-title");
        var entered = Signal();
        var release = Signal();
        using var client = new ScriptedChatClient((_, _) => Text("first answer"),
            (_, token) => Gated(entered, release, token, "修复诊断桥接"), (_, _) => Text("second answer"));
        await using var manager = Manager(area.Path, persistent, client, mode);
        var session = manager.Resolve(manager.Id);
        await CommitAsync(session, "修复诊断桥接", budget.Token);
        await entered.Task.WaitAsync(budget.Token);
        Assert.False(session.IsRunInProgress);
        await CommitAsync(session, "继续", budget.Token);
        Assert.Equal("pending", manager.GetTitleState(session.Id).Initial);
        release.SetResult();
        await manager.Titles.WaitForIdleAsync(budget.Token);
        Assert.Equal("修复诊断桥接", manager.GetTitleState(session.Id).Title);
        Assert.Equal("initial", manager.GetTitleState(session.Id).Source);
        Assert.Equal(3, client.Requests.Count);
        Assert.Equal(2, session.GetTranscriptSnapshot().Turns.Length);
        Assert.All(session.GetTranscriptSnapshot().Turns, turn => Assert.Equal(2, turn.Messages.Count));
        if (persistent)
        {
            using var reopened = new SqliteTranscriptStore(SqliteTranscriptStore.FamilyDatabasePath(area.Path, session.Id));
            Assert.Equal(manager.GetTitleState(session.Id), reopened.GetTitleState(session.Id));
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameThenClearRejectsInFlightTitleAndAllowsOneNewAttempt(bool persistent)
    {
        using var budget = Budget(TestContext.Current.CancellationToken);
        await using var area = TempArea.Create("title-race");
        var entered = Signal(); var release = Signal();
        using var client = new ScriptedChatClient((_, _) => Text("answer"),
            (_, token) => Gated(entered, release, token, "stale"),
            (_, _) => Text("next answer"), (_, _) => Text("fresh"));
        await using var manager = Manager(area.Path, persistent, client, "auto");
        var session = manager.Resolve(manager.Id);
        await CommitAsync(session, "request", budget.Token);
        await entered.Task.WaitAsync(budget.Token);
        manager.SetTitle(session.Id, "Pinned");
        manager.SetTitle(session.Id, "");
        release.TrySetResult();
        await manager.Titles.WaitForIdleAsync(budget.Token);
        Assert.Null(manager.GetTitleState(session.Id).Title);
        await CommitAsync(session, "continue", budget.Token);
        await manager.Titles.WaitForIdleAsync(budget.Token);
        Assert.Equal("fresh", manager.GetTitleState(session.Id).Title);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("auto", true)]
    [InlineData("initial", false)]
    [InlineData("manual", false)]
    public async Task ModeControlsToolVisibilityAndManualRenameLocksIt(string mode, bool visible)
    {
        using var budget = Budget(TestContext.Current.CancellationToken);
        await using var area = TempArea.Create("title-mode");
        using var client = new StubChatClient();
        await using var manager = Manager(area.Path, true, client, mode);
        var profile = manager.Titles.Decorate(new AgentRunProfile(client, new AgentSessionOptions()), manager.Id);
        Assert.Equal(visible, profile.Tools.Any(tool => tool.Name == "set_task_title"));
        manager.SetTitle(manager.Id, "User title");
        profile = manager.Titles.Decorate(new AgentRunProfile(client, new AgentSessionOptions()), manager.Id);
        Assert.DoesNotContain(profile.Tools, tool => tool.Name == "set_task_title");
        manager.Titles.SetMode(manager.Id, "auto");
        Assert.True(manager.GetTitleState(manager.Id).Manual);
        manager.SetTitle(manager.Id, "");
        Assert.True(manager.Titles.Eligible(manager.GetTitleState(manager.Id)));
        await manager.Titles.WaitForIdleAsync(budget.Token);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolProposalCommitsOnlyAfterSuccess(bool fail)
    {
        using var budget = Budget(TestContext.Current.CancellationToken);
        await using var area = TempArea.Create("title-tool");
        var entered = Signal(); var release = Signal();
        using var client = new ScriptedChatClient((_, _) => Call("New task"), (messages, token) =>
        {
            Assert.Contains(messages, message => message.Role == ChatRole.Tool && message.Contents.OfType<FunctionResultContent>().Any());
            return Gated(entered, release, token, "answer", fail);
        });
        await using var manager = Manager(area.Path, true, client, "auto");
        // Settle initial naming so this test exercises only the primary model's tool.
        manager.Titles.SetMode(manager.Id, "manual");
        manager.Titles.SetMode(manager.Id, "auto");
        var before = manager.GetTitleState(manager.Id);
        manager.TrySetTitleState(manager.Id, before, before with { Initial = "settled", Revision = before.Revision + 1 });
        var execution = CommitAsync(manager.Resolve(manager.Id), "New task", budget.Token);
        await entered.Task.WaitAsync(budget.Token);
        Assert.Null(manager.GetTitleState(manager.Id).Title);
        release.SetResult();
        if (fail) await Assert.ThrowsAnyAsync<Exception>(() => execution);
        else await execution;
        Assert.Equal(fail ? null : "New task", manager.GetTitleState(manager.Id).Title);
        Assert.Equal(fail ? "preview" : "tool", manager.GetTitleState(manager.Id).Source);
    }

    [Fact(Timeout = 30_000)]
    public async Task FailureDoesNotRetryOnEveryTurnAndManualModeMakesNoExtraRequest()
    {
        using var budget = Budget(TestContext.Current.CancellationToken);
        await using var area = TempArea.Create("title-failure");
        using var client = new ScriptedChatClient((_, _) => Text("answer"),
            (_, _) => throw new InvalidOperationException("unavailable"), (_, _) => Text("continue"), (_, _) => Text("manual"));
        await using var manager = Manager(area.Path, true, client, "auto");
        var session = manager.Resolve(manager.Id);
        await CommitAsync(session, "original request", budget.Token);
        await manager.Titles.WaitForIdleAsync(budget.Token);
        await CommitAsync(session, "continue", budget.Token);
        manager.Titles.SetMode(session.Id, "manual");
        await CommitAsync(session, "manual", budget.Token);
        Assert.Equal(4, client.Requests.Count);
        Assert.Equal("original request", manager.GetTitleState(session.Id).Title);
        Assert.Equal("settled", manager.GetTitleState(session.Id).Initial);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("rename")]
    [InlineData("mode")]
    [InlineData("cancel")]
    public async Task UserChangesAndCancellationInvalidateStagedToolProposals(string action)
    {
        using var budget = Budget(TestContext.Current.CancellationToken);
        await using var area = TempArea.Create("title-proposal-race");
        var entered = Signal(); var release = Signal();
        using var client = new ScriptedChatClient((_, _) => Call("Stale task"),
            (_, token) => Gated(entered, release, token, "answer"));
        await using var manager = Manager(area.Path, true, client, "auto");
        manager.Titles.SetMode(manager.Id, "manual");
        manager.Titles.SetMode(manager.Id, "auto");
        var state = manager.GetTitleState(manager.Id);
        manager.TrySetTitleState(manager.Id, state, state with { Initial = "settled", Revision = state.Revision + 1 });
        var session = manager.Resolve(manager.Id);
        await using var run = await session.StartTurnAsync(AgentTurn.FromText("new task"), budget.Token);
        var events = DrainAsync(run, budget.Token);
        await entered.Task.WaitAsync(budget.Token);
        if (action == "rename") manager.SetTitle(session.Id, "User title");
        if (action == "mode") manager.Titles.SetMode(session.Id, "initial");
        if (action == "cancel") await run.CancelAsync(budget.Token);
        release.TrySetResult();
        await events;
        if (action == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Completion);
        else await run.Completion;
        Assert.Equal(action == "rename" ? "User title" : null, manager.GetTitleState(session.Id).Title);
        Assert.False(session.IsRunInProgress);
    }

    [Fact(Timeout = 30_000)]
    public async Task ForkInheritsModeAndCanRefineItsDerivedTitleOnce()
    {
        using var budget = Budget(TestContext.Current.CancellationToken);
        await using var area = TempArea.Create("fork-titles");
        using var client = new ScriptedChatClient((_, _) => Text("root answer"), (_, _) => Text("Root task"),
            (_, _) => Text("branch answer"), (_, _) => Text("Branch task"));
        await using var manager = Manager(area.Path, true, client, "initial");
        await CommitAsync(manager.Resolve(manager.Id), "root request", budget.Token);
        await manager.Titles.WaitForIdleAsync(budget.Token);
        var parent = manager.Id;
        var fork = manager.Fork(parent, 1);
        Assert.Equal("initial", manager.GetTitleState(fork).Mode);
        Assert.False(manager.GetTitleState(fork).Manual);
        await CommitAsync(manager.Resolve(fork), "branch request", budget.Token);
        await manager.Titles.WaitForIdleAsync(budget.Token);
        Assert.Equal("Branch task", manager.GetTitleState(fork).Title);
        Assert.Equal("Root task", manager.GetTitleState(parent).Title);
    }

    [Fact(Timeout = 30_000)]
    public async Task RegenerationIsExplicitAndRunsWithoutAnotherAgentTurn()
    {
        using var budget = Budget(TestContext.Current.CancellationToken);
        await using var area = TempArea.Create("regenerate-title");
        using var client = new ScriptedChatClient((_, _) => Text("answer"), (_, _) => Text("First title"),
            (_, _) => Text("Refined title"));
        await using var manager = Manager(area.Path, true, client, "initial");
        await CommitAsync(manager.Resolve(manager.Id), "request", budget.Token);
        await manager.Titles.WaitForIdleAsync(budget.Token);
        await manager.Titles.RegenerateAsync(manager.Id, budget.Token);
        await manager.Titles.WaitForIdleAsync(budget.Token);
        Assert.Equal("Refined title", manager.GetTitleState(manager.Id).Title);
        Assert.Single(manager.GetTranscriptSnapshot().Turns);
        manager.SetTitle(manager.Id, "Locked");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => manager.Titles.RegenerateAsync(manager.Id, budget.Token));
    }

    [Fact(Timeout = 30_000)]
    public async Task ShutdownJoinsActiveNamingAndReleasesQueuedProfileLeasesAndPins()
    {
        using var budget = Budget(TestContext.Current.CancellationToken);
        await using var area = TempArea.Create("title-shutdown");
        var entered = Signal(); var release = Signal();
        using var client = new ScriptedChatClient((_, _) => Text("first"),
            (_, token) => Gated(entered, release, token, "title"), (_, _) => Text("second"));
        var profiles = new CountingProfiles(client);
        await using var manager = new MaieuticsAgentSessionManager(profiles, area.Path,
            id => new SqliteTranscriptStore(SqliteTranscriptStore.FamilyDatabasePath(area.Path, id)),
            NullLogger<MaieuticsAgentSessionManager>.Instance, titleMode: "initial");
        await CommitAsync(manager.Resolve(manager.Id), "first", budget.Token);
        await entered.Task.WaitAsync(budget.Token);
        var second = manager.StartNew();
        await CommitAsync(manager.Resolve(second), "second", budget.Token);
        Assert.Equal(2, profiles.Active);
        Assert.True(manager.IsPinned(second));
        await manager.DisposeAsync();
        Assert.Equal(0, profiles.Active);
        Assert.False(manager.IsPinned(second));
        Assert.Equal(3, client.Requests.Count);
    }

    private sealed class CountingProfiles(IChatClient client) : IAgentRunProfileProvider
    {
        private int _active;
        internal int Active => Volatile.Read(ref _active);
        public Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _active);
            return Task.FromResult<IAgentRunProfileLease>(new Lease(this, new AgentRunProfile(client, new AgentSessionOptions())));
        }
        private sealed class Lease(CountingProfiles owner, AgentRunProfile profile) : IAgentRunProfileLease
        {
            private int _disposed;
            public AgentRunProfile Profile => profile;
            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._active);
                return ValueTask.CompletedTask;
            }
        }
    }

    private static async Task DrainAsync(IAgentRun run, CancellationToken token)
    {
        await foreach (var unused in run.Events.WithCancellation(token)) { }
    }

    [Fact]
    public void TitleNormalizationIsSingleLineAndDoesNotSplitASurrogatePair()
    {
        Assert.Equal("修复 工作区", SessionTitleGenerator.Normalize("# 修复\n工作区 #"));
        Assert.Equal(new string('a', 79), SessionTitleGenerator.Normalize(new string('a', 79) + "😀"));
        Assert.Null(SessionTitleGenerator.Normalize("  "));
    }

    [Theory]
    [InlineData("bad\u0001title")]
    [InlineData("bad\u001b[31mtitle")]
    [InlineData("bad\u007ftitle")]
    public void InitialTitleNormalizationRejectsNonWhitespaceControlCharacters(string title)
    {
        Assert.Null(SessionTitleGenerator.Normalize(title));
    }

    private static CancellationTokenSource Budget(CancellationToken token)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(20)); return budget;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static MaieuticsAgentSessionManager Manager(string root, bool persistent, IChatClient client, string mode) => new(
        new FixedProfileProvider(client), persistent ? root : null,
        persistent ? id => new SqliteTranscriptStore(SqliteTranscriptStore.FamilyDatabasePath(root, id)) : null,
        NullLogger<MaieuticsAgentSessionManager>.Instance, titleMode: mode);
    private static async Task CommitAsync(IAgentSession session, string text, CancellationToken token)
    {
        await using var run = await session.StartTurnAsync(AgentTurn.FromText(text), token);
        await foreach (var unused in run.Events.WithCancellation(token)) { }
        await run.Completion;
    }
    private static async IAsyncEnumerable<ChatResponseUpdate> Text(string text)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, text); await Task.CompletedTask;
    }
    private static async IAsyncEnumerable<ChatResponseUpdate> Call(string title)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent("title-call", "set_task_title", new Dictionary<string, object?> { ["title"] = title })]);
        await Task.CompletedTask;
    }
    private static async IAsyncEnumerable<ChatResponseUpdate> Gated(TaskCompletionSource entered,
        TaskCompletionSource release, [EnumeratorCancellation] CancellationToken token, string text, bool fail = false)
    {
        entered.TrySetResult(); await release.Task.WaitAsync(token);
        if (fail) throw new InvalidOperationException("main model failed");
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }
}
