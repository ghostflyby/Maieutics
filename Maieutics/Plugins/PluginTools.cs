using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Maieutics.Agent;
using Maieutics.Control;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Maieutics.Plugins;

/// <summary>Classification hints a tool declaration may carry. These are hints, not
/// enforcement: no authorization decision reads them, and the kernel applies MCP's
/// pessimistic defaults for absent members.</summary>
/// <param name="Title">A human-readable display name; may contain spaces.</param>
/// <param name="ReadOnly">The tool changes no state.</param>
/// <param name="Destructive">The tool may destroy data or resources.</param>
/// <param name="Idempotent">Repeating the call has no additional effect.</param>
/// <param name="OpenWorld">The tool reaches an open set of external entities.</param>
internal sealed record PluginToolAnnotations(
    string? Title,
    bool ReadOnly,
    bool Destructive,
    bool Idempotent,
    bool OpenWorld)
{
    /// <summary>The defaults applied when a declaration carries no hints: MCP's own
    /// pessimistic direction — possibly destructive, non-idempotent, open-world.</summary>
    internal static readonly PluginToolAnnotations Default = new(null, false, true, false, true);
}

/// <summary>One described plugin tool: the provider-visible declaration plus the export the
/// kernel routes calls to. Immutable for the lifetime of a run profile snapshot.</summary>
internal sealed record PluginToolDefinition(
    string PluginId,
    string ExportName,
    string Name,
    string Description,
    JsonElement InputSchema,
    TimeSpan Timeout,
    PluginToolAnnotations Annotations)
{
    /// <summary>The default per-call budget when the declaration omits one.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
}

/// <summary>The phase-tagged request a tool export receives. Describe and call share one
/// extension point so a tool is one registration identity (plugin, export, point).</summary>
internal sealed record PluginToolRequest(
    string Phase,
    string? CallId = null,
    string? SessionId = null,
    JsonElement? Arguments = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = ReplControlLimits.MaximumJsonDepth)]
[JsonSerializable(typeof(PluginToolRequest))]
internal sealed partial class PluginToolJsonContext : JsonSerializerContext;

/// <summary>Builds and interprets the tool-extension wire values. The declaration is
/// validated here, once, so a malformed export can never reach the Agent tool registry.</summary>
internal static partial class PluginToolProtocol
{
    /// <summary>The largest input schema (serialized bytes) a declaration may carry.</summary>
    internal const int MaximumSchemaBytes = 64 * 1024;

    /// <summary>The longest accepted description.</summary>
    internal const int MaximumDescriptionLength = 4096;

    /// <summary>The longest accepted annotation title.</summary>
    internal const int MaximumTitleLength = 128;

    private static readonly TimeSpan MinimumTimeout = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The provider-visible tool-name grammar. Anchored with <c>\z</c>, not
    /// <c>$</c>: in .NET, <c>$</c> also matches before a trailing newline, so a name like
    /// "echo\n" would pass here and then be rejected by <c>AgentSession</c>'s own validator —
    /// throwing out of <c>StartTurnAsync</c> for every later turn instead of hiding the tool.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNamePattern();

    /// <summary>Builds the describe request.</summary>
    internal static JsonElement CreateDescribeRequest()
    {
        return JsonSerializer.SerializeToElement(
            new PluginToolRequest("describe"),
            PluginToolJsonContext.Default.PluginToolRequest);
    }

    /// <summary>Builds one call request.</summary>
    internal static JsonElement CreateCallRequest(
        string callId,
        string sessionId,
        JsonElement arguments)
    {
        return JsonSerializer.SerializeToElement(
            new PluginToolRequest("call", callId, sessionId, arguments),
            PluginToolJsonContext.Default.PluginToolRequest);
    }

