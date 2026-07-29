using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent.Tests;

public sealed class AgentTranscriptCodecTests
{
    [Fact]
    public void OfficialContentContractsRoundTripPrivateStateAndCreateSanitizedPublicSnapshot()
    {
        var text = new TextContent("answer")
        {
            RawRepresentation = "raw-content",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["provider_text"] = ParseJson("{\"kept\":true}")
            },
            Annotations =
            [
                new CitationAnnotation
                {
                    Title = "source",
                    Url = new Uri("https://example.test/source"),
                    RawRepresentation = "raw-annotation"
                }
            ]
        };
        var reasoning = new TextReasoningContent("private reasoning")
        {
            ProtectedData = "protected-token",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["provider_reasoning"] = ParseJson("42")
            }
        };
        var call = new FunctionCallContent(
            "provider-call",
            "lookup",
            new Dictionary<string, object?> { ["query"] = ParseJson("\"value\"") });
        var result = new FunctionResultContent("provider-call", ParseJson("{\"status\":\"ok\"}"));
        var usage = new UsageContent(new UsageDetails
        {
            InputTokenCount = 4,
            OutputTokenCount = 7,
            ReasoningTokenCount = 3
        });
        var data = new DataContent(Encoding.UTF8.GetBytes("{\"value\":1}"), "application/json");
        var assistant = new ChatMessage(
            ChatRole.Assistant,
            [text, reasoning, call, result, usage, data])
        {
            MessageId = "provider-message",
            RawRepresentation = "raw-message",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["provider_message"] = ParseJson("true")
            }
        };
        var sessionId = AgentSessionId.Create();
        var runId = AgentRunId.Create();
        var identity = new AgentModelIdentity(new AgentModelProfileId("deepseek"), "DeepSeek", "deepseek-reasoner");
        var detachedTurn = AgentTranscriptCodec.DetachPrivateTurn(
            runId,
            identity,
            [new ChatMessage(ChatRole.User, "question"), assistant]);
        var state = new AgentTranscriptState(sessionId, 1, [detachedTurn]);

        detachedTurn.MessageByteCount.Should().BePositive();
        var privateAssistant = detachedTurn.Messages[1];
        privateAssistant.MessageId.Should().Be("provider-message");
        privateAssistant.RawRepresentation.Should().BeNull();
        privateAssistant.AdditionalProperties.Should().ContainKey("provider_message");
        privateAssistant.Contents.Select(static content => content.GetType()).Should().Equal(
            typeof(TextContent),
            typeof(TextReasoningContent),
            typeof(FunctionCallContent),
            typeof(FunctionResultContent),
            typeof(UsageContent),
            typeof(DataContent));
        var privateReasoning = privateAssistant.Contents.OfType<TextReasoningContent>().Single();
        privateReasoning.Text.Should().Be("private reasoning");
        privateReasoning.ProtectedData.Should().NotBeNull();
        JsonSerializer.Serialize(privateReasoning.ProtectedData).Should().Contain("protected-token");
        privateAssistant.Contents.OfType<FunctionCallContent>().Single().CallId.Should().Be("provider-call");
        privateAssistant.Contents.OfType<FunctionResultContent>().Single().CallId.Should().Be("provider-call");
        privateAssistant.Contents.OfType<UsageContent>().Single().Details.ReasoningTokenCount.Should().Be(3);
        privateAssistant.Contents.OfType<DataContent>().Single().Data.ToArray().Should()
            .Equal(Encoding.UTF8.GetBytes("{\"value\":1}"));
        privateAssistant.Contents.OfType<TextContent>().Single().Annotations.Should()
            .ContainSingle().Which.Should().BeOfType<CitationAnnotation>();

        var publicTranscript = AgentTranscriptCodec.CreatePublicTranscript(state);
        var publicAssistant = publicTranscript.Turns.Single().Messages[1];
        publicAssistant.Contents.Should().NotContain(static content => content is TextReasoningContent);
        publicAssistant.RawRepresentation.Should().BeNull();
        publicAssistant.AdditionalProperties.Should().BeNull();
        publicAssistant.Contents.Should().OnlyContain(static content =>
            content.RawRepresentation == null && content.AdditionalProperties == null);
        publicAssistant.Contents.OfType<TextContent>().Single().Annotations.Should().ContainSingle()
            .Which.AdditionalProperties.Should().BeNull();
    }

    [Fact]
    public void CustomContentProducesTypedCompatibilityFailure()
    {
        var failure = FluentActions.Invoking(() => AgentTranscriptCodec.DetachPrivateTurn(
                AgentRunId.Create(),
                null,
                [
                    new ChatMessage(ChatRole.User, "question"),
                    new ChatMessage(ChatRole.Assistant, [new TextContent("answer"), new CustomContent()])
                ]))
            .Should().Throw<AgentContentCompatibilityException>().Which;

        failure.ContentType.Should().Contain(nameof(CustomContent));
        failure.InnerException.Should().BeAssignableTo<Exception>();
    }

    [Fact]
    public void MessageByteCountUsesCompactUtf8Size()
    {
        var ascii = AgentTranscriptCodec.DetachPrivateTurn(
            AgentRunId.Create(),
            null,
            [new ChatMessage(ChatRole.User, "aa"), new ChatMessage(ChatRole.Assistant, "bb")]);
        var nonAscii = AgentTranscriptCodec.DetachPrivateTurn(
            AgentRunId.Create(),
            null,
            [new ChatMessage(ChatRole.User, "你你"), new ChatMessage(ChatRole.Assistant, "界界")]);

        nonAscii.MessageByteCount.Should().BeGreaterThan(ascii.MessageByteCount);
    }

    [Fact]
    public void DetachingTurnRejectsInlineBinaryContentAndBinaryMetadata()
    {
        var direct = FluentActions.Invoking(() => DetachAssistant(
            new TextContent("answer"),
            new DataContent(new byte[] { 1, 2, 3 }, "image/png")));
        var nested = new ImageGenerationToolResultContent("image-call")
        {
            Outputs = [new DataContent(new byte[] { 4, 5, 6 }, "image/png")]
        };
        var nestedAction = FluentActions.Invoking(() => DetachAssistant(new TextContent("answer"), nested));
        var nestedResult = new FunctionResultContent(
            "result-call",
            new Dictionary<string, object?>
            {
                ["nested"] = new object?[] { "text", new byte[] { 6, 7, 8 } }
            });
        var nestedResultAction = FluentActions.Invoking(() => DetachAssistant(
            new TextContent("answer"),
            nestedResult));
        var invalidJsonAction = FluentActions.Invoking(() => DetachAssistant(
            new TextContent("answer"),
            new DataContent(Encoding.UTF8.GetBytes("not-json"), "application/json")));
        var metadata = new TextContent("answer")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["payload"] = new byte[] { 7, 8, 9 }
            }
        };
        var metadataAction = FluentActions.Invoking(() => DetachAssistant(metadata));
        var binaryDataMetadata = new TextContent("answer")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["payload"] = CreateBinaryDataLikeValue()
            }
        };
        var binaryDataAction = FluentActions.Invoking(() => DetachAssistant(binaryDataMetadata));

        direct.Should().Throw<AgentUnsupportedResponseException>();
        nestedAction.Should().Throw<AgentUnsupportedResponseException>();
        nestedResultAction.Should().Throw<AgentUnsupportedResponseException>();
        invalidJsonAction.Should().Throw<AgentUnsupportedResponseException>();
        metadataAction.Should().Throw<AgentUnsupportedResponseException>();
        binaryDataAction.Should().Throw<AgentUnsupportedResponseException>();
    }

    [Fact]
    public void ProtectedReasoningDataIsThePrivateBinaryException()
    {
        var detached = DetachAssistant(
            new TextReasoningContent("private") { ProtectedData = "AQID" },
            new TextContent("answer"));

        var reasoning = detached.Messages.Single().Contents.OfType<TextReasoningContent>().Single();
        JsonSerializer.Serialize(reasoning.ProtectedData).Should().Contain("AQID");
        AgentTranscriptCodec.CreatePublicMessage(detached.Messages.Single()).Contents.Should()
            .NotContain(static content => content is TextReasoningContent);
    }

    private static AgentTranscriptStateTurn DetachAssistant(params AIContent[] contents) =>
        AgentTranscriptCodec.DetachPrivateTurn(
            AgentRunId.Create(),
            null,
            [new ChatMessage(ChatRole.Assistant, contents)]);

    private static object CreateBinaryDataLikeValue()
    {
        var assemblyName = new AssemblyName($"BinaryDataStub-{Guid.NewGuid():N}");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule(assemblyName.Name!).DefineType("System.BinaryData").CreateType();
        return Activator.CreateInstance(type!)!;
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class CustomContent : AIContent;
}

