using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using FrameworkAgentSession = Microsoft.Agents.AI.AgentSession;

namespace Maieutics.Agent;

/// <summary>Runs a provider-neutral Agent conversation over Microsoft Agent Framework.</summary>
public sealed class AgentSession : IAgentSession
{
    private readonly IAgentRunProfileProvider profileProvider;
    private readonly Lock transcriptGate = new();
    private readonly ImmutableDictionary<string, ToolAIFunction> tools;
    private ImmutableArray<AgentTranscriptTurn> turns = [];
    private long transcriptVersion;
    private int runInProgress;

    /// <summary>Initializes an Agent session.</summary>
    public AgentSession(
        IChatClient chatClient,
        AgentSessionOptions? options = null,
        IEnumerable<IAgentTool>? tools = null)
        : this(
            new FixedAgentRunProfileProvider(
                new AgentRunProfile(
                    chatClient ?? throw new ArgumentNullException(nameof(chatClient)),
                    options ?? new AgentSessionOptions())),
            tools)
    {
    }

    /// <summary>Initializes an Agent session whose profile is captured independently for each run.</summary>
    /// <param name="profileProvider">The provider used to acquire each run's model client and options.</param>
    /// <param name="tools">The immutable set of tools available to the session.</param>
    public AgentSession(
        IAgentRunProfileProvider profileProvider,
        IEnumerable<IAgentTool>? tools = null)
    {
        this.profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        this.tools = CreateToolRegistry(tools);

        Id = AgentSessionId.Create();
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
            ValidateInput(turn, profile.Options);

            var userMessage = new AgentMessage(AgentMessageId.Create(), AgentMessageRole.User, turn.Contents);
            var run = new AgentRun(
                this,
                AgentRunId.Create(),
                userMessage,
                profileLease,
                profile,
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
        lock (transcriptGate)
        {
            return new AgentTranscript(Id, transcriptVersion, turns);
        }
    }

    private async Task<PreparedRunResult> ExecuteRunAsync(AgentRun run, CancellationToken cancellationToken)
    {
        var profile = run.Profile;
        var options = profile.Options;
        ValidateModelCapabilities(profile);
        var historyProvider = new StagingChatHistoryProvider(GetCommittedChatMessages);
        historyProvider.BeginRun(run.Id);
        var recordingClient = new RecordingChatClient(profile.ChatClient);
        var toolState = new RunToolState(this, run, recordingClient, options);
        recordingClient.SetUpdateObserver(toolState.ObserveProviderUpdateAsync);
        var agent = new ChatClientAgent(
            profile.ChatClient,
            new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions { Instructions = options.SystemPrompt },
                ChatHistoryProvider = historyProvider,
                UseProvidedChatClientAsIs = true,
                ClearOnChatHistoryProviderConflict = false,
                WarnOnChatHistoryProviderConflict = true,
                ThrowOnChatHistoryProviderConflict = true
            });
        try
        {
            FrameworkAgentSession session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            var runOptions = CreateRunOptions(recordingClient, toolState, options);
            try
            {
                await foreach (var _ in agent
                                   .RunStreamingAsync(
                                       ToChatMessage(run.UserMessage),
                                       session,
                                       runOptions,
                                       cancellationToken)
                                   .ConfigureAwait(false))
                {
                }
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

            var staged = historyProvider.TakeStaged(run.Id);
            ValidateStagedRequest(staged.RequestMessages, run.UserMessage);
            var turnMessages = await toolState.BuildCompletedMessagesAsync(cancellationToken).ConfigureAwait(false);
            return new PreparedRunResult(turnMessages, turnMessages[^1]);
        }
        finally
        {
            historyProvider.Discard(run.Id);
        }
    }

    private ChatClientAgentRunOptions CreateRunOptions(
        RecordingChatClient recordingClient,
        RunToolState toolState,
        AgentSessionOptions options)
    {
        var chatOptions = new ChatOptions
        {
            Tools = tools.Values.Cast<AITool>().ToList(),
            AllowMultipleToolCalls = true
        };
        return new ChatClientAgentRunOptions(chatOptions)
        {
            ChatClientFactory = _ => new FunctionInvokingChatClient(recordingClient)
            {
                AllowConcurrentInvocation = false,
                MaximumIterationsPerRequest = options.MaxModelIterationsPerTurn,
                MaximumConsecutiveErrorsPerRequest = 0,
                IncludeDetailedErrors = false,
                TerminateOnUnknownCalls = true,
                FunctionInvoker = toolState.InvokeAsync
            }
        };
    }

    private AgentTranscript CommitTurn(
        AgentRunId runId,
        ImmutableArray<AgentMessage> turnMessages,
        AgentSessionOptions options,
        AgentModelIdentity? modelIdentity)
    {
        lock (transcriptGate)
        {
            var builder = turns.ToBuilder();
            builder.Add(new AgentTranscriptTurn(runId, turnMessages, modelIdentity));

            while (builder.Count > options.MaxRetainedTurns ||
                   CountCharacters(builder) > options.MaxHistoryCharacters)
            {
                builder.RemoveAt(0);
            }

            turns = builder.ToImmutable();
            transcriptVersion++;
            return new AgentTranscript(Id, transcriptVersion, turns);
        }
    }

    private AgentRunResult CommitRun(AgentRun run, PreparedRunResult prepared)
    {
        var modelIdentity = run.Profile.ModelIdentity;
        var transcript = CommitTurn(run.Id, prepared.TurnMessages, run.Profile.Options, modelIdentity);
        return new AgentRunResult(
            run.Id,
            run.UserMessage,
            prepared.AssistantMessage,
            transcript,
            modelIdentity);
    }

    private IReadOnlyList<ChatMessage> GetCommittedChatMessages()
    {
        ImmutableArray<AgentTranscriptTurn> snapshot;
        lock (transcriptGate)
        {
            snapshot = turns;
        }

        return snapshot.SelectMany(static turn => turn.Messages).Select(ToChatMessage).ToArray();
    }

    private static ChatMessage ToChatMessage(AgentMessage message)
    {
        var role = message.Role switch
        {
            AgentMessageRole.User => ChatRole.User,
            AgentMessageRole.Assistant => ChatRole.Assistant,
            AgentMessageRole.Tool => ChatRole.Tool,
            _ => throw new ArgumentOutOfRangeException(nameof(message), message.Role, null)
        };
        var contents = message.Contents.Select(ToAIContent).ToList();
        return new ChatMessage(role, contents);
    }

    // ReSharper disable once InconsistentNaming
    private static AIContent ToAIContent(AgentContent content) => content switch
    {
        AgentTextContent text => new TextContent(text.Text),
        AgentToolCallContent call => new FunctionCallContent(
            call.CallId.ToString(),
            call.Name,
            ToArgumentDictionary(call.Arguments)),
        AgentToolResultContent result => new FunctionResultContent(
            result.CallId.ToString(),
            ToolJson.CreateResultEnvelope(result.Outcome)),
        _ => throw new AgentUnsupportedResponseException(
            $"Agent message content of type '{content.GetType().Name}' is not supported by the model adapter.")
    };

    private static Dictionary<string, object?> ToArgumentDictionary(AgentToolArguments arguments)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in arguments.Value.EnumerateObject())
        {
            result.Add(property.Name, property.Value.Clone());
        }

