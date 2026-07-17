using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

internal sealed class StagingChatHistoryProvider(Func<IReadOnlyList<ChatMessage>> loadCommitted)
    : ChatHistoryProvider
{
    private readonly Lock gate = new();
    private AgentRunId activeRunId;
    private StagedHistory? staged;

    internal void BeginRun(AgentRunId runId)
    {
        lock (gate)
        {
            activeRunId = runId;
            staged = null;
        }
    }

    internal StagedHistory TakeStaged(AgentRunId runId)
    {
        lock (gate)
        {
            if (activeRunId != runId || staged is null)
            {
                throw new AgentUnsupportedResponseException(
                    "Agent Framework completed without staging a response.");
            }

            var result = staged;
            staged = null;
            return result;
        }
    }

    internal void Discard(AgentRunId runId)
    {
        lock (gate)
        {
            if (activeRunId == runId)
            {
                activeRunId = default;
                staged = null;
            }
        }
    }

    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IEnumerable<ChatMessage>>(loadCommitted());

    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        var requestMessages = context.RequestMessages.Select(static message => message.Clone()).ToArray();
        var responseMessages = context.ResponseMessages?.Select(static message => message.Clone()).ToArray()
                               ?? [];
        lock (gate)
        {
            if (activeRunId.Value == Guid.Empty)
            {
                throw new InvalidOperationException("No Agent run is active for history staging.");
            }

            staged = new StagedHistory(requestMessages, responseMessages);
        }

        return ValueTask.CompletedTask;
    }

    internal sealed record StagedHistory(
        IReadOnlyList<ChatMessage> RequestMessages,
        IReadOnlyList<ChatMessage> ResponseMessages);
}