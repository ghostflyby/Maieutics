using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

/// <summary>Runs a provider-neutral Agent conversation over Microsoft.Extensions.AI.</summary>
public sealed class AgentSession : IAgentSession
{
    private readonly IAgentRunProfileProvider profileProvider;
    private readonly Lock transcriptGate = new();
    private readonly ImmutableArray<AIFunction> fixedTools;
    private AgentTranscriptState canonicalState;
    private int runInProgress;

    /// <summary>Initializes an Agent session.</summary>
    public AgentSession(
        IChatClient chatClient,
        AgentSessionOptions? options = null,
        IEnumerable<AIFunction>? tools = null)
        : this(
            new FixedAgentRunProfileProvider(
                new AgentRunProfile(
                    chatClient ?? throw new ArgumentNullException(nameof(chatClient)),
                    options ?? new AgentSessionOptions(),
                    tools: CreateToolRegistry(tools).Values)))
    {
    }

    /// <summary>Initializes an Agent session whose profile is captured independently for each run.</summary>
    /// <param name="profileProvider">The provider used to acquire each run's model client and options.</param>
    /// <param name="tools">The immutable set of tools available to the session.</param>
    public AgentSession(
        IAgentRunProfileProvider profileProvider,
        IEnumerable<AIFunction>? tools = null)
    {
        this.profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        fixedTools = CreateToolRegistry(tools).Values.ToImmutableArray();

        Id = AgentSessionId.Create();
        canonicalState = AgentTranscriptCodec.CreateInitialState(Id);
    }

    /// <inheritdoc />
    public AgentSessionId Id { get; }

