using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Maieutics.Agent;
using Maieutics.Permissions;
using Microsoft.Extensions.AI;

namespace Maieutics.Processes;

/// <summary>One reviewed process start, presented to the owner of the calling session.</summary>
internal sealed record ProcessExecutionApprovalRequest(
    AgentSessionId SessionId, string Tool, string Reason, JsonElement Command);

internal interface IProcessExecutionApproval
{
    ValueTask<AgentToolApprovalDecision> RequestAsync(
        ProcessExecutionApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Resolves the shared launch policy before either process owner starts a child.</summary>
internal sealed class ProcessExecution(IProcessSandboxPolicy sandbox, IProcessExecutionApproval? approval = null)
{
    internal const string SingleUseApprovalReason = "This command can only be approved once.";

    private static readonly JsonWriterOptions ApprovalJson = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    internal static AgentSessionId RequireOwner(AIFunctionArguments arguments)
    {
        if (arguments.Context?.TryGetValue(typeof(AgentToolContext), out var value) == true &&
            value is AgentToolContext agent)
            return agent.SessionId;
        if (arguments.Context?.TryGetValue(typeof(AgentSessionId), out value) == true &&
            value is AgentSessionId owner)
            return owner;

        throw new AgentToolException("process_session_required",
            "Process execution requires a verified owning Agent session.");
    }

    internal async ValueTask<ProcessLaunchPlan.LaunchPlan> PrepareAsync(
        EffectivePolicy policy,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string tool,
        string denialCode,
        AgentSessionId? sessionId,
        bool outsideSandbox,
        bool reviewable,
        CancellationToken cancellationToken,
        bool hasTerminal = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = sandbox.Plan(policy, executable, arguments, outsideSandbox, workingDirectory, hasTerminal);
        if (plan is ProcessLaunchPlan.LaunchPlan launch) return launch;
        if (plan is ProcessLaunchPlan.DeniedPlan denied)
            throw new AgentToolException(denialCode, denied.Reason);

        var pending = (ProcessLaunchPlan.ApprovalRequiredPlan)plan;
        if (!reviewable)
            throw new AgentToolException("process_sandbox_required",
                pending.Reason + " Execution outside the sandbox is available only for a one-shot command without input.");
        if (approval is null || sessionId is not { } owner)
            throw new AgentToolException("process_approval_unavailable",
                pending.Reason + " No interactive session can approve this process.");

        var command = BuildCommand(workingDirectory, pending.Candidate.Argv);
        var decision = await approval.RequestAsync(
            new ProcessExecutionApprovalRequest(owner, tool, pending.Reason, command), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (decision.AlwaysAllow)
            throw new AgentToolException("process_approval_denied", SingleUseApprovalReason);
        if (!decision.Approved)
            throw new AgentToolException("process_approval_denied", decision.Reason);

        return pending.Candidate with { AllowsTerminalInput = false };
    }

    private static JsonElement BuildCommand(string workingDirectory, IReadOnlyList<string> argv)
    {
        // Use JSON escaping so model-supplied arguments cannot add lines to the approval prompt.
        // The entire command must fit in the read-only approval context.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, ApprovalJson))
        {
            writer.WriteStartObject();
            writer.WriteString("workingDirectory", workingDirectory);
            writer.WriteStartArray("argv");
            foreach (var argument in argv)
            {
                if (argument.Length > 2_048)
                    throw new AgentToolException("process_approval_details_too_large",
                        "The command is too long to display in a process approval.");
                writer.WriteStringValue(argument);
                if (writer.BytesCommitted + writer.BytesPending > 2_044)
                    throw new AgentToolException("process_approval_details_too_large",
                        "The command is too long to display in a process approval.");
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
