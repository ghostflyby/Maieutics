using System.Text.Json;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Kernel;

public interface IJupyterKernelApplication
{
    JupyterKernelInfo KernelInfo { get; }

    ValueTask<JupyterExecuteResult> ExecuteAsync(
        JupyterExecutionContext context,
        JupyterExecuteRequest request,
        CancellationToken cancellationToken);
}

public interface IJupyterCompletionProvider
{
    ValueTask<JupyterCompletionResult> CompleteAsync(
        JupyterCompleteRequest request,
        CancellationToken cancellationToken);
}

public interface IJupyterInspectionProvider
{
    ValueTask<JupyterInspectionResult> InspectAsync(
        JupyterInspectRequest request,
        CancellationToken cancellationToken);
}

public interface IJupyterCodeCompletenessProvider
{
    ValueTask<JupyterCodeCompletenessResult> IsCompleteAsync(
        JupyterIsCompleteRequest request,
        CancellationToken cancellationToken);
}

public sealed record JupyterCompletionResult(
    IReadOnlyList<string> Matches,
    int CursorStart,
    int CursorEnd,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public sealed record JupyterInspectionResult(
    bool Found,
    MimeBundle Data,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public enum JupyterCodeCompletenessStatus
{
    Complete,
    Incomplete,
    Invalid,
    Unknown
}

public sealed record JupyterCodeCompletenessResult(
    JupyterCodeCompletenessStatus Status,
    string? Indent = null);

public sealed record JupyterExecuteResult(string Status = "ok")
{
    public static JupyterExecuteResult Ok { get; } = new();
}

public sealed class JupyterKernelExecutionException(
    string name,
    string message,
    IReadOnlyList<string>? traceback = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Name { get; } = name;

    public IReadOnlyList<string> Traceback { get; } = traceback ?? [];
}