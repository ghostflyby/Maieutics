using Maieutics.Agent;

namespace Maieutics.Frontend;

/// <summary>Renders one normalized Agent event as its frontend event frame, keyed by the
/// event's own run identifier and run-local sequence. Shared by the parent run's pump and the
/// subagent display-plane tap, so a child run's frames are wire-identical to a parent's.</summary>
internal static class AgentEventFrameMapper
{
    internal static FrontendEventFrame? Render(AgentEvent agentEvent)
    {
        return agentEvent switch
        {
            AgentTextDelta { Text.Length: > 0 } delta => new FrontendEventFrame(
                "text.delta",
                RunId: delta.RunId.Value.ToString("N"),
                Sequence: delta.Sequence,
                MessageId: delta.MessageId.Value.ToString("N"),
                Text: delta.Text),
            AgentReasoningSummaryDelta { Text.Length: > 0 } summary => new FrontendEventFrame(
                "reasoning.delta",
                RunId: summary.RunId.Value.ToString("N"),
                Sequence: summary.Sequence,
                MessageId: summary.MessageId.Value.ToString("N"),
                Text: summary.Text),
            AgentMessageCompleted message => new FrontendEventFrame(
                "message.completed",
                RunId: message.RunId.Value.ToString("N"),
                Sequence: message.Sequence,
                MessageId: message.AgentMessageId.Value.ToString("N"),
                AgentMessage: FrontendTranscriptMapper.ToMessage(message.Message)),
            AgentToolStarted started => new FrontendEventFrame(
                "tool.started",
                RunId: started.RunId.Value.ToString("N"),
                Sequence: started.Sequence,
                CallId: started.CallId.Value.ToString("N"),
                Tool: started.ToolName,
                Arguments: started.Arguments),
            AgentToolProgress progress => new FrontendEventFrame(
                "tool.progress",
                RunId: progress.RunId.Value.ToString("N"),
                Sequence: progress.Sequence,
                CallId: progress.CallId.Value.ToString("N"),
                Content: FrontendTranscriptMapper.ToProgressContent(progress.Content)),
            AgentToolFinished finished => new FrontendEventFrame(
                "tool.finished",
                RunId: finished.RunId.Value.ToString("N"),
                Sequence: finished.Sequence,
                CallId: finished.CallId.Value.ToString("N"),
                Result: finished.Result),
            AgentToolApprovalRequested approvalRequested => new FrontendEventFrame(
                "tool.approval_requested",
                RunId: approvalRequested.RunId.Value.ToString("N"),
                Sequence: approvalRequested.Sequence,
                CallId: approvalRequested.CallId.Value.ToString("N"),
                Tool: approvalRequested.ToolName,
                Reason: approvalRequested.Reason,
                Arguments: approvalRequested.Arguments),
            AgentToolApprovalResolved approvalResolved => new FrontendEventFrame(
                "tool.approval_resolved",
                RunId: approvalResolved.RunId.Value.ToString("N"),
                Sequence: approvalResolved.Sequence,
                CallId: approvalResolved.CallId.Value.ToString("N"),
                Approved: approvalResolved.Approved,
                Reason: approvalResolved.Reason,
                AlwaysAllow: approvalResolved.AlwaysAllow),
            AgentTurnTruncated turnTruncated => new FrontendEventFrame(
                "turn.truncated",
                RunId: turnTruncated.RunId.Value.ToString("N"),
                Sequence: turnTruncated.Sequence),
            _ => null
        };
    }
}
