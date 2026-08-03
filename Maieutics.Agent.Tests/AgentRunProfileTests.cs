using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent.Tests;

public sealed class AgentRunProfileTests
{
    [Fact]
    public void ModelProfileIdentifiersValidateAndRenderStableValues()
    {
        var id = new AgentModelProfileId("OpenAI_primary-1");

        id.Value.Should().Be("OpenAI_primary-1");
        id.ToString().Should().Be("OpenAI_primary-1");
        FluentActions.Invoking(() => new AgentModelProfileId(""))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new AgentModelProfileId("-invalid"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new AgentModelProfileId("contains space"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new AgentModelProfileId(new string('a', 65)))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void LegacyRunProfileKeepsCompatibleCapabilitiesAndNoIdentity()
    {
        var profile = new AgentRunProfile(
            new ScriptedChatClient((_, _) => StreamAsync("unused")),
            new AgentSessionOptions());

        profile.ModelIdentity.Should().BeNull();
        profile.Capabilities.Should().Be(
            AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling);
    }

    [Fact]
    public async Task StaticConstructorKeepsExistingClientOptionsAndTranscriptBehavior()
    {
        using var deadline = CreateDeadline();
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync("first answer"),
            (_, _) => StreamAsync("second answer"));
        var session = new AgentSession(client, new AgentSessionOptions { SystemPrompt = "static instructions" });

        await CompleteTurnAsync(session, "first question", deadline.Token);
        var result = await CompleteTurnAsync(session, "second question", deadline.Token);

        client.Instructions.Should().Equal("static instructions", "static instructions");
        client.Requests[1].Select(MessageTuple).Should().Equal(
            (ChatRole.User, "first question"),
            (ChatRole.Assistant, "first answer"),
            (ChatRole.User, "second question"));
        result.Transcript.Version.Should().Be(2);
        result.Transcript.Turns.Should().HaveCount(2);
        result.ModelIdentity.Should().BeNull();
        result.Transcript.Turns.Should().OnlyContain(static turn => turn.ModelIdentity == null);
    }

    [Fact]
    public async Task DynamicProfileAppliesToNextRunAndReplaysCanonicalTranscript()
    {
        using var deadline = CreateDeadline();
        var firstClient = new ScriptedChatClient((_, _) => StreamAsync("first answer"));
        var secondClient = new ScriptedChatClient((_, _) => StreamAsync("second answer"));
        var firstLease = new TrackingProfileLease(CreateProfile(firstClient, "first instructions"));
        var secondLease = new TrackingProfileLease(CreateProfile(secondClient, "second instructions"));
        var session = new AgentSession(new QueueProfileProvider(firstLease, secondLease));

        await CompleteTurnAsync(session, "first question", deadline.Token);
        var result = await CompleteTurnAsync(session, "second question", deadline.Token);

        firstClient.Instructions.Should().Equal("first instructions");
        secondClient.Instructions.Should().Equal("second instructions");
        secondClient.Requests.Single().Select(MessageTuple).Should().Equal(
            (ChatRole.User, "first question"),
            (ChatRole.Assistant, "first answer"),
            (ChatRole.User, "second question"));
        firstLease.DisposeCount.Should().Be(1);
        secondLease.DisposeCount.Should().Be(1);
        result.Transcript.Turns.Should().HaveCount(2);
    }

    [Fact]
    public async Task SuccessfulRunCommitsCapturedModelIdentityToResultAndTranscript()
    {
        using var deadline = CreateDeadline();
        var identity = new AgentModelIdentity(
            new AgentModelProfileId("claude"),
            "Anthropic",
            "claude-model");
        var profile = new AgentRunProfile(
            new ScriptedChatClient((_, _) => StreamAsync("answer")),
            new AgentSessionOptions(),
            identity,
            AgentModelCapabilities.StreamingText);
        var session = new AgentSession(new QueueProfileProvider(new TrackingProfileLease(profile)));

        var result = await CompleteTurnAsync(session, "question", deadline.Token);

        result.ModelIdentity.Should().Be(identity);
        result.Transcript.Turns.Should().ContainSingle();
        result.Transcript.Turns[0].ModelIdentity.Should().Be(identity);
        session.GetTranscriptSnapshot().Turns[0].ModelIdentity.Should().Be(identity);
    }

    [Fact]
    public async Task MissingStreamingCapabilityFailsBeforeProviderInvocation()
    {
        using var deadline = CreateDeadline();
        var client = new ScriptedChatClient((_, _) => StreamAsync("unused"));
        var identity = new AgentModelIdentity(new AgentModelProfileId("metadata"), "Test", "metadata-only");
        var lease = new TrackingProfileLease(new AgentRunProfile(
            client,
            new AgentSessionOptions(),
            identity,
            AgentModelCapabilities.None));
        var session = new AgentSession(new QueueProfileProvider(lease));

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("question"), deadline.Token);
        await ReadEventsAsync(run, deadline.Token);
        var failure = (await run.Completion.WaitAsync(deadline.Token)
            .Invoking(static task => task)
            .Should().ThrowAsync<AgentModelCapabilityException>()).Which;

        failure.RequiredCapability.Should().Be(AgentModelCapabilities.StreamingText);
        failure.ModelIdentity.Should().Be(identity);
        client.Requests.Should().BeEmpty();
        lease.DisposeCount.Should().Be(1);
        session.GetTranscriptSnapshot().Turns.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingFunctionCallingCapabilityFailsBeforeProviderInvocationWhenToolsExist()
    {
        using var deadline = CreateDeadline();
        var client = new ScriptedChatClient((_, _) => StreamAsync("unused"));
        var identity = new AgentModelIdentity(new AgentModelProfileId("text"), "Test", "text-only");
        var lease = new TrackingProfileLease(new AgentRunProfile(
            client,
            new AgentSessionOptions(),
            identity,
            AgentModelCapabilities.StreamingText));
        var session = new AgentSession(
            new QueueProfileProvider(lease),
            [CreateSuccessfulTool()]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("use a tool"), deadline.Token);
        await ReadEventsAsync(run, deadline.Token);
        var failure = (await run.Completion.WaitAsync(deadline.Token)
            .Invoking(static task => task)
            .Should().ThrowAsync<AgentModelCapabilityException>()).Which;

        failure.RequiredCapability.Should().Be(AgentModelCapabilities.FunctionCalling);
        failure.ModelIdentity.Should().Be(identity);
        client.Requests.Should().BeEmpty();
        lease.DisposeCount.Should().Be(1);
        session.GetTranscriptSnapshot().Turns.Should().BeEmpty();
    }

    [Fact]
    public async Task ActiveRunUsesOneCapturedClientAcrossToolIterations()
    {
        using var deadline = CreateDeadline();
        var firstClient = new ScriptedChatClient(
            (_, _) => StreamAsync(new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("provider-call", "echo", new Dictionary<string, object?>())])),
            (_, _) => StreamAsync("tool complete"));
        var nextClient = new ScriptedChatClient((_, _) => StreamAsync("next profile"));
        var provider = new QueueProfileProvider(
            new TrackingProfileLease(CreateProfile(firstClient, "first")),
            new TrackingProfileLease(CreateProfile(nextClient, "next")));
        var session = new AgentSession(provider, [CreateSuccessfulTool()]);

