using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client;

public interface IJupyterExecution
{
    JupyterMessageId RequestId { get; }

    IAsyncEnumerable<JupyterOutput> Outputs { get; }

    Task<JupyterExecutionResult> Completion { get; }

    Task ReplyInputAsync(
        JupyterInputRequest request,
        string value,
        CancellationToken cancellationToken = default);
}