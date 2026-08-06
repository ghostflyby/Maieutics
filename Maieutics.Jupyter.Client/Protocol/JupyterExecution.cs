using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client.Protocol;

internal sealed class JupyterExecution(
    JupyterMessageId requestId,
    ChannelReader<JupyterOutput> outputs,
    Task<JupyterExecutionResult> completion,
    Func<JupyterInputRequest, string, CancellationToken, Task> replyInput) : IJupyterExecution
{
    private int outputEnumerationStarted;

    public JupyterMessageId RequestId => requestId;

    public IAsyncEnumerable<JupyterOutput> Outputs => ReadOutputsAsync();

    public Task<JupyterExecutionResult> Completion => completion;

    public Task ReplyInputAsync(
        JupyterInputRequest request,
        string value,
        CancellationToken cancellationToken = default)
    {
        if (request.RequestId != requestId)
            throw new ArgumentException("The input request belongs to a different execution.", nameof(request));

        return replyInput(request, value, cancellationToken);
    }

    private async IAsyncEnumerable<JupyterOutput> ReadOutputsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref outputEnumerationStarted, 1) != 0)
            throw new InvalidOperationException("Jupyter execution output is a single-consumer stream.");

        await foreach (var output in outputs.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return output;
    }
}