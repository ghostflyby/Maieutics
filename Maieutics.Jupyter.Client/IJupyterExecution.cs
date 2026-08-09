using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Client;

/// <summary>
///     Represents one locally tracked Jupyter execution. Disposing an incomplete execution abandons
///     local routing and cancels its output and completion tasks; it never interrupts the kernel.
/// </summary>
public interface IJupyterExecution : IAsyncDisposable
{
    /// <summary>Gets the message ID of the execute request.</summary>
    JupyterMessageId RequestId { get; }

    /// <summary>Gets the ordered, single-consumer output stream for this execution.</summary>
    IAsyncEnumerable<JupyterOutput> Outputs { get; }

    /// <summary>
    ///     Gets the protocol completion, or a canceled task when the caller abandons this execution.
    /// </summary>
    Task<JupyterExecutionResult> Completion { get; }

    /// <summary>Replies to an input request emitted by this active execution.</summary>
    /// <param name="request">The input request to answer.</param>
    /// <param name="value">The input value.</param>
    /// <param name="cancellationToken">Cancels waiting to send the reply.</param>
    /// <returns>A task that completes when the reply is sent.</returns>
    Task ReplyInputAsync(
        JupyterInputRequest request,
        string value,
        CancellationToken cancellationToken = default);
}
