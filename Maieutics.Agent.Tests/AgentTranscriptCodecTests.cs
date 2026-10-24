using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
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

        Assert.True(detachedTurn.MessageByteCount > 0);
        var privateAssistant = detachedTurn.Messages[1];
        Assert.Equal("provider-message", privateAssistant.MessageId);
        Assert.Null(privateAssistant.RawRepresentation);
        Assert.True((privateAssistant.AdditionalProperties ?? throw new InvalidOperationException("the assertion subject is null")).ContainsKey("provider_message"));
        Assert.Equal([
            typeof(TextContent), typeof(TextReasoningContent), typeof(FunctionCallContent), typeof(FunctionResultContent), typeof(UsageContent), typeof(DataContent),
        ], privateAssistant.Contents.Select(static content => content.GetType()));
        var privateReasoning = privateAssistant.Contents.OfType<TextReasoningContent>().Single();
        Assert.Equal("private reasoning", privateReasoning.Text);
        Assert.NotNull(privateReasoning.ProtectedData);
        Assert.Contains("protected-token", JsonSerializer.Serialize(privateReasoning.ProtectedData));
        Assert.Equal("provider-call", privateAssistant.Contents.OfType<FunctionCallContent>().Single().CallId);
        Assert.Equal("provider-call", privateAssistant.Contents.OfType<FunctionResultContent>().Single().CallId);
        Assert.Equal(3, privateAssistant.Contents.OfType<UsageContent>().Single().Details.ReasoningTokenCount);
        Assert.Equal(Encoding.UTF8.GetBytes("{\"value\":1}"), privateAssistant.Contents.OfType<DataContent>().Single().Data.ToArray());
        Assert.IsType<CitationAnnotation>(Assert.Single(privateAssistant.Contents.OfType<TextContent>().Single().Annotations ?? throw new InvalidOperationException("the assertion subject is null")));

        var publicTranscript = AgentTranscriptCodec.CreatePublicTranscript(state);
        var publicAssistant = publicTranscript.Turns.Single().Messages[1];
        Assert.DoesNotContain(publicAssistant.Contents, static content => content is TextReasoningContent);
        Assert.Null(publicAssistant.RawRepresentation);
        Assert.Null(publicAssistant.AdditionalProperties);
        Assert.All(publicAssistant.Contents, static content => Assert.True(content.RawRepresentation == null && content.AdditionalProperties == null));
        Assert.Null(Assert.Single(publicAssistant.Contents.OfType<TextContent>().Single().Annotations ?? throw new InvalidOperationException("the assertion subject is null")).AdditionalProperties);
    }

    [Fact]
    public void TruncatedTurnsRoundTripPrivateAndPublicStates()
    {
        var runId = AgentRunId.Create();
        var sessionId = AgentSessionId.Create();
        ChatMessage[] messages =
        [
            new(ChatRole.User, "question"),
            new(ChatRole.Assistant, "answer")
        ];

        var truncated = AgentTranscriptCodec.DetachPrivateTurn(runId, null, messages, true);
        Assert.True(truncated.Truncated);
        var publicTruncated = AgentTranscriptCodec.CreatePublicTranscript(
            new AgentTranscriptState(sessionId, 1, [truncated]));
        Assert.True(publicTruncated.Turns.Single().Truncated);
        Assert.Equal(["question", "answer"], publicTruncated.Turns.Single().Messages.Select(static message => message.Text));

        var complete = AgentTranscriptCodec.DetachPrivateTurn(runId, null, messages);
        Assert.False(complete.Truncated);
        Assert.False(AgentTranscriptCodec.CreatePublicTranscript(
            new AgentTranscriptState(sessionId, 2, [complete])).Turns.Single().Truncated);
    }

    [Fact]
    public void CustomContentProducesTypedCompatibilityFailure()
    {
        var failure = Assert.ThrowsAny<AgentContentCompatibilityException>(() => AgentTranscriptCodec.DetachPrivateTurn(
            AgentRunId.Create(),
            null,
            [
                new ChatMessage(ChatRole.User, "question"),
                new ChatMessage(ChatRole.Assistant, [new TextContent("answer"), new CustomContent()])
            ]));

        Assert.Contains(nameof(CustomContent), failure.ContentType);
        Assert.IsAssignableFrom<Exception>(failure.InnerException);
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

        Assert.True(nonAscii.MessageByteCount > ascii.MessageByteCount);
    }

    [Fact]
    public void DetachingTurnRejectsInlineBinaryContentAndBinaryMetadata()
    {
        var nested = new ImageGenerationToolResultContent("image-call")
        {
            Outputs = [new DataContent(new byte[] { 4, 5, 6 }, "image/png")]
        };
        var nestedResult = new FunctionResultContent(
            "result-call",
            new Dictionary<string, object?>
            {
                ["nested"] = new object?[] { "text", new byte[] { 6, 7, 8 } }
            });
        var metadata = new TextContent("answer")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["payload"] = new byte[] { 7, 8, 9 }
            }
        };
        var binaryDataMetadata = new TextContent("answer")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["payload"] = CreateBinaryDataLikeValue()
            }
        };

        Assert.ThrowsAny<AgentUnsupportedResponseException>(() => DetachAssistant(
            new TextContent("answer"),
            new DataContent(new byte[] { 1, 2, 3 }, "image/png")));
        Assert.ThrowsAny<AgentUnsupportedResponseException>(() => DetachAssistant(new TextContent("answer"), nested));
        Assert.ThrowsAny<AgentUnsupportedResponseException>(() => DetachAssistant(
            new TextContent("answer"),
            nestedResult));
        Assert.ThrowsAny<AgentUnsupportedResponseException>(() => DetachAssistant(
            new TextContent("answer"),
            new DataContent(Encoding.UTF8.GetBytes("not-json"), "application/json")));
        Assert.ThrowsAny<AgentUnsupportedResponseException>(() => DetachAssistant(metadata));
        Assert.ThrowsAny<AgentUnsupportedResponseException>(() => DetachAssistant(binaryDataMetadata));
    }

    [Fact]
    public void ProtectedReasoningDataIsThePrivateBinaryException()
    {
        var detached = DetachAssistant(
            new TextReasoningContent("private") { ProtectedData = "AQID" },
            new TextContent("answer"));

        var reasoning = detached.Messages.Single().Contents.OfType<TextReasoningContent>().Single();
        Assert.Contains("AQID", JsonSerializer.Serialize(reasoning.ProtectedData));
        Assert.DoesNotContain(AgentTranscriptCodec.CreatePublicMessage(detached.Messages.Single()).Contents, static content => content is TextReasoningContent);
    }

    [Fact]
    public void CachedPublicProjectionMatchesPerContentProjectionForPrivatePayloads()
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
                    RawRepresentation = "raw-annotation",
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["provider_annotation"] = ParseJson("1")
                    }
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
        var usage = new UsageContent(new UsageDetails { InputTokenCount = 4, OutputTokenCount = 7 });
        var turn = AgentTranscriptCodec.DetachPrivateTurn(
            AgentRunId.Create(),
            null,
            [
                new ChatMessage(ChatRole.User, "question"),
                new ChatMessage(ChatRole.Assistant, [text, reasoning, call, result, usage])
            ]);

        // The committed-turn cache must produce exactly the projection the naive per-content
        // path produces, including reasoning removal and private-payload sanitization.
        var cached = AgentTranscriptCodec.CreatePublicTranscript(
            new AgentTranscriptState(AgentSessionId.Create(), 1, [turn]));
        var naive = AgentTranscriptCodec.CreatePublicMessages(turn.Messages);

        var item = Assert.Single(cached.Turns);
        Assert.Equivalent(naive, item.Messages, strict: true);
        Assert.DoesNotContain(cached.Turns[0].Messages, static message =>
            message.Contents.OfType<TextReasoningContent>().Any());
        Assert.All(cached.Turns[0].Messages.SelectMany(static message => message.Contents), static content => Assert.True(content.RawRepresentation == null && content.AdditionalProperties == null));
        var publicText = cached.Turns[0].Messages[1].Contents.OfType<TextContent>().Single();
        Assert.Null(Assert.Single(publicText.Annotations ?? throw new InvalidOperationException("the assertion subject is null")).AdditionalProperties);
        Assert.Null(Assert.Single(publicText.Annotations).RawRepresentation);

        // Re-projecting the cached state after mutating the first handout stays pristine.
        cached.Turns[0].Messages[1].Contents.OfType<TextContent>().Single().Text = "mutated";
        var second = AgentTranscriptCodec.CreatePublicTranscript(
            new AgentTranscriptState(AgentSessionId.Create(), 1, [turn]));
        Assert.Equivalent(naive, second.Turns[0].Messages, strict: true);
    }

    private static AgentTranscriptStateTurn DetachAssistant(params AIContent[] contents)
    {
        return AgentTranscriptCodec.DetachPrivateTurn(
            AgentRunId.Create(),
            null,
            [new ChatMessage(ChatRole.Assistant, contents)]);
    }

    private static object CreateBinaryDataLikeValue()
    {
        var name = $"BinaryDataStub-{Guid.NewGuid():N}";
        var assemblyName = new AssemblyName(name);
        var assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule(name).DefineType("System.BinaryData").CreateType();
        return Activator.CreateInstance(type) ?? throw new NullReferenceException("Cannot create object from");
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
    [Fact(Timeout = 30_000)]
    public async Task ReasoningAndProtectedDataStayPrivateAndReplayToTheNextTurn()
    {
        var reasoning = new TextReasoningContent("private chain") { ProtectedData = "opaque-state" };
        var client = new RecordingClient(
            (_, _) => StreamUpdatesAsync(
                new ChatResponseUpdate(ChatRole.Assistant, [reasoning]) { MessageId = "response" },
                new ChatResponseUpdate(ChatRole.Assistant, "visible answer") { MessageId = "response" }),
            (_, _) => StreamAsync("next answer"));
        var session = new AgentSession(client);

        var first = await CompleteTurnAsync(session, "question", TestContext.Current.CancellationToken);

        Assert.Equal(["visible answer"], first.Events.OfType<AgentTextDelta>().Select(static delta => delta.Text));
        Assert.DoesNotContain(first.Events.OfType<AgentMessageCompleted>().Single().Message.Contents, static content => content is TextReasoningContent);
        Assert.DoesNotContain(first.Result.AssistantMessage.Contents, static content => content is TextReasoningContent);
        Assert.DoesNotContain(first.Result.Transcript.Turns.Single().Messages[1].Contents, static content => content is TextReasoningContent);

        await CompleteTurnAsync(session, "continue", TestContext.Current.CancellationToken);

        var replayedAssistant = client.Requests[1].Single(static message => message.Role == ChatRole.Assistant);
        var replayedReasoning = replayedAssistant.Contents.OfType<TextReasoningContent>().Single();
        Assert.Equal("private chain", replayedReasoning.Text);
        Assert.Contains("opaque-state", JsonSerializer.Serialize(replayedReasoning.ProtectedData));
    }

    [Fact(Timeout = 30_000)]
    public async Task ReasoningOnlyAndCustomContentRollBackTranscriptVersion()
    {
        var reasoningOnly = new AgentSession(new RecordingClient((_, _) => StreamUpdatesAsync(new ChatResponseUpdate(
            ChatRole.Assistant,
            [new TextReasoningContent("private only")]))));

        await using (var run = await reasoningOnly.StartTurnAsync(AgentTurn.FromText("question"), TestContext.Current.CancellationToken))
        {
            await ReadEventsAsync(run, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<AgentUnsupportedResponseException>(async () => { await run.Completion.WaitAsync(TestContext.Current.CancellationToken); });
        }

        Assert.Equal(0, reasoningOnly.GetTranscriptSnapshot().Version);
        Assert.Empty(reasoningOnly.GetTranscriptSnapshot().Turns);

        var custom = new AgentSession(new RecordingClient((_, _) => StreamUpdatesAsync(new ChatResponseUpdate(
            ChatRole.Assistant,
            [new TextContent("visible"), new CustomContent()]))));
        await using (var run = await custom.StartTurnAsync(AgentTurn.FromText("question"), TestContext.Current.CancellationToken))
        {
            await ReadEventsAsync(run, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<AgentContentCompatibilityException>(async () => { await run.Completion.WaitAsync(TestContext.Current.CancellationToken); });
        }

        Assert.Equal(0, custom.GetTranscriptSnapshot().Version);
        Assert.Empty(custom.GetTranscriptSnapshot().Turns);
    }

    [Fact(Timeout = 30_000)]
    public async Task InlineBinaryResponseRollsBackAndSessionRemainsUsable()
    {
        var binary = new ChatResponseUpdate(
            ChatRole.Assistant,
            [new TextContent("visible"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
        var client = new RecordingClient(
            (_, _) => StreamUpdatesAsync(binary),
            (_, _) => StreamAsync("recovered"));
        var session = new AgentSession(client);

        await using (var run = await session.StartTurnAsync(AgentTurn.FromText("image"), TestContext.Current.CancellationToken))
        {
            var events = await ReadEventsAsync(run, TestContext.Current.CancellationToken);
            Assert.Equal(["visible"], events.OfType<AgentTextDelta>().Select(static delta => delta.Text));
            Assert.DoesNotContain(events, static agentEvent => agentEvent is AgentMessageCompleted);
            await Assert.ThrowsAnyAsync<AgentUnsupportedResponseException>(async () => { await run.Completion.WaitAsync(TestContext.Current.CancellationToken); });
        }

        var rolledBack = session.GetTranscriptSnapshot();
        Assert.Equal(0, rolledBack.Version);
        Assert.Empty(rolledBack.Turns);

        var recovered = await CompleteTurnAsync(session, "retry", TestContext.Current.CancellationToken);
        Assert.Equal("recovered", recovered.Result.AssistantMessage.Text);
        Assert.Equal(1, recovered.Result.Transcript.Version);
        Assert.Single(recovered.Result.Transcript.Turns);
    }

    [Fact(Timeout = 30_000)]
    public async Task MutatingPublicMessagesCannotChangeCanonicalReplay()
    {
        var client = new RecordingClient(
            (_, _) => StreamAsync("first answer"),
            (_, _) => StreamAsync("second answer"));
        var session = new AgentSession(client);

        var first = await CompleteTurnAsync(session, "first question", TestContext.Current.CancellationToken);
        first.Result.UserMessage.Contents.Clear();
        ((TextContent)first.Result.AssistantMessage.Contents.Single()).Text = "mutated result";
        ((TextContent)first.Result.Transcript.Turns.Single().Messages[1].Contents.Single()).Text = "mutated snapshot";

        await CompleteTurnAsync(session, "second question", TestContext.Current.CancellationToken);

        Assert.Equal(new (ChatRole Role, string Text)[] { (ChatRole.User, "first question"), (ChatRole.Assistant, "first answer"), (ChatRole.User, "second question") }, client.Requests[1].Select(static message => (message.Role, message.Text)));
        Assert.Equal(["first question", "first answer"], session.GetTranscriptSnapshot().Turns[0].Messages.Select(static message => message.Text));
    }

    [Fact(Timeout = 30_000)]
    public async Task ProviderCannotMutateCanonicalReplayMessages()
    {
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

        await CompleteTurnAsync(session, "first question", TestContext.Current.CancellationToken);
        await CompleteTurnAsync(session, "second question", TestContext.Current.CancellationToken);
        await CompleteTurnAsync(session, "third question", TestContext.Current.CancellationToken);

        Assert.Equal("first answer", client.Requests[2].First(static message => message.Role == ChatRole.Assistant).Text);
        Assert.Equal("first answer", session.GetTranscriptSnapshot().Turns[0].Messages[1].Text);
    }

    [Fact(Timeout = 30_000)]
    public async Task HistoryByteLimitEvictsCompleteOldestTurnForNonAsciiMessages()
    {
        var sample = AgentTranscriptCodec.DetachPrivateTurn(
            AgentRunId.Create(),
            null,
            [new ChatMessage(ChatRole.User, "问题"), new ChatMessage(ChatRole.Assistant, "答案")]);
        var oneTurnBytes = sample.MessageByteCount;
        var session = new AgentSession(
            new RecordingClient((_, _) => StreamAsync("答案"), (_, _) => StreamAsync("答案")),
            new AgentSessionOptions { MaxHistoryBytes = checked(oneTurnBytes * 2 - 1) });

        await CompleteTurnAsync(session, "问题", TestContext.Current.CancellationToken);
        await CompleteTurnAsync(session, "问题", TestContext.Current.CancellationToken);

        var transcript = session.GetTranscriptSnapshot();
        Assert.Equal(2, transcript.Version);
        var item = Assert.Single(transcript.Turns);
        Assert.Equal(["问题", "答案"], item.Messages.Select(static message => message.Text));
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
        await foreach (var agentEvent in run.Events.WithCancellation(cancellationToken)) events.Add(agentEvent);

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
        foreach (var update in updates) yield return update;
    }

    private sealed class RecordingClient(
        params Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>[] responses)
        : IChatClient
    {
        private readonly Queue<Func<IReadOnlyList<ChatMessage>, CancellationToken,
            IAsyncEnumerable<ChatResponseUpdate>>> _responses = new(responses);

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

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
            var request = messages.Select(static message => message.Clone()).ToArray();
            Requests.Add(request);
            return _responses.Dequeue()(request, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class CustomContent : AIContent;
}
