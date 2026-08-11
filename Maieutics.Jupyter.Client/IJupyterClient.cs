using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client;

public interface IJupyterClient : IAsyncDisposable
{
    IAsyncEnumerable<JupyterClientEvent> WatchEventsAsync(
        CancellationToken cancellationToken = default);

    Task<JupyterKernelInfo> GetKernelInfoAsync(CancellationToken cancellationToken = default);

    Task<IJupyterExecution> ExecuteAsync(
        JupyterExecuteRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Starts an execution with client-local observation options.</summary>
    /// <param name="request">The Jupyter execute request sent to the kernel.</param>
    /// <param name="options">Client-local behavior that is not serialized onto the Jupyter wire.</param>
    /// <param name="cancellationToken">Cancels starting the execution.</param>
    /// <returns>The active execution.</returns>
    /// <exception cref="NotSupportedException">
    ///     The implementation does not support a requested client-local option.
    /// </exception>
    Task<IJupyterExecution> ExecuteAsync(
        JupyterExecuteRequest request,
        JupyterExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ObserveOutputs)
            throw new NotSupportedException(
                "This Jupyter client implementation does not support execution output observation.");

        return ExecuteAsync(request, cancellationToken);
    }

    Task<JupyterCompleteReply> CompleteAsync(
        JupyterCompleteRequest request,
        CancellationToken cancellationToken = default);

    Task<JupyterInspectReply> InspectAsync(
        JupyterInspectRequest request,
        CancellationToken cancellationToken = default);

    Task<JupyterIsCompleteReply> IsCompleteAsync(
        JupyterIsCompleteRequest request,
        CancellationToken cancellationToken = default);

    Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default);
}