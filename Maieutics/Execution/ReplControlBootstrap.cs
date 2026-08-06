using Maieutics.Control;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Execution;

/// <summary>
///     Binds the `maieutics` REPL namespace by importing the materialized client module and verifying
///     the control channel health probe, with bounded retries. The global binding is idempotent so a
///     retry never re-declares an existing name.
/// </summary>
internal static class ReplControlBootstrap
{
    private const int MaxAttempts = 3;

    private const string ProbeCell = "typeof maieutics";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private static string BindCell =>
        $"globalThis.maieutics ??= await import(Deno.env.get(\"{ReplControlEnvironment.ClientModule}\"));" +
        " await globalThis.maieutics.health();";

    public static async Task RunAsync(
        IJupyterClient client,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0) await Task.Delay(RetryDelay, timeoutSource.Token).ConfigureAwait(false);

            try
            {
                await BindAsync(client, timeoutSource.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
            }
        }

        throw new InvalidOperationException(
            $"The 'maieutics' REPL namespace could not be bound: {lastFailure?.Message}");
    }

    private static async Task BindAsync(IJupyterClient client, CancellationToken cancellationToken)
    {
        var bind = await client.ExecuteAsync(
            new JupyterExecuteRequest(BindCell, AllowStdin: true),
            cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(bind, cancellationToken).ConfigureAwait(false);

        var probe = await client.ExecuteAsync(
            new JupyterExecuteRequest(ProbeCell, AllowStdin: true),
            cancellationToken).ConfigureAwait(false);
        var outputs = await ReadOutputsAsync(probe, cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(probe, cancellationToken).ConfigureAwait(false);
        var bound = outputs
            .OfType<JupyterExecuteResultOutput>()
            .Select(output => output.Data.Data["text/plain"].GetString())
            .Any(value => value is not null && value.Contains("object"));
        if (!bound) throw new InvalidOperationException("The 'maieutics' namespace was not visible after binding.");
    }

    private static async Task RequireSuccessAsync(
        IJupyterExecution execution,
        CancellationToken cancellationToken)
    {
        var reply = await execution.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (reply.Reply.Status != "ok")
            throw new InvalidOperationException(
                $"The REPL control bootstrap cell failed with status '{reply.Reply.Status}'.");
    }

    private static async Task<IReadOnlyList<JupyterOutput>> ReadOutputsAsync(
        IJupyterExecution execution,
        CancellationToken cancellationToken)
    {
        var outputs = new List<JupyterOutput>();
        await foreach (var output in execution.Outputs.WithCancellation(cancellationToken)) outputs.Add(output);

        return outputs;
    }
}