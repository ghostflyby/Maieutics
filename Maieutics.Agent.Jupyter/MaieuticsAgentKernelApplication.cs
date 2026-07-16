using System.Reflection;
using System.Text;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Agent.Jupyter;

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

        try
        {
            await foreach (var agentEvent in session.ExecuteTurnAsync(new AgentTurn(input), cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                switch (agentEvent)
                {
                    case AgentTextDelta delta when delta.Text.Length > 0:
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
                    case AgentTurnCompleted:
                        if (displayId is not null && flushedLength != response.Length)
                        {
                            await FlushAsync(context, displayId.Value, response, cancellationToken)
                                .ConfigureAwait(false);
                            flushedLength = response.Length;
                        }

                        break;
                }
            }
        }
        catch
        {
            if (displayId is not null && flushedLength != response.Length)
            {
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

            throw;
        }

        if (displayId is not null && flushedLength != response.Length)
        {
            await FlushAsync(context, displayId.Value, response, cancellationToken).ConfigureAwait(false);
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
        AgentUnsupportedResponseException => Create(
            "AgentUnsupportedResponse",
            "The model provider returned a response that this kernel does not support."),
        AgentTurnInProgressException => Create("AgentBusy", exception.Message),
        _ => Create("AgentError", "The agent turn failed.")
    };

    private static JupyterKernelExecutionException Create(string name, string message) =>
        new(name, message, []);
}