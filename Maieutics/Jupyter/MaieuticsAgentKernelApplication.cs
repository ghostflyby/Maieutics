using System.Reflection;
using System.Text;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Configuration;
using Maieutics.Control;
using Maieutics.DenoRepl;
using Maieutics.Execution;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;
using Maieutics.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter;

public sealed class MaieuticsAgentKernelApplication : IJupyterKernelApplication, IJupyterCompletionProvider,
    IJupyterCommSink
{
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyMetadata =
        new Dictionary<string, JsonElement>();

    private readonly MaieuticsCommandExecutor commandExecutor;
    private readonly Func<MaieuticsAgentKernelOptions> getOptions;
    private readonly ILogger<MaieuticsAgentKernelApplication> logger;
    private readonly IMaieuticsMcpController? mcpController;
    private readonly DenoReplRegistry? replRegistry;
    private readonly JupyterDenoReplPresentationRouter? replPresentationRouter;
    private readonly ReplControlHost? replControlHost;
    private readonly MaieuticsAgentSessionManager? sessionManager;
    private readonly IMaieuticsRuntimeConfiguration? runtimeConfiguration;

    private readonly IAgentSession session;
    private readonly MaieuticsStatusProvider? statusProvider;
    private readonly TimeProvider timeProvider;
    private readonly Workspace? workspace;

    public MaieuticsAgentKernelApplication(
        IAgentSession session,
        MaieuticsAgentKernelOptions? options = null,
        ILogger<MaieuticsAgentKernelApplication>? logger = null,
        TimeProvider? timeProvider = null)
        : this(session, () => options ?? new MaieuticsAgentKernelOptions(), null, logger, timeProvider)
    {
    }

    internal MaieuticsAgentKernelApplication(
        IAgentSession session,
        Func<MaieuticsAgentKernelOptions> getOptions,
        IMaieuticsRuntimeConfiguration? runtimeConfiguration,
        ILogger<MaieuticsAgentKernelApplication>? logger = null,
        TimeProvider? timeProvider = null,
        Workspace? workspace = null,
        JupyterDenoReplPresentationRouter? replPresentationRouter = null,
        IMaieuticsMcpController? mcpController = null,
        MaieuticsStatusProvider? statusProvider = null,
        ReplControlHost? replControlHost = null,
        DenoReplRegistry? replRegistry = null,
        MaieuticsAgentSessionManager? sessionManager = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.getOptions = getOptions ?? throw new ArgumentNullException(nameof(getOptions));
        this.runtimeConfiguration = runtimeConfiguration;
        this.mcpController = mcpController;
        this.workspace = workspace;
        this.replPresentationRouter = replPresentationRouter;
        this.statusProvider = statusProvider;
        this.replControlHost = replControlHost;
        this.replRegistry = replRegistry;
        this.sessionManager = sessionManager;
        if (runtimeConfiguration is null) this.getOptions().Validate();

        this.logger = logger ?? NullLogger<MaieuticsAgentKernelApplication>.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        commandExecutor = new MaieuticsCommandExecutor(
            sessionManager,
            runtimeConfiguration,
            workspace,
            statusProvider,
            mcpController);
    }

    public ValueTask<JupyterCompletionResult> CompleteAsync(
        JupyterCompleteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profiles = runtimeConfiguration?.GetModelProfileSelection().Profiles ?? [];
        var automaticProfiles = runtimeConfiguration?.GetCachedAutomaticModelProfiles() ?? [];
        var sourceIds = runtimeConfiguration?.GetModelSourceIds() ?? [];
        var completion = MaieuticsCommandLanguage.Complete(
            request.Code,
            JupyterCursorPosition.ToUtf16Index(request.Code, request.CursorPosition),
            profiles,
            automaticProfiles,
            sourceIds);
        return ValueTask.FromResult(new JupyterCompletionResult(
            completion.Matches,
            JupyterCursorPosition.FromUtf16Index(request.Code, completion.TokenStart),
            JupyterCursorPosition.FromUtf16Index(request.Code, completion.TokenEnd)));
    }

    public async ValueTask OnCommOpenAsync(
        JupyterCommMessage message,
        JupyterExecutionContext? context,
        CancellationToken cancellationToken)
    {
        await RelayCommAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask OnCommMsgAsync(
        JupyterCommMessage message,
        JupyterExecutionContext? context,
        CancellationToken cancellationToken)
    {
        await RelayCommAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask OnCommCloseAsync(
        JupyterCommMessage message,
        JupyterExecutionContext? context,
        CancellationToken cancellationToken)
    {
        await RelayCommAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task RelayCommAsync(JupyterCommMessage message, CancellationToken cancellationToken)
    {
        if (replRegistry is null || replControlHost is null)
            return;

        var session = await replRegistry.EnsureDefaultAsync(this.session.Id, cancellationToken)
            .ConfigureAwait(false);
        // The control channel is protocol-neutral: drop the Jupyter wire identity here.
        await replControlHost
            .PushCommMessageAsync(
                session.SessionId,
                new ReplCommMessage(
                    (ReplCommKind)message.Kind,
                    message.CommId,
                    message.TargetName,
                    message.Data,
                    message.Metadata,
                    message.Buffers),
                cancellationToken)
            .ConfigureAwait(false);
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
        if (string.IsNullOrWhiteSpace(request.Code)) return JupyterExecuteResult.Ok;

        if (IsMaieuticsCommand(request.Code))
        {
            await ExecuteCommandAsync(context, request.Code, cancellationToken).ConfigureAwait(false);
            return JupyterExecuteResult.Ok;
        }

        try
        {
            var options = getOptions();
            options.Validate();
            if (runtimeConfiguration is not null &&
                runtimeConfiguration.GetModelProfileSelection().Profiles.Count == 0)
                throw Create(
                    "AgentConfigurationError",
                    "No model profile is configured. Configure a model before submitting an Agent turn.");

            await RenderTurnAsync(context, request.Code, options, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask ExecuteCommandAsync(
        JupyterExecutionContext context,
        string code,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string output;
        try
        {
            output = await commandExecutor.ExecuteAsync(code, cancellationToken).ConfigureAwait(false);
        }
        catch (MaieuticsCommandException exception)
        {
            throw Create("MaieuticsCommandError", exception.Message);
        }

        await context.DisplayAsync(
            MimeBundle.FromMarkdown(output),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static bool IsMaieuticsCommand(string code)
    {
        return MaieuticsCommandLanguage.IsCommandCell(code);
    }

    private async Task RenderTurnAsync(
        JupyterExecutionContext context,
        string input,
        MaieuticsAgentKernelOptions options,
        CancellationToken cancellationToken)
    {
        var response = new StringBuilder();
        var flushTimestamp = timeProvider.GetTimestamp();
        JupyterDisplayId? displayId = null;
        var flushedLength = 0;

        await using var presentationScope = replPresentationRouter?.Attach(session.Id, context);
        var presentationSink = presentationScope?.Sink;
        await using var run = await session
            .StartTurnAsync(AgentTurn.FromText(input), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await foreach (var agentEvent in run.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
                switch (agentEvent)
                {
                    case AgentTextDelta { Text.Length: > 0 } delta:
                        response.Append(delta.Text);
                        if (displayId is null)
                        {
                            displayId = JupyterDisplayId.Create();
                            if (presentationSink is null)
                                await context.DisplayTrackedAsync(
                                    MimeBundle.FromMarkdown(response.ToString()),
                                    displayId,
                                    cancellationToken: cancellationToken).ConfigureAwait(false);
                            else
                            {
                                var trackedId = new ReplDisplayId(displayId.Value.Value);
                                await presentationSink.DisplayTrackedAsync(
                                    ReplDisplayBundle.FromMarkdown(response.ToString()),
                                    trackedId,
                                    EmptyMetadata,
                                    cancellationToken).ConfigureAwait(false);
                            }

                            flushedLength = response.Length;
                            flushTimestamp = timeProvider.GetTimestamp();
                        }
                        else if (response.Length - flushedLength >= options.FlushCharacters ||
                                 timeProvider.GetElapsedTime(flushTimestamp) >= options.FlushInterval)
                        {
                            await FlushAsync(context, presentationSink, displayId.Value, response, cancellationToken)
                                .ConfigureAwait(false);
                            flushedLength = response.Length;
                            flushTimestamp = timeProvider.GetTimestamp();
                        }

                        break;
                    case AgentMessageCompleted:
                        if (displayId is not null && flushedLength != response.Length)
                        {
                            await FlushAsync(context, presentationSink, displayId.Value, response, cancellationToken)
                                .ConfigureAwait(false);
                            flushedLength = response.Length;
                        }

                        break;
                    case AgentToolStarted started:
                        if (displayId is not null && flushedLength != response.Length)
                        {
                            await FlushAsync(
                                context,
                                presentationSink,
                                displayId.Value,
                                response,
                                cancellationToken).ConfigureAwait(false);
                            flushedLength = response.Length;
                            flushTimestamp = timeProvider.GetTimestamp();
                        }

                        replPresentationRouter?.OpenCall(session.Id, started.CallId);
                        break;
                }

            // Once the event writer closes normally, the run has crossed its commit boundary.
            var result = await run.Completion.ConfigureAwait(false);
            if (result.Truncated)
            {
                response.AppendLine();
                response.Append(
                    "> ⚠️ The agent turn was truncated after exhausting its model iteration budget. " +
                    "Partial progress is preserved; run a new cell to continue.");
                if (displayId is null)
                {
                    displayId = JupyterDisplayId.Create();
                    if (presentationSink is null)
                        await context.DisplayTrackedAsync(
                            MimeBundle.FromMarkdown(response.ToString()),
                            displayId,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    else
                    {
                        var trackedId = new ReplDisplayId(displayId.Value.Value);
                        await presentationSink.DisplayTrackedAsync(
                            ReplDisplayBundle.FromMarkdown(response.ToString()),
                            trackedId,
                            EmptyMetadata,
                            cancellationToken).ConfigureAwait(false);
                    }

                    flushedLength = response.Length;
                    timeProvider.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await run.CancelAsync(CancellationToken.None).ConfigureAwait(false);
            await ObserveCompletionAsync(run).ConfigureAwait(false);
            await TryFlushPartialAsync(context, presentationSink, displayId, response, flushedLength)
                .ConfigureAwait(false);
            throw;
        }
        catch
        {
            await TryFlushPartialAsync(context, presentationSink, displayId, response, flushedLength)
                .ConfigureAwait(false);
            throw;
        }

        if (displayId is not null && flushedLength != response.Length)
            await FlushAsync(context, presentationSink, displayId.Value, response, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task TryFlushPartialAsync(
        JupyterExecutionContext context,
        IDenoReplPresentationSink? presentationSink,
        JupyterDisplayId? displayId,
        StringBuilder response,
        int flushedLength)
    {
        if (displayId is null || flushedLength == response.Length) return;

        try
        {
            await FlushAsync(context, presentationSink, displayId.Value, response, CancellationToken.None)
                .ConfigureAwait(false);
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
        IDenoReplPresentationSink? presentationSink,
        JupyterDisplayId displayId,
        StringBuilder response,
        CancellationToken cancellationToken)
    {
        return presentationSink?.UpdateDisplayAsync(
            new ReplDisplayId(displayId.Value),
            ReplDisplayBundle.FromMarkdown(response.ToString()),
            EmptyMetadata,
            cancellationToken) ?? context.UpdateDisplayAsync(
            displayId,
            MimeBundle.FromMarkdown(response.ToString()),
            cancellationToken: cancellationToken);
    }

    private static JupyterKernelExecutionException ToKernelException(AgentException exception)
    {
        return exception switch
        {
            AgentProviderException => Create("AgentProviderError",
                "The model provider failed while producing a response."),
            AgentInputLimitExceededException => Create("AgentInputTooLarge", exception.Message),
            AgentResponseLimitExceededException => Create("AgentResponseTooLarge", exception.Message),
            AgentToolLimitExceededException => Create("AgentToolLimitExceeded", exception.Message),
            AgentToolArgumentsException => Create(
                "AgentToolArgumentsError",
                "The model supplied an invalid tool request."),
            AgentToolInvocationException => Create(
                "AgentToolError",
                "An Agent tool failed while processing the request."),
            AgentTurnDurationExceededException => Create("AgentTurnDurationExceeded", exception.Message),
            AgentModelIterationLimitExceededException => Create("AgentModelIterationLimit", exception.Message),
            AgentModelCapabilityException => Create("AgentModelCapabilityError", exception.Message),
            AgentContentCompatibilityException => Create(
                "AgentUnsupportedResponse",
                "The model provider returned a response that this kernel does not support."),
            AgentUnsupportedResponseException => Create(
                "AgentUnsupportedResponse",
                "The model provider returned a response that this kernel does not support."),
            AgentTurnInProgressException => Create("AgentBusy", exception.Message),
            _ => Create("AgentError", "The agent turn failed.")
        };
    }

    private static JupyterKernelExecutionException Create(string name, string message)
    {
        return new JupyterKernelExecutionException(name, message, []);
    }
}
