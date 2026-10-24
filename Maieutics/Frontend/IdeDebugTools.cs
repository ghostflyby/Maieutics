using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.Execution;
using Microsoft.Extensions.AI;

namespace Maieutics.Frontend;

/// <summary>
///     The debug bridge tools (ADR 0042 decision 13): run the project under the
///     user's debugger through a bounded slice — list launch configurations,
///     start (by launch name or inline configuration, optional stop-on-entry),
///     wait for a stop, inspect (stack, flattened scope variables, evaluate),
///     set breakpoints on a live session, step/continue/terminate. Sessions are
///     visible in the debug UI by design (the stable API has no headless mode;
///     the user accepted the impact). Requests ride the same announcement
///     machinery as every other ide tool; a frontend predating the debug family
///     fails typed <c>ide_method_unsupported</c> per call — debug has no local
///     implementation to degrade to, but the failure never retires the bridge.
/// </summary>
internal sealed class IdeDebugTools
{
    private static readonly JsonSerializerOptions SerializerOptions = IdeFunctionJsonContext.Default.Options;

    /// <summary>A cached empty JSON object for parameter-less requests: source-gen
    /// contexts cannot serialize anonymous types, and `SerializeToElement(new {})`
    /// would throw NotSupportedException at invocation time.</summary>
    private static readonly JsonElement EmptyObject = BuildEmptyObject();