public sealed class AgentTranscriptSessionTests
{
    [Fact]
    public async Task ReasoningAndProtectedDataStayPrivateAndReplayToTheNextTurn()
    {
        using var deadline = CreateDeadline();
        var reasoning = new TextReasoningContent("private chain") { ProtectedData = "opaque-state" };
        var client = new RecordingClient(
            (_, _) => StreamUpdatesAsync(
                new ChatResponseUpdate(ChatRole.Assistant, [reasoning]) { MessageId = "response" },
                new ChatResponseUpdate(ChatRole.Assistant, "visible answer") { MessageId = "response" }),
            (_, _) => StreamAsync("next answer"));
        var session = new AgentSession(client);

        var first = await CompleteTurnAsync(session, "question", deadline.Token);

        first.Events.OfType<AgentTextDelta>().Select(static delta => delta.Text).Should().Equal("visible answer");
        first.Events.OfType<AgentMessageCompleted>().Single().Message.Contents.Should()
            .NotContain(static content => content is TextReasoningContent);
        first.Result.AssistantMessage.Contents.Should()
            .NotContain(static content => content is TextReasoningContent);
        first.Result.Transcript.Turns.Single().Messages[1].Contents.Should()
            .NotContain(static content => content is TextReasoningContent);

        await CompleteTurnAsync(session, "continue", deadline.Token);

        var replayedAssistant = client.Requests[1].Single(static message => message.Role == ChatRole.Assistant);
        var replayedReasoning = replayedAssistant.Contents.OfType<TextReasoningContent>().Single();
        replayedReasoning.Text.Should().Be("private chain");
        JsonSerializer.Serialize(replayedReasoning.ProtectedData).Should().Contain("opaque-state");
    }

