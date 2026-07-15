using Maieutics.Jupyter.Client.Protocol;
using Maieutics.Jupyter.Client.Transport;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client;

public sealed class JupyterClient : IJupyterClient
{
    private readonly IJupyterProtocolSession protocolSession;

    private JupyterClient(IJupyterProtocolSession protocolSession)
    {
        this.protocolSession = protocolSession;
    }

    public static async Task<JupyterClient> ConnectAsync(
        JupyterConnectionInfo connectionInfo,
        JupyterSessionIdentity? session = null,
        JupyterTransportOptions? transportOptions = null,
        CancellationToken cancellationToken = default)
    {
        var transport = await NetMqJupyterTransport.ConnectAsync(
            connectionInfo,
            transportOptions,
            cancellationToken).ConfigureAwait(false);
        return new JupyterClient(new JupyterProtocolSession(transport, session));
    }

    internal static JupyterClient CreateForTransport(
        IJupyterTransport transport,
        JupyterSessionIdentity? session = null) =>
        new(new JupyterProtocolSession(transport, session));

    public IAsyncEnumerable<JupyterClientEvent> WatchEventsAsync(
        CancellationToken cancellationToken = default) =>
        protocolSession.WatchEventsAsync(cancellationToken);

    public Task<JupyterKernelInfo> GetKernelInfoAsync(CancellationToken cancellationToken = default) =>
        protocolSession.GetKernelInfoAsync(cancellationToken);

    internal Task<JupyterKernelInfo> WaitForReadyAsync(CancellationToken cancellationToken = default) =>
        protocolSession.WaitForReadyAsync(cancellationToken);

    public Task<IJupyterExecution> ExecuteAsync(
        JupyterExecuteRequest request,
        CancellationToken cancellationToken = default) =>
        protocolSession.StartExecutionAsync(request, cancellationToken);

    public Task<JupyterCompleteReply> CompleteAsync(
        JupyterCompleteRequest request,
        CancellationToken cancellationToken = default) =>
        protocolSession.CompleteAsync(request, cancellationToken);

    public Task<JupyterInspectReply> InspectAsync(
        JupyterInspectRequest request,
        CancellationToken cancellationToken = default) =>
        protocolSession.InspectAsync(request, cancellationToken);

    public Task<JupyterIsCompleteReply> IsCompleteAsync(
        JupyterIsCompleteRequest request,
        CancellationToken cancellationToken = default) =>
        protocolSession.IsCompleteAsync(request, cancellationToken);

    public Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default) =>
        protocolSession.PingAsync(cancellationToken);

    internal Task<JupyterInterruptReply> InterruptAsync(CancellationToken cancellationToken = default) =>
        protocolSession.InterruptAsync(cancellationToken);

    internal Task<JupyterShutdownReply> ShutdownAsync(
        bool restart,
        CancellationToken cancellationToken = default) =>
        protocolSession.ShutdownAsync(restart, cancellationToken);

    public ValueTask DisposeAsync() => protocolSession.DisposeAsync();
}