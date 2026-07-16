using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

public sealed class AgentSession : IAgentSession
{
    private readonly IChatClient chatClient;
    private readonly AgentSessionOptions options;
    private readonly Lock historyGate = new();
    private readonly List<AgentMessage> history = [];
    private int turnInProgress;

    public AgentSession(IChatClient chatClient, AgentSessionOptions? options = null)
    {
        this.chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        this.options = options ?? new AgentSessionOptions();
        this.options.Validate();
    }

    public IAsyncEnumerable<AgentEvent> ExecuteTurnAsync(
        AgentTurn turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return ExecuteTurnCoreAsync(turn, cancellationToken);
    }

    public IReadOnlyList<AgentMessage> GetHistorySnapshot()
    {
        lock (historyGate)
        {
            return history.ToArray();
        }
    }

    private async IAsyncEnumerable<AgentEvent> ExecuteTurnCoreAsync(
        AgentTurn turn,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref turnInProgress, 1, 0) != 0)
        {
            throw new AgentTurnInProgressException();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateInput(turn.Input);
            var messages = BuildRequestMessages(turn.Input);
            var response = new StringBuilder();

            var updates = CreateProviderEnumerator(messages, cancellationToken);
            try
            {
                while (true)
                {
                    ChatResponseUpdate update;
                    try
                    {
                        if (!await updates.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        update = updates.Current;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        throw new AgentProviderException(exception);
                    }

                    ValidateResponseContents(update);
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
                    yield return new AgentTextDelta(text);
                }
            }
            finally
            {
                await DisposeProviderEnumeratorAsync(updates, cancellationToken).ConfigureAwait(false);
            }

            if (response.Length == 0)
            {
                throw new AgentUnsupportedResponseException("The model provider returned no assistant text.");
            }

            var assistant = new AgentMessage(AgentMessageRole.Assistant, response.ToString());
            CommitTurn(new AgentMessage(AgentMessageRole.User, turn.Input), assistant);
            yield return new AgentTurnCompleted(assistant);
        }
        finally
        {
            Volatile.Write(ref turnInProgress, 0);
        }
    }

    private List<ChatMessage> BuildRequestMessages(string input)
    {
        AgentMessage[] snapshot;
        lock (historyGate)
        {
            snapshot = history.ToArray();
        }

        var messages = new List<ChatMessage>(snapshot.Length + 2);
        if (!string.IsNullOrWhiteSpace(options.SystemPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.System, options.SystemPrompt));
        }

        foreach (var message in snapshot)
        {
            messages.Add(new ChatMessage(ToChatRole(message.Role), message.Text));
        }

        messages.Add(new ChatMessage(ChatRole.User, input));
        return messages;
    }

    private void CommitTurn(AgentMessage user, AgentMessage assistant)
    {
        lock (historyGate)
        {
            history.Add(user);
            history.Add(assistant);

            while (history.Count / 2 > options.MaxRetainedTurns ||
                   HistoryCharacterCount() > options.MaxHistoryCharacters)
            {
                history.RemoveRange(0, 2);
            }
        }
    }

    private int HistoryCharacterCount()
    {
        var count = 0;
        foreach (var message in history)
        {
            count += message.Text.Length;
        }

        return count;
    }

    private void ValidateInput(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > options.MaxInputCharacters)
        {
            throw new AgentInputLimitExceededException(input.Length, options.MaxInputCharacters);
        }
    }

    private IAsyncEnumerator<ChatResponseUpdate> CreateProviderEnumerator(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            return chatClient
                .GetStreamingResponseAsync(messages, cancellationToken: cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AgentProviderException(exception);
        }
    }

    private static async ValueTask DisposeProviderEnumeratorAsync(
        IAsyncEnumerator<ChatResponseUpdate> updates,
        CancellationToken cancellationToken)
    {
        try
        {
            await updates.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AgentProviderException(exception);
        }
    }

    private static void ValidateResponseContents(ChatResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            if (content is TextContent or UsageContent or TextReasoningContent)
            {
                continue;
            }

            throw new AgentUnsupportedResponseException(
                $"The model provider returned unsupported content of type '{content.GetType().Name}'.");
        }
    }

    private static ChatRole ToChatRole(AgentMessageRole role) => role switch
    {
        AgentMessageRole.User => ChatRole.User,
        AgentMessageRole.Assistant => ChatRole.Assistant,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
    };
}