    [Fact]
    public async Task ReasoningOnlyAndCustomContentRollBackTranscriptVersion()
    {
        using var deadline = CreateDeadline();
        var reasoningOnly = new AgentSession(new RecordingClient((_, _) => StreamUpdatesAsync(new ChatResponseUpdate(
            ChatRole.Assistant,
            [new TextReasoningContent("private only")]))));

        await using (var run = await reasoningOnly.StartTurnAsync(AgentTurn.FromText("question"), deadline.Token))
        {
            await ReadEventsAsync(run, deadline.Token);
            await run.Completion.WaitAsync(deadline.Token)
                .Invoking(static task => task)
                .Should().ThrowAsync<AgentUnsupportedResponseException>();
        }

        reasoningOnly.GetTranscriptSnapshot().Version.Should().Be(0);
        reasoningOnly.GetTranscriptSnapshot().Turns.Should().BeEmpty();

        var custom = new AgentSession(new RecordingClient((_, _) => StreamUpdatesAsync(new ChatResponseUpdate(
            ChatRole.Assistant,
            [new TextContent("visible"), new CustomContent()]))));
        await using (var run = await custom.StartTurnAsync(AgentTurn.FromText("question"), deadline.Token))
        {
            await ReadEventsAsync(run, deadline.Token);
            await run.Completion.WaitAsync(deadline.Token)
                .Invoking(static task => task)
                .Should().ThrowAsync<AgentContentCompatibilityException>();
        }

        custom.GetTranscriptSnapshot().Version.Should().Be(0);
        custom.GetTranscriptSnapshot().Turns.Should().BeEmpty();
    }

