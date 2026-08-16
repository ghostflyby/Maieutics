using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client.Transport;

/// <summary>
/// Provides a compatibility facade for the former NetMQ-backed transport.
/// </summary>
[Obsolete("Use ZmqSharpJupyterTransport instead.")]
public sealed class NetMqJupyterTransport : IJupyterTransport, IJupyterTransportConnectionReadiness
{
    private readonly ZmqSharpJupyterTransport transport;

    private NetMqJupyterTransport(ZmqSharpJupyterTransport transport)
    {
        this.transport = transport;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<JupyterTransportMessage> IncomingMessages => transport.IncomingMessages;

    /// <inheritdoc />
    public ValueTask SendAsync(
        JupyterTransportChannel channel,
        JupyterMessage message,
        CancellationToken cancellationToken = default)
    {
        return transport.SendAsync(channel, message, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        return transport.PingAsync(cancellationToken);
    }

    Task IJupyterTransportConnectionReadiness.WaitForStdinConnectedAsync(CancellationToken cancellationToken)
    {
        return ((IJupyterTransportConnectionReadiness)transport).WaitForStdinConnectedAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return transport.DisposeAsync();
    }

    /// <summary>
    /// Starts a compatibility transport for the supplied Jupyter connection.
    /// </summary>
    public static async Task<NetMqJupyterTransport> ConnectAsync(
        JupyterConnectionInfo connectionInfo,
        JupyterTransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var transport = await ZmqSharpJupyterTransport.ConnectAsync(
                connectionInfo,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        return new NetMqJupyterTransport(transport);
    }
}
