using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.Execution;
using Microsoft.Extensions.AI;

namespace Maieutics.Frontend;

/// <summary>The read-group IDE methods (ADR 0042 decision 3, v1): the fixed allowlist the
/// kernel sends and the escape-hatch tool accepts. Write-group methods are catalogued by
/// the ADR but join only with stage C; anything outside this set fails
/// <c>ide_method_unsupported</c> without a round trip.</summary>
internal static class IdeMethodCatalog
{
    /// <summary>The IDE capability version this build sends requests for; it is the
    /// announcement version by definition, so the two constants cannot drift.</summary>
    internal const int Version = FrontendIdeSockets.Version;

    internal static readonly IReadOnlySet<string> ReadMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "maieutics/problems",
        "textDocument/hover",
        "textDocument/definition",
        "textDocument/typeDefinition",
        "textDocument/declaration",
        "textDocument/implementation",
        "textDocument/references",
        "textDocument/documentHighlight",
        "textDocument/documentSymbol",
        "textDocument/signatureHelp",
        "textDocument/foldingRange",
        "textDocument/inlayHint",
        "textDocument/selectionRange",
        "textDocument/semanticTokens/full",
        "workspace/symbol",
        "callHierarchy/prepare",
        "callHierarchy/incomingCalls",
        "callHierarchy/outgoingCalls",
        "typeHierarchy/prepare",
        "typeHierarchy/supertypes",
        "typeHierarchy/subtypes"
    };
}

/// <summary>
///     The agent-facing IDE tools (ADR 0042 stage A): diagnostics, hover, navigation,
///     symbols, and a read-only escape hatch, all riding the requesting run's event stream
///     as an <c>application/json</c> progress item that carries the
///     <c>ide.request</c> payload — sequenced and replayable with the run's own events —
///     and awaiting the answer through <see cref="IdeRequestRegistry" />. The agent-side
///     URI domain is exclusively <c>workspace://local</c> and the position convention is
///     1-based lines and UTF-16 columns; this class is the kernel-side translation point
///     for both, and every returned URI is re-checked against the workspace before it
///     reaches the model (untranslatable entries pass through unmapped, where no read tool
///     can address them).
/// </summary>
internal sealed class IdeFunctions
{
    private const int MaxTranslationDepth = 32;
    private const string WorkspaceUriPrefix = "workspace://local/";
    private const string FileUriPrefix = "file://";
    private const int MaxAggregateFiles = 20;

    private static readonly JsonSerializerOptions SerializerOptions = IdeFunctionJsonContext.Default.Options;

    private readonly Workspace workspace;
    private readonly IdeRequestRegistry requests;
    private readonly IIdeFrontendGate gate;