    private static JsonElement BuildEmptyObject()
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }

        buffer.Position = 0;
        return JsonDocument.Parse(buffer).RootElement.Clone();
    }

    private readonly IdeRequestRegistry requests;
    private readonly IIdeFrontendGate gate;
    private readonly FrontendIdeSockets sockets;
    private readonly Workspace workspace;

    public IdeDebugTools(
        IdeRequestRegistry requests,
        IIdeFrontendGate gate,
        FrontendIdeSockets sockets,
        Workspace workspace)
    {
        this.requests = requests ?? throw new ArgumentNullException(nameof(requests));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.sockets = sockets ?? throw new ArgumentNullException(nameof(sockets));
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

        Functions =
        [
            Create(
                (Func<AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)ConfigurationsAsync,
                "ide_debug_configurations",
                "Lists the workspace's launch configurations (from .vscode/launch.json) that ide_debug_start " +
                "can start by name."),
            Create(
                (Func<AIFunctionArguments, string?, JsonElement?, bool?, CancellationToken, ValueTask<JsonElement>>)StartAsync,
                "ide_debug_start",
                "Starts a debug session: by 'launchName' (an entry from ide_debug_configurations) or an inline " +
                "'configuration' object ({type, request, ...} — e.g. {type:'node', request:'launch', program:...}). " +
                "'stopOnEntry' pauses at the first line. The session is visible in the debug UI; the answer " +
                "carries the sessionId every other ide_debug_* tool takes."),
            Create(
                (Func<AIFunctionArguments, string, int?, CancellationToken, ValueTask<JsonElement>>)StateAsync,
                "ide_debug_state",
                "The debug session's state: running, paused (with the stop reason and thread), or terminated. " +
                "'waitSeconds' (default 20, max 20) waits for the next transition from running — the tool to " +
                "park on after continue or a breakpoint-armed start."),
            Create(
                (Func<AIFunctionArguments, string, int?, CancellationToken, ValueTask<JsonElement>>)StackAsync,
                "ide_debug_stack",
                "The call stack of one paused debug session ('threadId' defaults to the stopped thread). " +
                "Frames carry ids for ide_debug_variables and ide_debug_evaluate."),
            Create(
                (Func<AIFunctionArguments, string, int?, long?, CancellationToken, ValueTask<JsonElement>>)VariablesAsync,
                "ide_debug_variables",
                "The variables of one paused stack frame ('frameId' from ide_debug_stack): scopes flattened " +
                "with their members. Pass a 'variablesReference' from a previous answer to expand nested objects."),
            Create(
                (Func<AIFunctionArguments, string, string, int?, CancellationToken, ValueTask<JsonElement>>)EvaluateAsync,
                "ide_debug_evaluate",
                "Evaluates one expression in a paused debug session's context ('frameId' from ide_debug_stack " +
                "scopes the evaluation)."),
            Create(
                (Func<AIFunctionArguments, string, string, JsonElement[], CancellationToken, ValueTask<JsonElement>>)BreakpointsAsync,
                "ide_debug_breakpoints",
                "Sets (or, with an empty 'points', clears) the breakpoints of one file on a live debug session. " +
                "'points' addresses each breakpoint like the read tools' selectors: {'line': N}, an anchor block " +
                "({'anchor': [context lines], 'occurrence'?} — unique match), or {'patch': a V4A/git diff} whose " +
                "context locates the position. 'uri' is the file's workspace://local address."),
            Create(
                (Func<AIFunctionArguments, string, string, int?, CancellationToken, ValueTask<JsonElement>>)ControlAsync,
                "ide_debug_control",
                "Controls a debug session: 'action' is one of continue, stepOver, stepIn, stepOut, terminate. " +
                "The answer is the session's next state (use ide_debug_state to wait for the following stop)."),
        ];
    }

    internal IReadOnlyList<AIFunction> Functions { get; }

    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private static AIFunction Create(Delegate method, string name, string description)
    {
        return AIFunctionFactory.Create(
            method,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                SerializerOptions = SerializerOptions
            });
    }

    private async ValueTask<JsonElement> SendAsync(
        Func<AIContent, CancellationToken, ValueTask>? announce,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var registration = requests.Begin();
        try
        {
            var envelope = JsonSerializer.SerializeToElement(
                new IdeRequestEnvelope(
                    new IdeRequestPayload(registration.RequestId, method, parameters)),
                IdeFunctionJsonContext.Default.IdeRequestEnvelope);
            if (announce is { } report)
            {
                // The run-stream path (ADR 0042 decision 1): sequenced and
                // replayable with the run's own events.
                var payload = JsonSerializer.Serialize(
                    envelope,
                    IdeFunctionJsonContext.Default.JsonElement);
                await report(
                    new DataContent(Encoding.UTF8.GetBytes(payload), "application/json"),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var sent = await sockets
                    .TrySendToAnnouncingAsync(
                        new FrontendEventFrame("ide.request", Data: envelope.GetProperty("ideRequest").Clone()),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!sent)
                    throw new AgentToolException(
                        "ide_unavailable",
                        "No announcing frontend accepted the ide request. Retry.");
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(RequestTimeout);
            IdeRequestAnswer answer;
            try
            {
                answer = await registration.Completion.WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AgentToolException(
                    "ide_timeout",
                    $"The IDE frontend did not answer '{method}' within {RequestTimeout.TotalSeconds:N0} seconds.");
            }

            if (answer.ErrorCode is { } code)
                throw new AgentToolException(code, answer.ErrorMessage ?? "The IDE request failed.");

            return answer.Value ??
                   throw new AgentToolException("ide_result_invalid", "The IDE frontend answered without a value.");
        }
        finally
        {
            requests.Abandon(registration.RequestId);
        }
    }

    private void EnsureConnected(AIFunctionArguments arguments)
    {
        if (arguments.Context?.TryGetValue(typeof(AgentToolContext), out var value) == true &&
            value is AgentToolContext context)
        {
            if (!gate.IsIdeFrontendConnected(context.SessionId.Value.ToString("N")))
                throw new AgentToolException(
                    "ide_unavailable",
                    "No frontend connected to this session has announced IDE capability.");
            return;
        }

        var scriptSession = ScriptInvocationContext.TryGet(arguments)?.SessionId;
        if (scriptSession is { } sessionId)
        {
            if (!gate.IsIdeFrontendConnected(sessionId))
                throw new AgentToolException(
                    "ide_unavailable",
                    "No frontend connected to this session has announced IDE capability.");
            return;
        }

        if (!gate.HasIdeFrontend)
            throw new AgentToolException(
                "ide_unavailable",
                "No frontend has announced IDE capability; the debug tools need the extension host's events socket.");
    }

    // ---------- tools ----------

    [Description("Lists the workspace's launch configurations.")]
    private async ValueTask<JsonElement> ConfigurationsAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected(arguments);
        var answer = await SendAsync(
            ResolveAnnouncer(arguments),
            "maieutics/debug/configurations",
            EmptyObject,
            cancellationToken).ConfigureAwait(false);
        return answer.ValueKind == JsonValueKind.Object ? answer : EmptyObject;
    }

    [Description("Starts a debug session by launch name or inline configuration.")]
    private async ValueTask<JsonElement> StartAsync(
        AIFunctionArguments arguments,
        [Description("A launch configuration name from ide_debug_configurations.")]
        string? launchName = null,
        [Description("An inline debug configuration object ({type, request, ...}). Mutually exclusive with launchName.")]
        JsonElement? configuration = null,
        [Description("Pause at the first executable line after start. Defaults to false.")]
        bool? stopOnEntry = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected(arguments);
        if (launchName is null == (configuration is null || configuration.Value.ValueKind == JsonValueKind.Null))
            throw new AgentToolException(
                "ide_invalid_arguments",
                "Provide exactly one of 'launchName' or 'configuration'.");

        var parameters = JsonSerializer.SerializeToElement(
            new DebugStartParams(launchName, configuration, stopOnEntry is true),
            IdeFunctionJsonContext.Default.DebugStartParams);
        return await SendAsync(
            ResolveAnnouncer(arguments),
            "maieutics/debug/start",
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    [Description("The session's state; optionally waits for the next transition from running.")]
    private async ValueTask<JsonElement> StateAsync(
        AIFunctionArguments arguments,
        [Description("The sessionId from ide_debug_start.")]
        string sessionId,
        [Description("Seconds to wait for the next transition (0-20). Defaults to 0.")]
        int? waitSeconds = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected(arguments);
        var parameters = JsonSerializer.SerializeToElement(
            new DebugStateParams(sessionId, Math.Clamp(waitSeconds ?? 0, 0, 20)),
            IdeFunctionJsonContext.Default.DebugStateParams);
        return await SendAsync(
            ResolveAnnouncer(arguments),
            "maieutics/debug/state",
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    [Description("The call stack of a paused session.")]
    private async ValueTask<JsonElement> StackAsync(
        AIFunctionArguments arguments,
        [Description("The sessionId from ide_debug_start.")]
        string sessionId,
        [Description("The thread to inspect; defaults to the stopped thread.")]
        int? threadId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected(arguments);
        var parameters = JsonSerializer.SerializeToElement(
            new DebugThreadParams(sessionId, threadId),
            IdeFunctionJsonContext.Default.DebugThreadParams);
        return await SendAsync(
            ResolveAnnouncer(arguments),
            "maieutics/debug/stack",
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    [Description("The variables of a stack frame, or the children of a variablesReference.")]
    private async ValueTask<JsonElement> VariablesAsync(
        AIFunctionArguments arguments,
        [Description("The sessionId from ide_debug_start.")]
        string sessionId,
        [Description("A frame id from ide_debug_stack — answers the frame's scopes flattened.")]
        int? frameId = null,
        [Description("A variablesReference from a previous answer — answers its direct children.")]
        long? variablesReference = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected(arguments);
        if (frameId is null && variablesReference is null)
            throw new AgentToolException(
                "ide_invalid_arguments",
                "Provide 'frameId' or 'variablesReference'.");
        if (frameId is not null && variablesReference is not null)
            throw new AgentToolException(
                "ide_invalid_arguments",
                "Provide only one of 'frameId' and 'variablesReference'.");

        var parameters = JsonSerializer.SerializeToElement(
            new DebugVariablesParams(sessionId, frameId, variablesReference),
            IdeFunctionJsonContext.Default.DebugVariablesParams);
        return await SendAsync(
            ResolveAnnouncer(arguments),
            "maieutics/debug/variables",
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Evaluates one expression in the paused session's context.")]
    private async ValueTask<JsonElement> EvaluateAsync(
        AIFunctionArguments arguments,
        [Description("The sessionId from ide_debug_start.")]
        string sessionId,
        [Description("The expression to evaluate.")]
        string expression,
        [Description("A frame id from ide_debug_stack to scope the evaluation.")]
        int? frameId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected(arguments);
        if (string.IsNullOrWhiteSpace(expression))
            throw new AgentToolException("ide_invalid_arguments", "expression is required.");

        var parameters = JsonSerializer.SerializeToElement(
            new DebugEvaluateParams(sessionId, expression, frameId),
            IdeFunctionJsonContext.Default.DebugEvaluateParams);
        return await SendAsync(
            ResolveAnnouncer(arguments),
            "maieutics/debug/evaluate",
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Sets or clears the breakpoints of one file on a live session.")]
    private async ValueTask<JsonElement> BreakpointsAsync(
        AIFunctionArguments arguments,
        [Description("The sessionId from ide_debug_start.")]
        string sessionId,
        [Description("The file's workspace://local address.")]
        string uri,
        [Description("One selector per breakpoint: {'line': N}, {'anchor': [lines], 'occurrence'?}, or {'patch': diff}; empty clears the file's breakpoints.")]
        JsonElement[] points,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected(arguments);
        if (string.IsNullOrWhiteSpace(uri))
            throw new AgentToolException("ide_invalid_arguments", "uri is required.");
        if (points.Length > 100)
            throw new AgentToolException(
                "ide_invalid_arguments",
                "At most 100 breakpoints per file.");

        // Decision 4's translation authority: the kernel maps the workspace URI to
        // the physical address before the request crosses.
        string fullPath;
        var logicalRoot = workspace.Capture().ResolveLogical(uri, allowRoot: false);
        var displayPath = logicalRoot.DisplayPath;
        if (uri.StartsWith("workspace://local/", StringComparison.Ordinal))
        {
            try
            {
                var logical = workspace.Capture().ResolveLogical(uri, allowRoot: false);
                fullPath = workspace.Capture().ToPhysicalPath(logical.Segments);
                uri = new Uri(fullPath).AbsoluteUri;
            }
            catch (WorkspaceException exception)
            {
                throw new AgentToolException(exception.Code, exception.Message);
            }
        }
        else
        {
            fullPath = workspace.Capture().ToPhysicalPath(logicalRoot.Segments);
        }

        // Selector resolution is kernel-side (the translation authority, decision
        // 13 + stage B): anchors and patches resolve against the file's current
        // lines, so breakpoints survive line drift. The whole set resolves before
        // anything crosses — setBreakpoints replaces the file's breakpoints, so a
        // partial resolution must not degrade into a partial replacement.
        IReadOnlyList<string> fileLines = Array.Empty<string>();
        if (points.Any(static point => point.ValueKind != JsonValueKind.Object ||
                                       point.TryGetProperty("line", out _)))
        {
            fileLines = await IdeSelector.ReadLinesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        var lines = new List<int>(points.Length);
        foreach (var point in points)
        {
            if (point.ValueKind != JsonValueKind.Object)
                throw new AgentToolException(
                    "ide_invalid_arguments",
                    "Each breakpoint is a selector object: {'line': N}, {'anchor': [lines]}, or {'patch': diff}.");

            if (point.TryGetProperty("line", out var lineElement))
            {
                if (lineElement.ValueKind != JsonValueKind.Number || !lineElement.TryGetInt32(out var line))
                    throw new AgentToolException("ide_invalid_arguments", "The selector 'line' must be a number.");
                if (line is < 1 or > 100_000)
                    throw new AgentToolException(
                        "ide_invalid_arguments",
                        "lines must be 1-based positions.");
                lines.Add(line);
                continue;
            }

            if (point.TryGetProperty("anchor", out var anchorElement))
            {
                if (anchorElement.ValueKind != JsonValueKind.Array)
                    throw new AgentToolException("ide_invalid_arguments", "The selector 'anchor' must be an array of context lines.");
                var occurrence = 0;
                if (point.TryGetProperty("occurrence", out var occurrenceElement))
                {
                    if (occurrenceElement.ValueKind != JsonValueKind.Number || !occurrenceElement.TryGetInt32(out var parsed))
                        throw new AgentToolException("ide_invalid_arguments", "The selector 'occurrence' must be a number.");
                    occurrence = parsed;
                }

                var anchor = ImmutableArray.CreateBuilder<string>(anchorElement.GetArrayLength());
                foreach (var entry in anchorElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String)
                        throw new AgentToolException("ide_invalid_arguments", "Every selector 'anchor' entry must be a string.");
                    anchor.Add(entry.GetString() ?? string.Empty);
                }

                lines.Add(IdeSelector.LocateAnchor(fileLines, anchor.ToImmutable(), occurrence));
                continue;
            }

            if (point.TryGetProperty("patch", out var patchElement))
            {
                if (patchElement.ValueKind != JsonValueKind.String)
                    throw new AgentToolException("ide_invalid_arguments", "The selector 'patch' must be a string holding a V4A or git diff.");
                lines.Add(IdeSelector.LocatePatch(fileLines, patchElement.GetString() ?? string.Empty, displayPath));
                continue;
            }

            throw new AgentToolException(
                "ide_invalid_arguments",
                "Each breakpoint needs a 'line', an 'anchor', or a 'patch' selector.");
        }

        var parameters = JsonSerializer.SerializeToElement(
            new DebugBreakpointsParams(sessionId, uri, [.. lines]),
            IdeFunctionJsonContext.Default.DebugBreakpointsParams);
        return await SendAsync(
            ResolveAnnouncer(arguments),
            "maieutics/debug/breakpoints",
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Steps, continues, or terminates a debug session.")]
    private async ValueTask<JsonElement> ControlAsync(
        AIFunctionArguments arguments,
        [Description("The sessionId from ide_debug_start.")]
        string sessionId,
        [Description("One of: continue, stepOver, stepIn, stepOut, terminate.")]
        string action,
        [Description("The thread to control; defaults to the stopped thread.")]
        int? threadId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected(arguments);
        if (action is not ("continue" or "stepOver" or "stepIn" or "stepOut" or "terminate"))
            throw new AgentToolException(
                "ide_invalid_arguments",
                "action must be one of: continue, stepOver, stepIn, stepOut, terminate.");

        var parameters = JsonSerializer.SerializeToElement(
            new DebugControlParams(sessionId, action, threadId),
            IdeFunctionJsonContext.Default.DebugControlParams);
        return await SendAsync(
            ResolveAnnouncer(arguments),
            "maieutics/debug/control",
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    private Func<AIContent, CancellationToken, ValueTask>? ResolveAnnouncer(AIFunctionArguments arguments)
    {
        if (arguments.Context?.TryGetValue(typeof(AgentToolContext), out var value) == true &&
            value is AgentToolContext context)
        {
            return (content, token) => context.ReportProgressAsync(content, token);
        }

        return null;
    }
}

internal sealed record DebugStartParams(
    [property: JsonPropertyName("launchName")]
    string? LaunchName,
    [property: JsonPropertyName("configuration")]
    JsonElement? Configuration,
    [property: JsonPropertyName("stopOnEntry")]
    bool StopOnEntry);

internal sealed record DebugStateParams(
    [property: JsonPropertyName("sessionId")]
    string SessionId,
    [property: JsonPropertyName("waitSeconds")]
    int WaitSeconds);

internal sealed record DebugThreadParams(
    [property: JsonPropertyName("sessionId")]
    string SessionId,
    [property: JsonPropertyName("threadId")]
    int? ThreadId);

internal sealed record DebugVariablesParams(
    [property: JsonPropertyName("sessionId")]
    string SessionId,
    [property: JsonPropertyName("frameId")]
    int? FrameId,
    [property: JsonPropertyName("variablesReference")]
    long? VariablesReference);

internal sealed record DebugEvaluateParams(
    [property: JsonPropertyName("sessionId")]
    string SessionId,
    [property: JsonPropertyName("expression")]
    string Expression,
    [property: JsonPropertyName("frameId")]
    int? FrameId);

internal sealed record DebugBreakpointsParams(
    [property: JsonPropertyName("sessionId")]
    string SessionId,
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("lines")] int[] Lines);

internal sealed record DebugControlParams(
    [property: JsonPropertyName("sessionId")]
    string SessionId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("threadId")]
    int? ThreadId);
