using System.Text.Json.Nodes;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client;

public enum KernelState
{
    Starting,
    Idle,
    Busy,
    Unknown
}

public sealed record KernelInfoReply(
    string Implementation,
    string ImplementationVersion,
    string LanguageName,
    JupyterMessage Message);

public sealed record InputRequestId(string Value);

public abstract record KernelOutput(string ExecutionId);

public sealed record Stdout(string ExecutionId, string Text) : KernelOutput(ExecutionId);

public sealed record Stderr(string ExecutionId, string Text) : KernelOutput(ExecutionId);

public sealed record DisplayData(
    string ExecutionId,
    MimeBundle Data,
    IReadOnlyDictionary<string, JsonNode?> Metadata) : KernelOutput(ExecutionId);

public sealed record ExecuteResultOutput(
    string ExecutionId,
    MimeBundle Data,
    int? ExecutionCount) : KernelOutput(ExecutionId);

public sealed record ExecutionError(
    string ExecutionId,
    string Name,
    string Message,
    IReadOnlyList<string> StackTrace) : KernelOutput(ExecutionId);

public sealed record InputRequest(
    string ExecutionId,
    InputRequestId RequestId,
    string Prompt,
    bool Password) : KernelOutput(ExecutionId);

public sealed record ExecutionStatusChanged(
    string ExecutionId,
    KernelState State) : KernelOutput(ExecutionId);

public sealed record ExecutionResult(
    string Status,
    int? ExecutionCount,
    JupyterMessage Reply);

public abstract record KernelEvent;

public sealed record Connected : KernelEvent;

public sealed record Disconnected(Exception? Cause) : KernelEvent;

public sealed record KernelStatusChanged(KernelState State) : KernelEvent;

public sealed record UnhandledMessage(JupyterMessage Message) : KernelEvent;