    public IdeFunctions(Workspace workspace, IdeRequestRegistry requests, IIdeFrontendGate gate)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.requests = requests ?? throw new ArgumentNullException(nameof(requests));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        Functions =
        [
            CreateFunction(
                (Func<AIFunctionArguments, string?, string?, CancellationToken, ValueTask<JsonElement>>)DiagnosticsAsync,
                "ide_diagnostics",
                "Reads the IDE's Problems list for the workspace or one file. Without 'uri' returns an aggregate " +
                "view (severity totals, top files); with 'uri' returns that file's diagnostics. " +
                "'minSeverity' filters: error, warning, information, or hint. Positions in ranges are 1-based " +
                "lines and columns. An empty result means nothing has been reported by any provider — it is not " +
                "proof the code is clean (language services may not have analyzed it yet); the answer names the " +
                "analysis state so you can tell the two apart. Requires the ide-capable frontend."),
            CreateFunction(
                (Func<string, JsonElement, AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)HoverAsync,
                "ide_hover",
                "Returns the language service's hover information (type, documentation) at one position. " +
                "'uri' is a workspace://local file URI; 'select' is the coordinate selector " +
                "{line, column} with 1-based lines and columns (UTF-16 units). " +
                "Read the file region with read_text first so the position is meaningful. " +
                "Anchor and patch selectors arrive in a later stage."),
            CreateFunction(
                (Func<string, string, JsonElement, AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)NavigateAsync,
                "ide_navigate",
                "Navigates the language service from one position. 'kind' is one of: definition, type_definition, " +
                "declaration, implementation, references, highlights. 'uri' is a workspace://local file URI and " +
                "'select' the coordinate selector {line, column} (1-based lines and columns, UTF-16 units); " +
                "results are workspace URIs at 1-based positions. Prefer this over guessing where a symbol lives. " +
                "Anchor and patch selectors arrive in a later stage."),
            CreateFunction(
                (Func<string, AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)DocumentSymbolsAsync,
                "ide_document_symbols",
                "Returns the language service's symbol outline of one file: hierarchical symbols with their " +
                "1-based ranges, or flat symbol information, exactly as the provider shapes it. " +
                "'uri' is a workspace://local file URI. Prefer this over reading whole files to find structure."),
            CreateFunction(
                (Func<string, AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)WorkspaceSymbolsAsync,
                "ide_workspace_symbols",
                "Searches the language service's workspace-wide symbol index by fuzzy 'query' and returns symbol " +
                "information with 1-based workspace URIs and positions. Prefer this over text search when looking " +
                "for a type, function, or member by name."),
            CreateFunction(
                (Func<string, JsonElement, AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)RequestAsync,
                "ide_request",
                "Calls an IDE language-service request that the dedicated tools do not cover, from the fixed read " +
                "catalog: textDocument/signatureHelp, textDocument/foldingRange, textDocument/inlayHint, " +
                "textDocument/selectionRange, textDocument/semanticTokens/full, callHierarchy/* (prepare, " +
                "incomingCalls, outgoingCalls), typeHierarchy/* (prepare, supertypes, subtypes). 'method' is the " +
                "catalog name; 'parameters' is the LSP-shaped params object with workspace://local URIs. Position " +
                "and range objects ({line, character}) use this suite's 1-based convention and are converted; " +
                "foldingRange's bare startLine/endLine numbers are the provider's own 0-based lines — pass those " +
                "as returned. Write methods and unknown methods are rejected without a round trip.")
        ];
    }

    /// <summary>The IDE tool functions; assembled into a run profile only while the gate
    /// reports a live announcing socket, but reserved by name even when hidden.</summary>
    internal IReadOnlyList<AIFunction> Functions { get; }

    /// <summary>How long one IDE request waits for the frontend's answer before the typed
    /// timeout. Internal-settable so integration tests shrink the wait.</summary>
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    [Description("Reads the IDE Problems list for the workspace or one document.")]
    private async ValueTask<JsonElement> DiagnosticsAsync(
        AIFunctionArguments arguments,
        [Description("Optional workspace://local file URI to drill into; omit for the workspace aggregate view.")]
        string? uri = null,
        [Description("Optional minimum severity to include: error, warning, information, or hint. Omit for all.")]
        string? minSeverity = null,
        CancellationToken cancellationToken = default)
    {
        var context = AgentToolContext.GetRequired(arguments);
        EnsureConnected(context);
        var threshold = ParseSeverity(minSeverity);
        JsonElement parameters;
        string? requestedUri = null;
        if (uri is not null)
        {
            var resolved = ResolveDocumentUri(uri);
            requestedUri = resolved.WorkspaceUri;
            parameters = BuildObject(("uri", resolved.FileUri));
        }
        else
        {
            parameters = BuildObject();
        }

        var answer = await SendAsync(context, "maieutics/problems", parameters, cancellationToken).ConfigureAwait(false);
        return requestedUri is null
            ? BuildWorkspaceDiagnostics(answer, threshold)
            : BuildDocumentDiagnostics(answer, requestedUri, threshold);
    }

    [Description("Returns the language service's hover information at one position.")]
    private async ValueTask<JsonElement> HoverAsync(
        [Description("A workspace://local file URI, as returned by other tools.")] string uri,
        [Description("Coordinate selector: {\"line\": 42, \"column\": 7} (1-based lines and columns).")]
        JsonElement select,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var context = AgentToolContext.GetRequired(arguments);
        EnsureConnected(context);
        var resolved = ResolveDocumentUri(uri);
        var (line, character) = ParsePosition(select);
        var parameters = BuildPositionParams(resolved.FileUri, line, character);
        var answer = await SendAsync(context, "textDocument/hover", parameters, cancellationToken)
            .ConfigureAwait(false);
        return TranslateIncoming(answer);
    }

    [Description("Navigates the language service from one position.")]
    private async ValueTask<JsonElement> NavigateAsync(
        [Description("One of: definition, type_definition, declaration, implementation, references, highlights.")]
        string kind,
        [Description("A workspace://local file URI, as returned by other tools.")] string uri,
        [Description("Coordinate selector: {\"line\": 42, \"column\": 7} (1-based lines and columns).")]
        JsonElement select,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var context = AgentToolContext.GetRequired(arguments);
        EnsureConnected(context);
        var method = kind switch
        {
            "definition" => "textDocument/definition",
            "type_definition" => "textDocument/typeDefinition",
            "declaration" => "textDocument/declaration",
            "implementation" => "textDocument/implementation",
            "references" => "textDocument/references",
            "highlights" => "textDocument/documentHighlight",
            _ => throw new AgentToolException(
                "ide_kind_unknown",
                $"Unknown navigate kind '{kind}'. Use definition, type_definition, declaration, implementation, references, or highlights."),
        };
        var resolved = ResolveDocumentUri(uri);
        var (line, character) = ParsePosition(select);
        var parameters = method == "textDocument/references"
            ? BuildPositionParams(
                resolved.FileUri,
                line,
                character,
                [("context", JsonDocument.Parse("""{"includeDeclaration":true}""").RootElement)])
            : BuildPositionParams(resolved.FileUri, line, character);
        var answer = await SendAsync(context, method, parameters, cancellationToken).ConfigureAwait(false);
        return TranslateIncoming(answer);
    }

    [Description("Returns the language service's symbol outline of one document.")]
    private async ValueTask<JsonElement> DocumentSymbolsAsync(
        [Description("A workspace://local file URI, as returned by other tools.")] string uri,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var context = AgentToolContext.GetRequired(arguments);
        EnsureConnected(context);
        var resolved = ResolveDocumentUri(uri);
        var parameters = BuildObject(("uri", resolved.FileUri));
        var answer = await SendAsync(context, "textDocument/documentSymbol", parameters, cancellationToken)
            .ConfigureAwait(false);
        return TranslateIncoming(answer);
    }

    [Description("Searches the language service's workspace-wide symbol index by query.")]
    private async ValueTask<JsonElement> WorkspaceSymbolsAsync(
        [Description("The symbol query (fuzzy, as the language service interprets it).")]
        string query,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var context = AgentToolContext.GetRequired(arguments);
        EnsureConnected(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var parameters = BuildObject(("query", query));
        var answer = await SendAsync(context, "workspace/symbol", parameters, cancellationToken)
            .ConfigureAwait(false);
        return TranslateIncoming(answer);
    }

    [Description("Calls one read-catalog IDE request the dedicated tools do not cover.")]
    private async ValueTask<JsonElement> RequestAsync(
        [Description("A method from the read catalog, e.g. textDocument/signatureHelp or callHierarchy/prepare.")]
        string method,
        [Description("The LSP-shaped params object; workspace://local URIs and 1-based positions are converted.")]
        JsonElement parameters,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var context = AgentToolContext.GetRequired(arguments);
        EnsureConnected(context);
        if (!IdeMethodCatalog.ReadMethods.Contains(method))
            throw new AgentToolException(
                "ide_method_unsupported",
                $"The method '{method}' is not in the read catalog; write-group methods join in a later stage.");
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new AgentToolException("ide_invalid_arguments", "The parameters must be a JSON object.");

        var answer = await SendAsync(context, method, TranslateOutgoing(parameters), cancellationToken)
            .ConfigureAwait(false);
        return TranslateIncoming(answer);
    }

    /// <summary>Announces one IDE request on the run's event stream (the sequenced,
    /// replayable <c>ide.request</c> payload — ADR 0042 decision 1) and awaits its answer
    /// through the registry, bounded by the request timeout and the caller's cancellation.</summary>
    private async ValueTask<JsonElement> SendAsync(
        AgentToolContext context,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var registration = requests.Begin();
        try
        {
            var payload = JsonSerializer.Serialize(
                new IdeRequestEnvelope(
                    new IdeRequestPayload(registration.RequestId, method, parameters)),
                IdeFunctionJsonContext.Default.IdeRequestEnvelope);
            await context
                .ReportProgressAsync(
                    new DataContent(Encoding.UTF8.GetBytes(payload), "application/json"),
                    cancellationToken)
                .ConfigureAwait(false);

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
                    $"The IDE frontend did not answer '{method}' within {RequestTimeout.TotalSeconds:N0} seconds " +
                    "(the language service may still be loading). Retry once before giving up.");
            }

            if (answer.ErrorCode is { } code)
                throw new AgentToolException(code, answer.ErrorMessage ?? "The IDE request failed.");

            return answer.Value
                ?? throw new AgentToolException("ide_result_invalid", "The IDE frontend answered without a value.");
        }
        finally
        {
            // Every exit — answer, timeout, cancellation — drops the entry, so a late or
            // replayed answer always renders as a typed 404 at the answer endpoint.
            requests.Abandon(registration.RequestId);
        }
    }

    private void EnsureConnected(AgentToolContext context)
    {
        if (!gate.IsIdeFrontendConnected(context.SessionId.Value.ToString("N")))
            throw new AgentToolException(
                "ide_unavailable",
                "No frontend connected to this session has announced IDE capability; the IDE tools need the " +
                "extension host's events socket (ide=1). Reconnect the frontend or work from the workspace tools.");
    }

    private ResolvedDocument ResolveDocumentUri(string uri)
    {
        try
        {
            var resolved = workspace.Capture().Resolve(uri);
            if (resolved.IsDirectory)
                throw new WorkspaceException("ide_invalid_arguments", "The uri must address a file, not a directory.");
            return new ResolvedDocument(resolved.FullPath, ToFileUri(resolved.FullPath), resolved.Uri);
        }
        catch (WorkspaceException exception)
        {
            throw new AgentToolException(exception.Code, exception.Message);
        }
    }

    private static (int Line, int Character) ParsePosition(JsonElement select)
    {
        // Selector-kind detection precedes position validation, so an anchor or patch
        // selector is a typed stage-B rejection, not a missing-line complaint.
        if (select.ValueKind == JsonValueKind.Object &&
            (select.TryGetProperty("anchor", out _) || select.TryGetProperty("patch", out _)))
            throw new AgentToolException(
                "ide_selector_unsupported",
                "Anchor and patch selectors arrive in a later stage; use the coordinate object {line, column}.");
        if (select.ValueKind != JsonValueKind.Object)
            throw new AgentToolException(
                "ide_selector_unsupported",
                "The selector must be the coordinate object {line, column}; anchor and patch selectors arrive in a later stage.");
        if (!select.TryGetProperty("line", out var lineElement) || lineElement.ValueKind != JsonValueKind.Number ||
            !lineElement.TryGetInt32(out var line) || line < 1)
            throw new AgentToolException("ide_invalid_arguments", "The selector needs a 1-based 'line' number.");

        var column = 1;
        if (select.TryGetProperty("column", out var columnElement))
        {
            if (columnElement.ValueKind != JsonValueKind.Number || !columnElement.TryGetInt32(out var parsed) ||
                parsed < 1)
                throw new AgentToolException("ide_invalid_arguments", "The selector 'column' must be a 1-based number.");
            column = parsed;
        }

        if (select.TryGetProperty("endLine", out _) || select.TryGetProperty("endColumn", out _))
            throw new AgentToolException(
                "ide_selector_unsupported",
                "Position tools take a single position; range selectors arrive in a later stage.");

        return (line - 1, column - 1);
    }

    private static int? ParseSeverity(string? minSeverity)
    {
        return minSeverity switch
        {
            null => null,
            "error" => 0,
            "warning" => 1,
            "information" => 2,
            "hint" => 3,
            _ => throw new AgentToolException(
                "ide_invalid_arguments",
                "minSeverity must be one of: error, warning, information, hint."),
        };
    }

    private JsonElement BuildWorkspaceDiagnostics(JsonElement answer, int? threshold)
    {
        var documents = ReadDocuments(answer, out var analyzedDocuments);
        var totals = new DiagnosticsTotals(0, 0, 0, 0);
        List<(string Uri, int Error, int Warning, int Information, int Hint)> files = [];
        var unmappedFiles = 0;

        foreach (var document in documents)
        {
            if (!TryMapDocumentUri(document, out var mapped) || mapped is null)
            {
                unmappedFiles++;
                continue;
            }

            int error = 0, warning = 0, information = 0, hint = 0;
            foreach (var diagnostic in EnumerateDiagnostics(document))
            {
                if (!PassesSeverity(diagnostic, threshold)) continue;
                switch (SeverityOf(diagnostic))
                {
                    case 0: error++; break;
                    case 1: warning++; break;
                    case 2: information++; break;
                    default: hint++; break;
                }
            }

            if (error == 0 && warning == 0 && information == 0 && hint == 0) continue;
            files.Add((mapped, error, warning, information, hint));
            totals = new DiagnosticsTotals(
                totals.Error + error,
                totals.Warning + warning,
                totals.Information + information,
                totals.Hint + hint);
        }

        var ranked = files
            .OrderByDescending(static entry => entry.Error)
            .ThenByDescending(static entry => entry.Warning)
            .ThenByDescending(static entry => entry.Information)
            .ThenByDescending(static entry => entry.Hint)
            .ThenBy(static entry => entry.Uri, StringComparer.Ordinal)
            .Take(MaxAggregateFiles)
            .Select(static entry => new DiagnosticsFileCount(entry.Uri, entry.Error, entry.Warning, entry.Information, entry.Hint))
            .ToArray();

        var note = analyzedDocuments == 0
            ? "No diagnostics are currently reported by any provider — language services may not have analyzed " +
              "anything yet. Treat this as unknown state, not as a clean bill of health."
            : null;
        return JsonSerializer.SerializeToElement(
            new WorkspaceDiagnosticsValue(
                "workspace",
                totals,
                ranked,
                files.Count,
                unmappedFiles,
                analyzedDocuments,
                note),
            IdeFunctionJsonContext.Default.WorkspaceDiagnosticsValue);
    }

    private JsonElement BuildDocumentDiagnostics(JsonElement answer, string requestedUri, int? threshold)
    {
        var documents = ReadDocuments(answer, out var analyzedDocuments);
        var diagnostics = new List<JsonElement>();
        foreach (var document in documents)
        {
            if (!TryMapDocumentUri(document, out _)) continue;
            foreach (var diagnostic in EnumerateDiagnostics(document))
            {
                if (PassesSeverity(diagnostic, threshold)) diagnostics.Add(TranslateIncoming(diagnostic));
            }
        }

        return JsonSerializer.SerializeToElement(
            new DocumentDiagnosticsValue("document", requestedUri, [.. diagnostics], analyzedDocuments),
            IdeFunctionJsonContext.Default.DocumentDiagnosticsValue);
    }

    private static JsonElement[] ReadDocuments(JsonElement answer, out int analyzedDocuments)
    {
        if (answer.ValueKind != JsonValueKind.Object ||
            !answer.TryGetProperty("documents", out var documents) ||
            documents.ValueKind != JsonValueKind.Array)
            throw new AgentToolException("ide_result_invalid", "The problems answer is not the documented shape.");

        analyzedDocuments = 0;
        if (answer.TryGetProperty("analyzedDocuments", out var analyzed) &&
            analyzed.ValueKind == JsonValueKind.Number &&
            analyzed.TryGetInt32(out var parsed) && parsed >= 0)
            analyzedDocuments = parsed;

        return [.. documents.EnumerateArray()];
    }

    private bool TryMapDocumentUri(JsonElement document, out string? workspaceUri)
    {
        workspaceUri = null;
        if (document.ValueKind != JsonValueKind.Object ||
            !document.TryGetProperty("uri", out var uriElement) ||
            uriElement.ValueKind != JsonValueKind.String)
            return false;

        var physical = uriElement.GetString();
        if (physical is null || !physical.StartsWith(FileUriPrefix, StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(physical, UriKind.Absolute, out var parsed) || parsed.Scheme != "file") return false;
        return workspace.Capture().TryMapPhysicalToWorkspaceUri(parsed.LocalPath, out workspaceUri);
    }

    private static IEnumerable<JsonElement> EnumerateDiagnostics(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object ||
            !document.TryGetProperty("diagnostics", out var diagnostics) ||
            diagnostics.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var diagnostic in diagnostics.EnumerateArray())
            if (diagnostic.ValueKind == JsonValueKind.Object)
                yield return diagnostic;
    }

    private static int SeverityOf(JsonElement diagnostic)
    {
        return diagnostic.TryGetProperty("severity", out var severity) &&
               severity.ValueKind == JsonValueKind.Number &&
               severity.TryGetInt32(out var value)
            ? Math.Clamp(value, 0, 3)
            : 2;
    }

    private static bool PassesSeverity(JsonElement diagnostic, int? threshold)
    {
        return threshold is not { } minimum || SeverityOf(diagnostic) <= minimum;
    }

    /// <summary>The kernel-side translation points (ADR 0042 decisions 4 and 5):
    /// outgoing requests map <c>workspace://local</c> URIs to their physical
    /// <c>file://</c> addresses and rewrite 1-based positions to LSP 0-based; incoming
    /// results map physical URIs back to workspace URIs and rewrite positions to 1-based.
    /// Untranslatable strings pass through unchanged — a file URI outside the workspace
    /// reaches the model only where no read tool can follow it.</summary>
    private JsonElement TranslateOutgoing(JsonElement element) => Translate(element, outgoing: true);

    private JsonElement TranslateIncoming(JsonElement element) => Translate(element, outgoing: false);

    private JsonElement Translate(JsonElement element, bool outgoing)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteTranslated(element, writer, outgoing, depth: 0);

        buffer.Position = 0;
        using var document = JsonDocument.Parse(buffer);
        return document.RootElement.Clone();
    }

    private void WriteTranslated(JsonElement element, Utf8JsonWriter writer, bool outgoing, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                writer.WriteStringValue(TranslateString(element.GetString() ?? string.Empty, outgoing));
                return;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsPositionValue(property.Value)) WriteShiftedPosition(property.Value, writer, outgoing);
                    else if (depth < MaxTranslationDepth) WriteTranslated(property.Value, writer, outgoing, depth + 1);
                    else property.Value.WriteTo(writer);
                }

                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    if (depth < MaxTranslationDepth) WriteTranslated(item, writer, outgoing, depth + 1);
                    else item.WriteTo(writer);
                }

                writer.WriteEndArray();
                return;
            default:
                element.WriteTo(writer);
                return;
        }
    }

    private static bool IsPositionValue(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number &&
               element.TryGetProperty("character", out var character) && character.ValueKind == JsonValueKind.Number;
    }

    private static void WriteShiftedPosition(JsonElement position, Utf8JsonWriter writer, bool outgoing)
    {
        var shift = outgoing ? -1 : 1;
        writer.WriteStartObject();
        foreach (var property in position.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (property.Name is "line" or "character" &&
                property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt32(out var value))
                writer.WriteNumberValue(value + shift);
            else
                property.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private string TranslateString(string value, bool outgoing)
    {
        if (outgoing)
        {
            if (!value.StartsWith(WorkspaceUriPrefix, StringComparison.Ordinal) &&
                !string.Equals(value, "workspace://local/", StringComparison.Ordinal))
                return value;
            try
            {
                return ToFileUri(workspace.Capture().Resolve(value).FullPath);
            }
            catch (WorkspaceException exception)
            {
                throw new AgentToolException(
                    "ide_uri_unknown",
                    $"The uri '{value}' cannot be addressed by the IDE bridge: {exception.Message}");
            }
        }

        if (!value.StartsWith(FileUriPrefix, StringComparison.Ordinal)) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != "file") return value;
        return workspace.Capture().TryMapPhysicalToWorkspaceUri(parsed.LocalPath, out var mapped) && mapped is not null
            ? mapped
            : value;
    }

    private static string ToFileUri(string fullPath) => new Uri(fullPath).AbsoluteUri;

    private static JsonElement BuildPositionParams(
        string fileUri,
        int line,
        int character,
        IReadOnlyList<(string Name, JsonElement Value)>? extra = null)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("uri", fileUri);
            writer.WriteStartObject("position");
            writer.WriteNumber("line", line);
            writer.WriteNumber("character", character);
            writer.WriteEndObject();
            foreach (var (name, value) in extra ?? [])
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        buffer.Position = 0;
        using var document = JsonDocument.Parse(buffer);
        return document.RootElement.Clone();
    }

    private static JsonElement BuildObject(params (string Name, string Value)[] properties)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in properties) writer.WriteString(name, value);
            writer.WriteEndObject();
        }

        buffer.Position = 0;
        using var document = JsonDocument.Parse(buffer);
        return document.RootElement.Clone();
    }

    private static JsonDocument BuildJsonDocument(
        params (string Name, JsonElement Value)[] properties)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in properties)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        buffer.Position = 0;
        return JsonDocument.Parse(buffer);
    }

    private static AIFunction CreateFunction(Delegate method, string name, string description)
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

    private sealed record ResolvedDocument(string FullPath, string FileUri, string WorkspaceUri);
}

