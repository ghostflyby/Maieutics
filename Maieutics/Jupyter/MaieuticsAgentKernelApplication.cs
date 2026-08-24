using System.Reflection;
using System.Text;
using System.Text.Json;
using Maieutics.Agent;
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

    private readonly Func<MaieuticsAgentKernelOptions> getOptions;
    private readonly ILogger<MaieuticsAgentKernelApplication> logger;
    private readonly IMaieuticsMcpController? mcpController;
    private readonly DenoReplRegistry? replRegistry;
    private readonly JupyterDenoReplPresentationRouter? replPresentationRouter;
    private readonly ReplControlHost? replControlHost;
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
        DenoReplRegistry? replRegistry = null)
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
        if (runtimeConfiguration is null) this.getOptions().Validate();

        this.logger = logger ?? NullLogger<MaieuticsAgentKernelApplication>.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<JupyterCompletionResult> CompleteAsync(
        JupyterCompleteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profiles = runtimeConfiguration?.GetModelProfileSelection().Profiles ?? [];
        var automaticProfiles = runtimeConfiguration?.GetCachedAutomaticModelProfiles() ?? [];
        var sourceIds = runtimeConfiguration?.GetModelSourceIds() ?? [];
        return ValueTask.FromResult(MaieuticsCommandLanguage.Complete(
            request,
            profiles,
            automaticProfiles,
            sourceIds));
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
        await replControlHost
            .PushCommMessageAsync(session.SessionId, message, cancellationToken)
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
        var originalArguments = code.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arguments = MaieuticsCommandLanguage.NormalizeCommandArguments(originalArguments);
        if (arguments is null) throw Create("MaieuticsCommandError", "Unknown Maieutics command.");

        try
        {
            string output;
            if (string.Equals(arguments[1], MaieuticsCommandLanguage.Workspace, StringComparison.OrdinalIgnoreCase))
            {
                var pathTokenCount = originalArguments[0].Equals(
                    MaieuticsCommandLanguage.LegacyRoot,
                    StringComparison.OrdinalIgnoreCase)
                    ? 3
                    : 2;
                output = ExecuteWorkspaceCommand(code, arguments, pathTokenCount);
            }
            else if (string.Equals(arguments[1], MaieuticsCommandLanguage.Model, StringComparison.OrdinalIgnoreCase))
            {
                output = await ExecuteModelCommandAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(arguments[1], MaieuticsCommandLanguage.Mcp, StringComparison.OrdinalIgnoreCase))
            {
                output = ExecuteMcpCommand(arguments);
            }
            else if (string.Equals(arguments[1], MaieuticsCommandLanguage.Status, StringComparison.OrdinalIgnoreCase))
            {
                output = ExecuteStatusCommand(arguments);
            }
            else
            {
                throw new ArgumentException("Unknown Maieutics command.");
            }

            await context.DisplayAsync(
                MimeBundle.FromMarkdown(output),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                              UnauthorizedAccessException or NotSupportedException)
        {
            throw Create("MaieuticsCommandError", exception.Message);
        }
    }

    private async ValueTask<string> ExecuteModelCommandAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        if (runtimeConfiguration is null)
            throw new ArgumentException("Model profile commands are not available in this host.");

        if (arguments.Length >= 3 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.Available, StringComparison.OrdinalIgnoreCase))
        {
            var refresh = arguments.Contains(MaieuticsCommandLanguage.RefreshFlag,
                StringComparer.OrdinalIgnoreCase);
            var sourceId = arguments.FirstOrDefault(arg =>
                !string.Equals(arg, MaieuticsCommandLanguage.RefreshFlag, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(arg, MaieuticsCommandLanguage.LegacyRoot, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(arg, MaieuticsCommandLanguage.Model, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(arg, MaieuticsCommandLanguage.Available, StringComparison.OrdinalIgnoreCase));
            var groups = await runtimeConfiguration.GetDiscoveredModelsAsync(
                sourceId, refresh, cancellationToken).ConfigureAwait(false);
            return RenderAvailable(groups, runtimeConfiguration.GetModelProfileSelection());
        }

        if (arguments.Length == 2 ||
            (arguments.Length == 3 && string.Equals(
                arguments[2],
                MaieuticsCommandLanguage.Current,
                StringComparison.OrdinalIgnoreCase)))
            return RenderCurrent(runtimeConfiguration.GetModelProfileSelection());

        if (arguments.Length == 3 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.List, StringComparison.OrdinalIgnoreCase))
            return RenderList(runtimeConfiguration.GetModelProfileSelection());

        if (arguments.Length == 4 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.Use, StringComparison.OrdinalIgnoreCase))
        {
            await runtimeConfiguration.SelectModelProfileAsync(arguments[3], cancellationToken).ConfigureAwait(false);
            return RenderCurrent(runtimeConfiguration.GetModelProfileSelection());
        }

        if (arguments.Length == 3 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.Reset, StringComparison.OrdinalIgnoreCase))
        {
            runtimeConfiguration.ResetModelProfile();
            return RenderCurrent(runtimeConfiguration.GetModelProfileSelection());
        }

        throw new ArgumentException("Unknown model command or invalid arguments.");
    }

    private string ExecuteMcpCommand(string[] arguments)
    {
        if (mcpController is null) throw new ArgumentException("MCP commands are not available in this host.");

        if (arguments.Length == 3 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.List, StringComparison.OrdinalIgnoreCase))
            return RenderMcpList(mcpController.GetMcpServers());

        throw new ArgumentException("Unknown MCP command or invalid arguments.");
    }

    private string ExecuteWorkspaceCommand(string code, string[] arguments, int pathTokenCount)
    {
        if (workspace is null) throw new ArgumentException("Workspace commands are not available in this host.");

        WorkspaceSnapshot selection;
        if (arguments.Length == 2 ||
            (arguments.Length == 3 && string.Equals(
                arguments[2],
                MaieuticsCommandLanguage.Current,
                StringComparison.OrdinalIgnoreCase)))
        {
            selection = workspace.Capture();
        }
        else if (arguments.Length == 3 &&
                 string.Equals(arguments[2], MaieuticsCommandLanguage.Reset, StringComparison.OrdinalIgnoreCase))
        {
            selection = workspace.Reset();
        }
        else if (arguments.Length >= 4 &&
                 string.Equals(arguments[2], MaieuticsCommandLanguage.Use, StringComparison.OrdinalIgnoreCase))
        {
            var path = GetRemainderAfterTokens(code, pathTokenCount);
            if (path.Length == 0 || path.IndexOfAny(['\r', '\n']) >= 0)
                throw new ArgumentException("A single-line workspace path is required.");

            selection = workspace.Use(path);
        }
        else
        {
            throw new ArgumentException("Unknown workspace command or invalid arguments.");
        }

        return RenderWorkspace(selection);
    }

    private string ExecuteStatusCommand(string[] arguments)
    {
        if (arguments.Length != 2) throw new ArgumentException("The status command does not accept arguments.");

        if (statusProvider is null)
            throw new ArgumentException("Status is not available in this host.");

        return MaieuticsStatusRenderer.Render(statusProvider.Capture());
    }

    private static string GetRemainderAfterTokens(string code, int tokenCount)
    {
        var index = 0;
        for (var token = 0; token < tokenCount; token++)
        {
            while (index < code.Length && char.IsWhiteSpace(code[index])) index++;

            while (index < code.Length && !char.IsWhiteSpace(code[index])) index++;
        }

        while (index < code.Length && char.IsWhiteSpace(code[index])) index++;

        return code[index..].TrimEnd();
    }

    private static bool IsMaieuticsCommand(string code)
    {
        return MaieuticsCommandLanguage.IsCommandCell(code);
    }

    private static string RenderCurrent(MaieuticsModelProfileSelection selection)
    {
        if (selection.Profiles.Count == 0) return "### Current model\n\nNo model profile is configured.";

        var profile = selection.Profiles.Single(profile => profile.IsSelected);
        var selectionSource = profile.IsAutomatic
            ? "automatic session override"
            : selection.HasSessionOverride
                ? "session override"
                : "configured default";
        return $"""
                ### Current model

                - Profile: {MarkdownText.CodeSpan(profile.Id)} ({selectionSource})
                - Source: {MarkdownText.CodeSpan(profile.SourceId)}
                - Provider: {MarkdownText.CodeSpan(profile.Provider)}
                - Model: {MarkdownText.CodeSpan(profile.Model)}
                """;
    }

    private static string RenderWorkspace(WorkspaceSnapshot selection)
    {
        var selectionSource = selection.HasSessionOverride ? "session override" : "startup root";
        return $"""
                ### Current workspace

                - Root: {MarkdownText.CodeSpan(selection.RootPath)} ({selectionSource})
                """;
    }

    private static string RenderMcpList(IReadOnlyList<MaieuticsMcpServerInfo> servers)
    {
        if (servers.Count == 0) return "### MCP servers\n\nNo MCP servers are enabled.";

        var output = new StringBuilder("### MCP servers\n\n");
        foreach (var server in servers)
        {
            output.Append("- ")
                .Append(MarkdownText.CodeSpan(server.Id))
                .Append(" — transport ")
                .Append(MarkdownText.CodeSpan(server.Transport))
                .Append(", state ")
                .Append(MarkdownText.CodeSpan(server.State.ToString()));
            if (server is { State: MaieuticsMcpServerState.Reconnecting, NextReconnectDelay: { } reconnectDelay })
                output.Append(", next reconnect in ")
                    .Append(MarkdownText.CodeSpan(reconnectDelay.ToString()));

            output.AppendLine();
            foreach (var tool in server.Tools)
            {
                output.Append("  - ")
                    .Append(MarkdownText.CodeSpan(tool.RemoteName))
                    .Append(" → ")
                    .Append(MarkdownText.CodeSpan(tool.ExposedName));
                if (!tool.Available) output.Append(" (unavailable)");

                output.AppendLine();
            }
        }

        return output.ToString();
    }

    private static string RenderList(MaieuticsModelProfileSelection selection)
    {
        if (selection.Profiles.Count == 0) return "### Model profiles\n\nNo model profiles are configured.";

        var output = new StringBuilder("### Model profiles\n\n");
        foreach (var profile in selection.Profiles)
        {
            var markers = new List<string>(2);
            if (profile.IsSelected) markers.Add("selected");

            if (profile.IsDefault) markers.Add("default");

            if (profile.IsAutomatic) markers.Add("automatic");

            var suffix = markers.Count == 0 ? string.Empty : $" ({string.Join(", ", markers)})";
            output.Append("- ")
                .Append(MarkdownText.CodeSpan(profile.Id))
                .Append(": ")
                .Append(MarkdownText.CodeSpan(profile.Provider))
                .Append(" / ")
                .Append(MarkdownText.CodeSpan(profile.Model))
                .Append(", source ")
                .Append(MarkdownText.CodeSpan(profile.SourceId))
                .AppendLine(suffix);
        }

        return output.ToString();
    }

    private static string RenderAvailable(
        IReadOnlyList<DiscoveredModelGroup> groups,
        MaieuticsModelProfileSelection profiles)
    {
        if (groups.Count == 0)
        {
            var message = profiles.Profiles.Count == 0
                ? "No model sources are configured."
                : "No model sources support automatic discovery.";
            return $"### Available models\n\n{message}";
        }

        var output = new StringBuilder("### Available models\n\n");
        var configuredModelIds = profiles.Profiles
            .Where(static profile => !profile.IsAutomatic)
            .Select(static p => (p.SourceId, p.Model))
            .ToHashSet(TupleComparer.Instance);

        foreach (var group in groups)
        {
            output.Append(MarkdownText.CodeSpan(group.Provider))
                .Append(" (source: ")
                .Append(MarkdownText.CodeSpan(group.SourceId))
                .Append(')');

            if (group.Failure is not null)
            {
                output.AppendLine()
                    .Append("  ❌ ")
                    .Append(MarkdownText.PlainText(
                        "The provider could not return available models. Check the Maieutics logs for details."))
                    .Append(" (")
                    .Append(MarkdownText.CodeSpan(ModelDiscoveryFailureCode(group.Failure.Value)))
                    .AppendLine(")");
                continue;
            }

            if (group.Models.Count == 0)
            {
                output.AppendLine()
                    .AppendLine("  _No models returned._");
                continue;
            }

            output.AppendLine();
            foreach (var model in group.Models)
            {
                output.Append("- ").Append(MarkdownText.CodeSpan(model.Id));
                if (model.ContextWindow.HasValue)
                    output.Append(" (").Append(model.ContextWindow.Value).Append(" context)");

                var configured = configuredModelIds
                    .Contains((group.SourceId, model.Id));
                if (!configured)
                {
                    var selector = MaieuticsAutomaticProfileSelector.Format(group.SourceId, model.Id);
                    output.Append(" — automatic profile ")
                        .Append(MarkdownText.CodeSpan(selector));
                }

                output.AppendLine();
            }
        }

        var missingFromApi = profiles.Profiles
            .Where(static profile => !profile.IsAutomatic)
            .Where(profile => !groups.Any(g =>
                string.Equals(g.SourceId, profile.SourceId, StringComparison.OrdinalIgnoreCase) &&
                g.Failure is null &&
                g.Models.Any(m => string.Equals(m.Id, profile.Model, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
        if (missingFromApi.Length > 0)
        {
            output.AppendLine();
            output.AppendLine("> ⚠️ The following configured models were not found in API results:");
            foreach (var profile in missingFromApi)
                output.Append("- ").Append(MarkdownText.CodeSpan(profile.Id))
                    .Append(": ").Append(MarkdownText.CodeSpan(profile.Model))
                    .Append(" (source ").Append(MarkdownText.CodeSpan(profile.SourceId)).AppendLine(")");
        }

        return output.ToString();
    }

    private static string ModelDiscoveryFailureCode(ModelDiscoveryFailureKind failure)
    {
        return failure switch
        {
            ModelDiscoveryFailureKind.ProviderError => "provider_error",
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown discovery failure.")
        };
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
                                await presentationSink.DisplayTrackedAsync(
                                    MimeBundle.FromMarkdown(response.ToString()),
                                    displayId.Value,
                                    EmptyMetadata,
                                    cancellationToken).ConfigureAwait(false);

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
                        await presentationSink.DisplayTrackedAsync(
                            MimeBundle.FromMarkdown(response.ToString()),
                            displayId.Value,
                            EmptyMetadata,
                            cancellationToken).ConfigureAwait(false);

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
            displayId,
            MimeBundle.FromMarkdown(response.ToString()),
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

    private sealed class TupleComparer : IEqualityComparer<(string SourceId, string Model)>
    {
        internal static readonly TupleComparer Instance = new();

        public bool Equals((string SourceId, string Model) x, (string SourceId, string Model) y)
        {
            return string.Equals(x.SourceId, y.SourceId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.Model, y.Model, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string SourceId, string Model) obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Model));
        }
    }
}
