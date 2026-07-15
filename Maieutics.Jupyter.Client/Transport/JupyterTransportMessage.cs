using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client.Transport;

public enum JupyterTransportChannel
{
    Shell,
    Control,
    Iopub,
    Stdin
}

public sealed record JupyterTransportMessage(
    JupyterTransportChannel Channel,
    JupyterWireMessage WireMessage)
{
    public JupyterMessage Message => WireMessage.Message;
}

public sealed class JupyterBackpressureException(string message) : Exception(message);