    /// <summary>Interprets a describe response into a definition. Returns false with a
    /// diagnostic when the declaration violates the tool contract.</summary>
    internal static bool TryReadDefinition(
        string pluginId,
        string exportName,
        JsonElement? value,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out PluginToolDefinition? definition,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)]
        out string? error)
    {
        definition = null;
        if (value is not { ValueKind: JsonValueKind.Object } payload)
        {
            error = "The tool declaration is not a JSON object.";
            return false;
        }

        if (!payload.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            nameElement.GetString() is not { } name ||
            !ToolNamePattern().IsMatch(name))
        {
            error = "The tool name must contain 1-64 ASCII letters, digits, underscores, or hyphens.";
            return false;
        }

        if (!payload.TryGetProperty("description", out var descriptionElement) ||
            descriptionElement.ValueKind != JsonValueKind.String ||
            descriptionElement.GetString()?.Trim() is not { Length: > 0 } description)
        {
            error = $"The tool '{name}' must declare a non-empty description.";
            return false;
        }

        if (description.Length > MaximumDescriptionLength)
        {
            error =
                $"The tool '{name}' description exceeds {MaximumDescriptionLength} characters.";
            return false;
        }

        if (!payload.TryGetProperty("inputSchema", out var schema) ||
            schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("type", out var schemaType) ||
            schemaType.ValueKind != JsonValueKind.String ||
            !string.Equals(schemaType.GetString(), "object", StringComparison.Ordinal))
        {
            error = $"The tool '{name}' input schema must be a JSON object schema.";
            return false;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(schema.GetRawText()) > MaximumSchemaBytes)
        {
            error = $"The tool '{name}' input schema exceeds {MaximumSchemaBytes} bytes.";
            return false;
        }

        var timeout = PluginToolDefinition.DefaultTimeout;
        if (payload.TryGetProperty("timeoutMs", out var timeoutElement) &&
            timeoutElement.ValueKind != JsonValueKind.Null)
        {
            if (timeoutElement.ValueKind != JsonValueKind.Number ||
                !timeoutElement.TryGetInt32(out var timeoutMs) ||
                timeoutMs < MinimumTimeout.TotalMilliseconds ||
                timeoutMs > MaximumTimeout.TotalMilliseconds)
            {
                error =
                    $"The tool '{name}' timeoutMs must be an integer between " +
                    $"{(int)MinimumTimeout.TotalMilliseconds} and {(int)MaximumTimeout.TotalMilliseconds}.";
                return false;
            }

            timeout = TimeSpan.FromMilliseconds(timeoutMs);
        }

        if (!TryReadAnnotations(name, payload, out var annotations, out error)) return false;

        definition = new PluginToolDefinition(
            pluginId,
            exportName,
            name,
            description,
            schema.Clone(),
            timeout,
            annotations);
        error = null;
        return true;
    }

    /// <summary>Interprets the optional annotation block. An absent block takes the
    /// pessimistic defaults; a present block is validated strictly, because a hint the
    /// kernel cannot read is a declaration bug rather than a hint to ignore.</summary>
    private static bool TryReadAnnotations(
        string name,
        JsonElement payload,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out PluginToolAnnotations? annotations,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)]
        out string? error)
    {
        annotations = PluginToolAnnotations.Default;
        error = null;
        if (!payload.TryGetProperty("annotations", out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"The tool '{name}' annotations must be a JSON object.";
            return false;
        }

        string? title = null;
        if (element.TryGetProperty("title", out var titleElement) &&
            titleElement.ValueKind != JsonValueKind.Null)
        {
            if (titleElement.ValueKind != JsonValueKind.String ||
                titleElement.GetString()?.Trim() is not { Length: > 0 } declaredTitle)
            {
                error = $"The tool '{name}' annotation title must be a non-empty string.";
                return false;
            }

            if (declaredTitle.Length > MaximumTitleLength)
            {
                error = $"The tool '{name}' annotation title exceeds {MaximumTitleLength} characters.";
                return false;
            }

            title = declaredTitle;
        }

        if (!TryReadHint(name, element, "readOnly", PluginToolAnnotations.Default.ReadOnly, out var readOnly, out error) ||
            !TryReadHint(name, element, "destructive", PluginToolAnnotations.Default.Destructive, out var destructive, out error) ||
            !TryReadHint(name, element, "idempotent", PluginToolAnnotations.Default.Idempotent, out var idempotent, out error) ||
            !TryReadHint(name, element, "openWorld", PluginToolAnnotations.Default.OpenWorld, out var openWorld, out error))
        {
            annotations = null;
            return false;
        }

        annotations = new PluginToolAnnotations(title, readOnly, destructive, idempotent, openWorld);
        return true;
    }

    /// <summary>Reads one boolean hint, falling back to the pessimistic default. A member
    /// present but not a boolean fails the declaration instead of silently keeping the
    /// default — a mistyped hint would otherwise be read as an assertion.</summary>
    private static bool TryReadHint(
        string name,
        JsonElement annotations,
        string member,
        bool fallback,
        out bool value,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)]
        out string? error)
    {
        value = fallback;
        error = null;
        if (!annotations.TryGetProperty(member, out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = $"The tool '{name}' annotation '{member}' must be a boolean.";
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    /// <summary>Interprets one call response into the value the Agent wraps as its success
    /// envelope, or into a recoverable failure the caller raises as
    /// <see cref="AgentToolException"/>. The Agent owns envelope construction: returning a
    /// pre-built envelope here would double-wrap it. A malformed response is a
    /// <c>tool_failed</c> failure — a plugin bug must not abort the turn.</summary>
    internal static bool TryReadCallResponse(
        JsonElement? value,
        out JsonElement? result,
        out string code,
        out string message)
    {
        result = null;
        code = "tool_failed";
        message = "The plugin tool returned a malformed response.";
        if (value is not { ValueKind: JsonValueKind.Object } payload)
            return false;

        if (!payload.TryGetProperty("outcome", out var outcome) ||
            outcome.ValueKind != JsonValueKind.String)
            return false;

        if (string.Equals(outcome.GetString(), "ok", StringComparison.Ordinal))
        {
            result = payload.TryGetProperty("value", out var valueElement)
                ? valueElement.Clone()
                : null;
            code = string.Empty;
            message = string.Empty;
            return true;
        }

        if (string.Equals(outcome.GetString(), "error", StringComparison.Ordinal))
        {
            // A declared error always carries a code by construction; a malformed one falls
            // back to the same defaults the envelope reader used.
            code = payload.TryGetProperty("code", out var codeElement) &&
                   codeElement.ValueKind == JsonValueKind.String &&
                   codeElement.GetString() is { Length: > 0 } declared
                ? declared
                : "tool_failed";
            message = payload.TryGetProperty("message", out var messageElement) &&
                      messageElement.ValueKind == JsonValueKind.String &&
                      messageElement.GetString() is { Length: > 0 } declaredMessage
                ? declaredMessage
                : "The plugin tool failed.";
            return false;
        }

        message = "The plugin tool returned an unknown outcome.";
        return false;
    }
}

/// <summary>Bridges one plugin tool into the Agent's <see cref="AIFunction"/> registry. The
/// function is an immutable snapshot of the definition; calls route to the export by identity,
/// so a plugin reload that keeps the export simply serves the new implementation.</summary>
// ReSharper disable once InconsistentNaming
internal sealed class PluginToolAIFunction(
    PluginToolDefinition definition,
    Func<string, string, string, JsonElement?, CancellationToken, TimeSpan?, string?, Task<ExtensionCallOutcome>> invoke)
    : AIFunction
{
    /// <summary>The additional-properties key carrying the classification hints. Provider
    /// adapters render only <see cref="AIFunction.Name"/>, <see cref="AIFunction.Description"/>,
    /// and <see cref="AIFunction.JsonSchema"/>, so the hints reach approval and presentation
    /// surfaces without widening the provider wire.</summary>
    internal const string AnnotationsKey = "maieutics.pluginTool.annotations";

    private readonly AdditionalPropertiesDictionary additionalProperties = CreateProperties(definition);

    public override string Name => definition.Name;

    public override string Description => definition.Description;

    public override JsonElement JsonSchema => definition.InputSchema;

    public override IReadOnlyDictionary<string, object?> AdditionalProperties => additionalProperties;

    private static AdditionalPropertiesDictionary CreateProperties(PluginToolDefinition definition)
    {
        var annotations = definition.Annotations;
        return new AdditionalPropertiesDictionary
        {
            // The Agent runtime's own narrow shape: Maieutics.Agent cannot reference this
            // executable's plugin types, so the bridge publishes the provider-neutral record.
            [AgentToolClassification.PropertiesKey] = new AgentToolClassification(
                annotations.ReadOnly,
                annotations.Destructive),
            [AnnotationsKey] = annotations,
            ["pluginId"] = definition.PluginId,
            ["exportName"] = definition.ExportName,
        };
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // Plugin tools only enter the Agent tool list, so a missing context means the
        // function was invoked outside a run (a direct caller). Cancellation is bound to
        // the call identity, so refusing is honest rather than silently uncancellable.
        if (arguments.Context?.TryGetValue(typeof(AgentToolContext), out var value) != true ||
            value is not AgentToolContext context)
        {
            throw new AgentToolException(
                "tool_context_missing",
                $"The plugin tool '{definition.Name}' requires an Agent tool call.");
        }

        var callId = context.CallId.Value.ToString("D");
        var request = PluginToolProtocol.CreateCallRequest(
            callId,
            context.SessionId.Value.ToString("D"),
            ToolJson.CreateArguments(arguments.ToDictionary(static pair => pair.Key, static pair => pair.Value)));

        ExtensionCallOutcome outcome;
        try
        {
            outcome = await invoke(
                definition.PluginId,
                definition.ExportName,
                ReplExtensionPointName.Tool,
                request,
                cancellationToken,
                definition.Timeout,
                callId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per-call budget expired (the linked source the manager applies): a
            // recoverable failure, not a turn cancellation.
            throw new AgentToolException(
                "tool_timeout",
                $"The plugin tool '{definition.Name}' did not complete within " +
                $"{definition.Timeout.TotalSeconds:0.#} seconds.");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The plugin host is a separate process that can be disconnected, restarting, or
            // gone. Every such failure is a tool-scoped problem the model can route around —
            // never a turn failure. A typed recoverable failure is the same outcome the
            // manager already returns for a detached connection; the exception paths are the
            // ones that would otherwise escape as AgentToolInvocationException and roll the
            // turn back.
            throw new AgentToolException(
                "plugin_unavailable",
                $"The plugin tool '{definition.Name}' could not run: {exception.Message}");
        }

        if (outcome.IsError)
            throw new AgentToolException(
                string.IsNullOrWhiteSpace(outcome.Code) ? "tool_failed" : outcome.Code,
                string.IsNullOrWhiteSpace(outcome.Message) ? "The plugin tool failed." : outcome.Message);

        // The Agent wraps the returned value as {"status":"ok","value":...}; returning an
        // envelope here would double-wrap it.
        if (!PluginToolProtocol.TryReadCallResponse(outcome.Value, out var result, out var code, out var message))
            throw new AgentToolException(code, message);

        return result;
    }
}

/// <summary>Describes plugin tool exports as the host registry changes and republishes an
/// immutable declaration snapshot for run-profile acquisition. Describe is sticky last-good:
/// a failed pass keeps the previous definition and retries on the next registry frame.
/// The coordinator owns one single-flight drain; passes never hold the coordinator's gate
/// across a host invoke.</summary>
internal sealed class PluginToolCoordinator : IAsyncDisposable
{
    private readonly Func<string, string, string, JsonElement?, CancellationToken, TimeSpan?, string?, Task<ExtensionCallOutcome>> invoke;
    private readonly Func<IReadOnlySet<string>> reservedToolNames;
    private readonly ILogger logger;
    private readonly Lock gate = new();

    private readonly Dictionary<(string PluginId, string ExportName), PluginToolDefinition> definitions =
        new(new KeyComparer());

    private readonly HashSet<(string PluginId, string ExportName)> pending = new(new KeyComparer());
    private readonly HashSet<(string PluginId, string ExportName)> retryPending = new(new KeyComparer());
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>The token of the currently valid describe generation. A reset cancels it so
    /// passes still awaiting the old host unwind instead of publishing into the new one.
    /// Cancelled sources are retained until disposal: a pass may hold the token across an
    /// await, and disposing it early would turn a benign cancellation into an
    /// <see cref="ObjectDisposedException"/> at the next linked-source creation.</summary>
    private CancellationTokenSource passes = new();

    private readonly List<CancellationTokenSource> retiredPasses = [];

    private TaskCompletionSource idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? flight;
    private int disposed;

    internal PluginToolCoordinator(
        Func<string, string, string, JsonElement?, CancellationToken, TimeSpan?, string?, Task<ExtensionCallOutcome>> invoke,
        Func<IReadOnlySet<string>> reservedToolNames,
        ILogger logger)
    {
        this.invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        this.reservedToolNames = reservedToolNames ?? throw new ArgumentNullException(nameof(reservedToolNames));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Completes when no describe pass is running or queued. Tests await this to
    /// observe a registry frame's reconciliation deterministically.</summary>
    internal Task FlightIdle
    {
        get
        {
            lock (gate) return flight is null ? Task.CompletedTask : idle.Task;
        }
    }

    /// <summary>Reconciles the tool exports in one registry snapshot: removed exports drop
    /// their definitions, never-described exports and plugin-forced exports are queued for a
    /// describe pass. Called outside the host manager's gate.</summary>
    internal void OnRegistryFrame(
        IReadOnlyList<PluginRegistration> registrations,
        IReadOnlySet<string> forcedPlugins)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(forcedPlugins);
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;

            var current = new HashSet<(string, string)>(new KeyComparer());
            foreach (var registration in registrations)
            {
                if (!string.Equals(registration.ExtensionPoint, ReplExtensionPointName.Tool, StringComparison.Ordinal))
                    continue;
                current.Add((registration.PluginId, registration.ExportName));
            }

            foreach (var key in definitions.Keys.Where(key => !current.Contains(key)).ToArray())
                definitions.Remove(key);
            pending.RemoveWhere(key => !current.Contains(key));
            retryPending.RemoveWhere(key => !current.Contains(key));

            foreach (var key in current)
            {
                if (!definitions.ContainsKey(key) ||
                    retryPending.Contains(key) ||
                    forcedPlugins.Contains(key.Item1))
                {
                    pending.Add(key);
                }
            }

            if (pending.Count > 0 && flight is null) StartFlightLocked();
        }
    }

    /// <summary>Drops every declaration: the host generation that produced them is gone and
    /// the next registry frame describes the new workers. Any describe pass still running
    /// against the old host is abandoned through its generation token.</summary>
    internal void Reset()
    {
        CancellationTokenSource abandoned;
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            definitions.Clear();
            pending.Clear();
            retryPending.Clear();
            abandoned = passes;
            passes = new CancellationTokenSource();
            retiredPasses.Add(abandoned);
        }

        // Cancelled outside the gate so a linked host invoke observes the token immediately.
        abandoned.Cancel();
    }

    /// <summary>The current declaration snapshot as provider-visible functions, ordered by
    /// plugin and export so a run's tool list is deterministic.</summary>
    internal IReadOnlyList<AIFunction> SnapshotTools()
    {
        lock (gate)
        {
            return definitions.Values
                .OrderBy(static definition => definition.PluginId, StringComparer.Ordinal)
                .ThenBy(static definition => definition.ExportName, StringComparer.Ordinal)
                .Select(definition => (AIFunction)new PluginToolAIFunction(definition, invoke))
                .ToArray();
        }
    }

    private void StartFlightLocked()
    {
        idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        flight = Task.Run(DrainAsync, CancellationToken.None);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            (string PluginId, string ExportName)[] batch;
            CancellationToken passToken;
            lock (gate)
            {
                if (pending.Count == 0 || Volatile.Read(ref disposed) != 0)
                {
                    flight = null;
                    idle.TrySetResult();
                    return;
                }

                batch = pending.ToArray();
                pending.Clear();
                passToken = passes.Token;
            }

            try
            {
                foreach (var (pluginId, exportName) in batch)
                {
                    passToken.ThrowIfCancellationRequested();
                    await DescribeAsync(pluginId, exportName, passToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (
                passToken.IsCancellationRequested || lifetime.IsCancellationRequested)
            {
                // The host generation behind these passes is gone; Reset already cleared the
                // declarations, and any queued work belongs to the new generation's token.
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The plugin tool describe pass failed unexpectedly.");
            }
        }
    }

    private async Task DescribeAsync(string pluginId, string exportName, CancellationToken passToken)
    {
        ExtensionCallOutcome outcome;
        try
        {
            outcome = await invoke(
                pluginId,
                exportName,
                ReplExtensionPointName.Tool,
                PluginToolProtocol.CreateDescribeRequest(),
                passToken,
                null,
                null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (passToken.IsCancellationRequested || lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Describing the tool export '{PluginId}/{ExportName}' failed.",
                pluginId,
                exportName);
            MarkRetry(pluginId, exportName);
            return;
        }

        if (outcome.IsError)
        {
            logger.LogWarning(
                "Describing the tool export '{PluginId}/{ExportName}' failed: {Code}: {Message}",
                pluginId,
                exportName,
                outcome.Code,
                outcome.Message);
            MarkRetry(pluginId, exportName);
            return;
        }

        if (!PluginToolProtocol.TryReadDefinition(
                pluginId,
                exportName,
                outcome.Value,
                out var definition,
                out var error))
        {
            logger.LogWarning(
                "The tool export '{PluginId}/{ExportName}' declared an invalid tool: {Error}",
                pluginId,
                exportName,
                error);
            MarkRetry(pluginId, exportName);
            return;
        }

        var reserved = reservedToolNames();
        if (reserved.Contains(definition.Name))
        {
            logger.LogWarning(
                "The tool export '{PluginId}/{ExportName}' declared the reserved tool name '{Name}'; the tool is hidden.",
                pluginId,
                exportName,
                definition.Name);
            MarkRetry(pluginId, exportName);
            return;
        }

        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 || passToken.IsCancellationRequested) return;
            definitions[(pluginId, exportName)] = definition;
            retryPending.Remove((pluginId, exportName));
        }
    }

    private void MarkRetry(string pluginId, string exportName)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            retryPending.Add((pluginId, exportName));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Task? running;
        CancellationTokenSource current;
        lock (gate)
        {
            pending.Clear();
            running = flight;
            current = passes;
            idle.TrySetResult();
        }

        // Resolve the running pass first so disposing never waits on a describe that the
        // cancelled generation cannot complete.
        await current.CancelAsync().ConfigureAwait(false);
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (running is not null)
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The plugin tool coordinator failed during disposal.");
            }
        }

        current.Dispose();
        foreach (var retired in retiredPasses) retired.Dispose();
        retiredPasses.Clear();
        lifetime.Dispose();
    }

    private sealed class KeyComparer : IEqualityComparer<(string PluginId, string ExportName)>
    {
        public bool Equals(
            (string PluginId, string ExportName) left,
            (string PluginId, string ExportName) right) =>
            string.Equals(left.PluginId, right.PluginId, StringComparison.Ordinal) &&
            string.Equals(left.ExportName, right.ExportName, StringComparison.Ordinal);

        public int GetHashCode((string PluginId, string ExportName) key) =>
            HashCode.Combine(
                key.PluginId.GetHashCode(StringComparison.Ordinal),
                key.ExportName.GetHashCode(StringComparison.Ordinal));
    }
}