/// <summary>The <c>ide.request</c> payload carried as an application/json progress item on
/// the run's event stream: sequenced and replayable with the run's own events, answered at
/// <c>POST /v1/agent/ide/{requestId}</c> (ADR 0042 decision 1).</summary>
internal sealed record IdeRequestPayload(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params);

/// <summary>The wire envelope around <see cref="IdeRequestPayload" />: the progress item's
/// JSON value is <c>{"ideRequest":{requestId,method,params}}</c>, the shape the extension
/// host's <c>extractIdeRequest</c> reads. The wrapper, not the bare payload, is the
/// published contract.</summary>
internal sealed record IdeRequestEnvelope(
    [property: JsonPropertyName("ideRequest")] IdeRequestPayload IdeRequest);

/// <summary>Workspace-wide diagnostics aggregate (ADR 0042 decision 8): severity totals,
/// the top files, and the analysis-state marker that keeps "nothing reported" from being
/// read as "no problems".</summary>
internal sealed record WorkspaceDiagnosticsValue(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("totals")] DiagnosticsTotals Totals,
    [property: JsonPropertyName("files")] IReadOnlyList<DiagnosticsFileCount> Files,
    [property: JsonPropertyName("totalFiles")] int TotalFiles,
    [property: JsonPropertyName("unmappedFiles")] int UnmappedFiles,
    [property: JsonPropertyName("analyzedDocuments")] int AnalyzedDocuments,
    [property: JsonPropertyName("note")] string? Note = null);

