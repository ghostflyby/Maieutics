using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using FrameworkAgentSession = Microsoft.Agents.AI.AgentSession;

namespace Maieutics.Agent;

/// <summary>Runs a provider-neutral Agent conversation over Microsoft Agent Framework.</summary>
public sealed class AgentSession : IAgentSession
{
    private readonly AgentSessionOptions options;
    private readonly Lock transcriptGate = new();
    private readonly StagingChatHistoryProvider historyProvider;
    private readonly ChatClientAgent agent;
    private ImmutableArray<AgentMessage> messages = [];
    private FrameworkAgentSession? frameworkSession;
    private long transcriptVersion;
    private int runInProgress;

    /// <summary>Initializes an Agent session.</summary>
    public AgentSession(IChatClient chatClient, AgentSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        this.options = options ?? new AgentSessionOptions();
        this.options.Validate();

        Id = AgentSessionId.Create();
        historyProvider = new StagingChatHistoryProvider(GetCommittedChatMessages);
        agent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions { Instructions = this.options.SystemPrompt },
                ChatHistoryProvider = historyProvider,
                UseProvidedChatClientAsIs = true,
                ClearOnChatHistoryProviderConflict = false,
                WarnOnChatHistoryProviderConflict = true,
                ThrowOnChatHistoryProviderConflict = true
            });
    }

    /// <inheritdoc />
    public AgentSessionId Id { get; }

    /// <inheritdoc />
    public Task<IAgentRun> StartTurnAsync(
        AgentTurn turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateInput(turn);

        if (Interlocked.CompareExchange(ref runInProgress, 1, 0) != 0)
        {
            throw new AgentTurnInProgressException();
        }

        try
        {
            var userMessage = new AgentMessage(AgentMessageId.Create(), AgentMessageRole.User, turn.Contents);
            var run = new AgentRun(
                this,
                AgentRunId.Create(),
                userMessage,
                AgentMessageId.Create(),
                options.EventBufferCapacity);
            run.Start();
            return Task.FromResult<IAgentRun>(run);
        }
        catch
        {
            Volatile.Write(ref runInProgress, 0);
            throw;
        }
    }

    /// <inheritdoc />
    public AgentTranscript GetTranscriptSnapshot()
    {
        lock (transcriptGate)
        {
            return new AgentTranscript(Id, transcriptVersion, messages);
        }
    }

    private async Task<AgentRunResult> ExecuteRunAsync(AgentRun run, CancellationToken cancellationToken)
    {
        historyProvider.BeginRun(run.Id);
        var committed = false;
        try
        {
            var session = frameworkSession;
            if (session is null)
            {
                session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
                frameworkSession = session;
            }

            var response = new StringBuilder();
            var frameworkMessage = ToChatMessage(run.UserMessage);
            try
            {
                await foreach (var update in agent
                                   .RunStreamingAsync(frameworkMessage, session, cancellationToken: cancellationToken)
                                   .ConfigureAwait(false))
                {
                    ValidateResponseContents(update.Contents);
                    var text = update.Text;
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    if (response.Length + text.Length > options.MaxResponseCharacters)
                    {
                        throw new AgentResponseLimitExceededException(options.MaxResponseCharacters);
                    }

                    response.Append(text);
                    await run.WriteEventAsync(
                        new AgentTextDelta(run.Id, run.NextSequence(), run.AssistantMessageId, text),
                        cancellationToken).ConfigureAwait(false);
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
            var assistantText = ValidateFinalResponse(staged.ResponseMessages, response.ToString());
            var assistant = new AgentMessage(
                run.AssistantMessageId,
                AgentMessageRole.Assistant,
                [new AgentTextContent(assistantText)]);

            await run.WriteEventAsync(
                new AgentMessageCompleted(run.Id, run.NextSequence(), assistant),
                cancellationToken).ConfigureAwait(false);

            var transcript = CommitTurn(run.UserMessage, assistant);
            committed = true;
            return new AgentRunResult(run.Id, run.UserMessage, assistant, transcript);
        }
        finally
        {
            historyProvider.Discard(run.Id);
            if (!committed)
            {
                frameworkSession = null;
            }
        }
    }

    private AgentTranscript CommitTurn(AgentMessage user, AgentMessage assistant)
    {
        lock (transcriptGate)
        {
            var builder = messages.ToBuilder();
            builder.Add(user);
            builder.Add(assistant);

            while (builder.Count / 2 > options.MaxRetainedTurns ||
                   CountCharacters(builder) > options.MaxHistoryCharacters)
            {
                builder.RemoveRange(0, 2);
            }

            messages = builder.ToImmutable();
            transcriptVersion++;
            return new AgentTranscript(Id, transcriptVersion, messages);
        }
    }

    private IReadOnlyList<ChatMessage> GetCommittedChatMessages()
    {
        ImmutableArray<AgentMessage> snapshot;
        lock (transcriptGate)
        {
            snapshot = messages;
        }

        return snapshot.Select(ToChatMessage).ToArray();
    }

    private static ChatMessage ToChatMessage(AgentMessage message)
    {
        var role = message.Role switch
        {
            AgentMessageRole.User => ChatRole.User,
            AgentMessageRole.Assistant => ChatRole.Assistant,
            _ => throw new ArgumentOutOfRangeException(nameof(message), message.Role, null)
        };
        var contents = message.Contents.Select(ToAIContent).ToList();
        return new ChatMessage(role, contents);
    }

    // ReSharper disable once InconsistentNaming
    private static AIContent ToAIContent(AgentContent content) => content switch
    {
        AgentTextContent text => new TextContent(text.Text),
        _ => throw new AgentUnsupportedResponseException(
            $"Agent input content of type '{content.GetType().Name}' is not supported.")
    };

    private static string ValidateFinalResponse(
        IReadOnlyList<ChatMessage> responseMessages,
        string streamedText)
    {
        var text = new StringBuilder();
        foreach (var message in responseMessages)
        {
            if (message.Role != ChatRole.Assistant)
            {
                throw new AgentUnsupportedResponseException(
                    $"The model provider returned an unsupported '{message.Role}' response message.");
            }

            ValidateResponseContents(message.Contents);
            foreach (var content in message.Contents.OfType<TextContent>())
            {
                text.Append(content.Text);
            }
        }

        if (text.Length == 0)
        {
            throw new AgentUnsupportedResponseException("The model provider returned no assistant text.");
        }

        if (!string.Equals(text.ToString(), streamedText, StringComparison.Ordinal))
        {
            throw new AgentUnsupportedResponseException(
                "The model provider returned inconsistent streamed and completed assistant text.");
        }

        return text.ToString();
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
        foreach (var content in requestMessage.Contents)
        {
            if (content is not TextContent)
            {
                throw new AgentUnsupportedResponseException(
                    $"Agent Framework staged unsupported request content of type '{content.GetType().Name}'.");
            }
        }

        if (!string.Equals(requestMessage.Text, expectedText, StringComparison.Ordinal))
        {
            throw new AgentUnsupportedResponseException(
                "Agent Framework staged request text that differs from the submitted turn.");
        }
    }

    private static void ValidateResponseContents(IEnumerable<AIContent> contents)
    {
        foreach (var content in contents)
        {
            if (content is TextContent or UsageContent)
            {
                continue;
            }

            throw new AgentUnsupportedResponseException(
                $"The model provider returned unsupported content of type '{content.GetType().Name}'.");
        }
    }

    private void ValidateInput(AgentTurn turn)
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

    private static int CountCharacters(IEnumerable<AgentMessage> source)
    {
        var count = 0;
        foreach (var message in source)
        {
            foreach (var content in message.Contents.OfType<AgentTextContent>())
            {
                count = checked(count + content.Text.Length);
            }
        }

        return count;
    }

    private void ReleaseRun() => Volatile.Write(ref runInProgress, 0);

    private sealed class AgentRun(
        AgentSession owner,
        AgentRunId id,
        AgentMessage userMessage,
        AgentMessageId assistantMessageId,
        int eventBufferCapacity) : IAgentRun
    {
        private readonly CancellationTokenSource cancellation = new();

        private readonly Channel<AgentEvent> events = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(
            eventBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
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

        internal AgentMessageId AssistantMessageId { get; } = assistantMessageId;

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
            try
            {
                AgentRunResult result;
                try
                {
                    result = await owner.ExecuteRunAsync(this, cancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    owner.ReleaseRun();
                    events.Writer.TryComplete();
                }

                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellation.Token);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
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