    /// <inheritdoc />
    public async Task<IAgentRun> StartTurnAsync(
        AgentTurn turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref runInProgress, 1, 0) != 0)
        {
            throw new AgentTurnInProgressException();
        }

        IAgentRunProfileLease? profileLease = null;
        try
        {
            profileLease = profileProvider.Acquire() ??
                           throw new InvalidOperationException("The Agent run profile provider returned a null lease.");
            var profile = profileLease.Profile ??
                          throw new InvalidOperationException("The Agent run profile lease returned a null profile.");
            profile.Options.Validate();
            var tools = CreateToolRegistry(profile.Tools.Concat(fixedTools));
            ValidateInput(turn, profile.Options);

            var userMessage = AgentTranscriptCodec.DetachPrivateMessage(
                new ChatMessage(ChatRole.User, turn.Contents.ToList()));
            var run = new AgentRun(
                this,
                AgentRunId.Create(),
                userMessage,
                profileLease,
                profile,
                tools,
                profile.Options.EventBufferCapacity);
            run.Start();
            return run;
        }
        catch
        {
            Volatile.Write(ref runInProgress, 0);
            if (profileLease is not null)
            {
                await profileLease.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public AgentTranscript GetTranscriptSnapshot()
    {
        AgentTranscriptState snapshot;
        lock (transcriptGate)
        {
            snapshot = canonicalState;
        }

        return AgentTranscriptCodec.CreatePublicTranscript(snapshot);
    }

    private async Task<PreparedRunResult> ExecuteRunAsync(AgentRun run, CancellationToken cancellationToken)
    {
        var profile = run.Profile;
        var options = profile.Options;
        ValidateModelCapabilities(profile, run.Tools.Count);
        var recordingClient = new RecordingChatClient(profile.ChatClient);
        var toolState = new RunToolState(this, run, recordingClient, options);
        recordingClient.SetUpdateObserver(toolState.ObserveProviderUpdateAsync);
        using var functionClient = new FunctionInvokingChatClient(recordingClient)
        {
            AllowConcurrentInvocation = false,
            MaximumIterationsPerRequest = options.MaxModelIterationsPerTurn,
            MaximumConsecutiveErrorsPerRequest = 0,
            IncludeDetailedErrors = false,
            TerminateOnUnknownCalls = true,
            FunctionInvoker = toolState.InvokeAsync
        };
        using var budget = options.MaxTurnDuration > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (budget is not null)
        {
            budget.CancelAfter(options.MaxTurnDuration);
        }

        var loopToken = budget?.Token ?? cancellationToken;
        var requestMessages = GetCommittedChatMessages().ToList();
        requestMessages.Add(run.UserMessage);
        var chatOptions = new ChatOptions
        {
            Instructions = options.SystemPrompt,
            Tools = run.Tools.Values.Cast<AITool>().ToList(),
            AllowMultipleToolCalls = true
        };
        try
        {
            await foreach (var _ in functionClient
                               .GetStreamingResponseAsync(requestMessages, chatOptions, loopToken)
                               .ConfigureAwait(false))
            {
            }
        }
        catch (AgentModelIterationLimitExceededException)
        {
            // The model exhausted its per-turn iteration budget while requesting tools. The loop
            // stopped at a clean boundary (all completed tool rounds are recorded), so the run
            // commits partial progress below instead of failing the whole turn.
        }
        catch (OperationCanceledException) when (
            budget?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            throw new AgentTurnDurationExceededException(options.MaxTurnDuration);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AgentProviderException(exception);
        }

        var (turnMessages, truncated) = await toolState.BuildCompletedMessagesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (truncated)
        {
            await run.WriteEventAsync(
                new AgentTurnTruncated(run.Id, run.NextSequence()),
                cancellationToken).ConfigureAwait(false);
        }

        return new PreparedRunResult(turnMessages, turnMessages[^1], truncated);
    }

    private AgentTranscript CommitTurn(
        AgentRunId runId,
        IReadOnlyList<ChatMessage> turnMessages,
        AgentSessionOptions options,
        AgentModelIdentity? modelIdentity,
        bool truncated)
    {
        var detachedTurn = AgentTranscriptCodec.DetachPrivateTurn(runId, modelIdentity, turnMessages, truncated);
        AgentTranscriptState committed;
        lock (transcriptGate)
        {
            var builder = canonicalState.Turns.ToBuilder();
            builder.Add(detachedTurn);

            while (builder.Count > options.MaxRetainedTurns ||
                   CountHistoryBytes(builder) > options.MaxHistoryBytes)
            {
                builder.RemoveAt(0);
            }

            committed = new AgentTranscriptState(
                Id,
                checked(canonicalState.Version + 1),
                builder.ToImmutable());
            canonicalState = committed;
        }

        return AgentTranscriptCodec.CreatePublicTranscript(committed);
    }

    private AgentRunResult CommitRun(AgentRun run, PreparedRunResult prepared)
    {
        var modelIdentity = run.Profile.ModelIdentity;
        var userMessage = AgentTranscriptCodec.CreatePublicMessage(run.UserMessage);
        var assistantMessage = AgentTranscriptCodec.CreatePublicMessage(prepared.AssistantMessage);
        var transcript = CommitTurn(
            run.Id,
            prepared.TurnMessages,
            run.Profile.Options,
            modelIdentity,
            prepared.Truncated);
        return new AgentRunResult(
            run.Id,
            userMessage,
            assistantMessage,
            transcript,
            modelIdentity,
            prepared.Truncated);
    }

    private IReadOnlyList<ChatMessage> GetCommittedChatMessages()
    {
        AgentTranscriptState snapshot;
        lock (transcriptGate)
        {
            snapshot = canonicalState;
        }

        var messages = new List<ChatMessage>();
        foreach (var turn in snapshot.Turns)
        {
            messages.AddRange(turn.Truncated ? TrimTruncatedTurn(turn.Messages) : turn.Messages);
        }

        return AgentTranscriptCodec.DetachPrivateMessages(
            messages);
    }

    // A truncated turn ends with assistant tool calls that were never answered. Replaying them
    // would push stale requests into the next provider turn, so replay trims the unanswered tail.
    private static IReadOnlyList<ChatMessage> TrimTruncatedTurn(IReadOnlyList<ChatMessage> messages)
    {
        var trimmed = messages.ToList();
        while (trimmed.Count > 0 &&
               trimmed[^1].Role == ChatRole.Assistant &&
               trimmed[^1].Contents.Count > 0 &&
               trimmed[^1].Contents.All(static content => content is FunctionCallContent))
        {
            trimmed.RemoveAt(trimmed.Count - 1);
        }

        if (trimmed.Count > 0 &&
            trimmed[^1].Role == ChatRole.Assistant &&
            trimmed[^1].Contents.OfType<FunctionCallContent>().Any())
        {
            var last = trimmed[^1];
            trimmed[^1] = new ChatMessage(
                last.Role,
                last.Contents.Where(static content => content is not FunctionCallContent).ToArray())
            {
                AuthorName = last.AuthorName,
                CreatedAt = last.CreatedAt,
                MessageId = last.MessageId
            };
        }

        return trimmed;
    }

    private static void ValidateInput(AgentTurn turn, AgentSessionOptions options)
    {
        var characters = 0;
        foreach (var content in turn.Contents)
        {
            if (content is null)
            {
                throw new ArgumentException("Agent input contents cannot contain null items.", nameof(turn));
            }

            if (content is not TextContent text)
            {
                throw new AgentUnsupportedResponseException(
                    $"Agent input content of type '{content.GetType().Name}' is not supported.");
            }

            characters = checked(characters + text.Text.Length);
        }

        if (characters > options.MaxInputCharacters)
        {
            throw new AgentInputLimitExceededException(characters, options.MaxInputCharacters);
        }
    }

    private static void ValidateModelCapabilities(AgentRunProfile profile, int toolCount)
    {
        if ((profile.Capabilities & AgentModelCapabilities.StreamingText) == 0)
        {
            throw new AgentModelCapabilityException(
                AgentModelCapabilities.StreamingText,
                profile.ModelIdentity);
        }

        if (toolCount > 0 && (profile.Capabilities & AgentModelCapabilities.FunctionCalling) == 0)
        {
            throw new AgentModelCapabilityException(
                AgentModelCapabilities.FunctionCalling,
                profile.ModelIdentity);
        }
    }

    private static int CountHistoryBytes(IEnumerable<AgentTranscriptStateTurn> source)
    {
        var count = 0;
        foreach (var turn in source)
        {
            count = checked(count + turn.MessageByteCount);
        }

        return count;
    }

    private static ImmutableDictionary<string, AIFunction> CreateToolRegistry(IEnumerable<AIFunction>? source)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, AIFunction>(StringComparer.Ordinal);
        foreach (var function in source ?? [])
        {
            ArgumentNullException.ThrowIfNull(function);
            if (!IsValidToolName(function.Name))
            {
                throw new ArgumentException(
                    "Tool names must contain 1 to 64 ASCII letters, digits, underscores, or hyphens.",
                    nameof(source));
            }

            if (function.JsonSchema.ValueKind != JsonValueKind.Object ||
                !function.JsonSchema.TryGetProperty("type", out var schemaType) ||
                schemaType.ValueKind != JsonValueKind.String ||
                !string.Equals(schemaType.GetString(), "object", StringComparison.Ordinal))
            {
                throw new ArgumentException("A tool input schema must describe a JSON object.", nameof(source));
            }

            if (!builder.TryAdd(function.Name, function))
            {
                throw new ArgumentException($"An Agent tool named '{function.Name}' is already registered.",
                    nameof(source));
            }
        }

        return builder.ToImmutable();
    }

    private static bool IsValidToolName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64)
        {
            return false;
        }

        foreach (var character in name)
        {
            if (character is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and
                not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private void ReleaseRun() => Volatile.Write(ref runInProgress, 0);

    private sealed class FixedAgentRunProfileProvider(AgentRunProfile profile) : IAgentRunProfileProvider
    {
        public IAgentRunProfileLease Acquire() => new FixedAgentRunProfileLease(profile);
    }

    private sealed class FixedAgentRunProfileLease(AgentRunProfile profile) : IAgentRunProfileLease
    {
        public AgentRunProfile Profile { get; } = profile;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RunToolState(
        AgentSession owner,
        AgentRun run,
        RecordingChatClient recordingClient,
        AgentSessionOptions options)
    {
        private readonly Dictionary<string, ToolInvocationRecord> calls = new(StringComparer.Ordinal);
        private readonly Dictionary<(int Iteration, string MessageId), AgentMessageId> messageIds = [];
        private readonly HashSet<int> publishedIterations = [];
        private readonly List<ChatMessage> intermediateMessages = [];
        private int responseCharacters;
        private int toolCallCount;

        internal async ValueTask<object?> InvokeAsync(
            FunctionInvocationContext invocation,
            CancellationToken cancellationToken)
        {
            if (!run.Tools.TryGetValue(invocation.Function.Name, out var function) ||
                !ReferenceEquals(function, invocation.Function))
            {
                throw new AgentToolArgumentsException("The model requested an unregistered Agent tool.");
            }

            var recordedIteration = checked(invocation.Iteration + 1);
            if (recordedIteration >= options.MaxModelIterationsPerTurn)
            {
                throw new AgentModelIterationLimitExceededException(
                    options.MaxModelIterationsPerTurn);
            }

            EnsureIterationCalls(recordedIteration);
            await PublishIterationMessagesAsync(recordedIteration, cancellationToken).ConfigureAwait(false);
            var callContent = invocation.CallContent;
            if (callContent.Exception is not null)
            {
                throw new AgentToolArgumentsException(
                    $"The model supplied malformed arguments for tool '{function.Name}'.",
                    callContent.Exception);
            }

            if (!calls.TryGetValue(callContent.CallId, out var record) ||
                !ReferenceEquals(record.Function, function))
            {
                throw new AgentToolArgumentsException(
                    "The function invoker could not correlate the requested Agent tool.");
            }

            await run.WriteEventAsync(
                new AgentToolStarted(
                    run.Id,
                    run.NextSequence(),
                    record.CallId,
                    function.Name,
                    record.Arguments),
                cancellationToken).ConfigureAwait(false);

            var progressCount = 0;
            var context = new AgentToolContext(
                owner.Id,
                run.Id,
                record.CallId,
                async (content, token) =>
                {
                    if (Interlocked.Increment(ref progressCount) > options.MaxToolProgressEventsPerCall)
                    {
                        throw new AgentToolLimitExceededException(
                            nameof(AgentSessionOptions.MaxToolProgressEventsPerCall),
                            options.MaxToolProgressEventsPerCall);
                    }

                    await run.WriteEventAsync(
                        new AgentToolProgress(
                            run.Id,
                            run.NextSequence(),
                            record.CallId,
                            AgentTranscriptCodec.CreatePublicContent(content)),
                        token).ConfigureAwait(false);
                });

            invocation.Arguments.Context ??= new Dictionary<object, object?>();
            invocation.Arguments.Context[typeof(AgentToolContext)] = context;

            JsonElement envelope;
            try
            {
                var result = await function.InvokeAsync(invocation.Arguments, cancellationToken).ConfigureAwait(false);
                envelope = result switch
                {
                    null => ToolJson.CreateSuccessEnvelope(null),
                    JsonElement element => ToolJson.CreateSuccessEnvelope(element),
                    _ => throw new InvalidOperationException(
                        $"AI function results must be JsonElement or null, but '{result.GetType().Name}' was returned.")
                };
            }
            catch (AgentToolException exception)
            {
                envelope = ToolJson.CreateFailureEnvelope(exception.Code, exception.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AgentException)
            {
                await PublishTerminalFailureAsync(record.CallId, cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                await PublishTerminalFailureAsync(record.CallId, cancellationToken).ConfigureAwait(false);
                throw new AgentToolInvocationException(function.Name, exception);
            }

            if (Encoding.UTF8.GetByteCount(envelope.GetRawText()) > options.MaxToolResultBytes)
            {
                await PublishTerminalFailureAsync(record.CallId, cancellationToken).ConfigureAwait(false);
                throw new AgentToolLimitExceededException(
                    nameof(AgentSessionOptions.MaxToolResultBytes),
                    options.MaxToolResultBytes);
            }

            intermediateMessages.Add(new ChatMessage(
                ChatRole.Tool,
                [
                    new FunctionResultContent(record.ProviderCallId, envelope)
                ]));
            await run.WriteEventAsync(
                new AgentToolFinished(run.Id, run.NextSequence(), record.CallId, envelope),
                cancellationToken).ConfigureAwait(false);

            return envelope;
        }

        private ValueTask PublishTerminalFailureAsync(
            AgentToolCallId callId,
            CancellationToken cancellationToken) =>
            run.WriteEventAsync(
                new AgentToolFinished(
                    run.Id,
                    run.NextSequence(),
                    callId,
                    ToolJson.CreateFailureEnvelope(
                        "tool_execution_failed",
                        "The tool failed unexpectedly.")),
                cancellationToken);

        internal async ValueTask ObserveProviderUpdateAsync(
            int iteration,
            ChatResponseUpdate update,
            CancellationToken cancellationToken)
        {
            var text = update.Text;
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            responseCharacters = checked(responseCharacters + text.Length);
            if (responseCharacters > options.MaxResponseCharacters)
            {
                throw new AgentResponseLimitExceededException(options.MaxResponseCharacters);
            }

            await run.WriteEventAsync(
                new AgentTextDelta(
                    run.Id,
                    run.NextSequence(),
                    GetMessageId(iteration, update.MessageId),
                    text),
                cancellationToken).ConfigureAwait(false);
        }

        internal async Task<(IReadOnlyList<ChatMessage> Messages, bool Truncated)> BuildCompletedMessagesAsync(
            CancellationToken cancellationToken)
        {
            var iterations = recordingClient.GetIterations();
            if (iterations.Count == 0)
            {
                throw new AgentUnsupportedResponseException("The model provider returned no response.");
            }

            for (var index = 1; index < iterations.Count; index++)
            {
                await PublishIterationMessagesAsync(index, cancellationToken).ConfigureAwait(false);
            }

            await PublishIterationMessagesAsync(iterations.Count, cancellationToken).ConfigureAwait(false);
            List<ChatMessage> messages =
            [
                run.UserMessage,
                .. intermediateMessages
            ];
            if (messages[^1].Role != ChatRole.Assistant)
            {
                throw new AgentUnsupportedResponseException("The model provider returned no final assistant text.");
            }

            var atIterationLimit = iterations.Count >= options.MaxModelIterationsPerTurn;
            var lastRequestsTools = messages[^1].Contents.OfType<FunctionCallContent>().Any();
            if (atIterationLimit && lastRequestsTools)
            {
                return (messages, true);
            }

            if (messages[^1].Contents.OfType<TextContent>().Any(static content => content.Text.Length > 0))
            {
                return (messages, false);
            }

            if (lastRequestsTools)
            {
                throw new AgentModelIterationLimitExceededException(options.MaxModelIterationsPerTurn);
            }

            throw new AgentUnsupportedResponseException("The model provider returned no final assistant text.");
        }

        private async Task PublishIterationMessagesAsync(int iteration, CancellationToken cancellationToken)
        {
            if (!publishedIterations.Add(iteration))
            {
                return;
            }

            var recorded = recordingClient.GetIteration(iteration);
            if (recorded.ResponseMessages
                .SelectMany(static message => message.Contents)
                .OfType<FunctionCallContent>()
                .Any())
            {
                EnsureIterationCalls(iteration);
            }

            foreach (var responseMessage in recorded.ResponseMessages)
            {
                if (responseMessage.Role != ChatRole.Assistant)
                {
                    throw new AgentUnsupportedResponseException(
                        $"The model provider returned an unsupported '{responseMessage.Role}' response message.");
                }

                var assistant = responseMessage.Clone();
                intermediateMessages.Add(assistant);
                var messageId = GetMessageId(iteration, responseMessage.MessageId);
                await run.WriteEventAsync(
                    new AgentMessageCompleted(
                        run.Id,
                        run.NextSequence(),
                        messageId,
                        AgentTranscriptCodec.CreatePublicMessage(assistant)),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private AgentMessageId GetMessageId(int iteration, string? providerMessageId)
        {
            var key = (iteration, providerMessageId ?? string.Empty);
            if (messageIds.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var created = AgentMessageId.Create();
            messageIds.Add(key, created);
            return created;
        }

        private void EnsureIterationCalls(int iteration)
        {
            var recorded = recordingClient.GetIteration(iteration);
            foreach (var call in recorded.ResponseMessages
                         .SelectMany(static message => message.Contents)
                         .OfType<FunctionCallContent>())
            {
                if (calls.ContainsKey(call.CallId))
                {
                    continue;
                }

                if (!run.Tools.TryGetValue(call.Name, out var function))
                {
                    throw new AgentToolArgumentsException(
                        $"The model requested unregistered tool '{call.Name}'.");
                }

                if (call.Exception is not null)
                {
                    throw new AgentToolArgumentsException(
                        $"The model supplied malformed arguments for tool '{call.Name}'.",
                        call.Exception);
                }

                if (Interlocked.Increment(ref toolCallCount) > options.MaxToolCallsPerTurn)
                {
                    throw new AgentToolLimitExceededException(
                        nameof(AgentSessionOptions.MaxToolCallsPerTurn),
                        options.MaxToolCallsPerTurn);
                }

                var arguments = ToolJson.CreateArguments(call.Arguments ?? new Dictionary<string, object?>());
                if (Encoding.UTF8.GetByteCount(arguments.GetRawText()) > options.MaxToolArgumentsBytes)
                {
                    throw new AgentToolLimitExceededException(
                        nameof(AgentSessionOptions.MaxToolArgumentsBytes),
                        options.MaxToolArgumentsBytes);
                }

                calls.Add(call.CallId, new ToolInvocationRecord(
                    AgentToolCallId.Create(),
                    call.CallId,
                    function,
                    arguments));
            }
        }

        private sealed class ToolInvocationRecord(
            AgentToolCallId callId,
            string providerCallId,
            AIFunction function,
            JsonElement arguments)
        {
            internal AgentToolCallId CallId { get; } = callId;

            internal string ProviderCallId { get; } = providerCallId;

            internal AIFunction Function { get; } = function;

            internal JsonElement Arguments { get; } = arguments;
        }
    }

    private sealed class RecordingChatClient(IChatClient innerClient) : IChatClient
    {
        private readonly Lock gate = new();
        private readonly List<RecordedIteration> iterations = [];
        private Func<int, ChatResponseUpdate, CancellationToken, ValueTask>? updateObserver;
        private int iterationCount;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var request = messages.Select(static message => message.Clone()).ToArray();
            var response = await innerClient
                .GetResponseAsync(request, options, cancellationToken)
                .ConfigureAwait(false);
            ValidateConversationId(response.ConversationId);
            AddIteration(request, response.Messages);
            return response;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            RecordStreamingAsync(
                Interlocked.Increment(ref iterationCount),
                messages.Select(static message => message.Clone()).ToArray(),
                options,
                cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            innerClient.GetService(serviceType, serviceKey);

        public void Dispose()
        {
        }

        internal void SetUpdateObserver(
            Func<int, ChatResponseUpdate, CancellationToken, ValueTask> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            if (Interlocked.CompareExchange(ref updateObserver, observer, null) is not null)
            {
                throw new InvalidOperationException("A recording update observer is already configured.");
            }
        }

        internal IReadOnlyList<RecordedIteration> GetIterations()
        {
            lock (gate)
            {
                return iterations.ToArray();
            }
        }

        internal RecordedIteration GetIteration(int iteration)
        {
            lock (gate)
            {
                if (iteration < 1 || iteration > iterations.Count)
                {
                    throw new AgentUnsupportedResponseException(
                        $"Agent tool invocation referenced unavailable model iteration {iteration}.");
                }

                return iterations[iteration - 1];
            }
        }

        private async IAsyncEnumerable<ChatResponseUpdate> RecordStreamingAsync(
            int iteration,
            IReadOnlyList<ChatMessage> request,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in innerClient
                               .GetStreamingResponseAsync(request, options, cancellationToken)
                               .ConfigureAwait(false))
            {
                ValidateConversationId(update.ConversationId);
                updates.Add(update.Clone());
                var observer = updateObserver;
                if (observer is not null)
                {
                    await observer(iteration, update, cancellationToken).ConfigureAwait(false);
                }

                yield return update;
            }

            AddIteration(request, updates.ToChatResponse().Messages);
        }

        private static void ValidateConversationId(string? conversationId)
        {
            if (!string.IsNullOrEmpty(conversationId))
            {
                throw new AgentUnsupportedResponseException(
                    "The model provider returned conversation state that conflicts with the canonical Agent transcript.");
            }
        }

        private void AddIteration(
            IReadOnlyList<ChatMessage> requestMessages,
            IEnumerable<ChatMessage> responseMessages)
        {
            var iteration = new RecordedIteration(
                requestMessages.Select(static message => message.Clone()).ToArray(),
                responseMessages.Select(static message => message.Clone()).ToArray());
            lock (gate)
            {
                iterations.Add(iteration);
            }
        }

        internal sealed record RecordedIteration(
            IReadOnlyList<ChatMessage> RequestMessages,
            IReadOnlyList<ChatMessage> ResponseMessages);
    }

    private sealed record PreparedRunResult(
        IReadOnlyList<ChatMessage> TurnMessages,
        ChatMessage AssistantMessage,
        bool Truncated);

    private sealed class AgentRun(
        AgentSession owner,
        AgentRunId id,
        ChatMessage userMessage,
        IAgentRunProfileLease profileLease,
        AgentRunProfile profile,
        ImmutableDictionary<string, AIFunction> tools,
        int eventBufferCapacity) : IAgentRun
    {
        private readonly CancellationTokenSource cancellation = new();

        private readonly Channel<AgentEvent> events = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(
            eventBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        private readonly TaskCompletionSource<AgentRunResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Task backgroundTask = Task.CompletedTask;
        private long sequence;
        private int enumerationStarted;
        private int started;
        private int disposed;

        public AgentRunId Id { get; } = id;

        public AgentSessionId SessionId => owner.Id;

        public IAsyncEnumerable<AgentEvent> Events => ReadEventsAsync();

        public Task<AgentRunResult> Completion => completion.Task;

        internal ChatMessage UserMessage { get; } = userMessage;

        internal AgentRunProfile Profile { get; } = profile;

        internal ImmutableDictionary<string, AIFunction> Tools { get; } = tools;

        internal void Start()
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
            {
                throw new InvalidOperationException("The Agent run has already started.");
            }

            backgroundTask = Task.Run(ExecuteAsync);
        }

        internal long NextSequence() => Interlocked.Increment(ref sequence);

        internal ValueTask WriteEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken) =>
            events.Writer.WriteAsync(agentEvent, cancellationToken);

        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await cancellation.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }

            await backgroundTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                await backgroundTask.ConfigureAwait(false);
                return;
            }

            await cancellation.CancelAsync();
            await backgroundTask.ConfigureAwait(false);
            cancellation.Dispose();
        }

        private async Task ExecuteAsync()
        {
            PreparedRunResult? prepared = null;
            AgentRunResult? result = null;
            Exception? failure = null;
            var canceled = false;
            try
            {
                prepared = await owner.ExecuteRunAsync(this, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                canceled = true;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                events.Writer.TryComplete();
                try
                {
                    await profileLease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception) when (failure is not null || canceled)
                {
                    // Preserve the run's primary terminal cause after ownership has been released.
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                if (failure is null && !canceled)
                {
                    try
                    {
                        result = owner.CommitRun(
                            this,
                            prepared ?? throw new InvalidOperationException(
                                "The Agent run completed without a prepared result."));
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                }

                owner.ReleaseRun();
            }

            if (failure is not null)
            {
                completion.TrySetException(failure);
            }
            else if (canceled)
            {
                completion.TrySetCanceled(cancellation.Token);
            }
            else
            {
                completion.TrySetResult(result ?? throw new InvalidOperationException(
                    "The Agent run completed without a result."));
            }
        }

        private async IAsyncEnumerable<AgentEvent> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref enumerationStarted, 1) != 0)
            {
                throw new InvalidOperationException("Agent run events support only one consumer.");
            }

            await foreach (var agentEvent in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return agentEvent;
            }
        }
    }
}

internal static class ToolJson
{
    internal static JsonElement CreateArguments(IDictionary<string, object?> arguments)
    {
        var root = new JsonObject();
        foreach (var (name, value) in arguments)
        {
            root.Add(name, ToJsonNode(value));
        }

        return ParseElement(root);
    }

    internal static JsonElement CreateSuccessEnvelope(JsonElement? value) =>
        JsonSerializer.SerializeToElement(
            new ToolSuccessEnvelope("ok", value?.Clone()),
            AgentToolJsonSerializerContext.Default.ToolSuccessEnvelope);

    internal static JsonElement CreateFailureEnvelope(string code, string message) =>
        JsonSerializer.SerializeToElement(
            new ToolFailureEnvelope("error", code, message),
            AgentToolJsonSerializerContext.Default.ToolFailureEnvelope);

    internal static JsonElement CreateCancelledEnvelope() =>
        JsonSerializer.SerializeToElement(
            new ToolCancelledEnvelope("cancelled"),
            AgentToolJsonSerializerContext.Default.ToolCancelledEnvelope);

    private static JsonElement ParseElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        JsonNode node => node.DeepClone(),
        string text => JsonValue.Create(text),
        bool boolean => JsonValue.Create(boolean),
        byte number => JsonValue.Create(number),
        sbyte number => JsonValue.Create(number),
        short number => JsonValue.Create(number),
        ushort number => JsonValue.Create(number),
        int number => JsonValue.Create(number),
        uint number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        ulong number => JsonValue.Create(number),
        float number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        IDictionary<string, object?> dictionary => ToJsonObject(dictionary),
        IReadOnlyDictionary<string, object?> dictionary => ToJsonObject(dictionary),
        IEnumerable sequence => ToJsonArray(sequence),
        _ => throw new AgentToolArgumentsException(
            $"Tool argument value of type '{value.GetType().Name}' is not supported.")
    };

    private static JsonObject ToJsonObject(IEnumerable<KeyValuePair<string, object?>> values)
    {
        var result = new JsonObject();
        foreach (var (name, value) in values)
        {
            result.Add(name, ToJsonNode(value));
        }

        return result;
    }

    private static JsonArray ToJsonArray(IEnumerable values)
    {
        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add(ToJsonNode(value));
        }

        return result;
    }
}