/// <summary>One document's diagnostics drill-down.</summary>
internal sealed record DocumentDiagnosticsValue(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<JsonElement> Diagnostics,
    [property: JsonPropertyName("analyzedDocuments")] int AnalyzedDocuments);

/// <summary>Severity counts for one file in the aggregate view.</summary>
internal sealed record DiagnosticsFileCount(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("error")] int Error,
    [property: JsonPropertyName("warning")] int Warning,
    [property: JsonPropertyName("information")] int Information,
    [property: JsonPropertyName("hint")] int Hint);

/// <summary>Workspace severity totals.</summary>
internal sealed record DiagnosticsTotals(
    [property: JsonPropertyName("error")] int Error,
    [property: JsonPropertyName("warning")] int Warning,
    [property: JsonPropertyName("information")] int Information,
    [property: JsonPropertyName("hint")] int Hint);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IdeRequestPayload))]
[JsonSerializable(typeof(IdeRequestEnvelope))]
[JsonSerializable(typeof(WorkspaceDiagnosticsValue))]
[JsonSerializable(typeof(DocumentDiagnosticsValue))]
[JsonSerializable(typeof(DiagnosticsTotals))]
[JsonSerializable(typeof(DiagnosticsFileCount))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class IdeFunctionJsonContext : JsonSerializerContext;
