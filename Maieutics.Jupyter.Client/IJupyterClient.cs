using System.Text.Json.Nodes;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client;

public interface IJupyterClient : IAsyncDisposable
{
    ValueTask<JupyterMessage> SendShellAsync(string messageType, JsonObject content,
        CancellationToken cancellationToken = default);

    ValueTask<JupyterMessage> RequestKernelInfoAsync(CancellationToken cancellationToken = default);

    ValueTask<ExecuteResult> ExecuteAsync(string code, CancellationToken cancellationToken = default);
}

public sealed record ExecuteResult(
    string Status,
    int? ExecutionCount,
    IReadOnlyList<JupyterMessage> IopubMessages,
    JupyterMessage Reply);