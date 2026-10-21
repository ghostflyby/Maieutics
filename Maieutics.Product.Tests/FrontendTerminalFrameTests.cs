using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Frontend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

/// <summary>
///     The terminal pair closes a run's frontend stream (invariant 7: lifecycle frames lead
///     output, and the terminal frames precede idle). A presentation frame offered after the
///     pair used to be appended to the replay buffer and fanned out to nobody — the live
///     subscribers were already released — so a client resuming with <c>sinceSequence</c>
///     replayed it after <c>run.status: idle</c>: output published after the run had ended.
///     The publisher must be refused instead, which is the outcome the approval and
///     elicitation presenters already handle (<c>false</c> denies or cancels rather than
///     hanging on an answer that can never arrive).
/// </summary>
public sealed class FrontendTerminalFrameTests
{
    private static readonly JsonElement LateDisplayBundle = JsonSerializer.SerializeToElement(
        new Dictionary<string, JsonElement>
        {
            ["text/plain"] = JsonSerializer.SerializeToElement("late output")
        });

    [Fact(Timeout = 30_000)]
    public async Task APresentationFrameOfferedAfterTheTerminalPairIsRefusedAndNeverReplays()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken);
        var harness = CreateHarness();
        var sessionId = harness.Manager.Id;

        var accepted = await harness.Service.StartTurnAsync(
            sessionId.ToString(),
            "hello",
            deadline.Token);
        var stream = await SettledStreamAsync(harness.Service, accepted.RunId, deadline.Token);

        var (before, beforeChannel) = stream.Subscribe(sinceSequence: 0);
        Assert.Equal("run.status", before[^1].Type);
        Assert.Equal("idle", before[^1].State);
        stream.Unsubscribe(beforeChannel);

        // The run already published its terminal pair, so the session-level presentation
        // seam must report the refusal the presenters switch on instead of accepting a frame
        // the stream can no longer deliver in protocol order.
        var published = harness.Service.TryPublishPresentation(sessionId, "repl.display", LateDisplayBundle);
        Assert.False(published);

        var (after, afterChannel) = stream.Subscribe(sinceSequence: 0);
        Assert.DoesNotContain(after, frame => frame.Type == "repl.display");
        Assert.Equal(before.Count, after.Count);
        // Nothing may follow the terminal pair: a resuming client rebuilds the run from this
        // buffer, so an appended frame is rendered as output of a run that already ended.
        Assert.Equal("run.completed", after[^2].Type);
        Assert.Equal("run.status", after[^1].Type);
        Assert.Equal("idle", after[^1].State);

        stream.Unsubscribe(afterChannel);
        await stream.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task ThePresentationTargetDropsFramesOfferedAfterTheTerminalPair()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken);
        var harness = CreateHarness();

        var accepted = await harness.Service.StartTurnAsync(
            harness.Manager.Id.ToString(),
            "hello",
            deadline.Token);
        var stream = await SettledStreamAsync(harness.Service, accepted.RunId, deadline.Token);

        var (before, beforeChannel) = stream.Subscribe(sinceSequence: 0);
        stream.Unsubscribe(beforeChannel);
        Assert.Equal("run.status", before[^1].Type);
        Assert.Equal("idle", before[^1].State);

        // The REPL presentation sink reaches the stream through the target's void publish, so
        // the fence must hold on that path too: a late REPL display, plugin-UI announcement,
        // or pending input request must not enter the replay behind run.status idle.
        stream.PublishPresentation(
            "repl.display",
            displayId: null,
            LateDisplayBundle,
            TestContext.Current.CancellationToken);

        var (after, afterChannel) = stream.Subscribe(sinceSequence: 0);
        Assert.DoesNotContain(after, frame => frame.Type == "repl.display");
        Assert.Equal(before.Count, after.Count);
        Assert.Equal("run.status", after[^1].Type);
        Assert.Equal("idle", after[^1].State);

        stream.Unsubscribe(afterChannel);
        await stream.DisposeAsync();
    }

    /// <summary>Resolves the submitted run's retained stream and waits for its full
    /// settlement: the pump publishes the terminal pair before it settles the run, so this is
    /// the deterministic boundary the frames below must not cross (no timing is involved).</summary>
    private static async Task<FrontendRunStream> SettledStreamAsync(
        FrontendSessionService service,
        string runId,
        CancellationToken cancellationToken)
    {
        if (!service.TryGetRun(runId, out var stream) || stream is null)
            throw new InvalidOperationException($"Run {runId} left no retained frontend stream.");

        await stream.Settled.WaitAsync(cancellationToken);
        return stream;
    }

    private static Harness CreateHarness()
    {
        var manager = new MaieuticsAgentSessionManager(
            new FixedProfileProvider(
                new AgentRunProfile(
                    new ScriptedChatClient((_, _) => StreamAsync("answer")),
                    new AgentSessionOptions())),
            familiesRoot: null,
            storeFactory: null,
            NullLogger<MaieuticsAgentSessionManager>.Instance);
        var service = new FrontendSessionService(
            manager,
            new MaieuticsCommandExecutor(manager, null, null, null, null),
            new FrontendDenoReplPresentationRouter(),
            NullLogger<FrontendSessionService>.Instance);
        return new Harness(manager, service);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(string text)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    private sealed record Harness(MaieuticsAgentSessionManager Manager, FrontendSessionService Service);

    private sealed class ScriptedChatClient(
        Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> response)
        : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<ChatResponse>(new NotSupportedException());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return response(messages.ToArray(), cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FixedProfileProvider(AgentRunProfile profile) : IAgentRunProfileProvider
    {
        public Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IAgentRunProfileLease>(new Lease(profile));
        }

        private sealed class Lease(AgentRunProfile profile) : IAgentRunProfileLease
        {
            public AgentRunProfile Profile { get; } = profile;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
