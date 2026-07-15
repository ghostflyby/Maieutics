using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Kernel.Transport;

internal interface IJupyterKernelTransport : IAsyncDisposable
{
    IAsyncEnumerable<JupyterKernelTransportEvent> IncomingEvents { get; }

    ValueTask SendAsync(
        JupyterKernelChannel channel,
        JupyterWireMessage message,
        CancellationToken cancellationToken = default);
}

internal enum JupyterKernelChannel
{
    Shell,
    Control,
    Iopub,
    Stdin
}

internal abstract record JupyterKernelTransportEvent;

internal sealed record JupyterKernelMessageReceived(
    JupyterKernelChannel Channel,
    JupyterWireMessage WireMessage) : JupyterKernelTransportEvent;

internal sealed record JupyterIopubSubscriptionReceived(byte[] Topic) : JupyterKernelTransportEvent;

internal sealed record JupyterKernelTransportOptions
{
    public int IncomingCapacity { get; init; } = 1024;

    public int OutgoingCapacity { get; init; } = 256;
}