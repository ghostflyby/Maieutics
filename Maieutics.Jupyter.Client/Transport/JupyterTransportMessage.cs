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
    JupyterMessage Message);