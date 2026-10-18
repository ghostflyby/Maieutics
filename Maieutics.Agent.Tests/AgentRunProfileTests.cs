using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent.Tests;

public sealed class AgentRunProfileTests
{
    [Fact]
    public void ModelProfileIdentifiersValidateAndRenderStableValues()
    {
        var id = new AgentModelProfileId("OpenAI_primary-1");

        Assert.Equal("OpenAI_primary-1", id.Value);
        Assert.Equal("OpenAI_primary-1", id.ToString());
        Assert.ThrowsAny<ArgumentException>(() => new AgentModelProfileId(""));
        Assert.ThrowsAny<ArgumentException>(() => new AgentModelProfileId("-invalid"));
        Assert.ThrowsAny<ArgumentException>(() => new AgentModelProfileId("contains space"));
        Assert.ThrowsAny<ArgumentException>(() => new AgentModelProfileId(new string('a', 65)));
    }

    [Fact]
    public void LegacyRunProfileKeepsCompatibleCapabilitiesAndNoIdentity()
    {
        var profile = new AgentRunProfile(
            new ScriptedChatClient((_, _) => StreamAsync("unused")),
            new AgentSessionOptions());

        Assert.Null(profile.ModelIdentity);
        Assert.Equal(AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling, profile.Capabilities);
        Assert.Empty(profile.HostedCapabilities);
    }

    [Fact]
    public void HostedCapabilitiesAreSortedAndDeduplicated()
    {
        var profile = new AgentRunProfile(
            new ScriptedChatClient((_, _) => StreamAsync("unused")),
            new AgentSessionOptions(),
            hostedCapabilities: ["WebSearch", "FileSearch", "WebSearch"]);

        Assert.Equal(["FileSearch", "WebSearch"], profile.HostedCapabilities);
    }

