using System.Text.Json;
using Maieutics.Jupyter.Client.Transport;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client;

public enum JupyterKernelState
{
    Starting,
    Idle,
    Busy,
    Unknown
}

public abstract record JupyterOutput(JupyterMessageId RequestId);

public sealed record JupyterStdout(JupyterMessageId RequestId, string Text) : JupyterOutput(RequestId);

public sealed record JupyterStderr(JupyterMessageId RequestId, string Text) : JupyterOutput(RequestId);

public sealed record JupyterDisplayOutput(
    JupyterMessageId RequestId,
    MimeBundle Data,
    IReadOnlyDictionary<string, JsonElement> Metadata) : JupyterOutput(RequestId)
{
    public IReadOnlyDictionary<string, JsonElement>? Transient { get; init; }

    public JupyterDisplayId? DisplayId { get; init; }
}

public sealed record JupyterDisplayUpdateOutput(
    JupyterMessageId RequestId,
    MimeBundle Data,
    IReadOnlyDictionary<string, JsonElement> Metadata,
    IReadOnlyDictionary<string, JsonElement> Transient,
    JupyterDisplayId DisplayId) : JupyterOutput(RequestId);

/// <summary>
///     Represents a non-critical Jupyter output message that could not be projected into its typed output model.
/// </summary>
/// <param name="RequestId">The execution request that caused the output.</param>
/// <param name="MessageType">The Jupyter message type that could not be projected.</param>
/// <param name="ErrorCode">A stable diagnostic code describing why the output was rejected.</param>
public sealed record JupyterMalformedOutput(
    JupyterMessageId RequestId,
    string MessageType,
    string ErrorCode) : JupyterOutput(RequestId);

public sealed record JupyterClearOutput(
    JupyterMessageId RequestId,
    bool Wait) : JupyterOutput(RequestId);

public sealed record JupyterExecuteInputOutput(
    JupyterMessageId RequestId,
    string Code,
    int ExecutionCount) : JupyterOutput(RequestId);

public sealed record JupyterExecuteResultOutput(
    JupyterMessageId RequestId,
    MimeBundle Data,
    IReadOnlyDictionary<string, JsonElement> Metadata,
    int ExecutionCount) : JupyterOutput(RequestId);

public sealed record JupyterExecutionError(
    JupyterMessageId RequestId,
    string Name,
    string Value,
    IReadOnlyList<string> Traceback) : JupyterOutput(RequestId);

public sealed record JupyterExecutionStatusChanged(
    JupyterMessageId RequestId,
    JupyterKernelState State) : JupyterOutput(RequestId);

public sealed record JupyterInputRequest(
    JupyterMessageId RequestId,
    JupyterMessageId InputRequestId,
    string Prompt,
    bool Password) : JupyterOutput(RequestId)
{
    internal JupyterMessageHeader? Header { get; init; }
}

public sealed record JupyterExecutionResult(
    JupyterExecuteReply Reply,
    JupyterMessage RawMessage);

public abstract record JupyterClientEvent;

public sealed record JupyterClientConnected : JupyterClientEvent;

public sealed record JupyterClientDisconnected(Exception? Cause) : JupyterClientEvent;

public sealed record JupyterKernelStatusChanged(JupyterKernelState State) : JupyterClientEvent;

/// <summary>
///     Represents parented IOPub output observed after an execution's idle status.
/// </summary>
public sealed record JupyterLateOutput(JupyterMessageId RequestId, JupyterMessage Message) : JupyterClientEvent
{
    /// <summary>Gets the typed output projection when the message type is supported.</summary>
    public JupyterOutput? Output { get; init; }

    /// <summary>
    ///     Gets whether the typed output was also retained in the execution output stream because the shell reply had
    ///     not yet arrived and protocol completion had not occurred.
    /// </summary>
    public bool IncludedInExecution { get; init; }
}

public sealed record JupyterUnhandledMessage(JupyterTransportChannel Channel, JupyterMessage Message)
    : JupyterClientEvent;
