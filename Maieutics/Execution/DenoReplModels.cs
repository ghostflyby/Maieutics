using System.Text.Json;
using Maieutics.Control;
using Maieutics.Jupyter.Client;

namespace Maieutics.Execution;

internal sealed record DenoReplStartResult(IJupyterKernelManager Manager, ReplControlHost? ControlChannel);

internal sealed record DenoReplSessionResult(
    string SessionId,
    int Generation,
    string State,
    string Cwd,
    bool IsDefault);

internal sealed record DenoReplListResult(IReadOnlyList<DenoReplSessionResult> Sessions);

internal sealed record DenoReplCloseResult(string SessionId, bool Closed);

internal sealed record DenoReplExecutionResult(
    string SessionId,
    int Generation,
    int? ExecutionCount,
    string ExecutionStatus,
    IReadOnlyList<DenoReplOutputItem> Outputs,
    DenoReplPresentationResult Presentation,
    bool Truncated,
    int OmittedBytes);

internal sealed record DenoReplOutputItem(
    string Kind,
    string? Text = null,
    string? MediaType = null,
    JsonElement? Value = null,
    string? Name = null,
    IReadOnlyList<string>? Traceback = null,
    IReadOnlyList<string>? MediaTypes = null);

internal sealed record DenoReplPresentationResult(
    int DisplayCount,
    int UpdateCount,
    int ClearCount,
    int SkippedCount);

internal enum DenoReplSessionState
{
    Created,
    Starting,
    Idle,
    Busy,
    Restarting,
    Faulted,
    Closing,
    Closed
}
