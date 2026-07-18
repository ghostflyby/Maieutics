using System.Reflection;
using System.Text;
using Maieutics.Agent;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter;

public sealed class MaieuticsAgentKernelApplication : IJupyterKernelApplication
{
    private readonly IAgentSession session;
    private readonly MaieuticsAgentKernelOptions options;
    private readonly ILogger<MaieuticsAgentKernelApplication> logger;
    private readonly TimeProvider timeProvider;

    public MaieuticsAgentKernelApplication(
        IAgentSession session,
        MaieuticsAgentKernelOptions? options = null,
        ILogger<MaieuticsAgentKernelApplication>? logger = null,
        TimeProvider? timeProvider = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.options = options ?? new MaieuticsAgentKernelOptions();
        this.options.Validate();
        this.logger = logger ?? NullLogger<MaieuticsAgentKernelApplication>.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public JupyterKernelInfo KernelInfo { get; } = new(
        "5.5",
        "maieutics",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
        new JupyterLanguageInfo("markdown", "1.0", "text/markdown", ".md"),
        "Maieutics notebook-native agent kernel");

    public async ValueTask<JupyterExecuteResult> ExecuteAsync(
        JupyterExecutionContext context,
        JupyterExecuteRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return JupyterExecuteResult.Ok;
        }

        try
        {
            await RenderTurnAsync(context, request.Code, cancellationToken).ConfigureAwait(false);
            return JupyterExecuteResult.Ok;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentException exception)
        {
            logger.LogError(exception, "Agent turn failed for Jupyter request {RequestId}.", context.RequestId);
            throw ToKernelException(exception);
        }
    }

    private async Task RenderTurnAsync(
        JupyterExecutionContext context,
        string input,
        CancellationToken cancellationToken)
    {
        var response = new StringBuilder();
        var flushTimestamp = timeProvider.GetTimestamp();
        JupyterDisplayId? displayId = null;
        var flushedLength = 0;

        await using var run = await session
            .StartTurnAsync(AgentTurn.FromText(input), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await foreach (var agentEvent in run.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                switch (agentEvent)
                {
                    case AgentTextDelta { Text.Length: > 0 } delta:
                        response.Append(delta.Text);
                        if (displayId is null)
                        {
                            displayId = await context.DisplayTrackedAsync(
                                MimeBundle.FromMarkdown(response.ToString()),
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                            flushedLength = response.Length;
                            flushTimestamp = timeProvider.GetTimestamp();
                        }
                        else if (response.Length - flushedLength >= options.FlushCharacters ||
                                 timeProvider.GetElapsedTime(flushTimestamp) >= options.FlushInterval)
                        {
                            await FlushAsync(context, displayId.Value, response, cancellationToken)
                                .ConfigureAwait(false);
                            flushedLength = response.Length;
                            flushTimestamp = timeProvider.GetTimestamp();
                        }

                        break;
                    case AgentMessageCompleted:
                        if (displayId is not null && flushedLength != response.Length)
                        {
                            await FlushAsync(context, displayId.Value, response, cancellationToken)
                                .ConfigureAwait(false);
                            flushedLength = response.Length;
                        }

                        break;
                }
            }

            // Once the event writer closes normally, the run has crossed its commit boundary.
            await run.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await run.CancelAsync(CancellationToken.None).ConfigureAwait(false);
            await ObserveCompletionAsync(run).ConfigureAwait(false);
            await TryFlushPartialAsync(context, displayId, response, flushedLength).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await TryFlushPartialAsync(context, displayId, response, flushedLength).ConfigureAwait(false);
            throw;
        }

        if (displayId is not null && flushedLength != response.Length)
        {
            await FlushAsync(context, displayId.Value, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryFlushPartialAsync(
        JupyterExecutionContext context,
        JupyterDisplayId? displayId,
        StringBuilder response,
        int flushedLength)
    {
        if (displayId is null || flushedLength == response.Length)
        {
            return;
        }

        try
        {
            await FlushAsync(context, displayId.Value, response, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not flush partial Agent output for Jupyter request {RequestId}.",
                context.RequestId);
        }
    }

    private static async Task ObserveCompletionAsync(IAgentRun run)
    {
        try
        {
            await run.Completion.ConfigureAwait(false);
        }
        catch
        {
            // The caller is already propagating the interrupt; this only observes the terminal task.
        }
    }

    private static ValueTask FlushAsync(
        JupyterExecutionContext context,
        JupyterDisplayId displayId,
        StringBuilder response,
        CancellationToken cancellationToken) =>
        context.UpdateDisplayAsync(
            displayId,
            MimeBundle.FromMarkdown(response.ToString()),
            cancellationToken: cancellationToken);

    private static JupyterKernelExecutionException ToKernelException(AgentException exception) => exception switch
    {
        AgentProviderException => Create("AgentProviderError", "The model provider failed while producing a response."),
        AgentInputLimitExceededException => Create("AgentInputTooLarge", exception.Message),
        AgentResponseLimitExceededException => Create("AgentResponseTooLarge", exception.Message),
        AgentToolLimitExceededException => Create("AgentToolLimitExceeded", exception.Message),
        AgentToolArgumentsException => Create(
            "AgentToolArgumentsError",
            "The model supplied an invalid tool request."),
        AgentToolInvocationException => Create(
            "AgentToolError",
            "An Agent tool failed while processing the request."),
        AgentModelIterationLimitExceededException => Create("AgentModelIterationLimit", exception.Message),
        AgentUnsupportedResponseException => Create(
            "AgentUnsupportedResponse",
            "The model provider returned a response that this kernel does not support."),
        AgentTurnInProgressException => Create("AgentBusy", exception.Message),
        _ => Create("AgentError", "The agent turn failed.")
    };

    private static JupyterKernelExecutionException Create(string name, string message) =>
        new(name, message, []);
}