        var first = await CompleteTurnAsync(session, "use tool", deadline.Token);

        first.AssistantMessage.Text.Should().Be("tool complete");
        firstClient.Requests.Should().HaveCount(2);
        nextClient.Requests.Should().BeEmpty();
        provider.AcquireCount.Should().Be(1);

        await CompleteTurnAsync(session, "next", deadline.Token);
        nextClient.Requests.Should().ContainSingle();
        provider.AcquireCount.Should().Be(2);
    }

    [Fact]
    public async Task DynamicProfilesExposeDifferentRunLocalToolRegistries()
    {
        using var deadline = CreateDeadline();
        var firstClient = new ScriptedChatClient(
            (_, _) => StreamAsync(new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("first-call", "first_tool", new Dictionary<string, object?>())])),
            (_, _) => StreamAsync("first complete"));
        var secondClient = new ScriptedChatClient(
            (_, _) => StreamAsync(new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("second-call", "second_tool", new Dictionary<string, object?>())])),
            (_, _) => StreamAsync("second complete"));
        var firstLease = new TrackingProfileLease(new AgentRunProfile(
            firstClient,
            new AgentSessionOptions(),
            tools: [CreateSuccessfulTool("first_tool")]));
        var secondLease = new TrackingProfileLease(new AgentRunProfile(
            secondClient,
            new AgentSessionOptions(),
            tools: [CreateSuccessfulTool("second_tool")]));
        var session = new AgentSession(new QueueProfileProvider(firstLease, secondLease));

        await CompleteTurnAsync(session, "first", deadline.Token);
        await CompleteTurnAsync(session, "second", deadline.Token);

        firstClient.ToolNames.Should().AllSatisfy(static names => names.Should().Equal("first_tool"));
        secondClient.ToolNames.Should().AllSatisfy(static names => names.Should().Equal("second_tool"));
        firstLease.DisposeCount.Should().Be(1);
        secondLease.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task InputLimitIsRunLocalAndRejectedStartReleasesItsLease()
    {
        using var deadline = CreateDeadline();
        var rejectedClient = new ScriptedChatClient((_, _) => StreamAsync("unused"));
        var acceptedClient = new ScriptedChatClient((_, _) => StreamAsync("accepted"));
        var rejectedLease = new TrackingProfileLease(new AgentRunProfile(
            rejectedClient,
            new AgentSessionOptions { MaxInputCharacters = 3 }));
        var acceptedLease = new TrackingProfileLease(new AgentRunProfile(
            acceptedClient,
            new AgentSessionOptions { MaxInputCharacters = 10 }));
        var session = new AgentSession(new QueueProfileProvider(rejectedLease, acceptedLease));

        await (Session: session, deadline.Token)
            .Awaiting(static state => state.Session.StartTurnAsync(AgentTurn.FromText("four"), state.Token))
            .Should().ThrowAsync<AgentInputLimitExceededException>();

        rejectedLease.DisposeCount.Should().Be(1);
        rejectedClient.Requests.Should().BeEmpty();
        var result = await CompleteTurnAsync(session, "four", deadline.Token);
        result.AssistantMessage.Text.Should().Be("accepted");
        acceptedLease.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task CompletionRemainsPendingUntilLeaseIsReleased()
    {
        using var deadline = CreateDeadline();
        var releaseLease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new TrackingProfileLease(
            CreateProfile(new ScriptedChatClient((_, _) => StreamAsync("answer")), "instructions"),
            releaseLease.Task);
        var session = new AgentSession(new QueueProfileProvider(lease));

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("question"), deadline.Token);
        await ReadEventsAsync(run, deadline.Token);
        await lease.DisposeStarted.Task.WaitAsync(deadline.Token);

        run.Completion.IsCompleted.Should().BeFalse();
        lease.DisposeCount.Should().Be(1);

        releaseLease.SetResult();
        await run.Completion.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task LeaseReleaseFailureRollsBackSuccessfulTurn()
    {
        using var deadline = CreateDeadline();
        var failedLease = new TrackingProfileLease(
            CreateProfile(new ScriptedChatClient((_, _) => StreamAsync("uncommitted")), "first"),
            releaseException: new InvalidOperationException("lease release failed"));
        var recoveredLease = new TrackingProfileLease(
            CreateProfile(new ScriptedChatClient((_, _) => StreamAsync("committed")), "second"));
        var session = new AgentSession(new QueueProfileProvider(failedLease, recoveredLease));

        await using (var failed = await session.StartTurnAsync(AgentTurn.FromText("first"), deadline.Token))
        {
            await ReadEventsAsync(failed, deadline.Token);
            await failed.Completion.WaitAsync(deadline.Token)
                .Invoking(static task => task)
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("lease release failed");
        }

        session.GetTranscriptSnapshot().Turns.Should().BeEmpty();
        var recovered = await CompleteTurnAsync(session, "second", deadline.Token);
        recovered.Transcript.Version.Should().Be(1);
        recovered.Transcript.Turns.Should().ContainSingle();
    }

    [Fact]
    public async Task ProviderFailureReleasesLeaseAndAllowsNextProfile()
    {
        using var deadline = CreateDeadline();
        var failedLease = new TrackingProfileLease(CreateProfile(
            new ScriptedChatClient((_, _) => FailAsync(new InvalidOperationException("failed"))),
            "failed"));
        var recoveredLease = new TrackingProfileLease(CreateProfile(
            new ScriptedChatClient((_, _) => StreamAsync("recovered")),
            "recovered"));
        var session = new AgentSession(new QueueProfileProvider(failedLease, recoveredLease));

        await using (var failed = await session.StartTurnAsync(AgentTurn.FromText("first"), deadline.Token))
        {
            await ReadEventsAsync(failed, deadline.Token);
            await failed.Completion.WaitAsync(deadline.Token)
                .Invoking(static task => task)
                .Should().ThrowAsync<AgentProviderException>();
        }

        failedLease.DisposeCount.Should().Be(1);
        var recovered = await CompleteTurnAsync(session, "retry", deadline.Token);
        recovered.AssistantMessage.Text.Should().Be("recovered");
        recoveredLease.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task CancellationReleasesLeaseAndSessionReservation()
    {
        using var deadline = CreateDeadline();
        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledLease = new TrackingProfileLease(CreateProfile(
            new ScriptedChatClient((_, token) => WaitUntilCanceledAsync(providerStarted, token)),
            "cancel"));
        var nextLease = new TrackingProfileLease(CreateProfile(
            new ScriptedChatClient((_, _) => StreamAsync("next")),
            "next"));
        var session = new AgentSession(new QueueProfileProvider(canceledLease, nextLease));

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("cancel"), deadline.Token);
        await providerStarted.Task.WaitAsync(deadline.Token);
        await run.CancelAsync(deadline.Token);
        await run.Completion
            .Invoking(static task => task)
            .Should().ThrowAsync<OperationCanceledException>();

        canceledLease.DisposeCount.Should().Be(1);
        await CompleteTurnAsync(session, "next", deadline.Token);
        nextLease.DisposeCount.Should().Be(1);
    }

    private static AgentRunProfile CreateProfile(IChatClient client, string instructions) =>
        new(client, new AgentSessionOptions { SystemPrompt = instructions });

    private static async Task<AgentRunResult> CompleteTurnAsync(
        AgentSession session,
        string input,
        CancellationToken cancellationToken)
    {
        await using var run = await session.StartTurnAsync(AgentTurn.FromText(input), cancellationToken);
        await ReadEventsAsync(run, cancellationToken);
        return await run.Completion.WaitAsync(cancellationToken);
    }

    private static async Task ReadEventsAsync(IAgentRun run, CancellationToken cancellationToken)
    {
        await foreach (var _ in run.Events.WithCancellation(cancellationToken))
        {
        }
    }

    private static (ChatRole Role, string Text) MessageTuple(ChatMessage message) =>
        (message.Role, message.Text);

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(params string[] values)
    {
        await Task.Yield();
        foreach (var value in values)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, value);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(ChatResponseUpdate update)
    {
        await Task.Yield();
        yield return update;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FailAsync(Exception exception)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
        await Task.Yield();
        throw exception;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WaitUntilCanceledAsync(
        TaskCompletionSource started,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    private static CancellationTokenSource CreateDeadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return deadline;
    }

    private sealed class QueueProfileProvider(params TrackingProfileLease[] leases) : IAgentRunProfileProvider
    {
        private readonly Queue<TrackingProfileLease> leases = new(leases);

        public int AcquireCount { get; private set; }

        public IAgentRunProfileLease Acquire()
        {
            AcquireCount++;
            return leases.Dequeue();
        }
    }

    private sealed class TrackingProfileLease(
        AgentRunProfile profile,
        Task? release = null,
        Exception? releaseException = null) : IAgentRunProfileLease
    {
        public AgentRunProfile Profile { get; } = profile;

        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeStarted.TrySetResult();
            if (release is not null)
            {
                await release.ConfigureAwait(false);
            }

            if (releaseException is not null)
            {
                throw releaseException;
            }
        }
    }

    private sealed class ScriptedChatClient(
        params Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>[] responses)
        : IChatClient
    {
        private readonly Queue<Func<IReadOnlyList<ChatMessage>, CancellationToken,
            IAsyncEnumerable<ChatResponseUpdate>>> responses = new(responses);

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public List<string?> Instructions { get; } = [];

        public List<string[]> ToolNames { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(new NotSupportedException());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.Select(static message => message.Clone()).ToArray());
            Instructions.Add(options?.Instructions);
            ToolNames.Add(options?.Tools?.OfType<AIFunction>().Select(static tool => tool.Name).ToArray() ?? []);
            return responses.Dequeue()(Requests[^1], cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static AIFunction CreateSuccessfulTool(string name = "echo") =>
        AIFunctionFactory.Create(
            () => "ok",
            name: name,
            description: "Returns success.");
}