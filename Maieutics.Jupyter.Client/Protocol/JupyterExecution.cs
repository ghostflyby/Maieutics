using System.Threading.Channels;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client.Protocol;

internal sealed class JupyterExecution(
    JupyterMessageHeader requestHeader,
    Channel<KernelOutput> outputs,
    TaskCompletionSource<ExecutionResult> completion,
    Func<InputRequestId, string, CancellationToken, Task> replyInput,
    Func<CancellationToken, Task> cancel) : IJupyterExecution
{
    public string MessageId => requestHeader.MessageId;

    public IAsyncEnumerable<KernelOutput> Outputs => outputs.Reader.ReadAllAsync();

    public Task<ExecutionResult> Completion => completion.Task;

    public Task ReplyInputAsync(
        InputRequestId requestId,
        string value,
        CancellationToken cancellationToken = default)
    {
        return replyInput(requestId, value, cancellationToken);
    }

    public Task CancelAsync(CancellationToken cancellationToken = default)
    {
        outputs.Writer.TryComplete();
        completion.TrySetCanceled(cancellationToken);
        return cancel(cancellationToken);
    }
}