        return result;
    }

    private static void ValidateStagedRequest(
        IReadOnlyList<ChatMessage> requestMessages,
        AgentMessage userMessage)
    {
        if (requestMessages.Count != 1 || requestMessages[0].Role != ChatRole.User)
        {
            throw new AgentUnsupportedResponseException(
                "Agent Framework staged an unexpected request message set.");
        }

        var expectedText = string.Concat(
            userMessage.Contents.OfType<AgentTextContent>().Select(static content => content.Text));
        var requestMessage = requestMessages[0];
        if (requestMessage.Contents.Any(static content => content is not TextContent) ||
            !string.Equals(requestMessage.Text, expectedText, StringComparison.Ordinal))
        {
            throw new AgentUnsupportedResponseException(
                "Agent Framework staged request content that differs from the submitted turn.");
        }
    }

    private static void ValidateStreamingContents(IEnumerable<AIContent> contents)
    {
        foreach (var content in contents)
        {
            if (content is TextContent or UsageContent or FunctionCallContent or FunctionResultContent)
            {
                continue;
            }

            throw new AgentUnsupportedResponseException(
                $"The model provider returned unsupported content of type '{content.GetType().Name}'.");
        }
    }

    private static void ValidateInput(AgentTurn turn, AgentSessionOptions options)
    {
        var characters = 0;
        foreach (var content in turn.Contents)
        {
            if (content is not AgentTextContent text)
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

    private void ValidateModelCapabilities(AgentRunProfile profile)
    {
        if ((profile.Capabilities & AgentModelCapabilities.StreamingText) == 0)
        {
            throw new AgentModelCapabilityException(
                AgentModelCapabilities.StreamingText,
                profile.ModelIdentity);
        }

        if (tools.Count > 0 && (profile.Capabilities & AgentModelCapabilities.FunctionCalling) == 0)
        {
            throw new AgentModelCapabilityException(
                AgentModelCapabilities.FunctionCalling,
                profile.ModelIdentity);
        }
    }

    private static int CountCharacters(IEnumerable<AgentTranscriptTurn> source)
    {
        var count = 0;
        foreach (var content in source
                     .SelectMany(static turn => turn.Messages)
                     .SelectMany(static message => message.Contents))
        {
            count = content switch
            {
                AgentTextContent text => checked(count + text.Text.Length),
                AgentToolCallContent call => checked(count + call.Arguments.Value.GetRawText().Length),
                AgentToolResultContent result => checked(count + ToolJson.CreateResultEnvelope(result.Outcome)
                    .GetRawText().Length),
                AgentJsonContent json => checked(count + json.Value.GetRawText().Length),
                _ => count
            };
        }

        return count;
    }

    private static ImmutableDictionary<string, ToolAIFunction> CreateToolRegistry(IEnumerable<IAgentTool>? source)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, ToolAIFunction>(StringComparer.Ordinal);
        foreach (var tool in source ?? [])
        {
            ArgumentNullException.ThrowIfNull(tool);
            var descriptor = tool.Descriptor ??
                             throw new ArgumentException("Agent tools must provide a descriptor.", nameof(source));
            if (!builder.TryAdd(descriptor.Name, new ToolAIFunction(tool)))
            {
                throw new ArgumentException($"An Agent tool named '{descriptor.Name}' is already registered.",
                    nameof(source));
            }
        }

        return builder.ToImmutable();
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
        private readonly List<AgentMessage> intermediateMessages = [];
        private int responseCharacters;
        private int toolCallCount;

        internal async ValueTask<object?> InvokeAsync(
            FunctionInvocationContext invocation,
            CancellationToken cancellationToken)
        {
            if (invocation.Function is not ToolAIFunction function)
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
                !ReferenceEquals(record.Tool, function.Tool))
            {
                throw new AgentToolArgumentsException(
                    "The function invoker could not correlate the requested Agent tool.");
            }

            await run.WriteEventAsync(
                new AgentToolRequested(
                    run.Id,
                    run.NextSequence(),
                    record.CallId,
                    function.Tool.Descriptor,
                    record.Arguments),
                cancellationToken).ConfigureAwait(false);
            await run.WriteEventAsync(
                new AgentToolStarted(run.Id, run.NextSequence(), record.CallId, function.Tool.Descriptor),
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
                        new AgentToolProgress(run.Id, run.NextSequence(), record.CallId, content),
                        token).ConfigureAwait(false);
                });

            AgentToolOutcome outcome;
            try
            {
                outcome = await function.Tool
                              .InvokeAsync(context, record.Arguments, cancellationToken)
                              .ConfigureAwait(false)
                          ?? throw new AgentToolInvocationException(
                              function.Name,
                              new InvalidOperationException("The tool returned a null outcome."));
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
                await run.WriteEventAsync(
                    new AgentToolFailed(
                        run.Id,
                        run.NextSequence(),
                        record.CallId,
                        "tool_execution_failed",
                        "The tool failed unexpectedly."),
                    cancellationToken).ConfigureAwait(false);
                throw new AgentToolInvocationException(function.Name, exception);
            }

            var envelope = ToolJson.CreateResultEnvelope(outcome);
            if (Encoding.UTF8.GetByteCount(envelope.GetRawText()) > options.MaxToolResultBytes)
            {
                throw new AgentToolLimitExceededException(
                    nameof(AgentSessionOptions.MaxToolResultBytes),
                    options.MaxToolResultBytes);
            }

            record.Outcome = outcome;
            intermediateMessages.Add(new AgentMessage(
                AgentMessageId.Create(),
                AgentMessageRole.Tool,
                [
                    new AgentToolResultContent(
                        record.CallId,
                        record.Tool.Descriptor.Name,
                        outcome)
                ]));
            switch (outcome)
            {
                case AgentToolSuccess success:
                    await run.WriteEventAsync(
                        new AgentToolCompleted(run.Id, run.NextSequence(), record.CallId, success),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case AgentToolFailure failure:
                    await run.WriteEventAsync(
                        new AgentToolFailed(
                            run.Id,
                            run.NextSequence(),
                            record.CallId,
                            failure.Code,
                            failure.Message),
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new AgentUnsupportedResponseException(
                        $"Tool '{function.Name}' returned unsupported outcome type '{outcome.GetType().Name}'.");
            }

            return envelope;
        }

        internal async ValueTask ObserveProviderUpdateAsync(
            int iteration,
            ChatResponseUpdate update,
            CancellationToken cancellationToken)
        {
            ValidateStreamingContents(update.Contents);
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

        internal async Task<ImmutableArray<AgentMessage>> BuildCompletedMessagesAsync(
            CancellationToken cancellationToken)
        {
            var iterations = recordingClient.GetIterations();
            if (iterations.Count == 0)
            {
                throw new AgentUnsupportedResponseException("The model provider returned no response.");
            }

            if (iterations.Count >= options.MaxModelIterationsPerTurn &&
                iterations[^1].ResponseMessages
                    .SelectMany(static message => message.Contents)
                    .OfType<FunctionCallContent>()
                    .Any())
            {
                throw new AgentModelIterationLimitExceededException(options.MaxModelIterationsPerTurn);
            }

            for (var index = 1; index < iterations.Count; index++)
            {
                await PublishIterationMessagesAsync(index, cancellationToken).ConfigureAwait(false);
            }

            await PublishIterationMessagesAsync(iterations.Count, cancellationToken).ConfigureAwait(false);
            var messages = ImmutableArray.CreateBuilder<AgentMessage>();
            messages.Add(run.UserMessage);
            messages.AddRange(intermediateMessages);
            if (messages[^1].Role != AgentMessageRole.Assistant ||
                !messages[^1].Contents.OfType<AgentTextContent>().Any(static content => content.Text.Length > 0))
            {
                if (messages[^1].Contents.OfType<AgentToolCallContent>().Any())
                {
                    throw new AgentModelIterationLimitExceededException(options.MaxModelIterationsPerTurn);
                }

                throw new AgentUnsupportedResponseException("The model provider returned no final assistant text.");
            }

            return messages.ToImmutable();
        }

        private async Task PublishIterationMessagesAsync(int iteration, CancellationToken cancellationToken)
        {
            if (!publishedIterations.Add(iteration))
            {
                return;
            }

            var recorded = recordingClient.GetIteration(iteration);
            foreach (var responseMessage in recorded.ResponseMessages)
            {
                if (responseMessage.Role != ChatRole.Assistant)
                {
                    throw new AgentUnsupportedResponseException(
                        $"The model provider returned an unsupported '{responseMessage.Role}' response message.");
                }

                var assistant = ConvertAssistantMessage(responseMessage, iteration);
                intermediateMessages.Add(assistant);
                await run.WriteEventAsync(
                    new AgentMessageCompleted(run.Id, run.NextSequence(), assistant),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private AgentMessage ConvertAssistantMessage(ChatMessage message, int iteration)
        {
            var contents = ImmutableArray.CreateBuilder<AgentContent>();
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        contents.Add(new AgentTextContent(text.Text));
                        break;
                    case FunctionCallContent call:
                    {
                        if (!calls.TryGetValue(call.CallId, out var record))
                        {
                            throw new AgentToolArgumentsException(
                                $"The model requested unknown tool '{call.Name}'.");
                        }

                        contents.Add(new AgentToolCallContent(record.CallId, call.Name, record.Arguments));
                        break;
                    }
                    case UsageContent:
                        break;
                    default:
                        throw new AgentUnsupportedResponseException(
                            $"The model provider returned unsupported content of type '{content.GetType().Name}'.");
                }
            }

            if (contents.Count == 0)
            {
                throw new AgentUnsupportedResponseException("The model provider returned an empty assistant message.");
            }

            var messageId = GetMessageId(iteration, message.MessageId);
            return new AgentMessage(messageId, AgentMessageRole.Assistant, contents.ToImmutable());
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
            var index = 0;
            foreach (var call in recorded.ResponseMessages
                         .SelectMany(static message => message.Contents)
                         .OfType<FunctionCallContent>())
            {
                if (calls.ContainsKey(call.CallId))
                {
                    index++;
                    continue;
                }

                if (!owner.tools.TryGetValue(call.Name, out var function))
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
                if (arguments.GetUtf8Size() > options.MaxToolArgumentsBytes)
                {
                    throw new AgentToolLimitExceededException(
                        nameof(AgentSessionOptions.MaxToolArgumentsBytes),
                        options.MaxToolArgumentsBytes);
                }

                calls.Add(call.CallId, new ToolInvocationRecord(
                    AgentToolCallId.Create(),
                    function.Tool,
                    arguments)
                {
                    Iteration = iteration,
                    Index = index
                });
                index++;
            }
        }

        private sealed class ToolInvocationRecord(
            AgentToolCallId callId,
            IAgentTool tool,
            AgentToolArguments arguments)
        {
            internal AgentToolCallId CallId { get; } = callId;

            internal IAgentTool Tool { get; } = tool;

            internal AgentToolArguments Arguments { get; } = arguments;

            internal int Iteration { get; set; }

            internal int Index { get; set; }

            internal AgentToolOutcome? Outcome { get; set; }
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
        ImmutableArray<AgentMessage> TurnMessages,
        AgentMessage AssistantMessage);

    // ReSharper disable once InconsistentNaming
    private sealed class ToolAIFunction(IAgentTool tool) : AIFunction
    {
        internal IAgentTool Tool { get; } = tool;

        public override string Name => Tool.Descriptor.Name;

        public override string Description => Tool.Descriptor.Description;

        public override JsonElement JsonSchema => Tool.Descriptor.InputSchema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<object?>(new InvalidOperationException(
                "Maieutics tools must be invoked through the configured FunctionInvokingChatClient."));
    }

    private sealed class AgentRun(
        AgentSession owner,
        AgentRunId id,
        AgentMessage userMessage,
        IAgentRunProfileLease profileLease,
        AgentRunProfile profile,
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

        internal AgentMessage UserMessage { get; } = userMessage;

        internal AgentRunProfile Profile { get; } = profile;

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
                cancellation.Cancel();
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

            cancellation.Cancel();
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

file static class ToolJson
{
    internal static AgentToolArguments CreateArguments(IDictionary<string, object?> arguments)
    {
        var root = new JsonObject();
        foreach (var (name, value) in arguments)
        {
            root.Add(name, ToJsonNode(value));
        }

        return new AgentToolArguments(ParseElement(root));
    }

    internal static JsonElement CreateResultEnvelope(AgentToolOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var envelope = outcome switch
        {
            AgentToolSuccess success => new ToolResultEnvelope(
                "ok",
                success.Contents.Select(static content => content switch
                    {
                        AgentTextContent text => new ToolResultContentEnvelope("text", Text: text.Text),
                        AgentJsonContent json => new ToolResultContentEnvelope("json", Value: json.Value),
                        _ => throw new AgentUnsupportedResponseException(
                            $"Tool result content of type '{content.GetType().Name}' is not supported.")
                    })
                    .ToImmutableArray()),
            AgentToolFailure failure => new ToolResultEnvelope(
                "error",
                Code: failure.Code,
                Message: failure.Message),
            _ => throw new AgentUnsupportedResponseException(
                $"Tool outcome type '{outcome.GetType().Name}' is not supported.")
        };
        return JsonSerializer.SerializeToElement(
            envelope,
            AgentToolJsonSerializerContext.Default.ToolResultEnvelope);
    }

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