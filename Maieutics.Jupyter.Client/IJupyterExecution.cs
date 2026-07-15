namespace Maieutics.Jupyter.Client;

public interface IJupyterExecution
{
    string MessageId { get; }

    IAsyncEnumerable<KernelOutput> Outputs { get; }

    Task<ExecutionResult> Completion { get; }

    Task ReplyInputAsync(
        InputRequestId requestId,
        string value,
        CancellationToken cancellationToken = default);

    Task CancelAsync(CancellationToken cancellationToken = default);
}