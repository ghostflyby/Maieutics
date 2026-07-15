using Maieutics.Jupyter.Client.Protocol;
using Maieutics.Jupyter.Client.Transport;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client;

public sealed class JupyterClient : IJupyterClient
{
    private readonly IJupyterProtocolSession protocolSession;

    public JupyterClient(JupyterConnectionInfo connectionInfo, JupyterSessionIdentity? session = null)
        : this(new JupyterProtocolSession(new NetMqJupyterTransport(connectionInfo), session))
    {
    }

    internal JupyterClient(IJupyterProtocolSession protocolSession)
    {
        this.protocolSession = protocolSession;
    }

    public IAsyncEnumerable<KernelEvent> Events => protocolSession.Events;

    public Task<KernelInfoReply> GetKernelInfoAsync(CancellationToken cancellationToken = default)
    {
        return protocolSession.GetKernelInfoAsync(cancellationToken);
    }

    public Task<IJupyterExecution> ExecuteAsync(
        ExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        return protocolSession.StartExecutionAsync(request, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return protocolSession.DisposeAsync();
    }
}