    [Fact]
    public void BlankHostedCapabilitiesAreRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => new AgentRunProfile(
                new ScriptedChatClient((_, _) => StreamAsync("unused")),
                new AgentSessionOptions(),
                hostedCapabilities: ["WebSearch", " "]));
    }

    [Fact]
    public void HostedToolsAreCarriedApartFromLocalFunctions()
    {
        var function = AIFunctionFactory.Create(() => "ok", "read_text");
        var profile = new AgentRunProfile(
            new ScriptedChatClient((_, _) => StreamAsync("unused")),
            new AgentSessionOptions(),
            hostedCapabilities: ["WebSearch"],
            tools: [function],
            hostedTools: [new HostedWebSearchTool()]);

        Assert.Same(function, Assert.Single(profile.Tools));
        Assert.IsType<HostedWebSearchTool>(Assert.Single(profile.HostedTools));
    }

    [Fact]
    public void HostedToolsRejectNameCollisionsWithLocalFunctions()
    {
        // The hosted web search tool is named "web_search", so a local function by that name
        // would make the provider's tool choice ambiguous.
        var function = AIFunctionFactory.Create(() => "ok", "web_search");

        Assert.ThrowsAny<ArgumentException>(() => new AgentRunProfile(
                new ScriptedChatClient((_, _) => StreamAsync("unused")),
                new AgentSessionOptions(),
                tools: [function],
                hostedTools: [new HostedWebSearchTool()]));
    }

    [Fact]
    public void HostedToolsRejectLocalFunctionsAndDuplicates()
    {
        var function = AIFunctionFactory.Create(() => "ok", "read_text");

        Assert.ThrowsAny<ArgumentException>(() => new AgentRunProfile(
                new ScriptedChatClient((_, _) => StreamAsync("unused")),
                new AgentSessionOptions(),
                hostedTools: [function]));

        Assert.ThrowsAny<ArgumentException>(() => new AgentRunProfile(
                new ScriptedChatClient((_, _) => StreamAsync("unused")),
                new AgentSessionOptions(),
                hostedTools: [new HostedWebSearchTool(), new HostedWebSearchTool()]));
    }

    [Fact(Timeout = 30_000)]
    public async Task StaticConstructorKeepsExistingClientOptionsAndTranscriptBehavior()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var client = new ScriptedChatClient(
            (_, _) => StreamAsync("first answer"),
            (_, _) => StreamAsync("second answer"));
        var session = new AgentSession(client, new AgentSessionOptions { SystemPrompt = "static instructions" });

        await CompleteTurnAsync(session, "first question", deadline.Token);
        var result = await CompleteTurnAsync(session, "second question", deadline.Token);

        Assert.Equal(new[] { "static instructions", "static instructions" }, client.Instructions);
        Assert.Equal(new (ChatRole Role, string Text)[] { (ChatRole.User, "first question"), (ChatRole.Assistant, "first answer"), (ChatRole.User, "second question") }, client.Requests[1].Select(MessageTuple));
        Assert.Equal(2, result.Transcript.Version);
        Assert.Equal(2, result.Transcript.Turns.Count());
        Assert.Null(result.ModelIdentity);
        Assert.All(result.Transcript.Turns, static turn => Assert.True(turn.ModelIdentity == null));
    }

    [Fact(Timeout = 30_000)]
    public async Task DynamicProfileAppliesToNextRunAndReplaysCanonicalTranscript()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var firstClient = new ScriptedChatClient((_, _) => StreamAsync("first answer"));
        var secondClient = new ScriptedChatClient((_, _) => StreamAsync("second answer"));
        var firstLease = new TrackingProfileLease(CreateProfile(firstClient, "first instructions"));
        var secondLease = new TrackingProfileLease(CreateProfile(secondClient, "second instructions"));
        var session = new AgentSession(new QueueProfileProvider(firstLease, secondLease));

        await CompleteTurnAsync(session, "first question", deadline.Token);
        var result = await CompleteTurnAsync(session, "second question", deadline.Token);

        Assert.Equal(new[] { "first instructions" }, firstClient.Instructions);
        Assert.Equal(new[] { "second instructions" }, secondClient.Instructions);
        Assert.Equal(new (ChatRole Role, string Text)[] { (ChatRole.User, "first question"), (ChatRole.Assistant, "first answer"), (ChatRole.User, "second question") }, secondClient.Requests.Single().Select(MessageTuple));
        Assert.Equal(1, firstLease.DisposeCount);
        Assert.Equal(1, secondLease.DisposeCount);
        Assert.Equal(2, result.Transcript.Turns.Count());
    }

    [Fact(Timeout = 30_000)]
    public async Task PendingProfileAcquisitionKeepsTheSessionReserved()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var acquisition = new TaskCompletionSource<IAgentRunProfileLease>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new TrackingProfileLease(
            CreateProfile(new ScriptedChatClient((_, _) => StreamAsync("answer")), "instructions"));
        var provider = new AsyncQueueProfileProvider(acquisition.Task);
        var session = new AgentSession(provider);

        var pending = session.StartTurnAsync(AgentTurn.FromText("first"), deadline.Token);
        Assert.False(pending.IsCompleted);
        await Assert.ThrowsAnyAsync<AgentTurnInProgressException>(async () => { await (Session: session, deadline.Token).Session.StartTurnAsync(AgentTurn.FromText("second"), (Session: session, deadline.Token).Token); });

        acquisition.SetResult(lease);
        await using var run = await pending.WaitAsync(deadline.Token);
        await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        Assert.Equal(1, provider.AcquireCount);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task CanceledProfileAcquisitionReleasesTheSessionReservation()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var pendingAcquisition = new TaskCompletionSource<IAgentRunProfileLease>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredLease = new TrackingProfileLease(
            CreateProfile(new ScriptedChatClient((_, _) => StreamAsync("recovered")), "instructions"));
        var provider = new AsyncQueueProfileProvider(
            pendingAcquisition.Task,
            Task.FromResult<IAgentRunProfileLease>(recoveredLease));
        var session = new AgentSession(provider);

        var pending = session.StartTurnAsync(AgentTurn.FromText("cancel"), cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await pending; });
        var result = await CompleteTurnAsync(session, "next", deadline.Token);

        Assert.Equal("recovered", result.AssistantMessage.Text);
        Assert.Equal(2, provider.AcquireCount);
        Assert.Equal(1, recoveredLease.DisposeCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task SuccessfulRunCommitsCapturedModelIdentityToResultAndTranscript()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
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

        Assert.Equal(identity, result.ModelIdentity);
        Assert.Single(result.Transcript.Turns);
        Assert.Equal(identity, result.Transcript.Turns[0].ModelIdentity);
        Assert.Equal(identity, session.GetTranscriptSnapshot().Turns[0].ModelIdentity);
    }

    [Fact(Timeout = 30_000)]
    public async Task MissingStreamingCapabilityFailsBeforeProviderInvocation()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
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
        var failure = await Assert.ThrowsAnyAsync<AgentModelCapabilityException>(async () => { await run.Completion.WaitAsync(deadline.Token); });

        Assert.Equal(AgentModelCapabilities.StreamingText, failure.RequiredCapability);
        Assert.Equal(identity, failure.ModelIdentity);
        Assert.Empty(client.Requests);
        Assert.Equal(1, lease.DisposeCount);
        Assert.Empty(session.GetTranscriptSnapshot().Turns);
    }

    [Fact(Timeout = 30_000)]
    public async Task MissingFunctionCallingCapabilityFailsBeforeProviderInvocationWhenToolsExist()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
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
        var failure = await Assert.ThrowsAnyAsync<AgentModelCapabilityException>(async () => { await run.Completion.WaitAsync(deadline.Token); });

        Assert.Equal(AgentModelCapabilities.FunctionCalling, failure.RequiredCapability);
        Assert.Equal(identity, failure.ModelIdentity);
        Assert.Empty(client.Requests);
        Assert.Equal(1, lease.DisposeCount);
        Assert.Empty(session.GetTranscriptSnapshot().Turns);
    }

    [Fact(Timeout = 30_000)]
    public async Task ActiveRunUsesOneCapturedClientAcrossToolIterations()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
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

        Assert.Equal("tool complete", first.AssistantMessage.Text);
        Assert.Equal(2, firstClient.Requests.Count());
        Assert.Empty(nextClient.Requests);
        Assert.Equal(1, provider.AcquireCount);

        await CompleteTurnAsync(session, "next", deadline.Token);
        Assert.Single(nextClient.Requests);
        Assert.Equal(2, provider.AcquireCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task DynamicProfilesExposeDifferentRunLocalToolRegistries()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
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

        Assert.All(firstClient.ToolNames, names => Assert.Equal(new[] { "first_tool" }, names));
        Assert.All(secondClient.ToolNames, names => Assert.Equal(new[] { "second_tool" }, names));
        Assert.Equal(1, firstLease.DisposeCount);
        Assert.Equal(1, secondLease.DisposeCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task InputLimitIsRunLocalAndRejectedStartReleasesItsLease()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var rejectedClient = new ScriptedChatClient((_, _) => StreamAsync("unused"));
        var acceptedClient = new ScriptedChatClient((_, _) => StreamAsync("accepted"));
        var rejectedLease = new TrackingProfileLease(new AgentRunProfile(
            rejectedClient,
            new AgentSessionOptions { MaxInputCharacters = 3 }));
        var acceptedLease = new TrackingProfileLease(new AgentRunProfile(
            acceptedClient,
            new AgentSessionOptions { MaxInputCharacters = 10 }));
        var session = new AgentSession(new QueueProfileProvider(rejectedLease, acceptedLease));

        await Assert.ThrowsAnyAsync<AgentInputLimitExceededException>(async () => { await (Session: session, deadline.Token).Session.StartTurnAsync(AgentTurn.FromText("four"), (Session: session, deadline.Token).Token); });

        Assert.Equal(1, rejectedLease.DisposeCount);
        Assert.Empty(rejectedClient.Requests);
        var result = await CompleteTurnAsync(session, "four", deadline.Token);
        Assert.Equal("accepted", result.AssistantMessage.Text);
        Assert.Equal(1, acceptedLease.DisposeCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task CompletionRemainsPendingUntilLeaseIsReleased()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var releaseLease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new TrackingProfileLease(
            CreateProfile(new ScriptedChatClient((_, _) => StreamAsync("answer")), "instructions"),
            releaseLease.Task);
        var session = new AgentSession(new QueueProfileProvider(lease));

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("question"), deadline.Token);
        await ReadEventsAsync(run, deadline.Token);
        await lease.DisposeStarted.Task.WaitAsync(deadline.Token);

        Assert.False(run.Completion.IsCompleted);
        Assert.Equal(1, lease.DisposeCount);

        releaseLease.SetResult();
        await run.Completion.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task LeaseReleaseFailureRollsBackSuccessfulTurn()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var failedLease = new TrackingProfileLease(
            CreateProfile(new ScriptedChatClient((_, _) => StreamAsync("uncommitted")), "first"),
            releaseException: new InvalidOperationException("lease release failed"));
        var recoveredLease = new TrackingProfileLease(
            CreateProfile(new ScriptedChatClient((_, _) => StreamAsync("committed")), "second"));
        var session = new AgentSession(new QueueProfileProvider(failedLease, recoveredLease));

        await using (var failed = await session.StartTurnAsync(AgentTurn.FromText("first"), deadline.Token))
        {
            await ReadEventsAsync(failed, deadline.Token);
            var assertion1 = await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => { await failed.Completion.WaitAsync(deadline.Token); });
            Assert.Equal("lease release failed", assertion1.Message);
        }

        Assert.Empty(session.GetTranscriptSnapshot().Turns);
        var recovered = await CompleteTurnAsync(session, "second", deadline.Token);
        Assert.Equal(1, recovered.Transcript.Version);
        Assert.Single(recovered.Transcript.Turns);
    }

    [Fact(Timeout = 30_000)]
    public async Task ProviderFailureReleasesLeaseAndAllowsNextProfile()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
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
            await Assert.ThrowsAnyAsync<AgentProviderException>(async () => { await failed.Completion.WaitAsync(deadline.Token); });
        }

        Assert.Equal(1, failedLease.DisposeCount);
        var recovered = await CompleteTurnAsync(session, "retry", deadline.Token);
        Assert.Equal("recovered", recovered.AssistantMessage.Text);
        Assert.Equal(1, recoveredLease.DisposeCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellationReleasesLeaseAndSessionReservation()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await run.Completion; });

        Assert.Equal(1, canceledLease.DisposeCount);
        await CompleteTurnAsync(session, "next", deadline.Token);
        Assert.Equal(1, nextLease.DisposeCount);
    }

    private static AgentRunProfile CreateProfile(IChatClient client, string instructions)
    {
        return new AgentRunProfile(client, new AgentSessionOptions { SystemPrompt = instructions });
    }

    [Fact(Timeout = 30_000)]
    public async Task HostedToolsReachTheProviderRequestAlongsideLocalFunctions()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var client = new ScriptedChatClient((_, _) => StreamAsync("answer"));
        var profile = new AgentRunProfile(
            client,
            new AgentSessionOptions(),
            hostedCapabilities: ["WebSearch"],
            tools: [AIFunctionFactory.Create(() => "ok", "read_text")],
            hostedTools: [new HostedWebSearchTool()]);
        var session = new AgentSession(new SingleProfileProvider(profile));

        await CompleteTurnAsync(session, "search something", deadline.Token);

        // The request carries both the local function (still invocable by the runtime) and the
        // provider-hosted tool (executed by the provider).
        Assert.Contains(Assert.Single(client.AllTools), tool => tool is HostedWebSearchTool);
        Assert.Equal(new[] { "read_text" }, Assert.Single(client.ToolNames));
    }

    private sealed class SingleProfileProvider(AgentRunProfile profile) : IAgentRunProfileProvider
    {
        public Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAgentRunProfileLease>(new Lease(profile));

        private sealed class Lease(AgentRunProfile profile) : IAgentRunProfileLease
        {
            public AgentRunProfile Profile => profile;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

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

    private static (ChatRole Role, string Text) MessageTuple(ChatMessage message)
    {
        return (message.Role, message.Text);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(params string[] values)
    {
        await Task.Yield();
        foreach (var value in values) yield return new ChatResponseUpdate(ChatRole.Assistant, value);
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

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return deadline;
    }

    private static AIFunction CreateSuccessfulTool(string name = "echo")
    {
        return AIFunctionFactory.Create(
            () => "ok",
            name,
            "Returns success.");
    }

    private sealed class QueueProfileProvider(params TrackingProfileLease[] leases) : IAgentRunProfileProvider
    {
        private readonly Queue<TrackingProfileLease> leases = new(leases);

        public int AcquireCount { get; private set; }

        public Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            return Task.FromResult<IAgentRunProfileLease>(leases.Dequeue());
        }
    }

    private sealed class AsyncQueueProfileProvider(params Task<IAgentRunProfileLease>[] acquisitions)
        : IAgentRunProfileProvider
    {
        private readonly Queue<Task<IAgentRunProfileLease>> acquisitions = new(acquisitions);

        public int AcquireCount { get; private set; }

        public Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            return acquisitions.Dequeue().WaitAsync(cancellationToken);
        }
    }

    private sealed class TrackingProfileLease(
        AgentRunProfile profile,
        Task? release = null,
        Exception? releaseException = null) : IAgentRunProfileLease
    {
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }
        public AgentRunProfile Profile { get; } = profile;

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeStarted.TrySetResult();
            if (release is not null) await release.ConfigureAwait(false);

            if (releaseException is not null) throw releaseException;
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

        /// <summary>Every tool attached to each request, including provider-hosted ones.</summary>
        public List<AITool[]> AllTools { get; } = [];

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
            Requests.Add(messages.Select(static message => message.Clone()).ToArray());
            Instructions.Add(options?.Instructions);
            ToolNames.Add(options?.Tools?.OfType<AIFunction>().Select(static tool => tool.Name).ToArray() ?? []);
            AllTools.Add(options?.Tools?.ToArray() ?? []);
            return responses.Dequeue()(Requests[^1], cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }
}