    [Fact]
    public async Task InlineBinaryResponseRollsBackAndSessionRemainsUsable()
    {
        using var deadline = CreateDeadline();
        var binary = new ChatResponseUpdate(
            ChatRole.Assistant,
            [new TextContent("visible"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
        var client = new RecordingClient(
            (_, _) => StreamUpdatesAsync(binary),
            (_, _) => StreamAsync("recovered"));
        var session = new AgentSession(client);

        await using (var run = await session.StartTurnAsync(AgentTurn.FromText("image"), deadline.Token))
        {
            var events = await ReadEventsAsync(run, deadline.Token);
            events.OfType<AgentTextDelta>().Select(static delta => delta.Text).Should().Equal("visible");
            events.Should().NotContain(static agentEvent => agentEvent is AgentMessageCompleted);
            await run.Completion.WaitAsync(deadline.Token)
                .Invoking(static task => task)
                .Should().ThrowAsync<AgentUnsupportedResponseException>();
        }

        var rolledBack = session.GetTranscriptSnapshot();
        rolledBack.Version.Should().Be(0);
        rolledBack.Turns.Should().BeEmpty();

        var recovered = await CompleteTurnAsync(session, "retry", deadline.Token);
        recovered.Result.AssistantMessage.Text.Should().Be("recovered");
        recovered.Result.Transcript.Version.Should().Be(1);
        recovered.Result.Transcript.Turns.Should().ContainSingle();
    }

    [Fact]
    public async Task MutatingPublicMessagesCannotChangeCanonicalReplay()
    {
        using var deadline = CreateDeadline();
        var client = new RecordingClient(
            (_, _) => StreamAsync("first answer"),
            (_, _) => StreamAsync("second answer"));
        var session = new AgentSession(client);

        var first = await CompleteTurnAsync(session, "first question", deadline.Token);
        first.Result.UserMessage.Contents.Clear();
        ((TextContent)first.Result.AssistantMessage.Contents.Single()).Text = "mutated result";
        ((TextContent)first.Result.Transcript.Turns.Single().Messages[1].Contents.Single()).Text = "mutated snapshot";

        await CompleteTurnAsync(session, "second question", deadline.Token);

        client.Requests[1].Select(static message => (message.Role, message.Text)).Should().Equal(
            (ChatRole.User, "first question"),
            (ChatRole.Assistant, "first answer"),
            (ChatRole.User, "second question"));
        session.GetTranscriptSnapshot().Turns[0].Messages.Select(static message => message.Text).Should()
            .Equal("first question", "first answer");
    }

    [Fact]
    public async Task ProviderCannotMutateCanonicalReplayMessages()
    {
        using var deadline = CreateDeadline();
        var client = new RecordingClient(
            (_, _) => StreamAsync("first answer"),
            (messages, _) =>
            {
                ((TextContent)messages.Single(static message => message.Role == ChatRole.Assistant)
                    .Contents.Single()).Text = "provider mutation";
                return StreamAsync("second answer");
            },
            (_, _) => StreamAsync("third answer"));
        var session = new AgentSession(client);

        await CompleteTurnAsync(session, "first question", deadline.Token);
        await CompleteTurnAsync(session, "second question", deadline.Token);
        await CompleteTurnAsync(session, "third question", deadline.Token);

        client.Requests[2].First(static message => message.Role == ChatRole.Assistant).Text.Should()
            .Be("first answer");
        session.GetTranscriptSnapshot().Turns[0].Messages[1].Text.Should().Be("first answer");
    }

    [Fact]
    public async Task HistoryByteLimitEvictsCompleteOldestTurnForNonAsciiMessages()
    {
        using var deadline = CreateDeadline();
        var sample = AgentTranscriptCodec.DetachPrivateTurn(
            AgentRunId.Create(),
            null,
            [new ChatMessage(ChatRole.User, "问题"), new ChatMessage(ChatRole.Assistant, "答案")]);
        var oneTurnBytes = sample.MessageByteCount;
        var session = new AgentSession(
            new RecordingClient((_, _) => StreamAsync("答案"), (_, _) => StreamAsync("答案")),
            new AgentSessionOptions { MaxHistoryBytes = checked(oneTurnBytes * 2 - 1) });

        await CompleteTurnAsync(session, "问题", deadline.Token);
        await CompleteTurnAsync(session, "问题", deadline.Token);

        var transcript = session.GetTranscriptSnapshot();
        transcript.Version.Should().Be(2);
        transcript.Turns.Should().ContainSingle();
        transcript.Turns.Single().Messages.Select(static message => message.Text).Should().Equal("问题", "答案");
    }

    private static async Task<(AgentRunResult Result, IReadOnlyList<AgentEvent> Events)> CompleteTurnAsync(
        AgentSession session,
        string input,
        CancellationToken cancellationToken)
    {
        await using var run = await session.StartTurnAsync(AgentTurn.FromText(input), cancellationToken);
        var events = await ReadEventsAsync(run, cancellationToken);
        var result = await run.Completion.WaitAsync(cancellationToken);
        return (result, events);
    }

    private static async Task<IReadOnlyList<AgentEvent>> ReadEventsAsync(
        IAgentRun run,
        CancellationToken cancellationToken)
    {
        var events = new List<AgentEvent>();
        await foreach (var agentEvent in run.Events.WithCancellation(cancellationToken))
        {
            events.Add(agentEvent);
        }

        return events;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(string text)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamUpdatesAsync(
        params ChatResponseUpdate[] updates)
    {
        await Task.Yield();
        foreach (var update in updates)
        {
            yield return update;
        }
    }

    private static CancellationTokenSource CreateDeadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return deadline;
    }

    private sealed class RecordingClient(
        params Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>[] responses)
        : IChatClient
    {
        private readonly Queue<Func<IReadOnlyList<ChatMessage>, CancellationToken,
            IAsyncEnumerable<ChatResponseUpdate>>> responses = new(responses);

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

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
            var request = messages.Select(static message => message.Clone()).ToArray();
            Requests.Add(request);
            return responses.Dequeue()(request, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CustomContent : AIContent;
}