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
        "maieutics/dirty",
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
    private readonly WorkspaceEditFunctions workspaceEdits;
    private readonly FrontendIdeSockets sockets;

    public IdeFunctions(
        Workspace workspace,
        IdeRequestRegistry requests,
        IIdeFrontendGate gate,
        WorkspaceEditFunctions workspaceEdits,
        FrontendIdeSockets sockets)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.requests = requests ?? throw new ArgumentNullException(nameof(requests));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.workspaceEdits = workspaceEdits ?? throw new ArgumentNullException(nameof(workspaceEdits));
        this.sockets = sockets ?? throw new ArgumentNullException(nameof(sockets));

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
                (Func<string, JsonElement, string, AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)RenameAsync,
                "ide_rename",
                "Renames the symbol at one position through the language service and returns the resulting workspace " +
                "edit as data — nothing is written; apply it with ide_apply_edit. 'uri' is a workspace://local file URI, " +
                "'select' the coordinate or anchor selector (1-based), 'newName' the symbol's new name. Dirty documents " +
                "are rejected: save or revert first (the edit is computed against buffers, applied against disk)."),
            CreateFunction(
                (Func<string, JsonElement, AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)CodeActionsAsync,
                "ide_code_actions",
                "Returns the code actions (quick fixes, refactors) the language service offers at one position, as data — " +
                "nothing is applied. 'uri' is a workspace://local file URI, 'select' the coordinate or anchor selector " +
                "(1-based). Actions carrying an edit can be applied by passing its 'changes' envelope to ide_apply_edit; " +
                "command-only actions are listed but cannot be executed."),
            CreateFunction(
                (Func<AIFunctionArguments, string, int?, bool?, CancellationToken, ValueTask<JsonElement>>)FormatDocumentAsync,
                "ide_format_document",
                "Formats one file through the language service's document formatter and writes the result back in one step " +
                "(the compute-then-apply two-phase of the other write tools, folded for the common case). 'uri' is a " +
                "workspace://local file URI; optional tabSize (default 4) and insertSpaces (default true) shape the " +
                "formatting options. Dirty documents are rejected: save or revert first. Returns the per-file unified diff."),
            CreateFunction(
                (Func<JsonElement, AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)ApplyEditAsync,
                "ide_apply_edit",
                "Applies one LSP-shaped workspace edit through the kernel's gated write path — the only way IDE-computed " +
                "edits reach the disk. 'edit' is {\"changes\": {\"workspace://local/…\": [{\"range\": {\"start\": {\"line\", " +
                "\"character\"}, \"end\": {…}}, \"newText\": \"…\"}]}} with 1-based positions (the same shapes ide_code_actions " +
                "and ide_rename return). The whole edit is pre-validated first — containment, decodable text, size bounds, " +
                "and no unsaved buffers — and any failure rejects everything unchanged (ide_edit_rejected / " +
                "ide_document_dirty). A file that fails mid-apply is reported with what changed and what is pending."),

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
        var announce = ResolveAnnouncer(arguments);
        EnsureConnected(arguments);
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

        var answer = await SendAsync(announce, "maieutics/problems", parameters, cancellationToken).ConfigureAwait(false);
        return requestedUri is null
            ? BuildWorkspaceDiagnostics(answer, threshold)
            : BuildDocumentDiagnostics(answer, requestedUri, threshold);
    }

    [Description("Returns the language service's hover information at one position.")]
    private async ValueTask<JsonElement> HoverAsync(
        [Description("A workspace://local file URI, as returned by other tools.")] string uri,
        [Description("Selector: {\"line\": 42, \"column\": 7} coordinates, {\"anchor\": [lines], \"occurrence?\"} (1-based, must be unique), or {\"patch\": \"…\"} (V4A or git diff; its context lines locate the position).")]
        JsonElement select,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var announce = ResolveAnnouncer(arguments);
        EnsureConnected(arguments);
        var resolved = ResolveDocumentUri(uri);
        var (line, character) = await ResolvePositionAsync(resolved, select, cancellationToken);
        var parameters = BuildPositionParams(resolved.FileUri, line, character);
        var answer = await SendAsync(announce, "textDocument/hover", parameters, cancellationToken)
            .ConfigureAwait(false);
        return TranslateIncoming(answer);
    }

    [Description("Navigates the language service from one position.")]
    private async ValueTask<JsonElement> NavigateAsync(
        [Description("One of: definition, type_definition, declaration, implementation, references, highlights.")]
        string kind,
        [Description("A workspace://local file URI, as returned by other tools.")] string uri,
        [Description("Selector: {\"line\": 42, \"column\": 7} coordinates, {\"anchor\": [lines], \"occurrence?\"} (1-based, must be unique), or {\"patch\": \"…\"} (V4A or git diff; its context lines locate the position).")]
        JsonElement select,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var announce = ResolveAnnouncer(arguments);
        EnsureConnected(arguments);
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
        var (line, character) = await ResolvePositionAsync(resolved, select, cancellationToken);
        var parameters = method == "textDocument/references"
            ? BuildPositionParams(
                resolved.FileUri,
                line,
                character,
                [("context", JsonDocument.Parse("""{"includeDeclaration":true}""").RootElement)])
            : BuildPositionParams(resolved.FileUri, line, character);
        var answer = await SendAsync(announce, method, parameters, cancellationToken).ConfigureAwait(false);
        return TranslateIncoming(answer);
    }

    [Description("Returns the language service's symbol outline of one document.")]
    private async ValueTask<JsonElement> DocumentSymbolsAsync(
        [Description("A workspace://local file URI, as returned by other tools.")] string uri,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var announce = ResolveAnnouncer(arguments);
        EnsureConnected(arguments);
        var resolved = ResolveDocumentUri(uri);
        var parameters = BuildObject(("uri", resolved.FileUri));
        var answer = await SendAsync(announce, "textDocument/documentSymbol", parameters, cancellationToken)
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
        var announce = ResolveAnnouncer(arguments);
        EnsureConnected(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var parameters = BuildObject(("query", query));
        var answer = await SendAsync(announce, "workspace/symbol", parameters, cancellationToken)
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
        var announce = ResolveAnnouncer(arguments);
        EnsureConnected(arguments);
        if (!IdeMethodCatalog.ReadMethods.Contains(method))
            throw new AgentToolException(
                "ide_method_unsupported",
                $"The method '{method}' is not in the read catalog; write-group methods join in a later stage.");
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new AgentToolException("ide_invalid_arguments", "The parameters must be a JSON object.");

        var answer = await SendAsync(announce, method, TranslateOutgoing(parameters), cancellationToken)
            .ConfigureAwait(false);
        return TranslateIncoming(answer);
    }

    [Description("Renames a symbol; returns the workspace edit as data without writing.")]
    private async ValueTask<JsonElement> RenameAsync(
        [Description("A workspace://local file URI, as returned by other tools.")] string uri,
        [Description("Selector: coordinates {\"line\": 42, \"column\": 7} or an anchor block (1-based).")]
        JsonElement select,
        [Description("The symbol's new name.")] string newName,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var announce = ResolveAnnouncer(arguments);
        EnsureConnected(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var resolved = ResolveDocumentUri(uri);
        var (line, character) = await ResolvePositionAsync(resolved, select, cancellationToken).ConfigureAwait(true);
        await EnsureNotDirtyAsync(announce, [resolved.FileUri], cancellationToken).ConfigureAwait(true);
        var parameters = BuildRenameParams(resolved.FileUri, line, character, newName);
        var answer = await SendAsync(announce, "textDocument/rename", parameters, cancellationToken)
            .ConfigureAwait(false);
        return TranslateIncoming(answer);
    }

    [Description("Returns the code actions at one position as data without applying anything.")]
    private async ValueTask<JsonElement> CodeActionsAsync(
        [Description("A workspace://local file URI, as returned by other tools.")] string uri,
        [Description("Selector: coordinates {\"line\": 42, \"column\": 7} or an anchor block (1-based).")]
        JsonElement select,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var announce = ResolveAnnouncer(arguments);
        EnsureConnected(arguments);
        var resolved = ResolveDocumentUri(uri);
        var (line, character) = await ResolvePositionAsync(resolved, select, cancellationToken).ConfigureAwait(true);
        var parameters = BuildPositionParams(
            resolved.FileUri,
            line,
            character,
            [("context", EmptyDiagnosticsContext())]);
        var answer = await SendAsync(announce, "textDocument/codeAction", parameters, cancellationToken)
            .ConfigureAwait(false);
        return TranslateIncoming(answer);
    }

    [Description("Formats one document through the language service and writes the result back.")]
    private async ValueTask<JsonElement> FormatDocumentAsync(
        AIFunctionArguments arguments,
        [Description("A workspace://local file URI, as returned by other tools.")] string uri,
        [Description("Spaces per tab for the formatting options. Defaults to 4.")] int? tabSize = null,
        [Description("Indent with spaces rather than tabs. Defaults to true.")] bool? insertSpaces = null,
        CancellationToken cancellationToken = default)
    {
        var announce = ResolveAnnouncer(arguments);
        EnsureConnected(arguments);
        var resolved = ResolveDocumentUri(uri);
        await EnsureNotDirtyAsync(announce, [resolved.FileUri], cancellationToken).ConfigureAwait(true);
        var parameters = BuildFormatParams(resolved.FileUri, tabSize ?? 4, insertSpaces ?? true);
        var answer = await SendAsync(announce, "textDocument/formatting", parameters, cancellationToken)
            .ConfigureAwait(false);
        var edits = TranslateIncoming(answer);
        if (edits.ValueKind != JsonValueKind.Array)
            throw new AgentToolException("ide_result_invalid", "The formatting answer is not an edit list.");
        if (edits.GetArrayLength() == 0)
            return JsonSerializer.SerializeToElement(
                new IdeApplyEditsValue("unchanged", [], null, null, null),
                IdeFunctionJsonContext.Default.IdeApplyEditsValue);
        var rawEdits = ImmutableArray.CreateBuilder<IdeRawEdit>();
        foreach (var entry in edits.EnumerateArray())
        {
            var (startLine, startCharacter, endLine, endCharacter, newText) =
                ParseTextEditsEntry(entry, resolved.WorkspaceUri);
            rawEdits.Add(new IdeRawEdit(startLine, startCharacter, endLine, endCharacter, newText));
        }

        return await ApplyTargetsAsync(
            announce,
            [new IdeEditTarget(resolved.WorkspaceUri, rawEdits.ToImmutable())],
            arguments,
            cancellationToken).ConfigureAwait(true);
    }

    [Description("Applies one LSP-shaped workspace edit through the kernel's gated write path.")]
    private async ValueTask<JsonElement> ApplyEditAsync(
        [Description("The edit envelope: {\"changes\": {\"workspace://local/…\": [{\"range\": {\"start\": " +
                     "{\"line\", \"character\"}, \"end\": {…}}, \"newText\": \"…\"}]}} with 1-based positions (the " +
                     "shapes ide_code_actions and ide_rename return). The whole edit is pre-validated first — " +
                     "containment, decodable text, size bounds, and no unsaved buffers — and any failure rejects " +
                     "everything unchanged (ide_edit_rejected / ide_document_dirty); a mid-apply failure is reported " +
                     "with what changed and what is pending.")]
        JsonElement edit,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var announce = ResolveAnnouncer(arguments);
        EnsureConnected(arguments);
        var targets = IdeWorkspaceEdit.ParseEnvelope(edit);
        return await ApplyTargetsAsync(announce, targets, arguments, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>The decision-6 apply core shared by every write tool: the whole
    /// edit is pre-validated (containment, decode, range table, computed size)
    /// before anything mutates; then the dirty check (decision 7) answers over
    /// the bridge; then each file flows through the gated write seam. A
    /// pre-validation failure rejects everything typed; a mid-apply failure is
    /// reported with exactly what changed and what is pending.</summary>
    private async ValueTask<JsonElement> ApplyTargetsAsync(
        Func<AIContent, CancellationToken, ValueTask>? announce,
        ImmutableArray<IdeEditTarget> targets,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var validated = new List<(IdeEditTarget Target, ResolvedDocument Resolved, string Content)>();
        var rejected = new List<string>();
        foreach (var target in targets)
        {
            try
            {
                var resolved = ResolveDocumentUri(target.WorkspaceUri);
                var current = await IdeSelector.ReadTextAsync(resolved.FullPath, cancellationToken).ConfigureAwait(true);
                var content = IdeWorkspaceEdit.ComputeContent(target, current);
                if (Encoding.UTF8.GetByteCount(content) > workspaceEdits.MaximumWriteBytes)
                    throw new AgentToolException(
                        "ide_edit_rejected",
                        $"the computed content exceeds the {workspaceEdits.MaximumWriteBytes} byte write bound.");
                validated.Add((target, resolved, content));
            }
            catch (AgentToolException exception)
            {
                rejected.Add($"'{target.WorkspaceUri}': {exception.Message}");
            }
        }

        if (rejected.Count > 0)
            throw new AgentToolException(
                "ide_edit_rejected",
                $"The edit was rejected before any file changed: {string.Join("; ", rejected)}");

        await EnsureNotDirtyAsync(
            announce,
            validated.Select(static entry => entry.Resolved.FileUri).Distinct().ToList(),
            cancellationToken).ConfigureAwait(true);

        var applied = new List<WriteTextResult>();
        string? failedUri = null;
        string? failedError = null;
        var pending = new List<string>();
        foreach (var (target, resolved, content) in validated)
        {
            if (failedUri is not null)
            {
                pending.Add(target.WorkspaceUri);
                continue;
            }

            try
            {
                applied.Add(await workspaceEdits
                    .ApplyComputedContentAsync(resolved.WorkspaceUri, content, arguments, cancellationToken)
                    .ConfigureAwait(true));
            }
            catch (AgentToolException exception)
            {
                failedUri = target.WorkspaceUri;
                failedError = exception.Message;
            }
        }

        var status = failedUri is not null ? "partial" : "applied";
        return JsonSerializer.SerializeToElement(
            new IdeApplyEditsValue(status, applied, failedUri, failedError, pending),
            IdeFunctionJsonContext.Default.IdeApplyEditsValue);
    }

    /// <summary>Decision 7's dirty gate: one maieutics/dirty query over the
    /// bridge answers for every target at once; an unsaved buffer computed one
    /// shape while the disk holds another, so any dirty target fails the write
    /// typed instead of misplacing.</summary>
    private async ValueTask EnsureNotDirtyAsync(
        Func<AIContent, CancellationToken, ValueTask>? announce,
        IReadOnlyList<string> fileUris,
        CancellationToken cancellationToken)
    {
        if (fileUris.Count == 0) return;

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("uris");
            foreach (var uri in fileUris) writer.WriteStringValue(uri);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        buffer.Position = 0;
        using var document = JsonDocument.Parse(buffer);
        var answer = await SendAsync(announce, "maieutics/dirty", document.RootElement.Clone(), cancellationToken)
            .ConfigureAwait(false);
        if (answer.ValueKind != JsonValueKind.Object ||
            !answer.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            throw new AgentToolException("ide_result_invalid", "The dirty answer is not the documented shape.");

        var dirty = new List<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("dirty", out var dirtyElement) ||
                dirtyElement.ValueKind != JsonValueKind.True) continue;
            if (entry.TryGetProperty("uri", out var uriElement) && uriElement.ValueKind == JsonValueKind.String)
                dirty.Add(uriElement.GetString()!);
        }

        if (dirty.Count > 0)
            throw new AgentToolException(
                "ide_document_dirty",
                "These documents have unsaved changes; save or revert them before kernel-side writes: " +
                string.Join(", ", dirty));
    }

    private static JsonElement BuildRenameParams(string fileUri, int line, int character, string newName)
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
            writer.WriteString("newName", newName);
            writer.WriteEndObject();
        }

        buffer.Position = 0;
        using var document = JsonDocument.Parse(buffer);
        return document.RootElement.Clone();
    }

    private static JsonElement BuildFormatParams(string fileUri, int tabSize, bool insertSpaces)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("uri", fileUri);
            writer.WriteStartObject("options");
            writer.WriteNumber("tabSize", tabSize);
            writer.WriteBoolean("insertSpaces", insertSpaces);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        buffer.Position = 0;
        using var document = JsonDocument.Parse(buffer);
        return document.RootElement.Clone();
    }

    private static JsonElement EmptyDiagnosticsContext()
    {
        using var document = JsonDocument.Parse("""{"diagnostics":[]}""");
        return document.RootElement.Clone();
    }

    /// <summary>Parses one wire text edit (1-based after the incoming
    /// translation) into the raw-edit form the apply core validates; failures
    /// name the target file.</summary>
    private static (int StartLine, int StartCharacter, int EndLine, int EndCharacter, string NewText)
        ParseTextEditsEntry(JsonElement entry, string targetUri)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("range", out var range) ||
            range.ValueKind != JsonValueKind.Object ||
            !TryReadPosition(range, "start", out var startLine, out var startCharacter) ||
            !TryReadPosition(range, "end", out var endLine, out var endCharacter) ||
            !entry.TryGetProperty("newText", out var newText) ||
            newText.ValueKind != JsonValueKind.String)
            throw new AgentToolException(
                "ide_result_invalid",
                $"The formatting edit for '{targetUri}' is not a range+newText pair.");

        return (startLine, startCharacter, endLine, endCharacter, newText.GetString()!);
    }

    private static bool TryReadPosition(JsonElement range, string side, out int line, out int character)
    {
        line = 0;
        character = 0;
        return range.TryGetProperty(side, out var position) &&
               position.ValueKind == JsonValueKind.Object &&
               position.TryGetProperty("line", out var lineElement) &&
               lineElement.ValueKind == JsonValueKind.Number &&
               lineElement.TryGetInt32(out line) &&
               position.TryGetProperty("character", out var characterElement) &&
               characterElement.ValueKind == JsonValueKind.Number &&
               characterElement.TryGetInt32(out character);
    }

    /// <summary>Announces one IDE request on the run's event stream
    /// <summary>Announces one IDE request on the run's event stream
    /// <summary>Announces one IDE request on the run's event stream (the sequenced,
    /// replayable <c>ide.request</c> payload — ADR 0042 decision 1) and awaits its answer
    /// through the registry, bounded by the request timeout and the caller's cancellation.</summary>
    /// <summary>Resolves the announcement path for this call: an agent run
    /// context announces through the run stream (sequenced, replayable
    /// progress); a script/plugin invocation has no run to ride, so the
    /// request pushes directly to the announcing sockets — unordered, not
    /// replayed, private to announcers (ADR 0042 decision 10 as amended by
    /// stage D). The answer endpoint is identical on both paths.</summary>
    private Func<AIContent, CancellationToken, ValueTask>? ResolveAnnouncer(AIFunctionArguments arguments)
    {
        if (arguments.Context?.TryGetValue(typeof(AgentToolContext), out var value) == true &&
            value is AgentToolContext context)
        {
            return (content, token) => context.ReportProgressAsync(content, token);
        }

        return null;
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
            if (announce is null)
            {
                var sent = await sockets
                    .TrySendToAnnouncingAsync(new FrontendEventFrame("ide.request", Data: envelope.GetProperty("ideRequest").Clone()), cancellationToken)
                    .ConfigureAwait(false);
                if (!sent)
                    throw new AgentToolException(
                        "ide_unavailable",
                        "No announcing frontend accepted the ide request (the announcing socket may have just disconnected). Retry.");
            }
            else
            {
                var payload = JsonSerializer.Serialize(
                    envelope,
                    IdeFunctionJsonContext.Default.JsonElement);
                await announce(
                    new DataContent(Encoding.UTF8.GetBytes(payload), "application/json"),
                    cancellationToken).ConfigureAwait(false);
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
        EnsureConnectedCore(context.SessionId.Value.ToString("N"));
    }

    /// <summary>Script-tool invocations (REPL, ide.query) carry no agent tool
    /// context, so the gate degrades to the process-level check: some
    /// announcing socket must exist; the caller's session membership cannot
    /// be verified. Context-bearing agent calls keep the strict per-session
    /// check.</summary>
    private void EnsureConnected(AIFunctionArguments arguments)
    {
        if (arguments.Context?.TryGetValue(typeof(AgentToolContext), out var value) == true &&
            value is AgentToolContext context)
        {
            EnsureConnectedCore(context.SessionId.Value.ToString("N"));
            return;
        }

        if (!gate.HasIdeFrontend)
            throw new AgentToolException(
                "ide_unavailable",
                "No frontend has announced IDE capability; the IDE tools need the extension host's events socket (ide=1).");
    }

    private void EnsureConnectedCore(string sessionId)
    {
        if (!gate.IsIdeFrontendConnected(sessionId))
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
            return new ResolvedDocument(
                resolved.FullPath,
                ToFileUri(resolved.FullPath),
                resolved.Uri,
                WorkspaceSnapshot.ToDisplayPath(resolved.Segments));
        }
        catch (WorkspaceException exception)
        {
            throw new AgentToolException(exception.Code, exception.Message);
        }
    }

    /// <summary>Resolves one selector — coordinates (stage A), an anchor block
    /// or a patch (stage B) — to a 0-based position inside the resolved
    /// document. Anchor and patch forms need the file's lines for the
    /// unique-match locator (bounded at the read scan scale).</summary>
    private async ValueTask<(int Line, int Character)> ResolvePositionAsync(
        ResolvedDocument resolved,
        JsonElement select,
        CancellationToken cancellationToken)
    {
        JsonElement anchor = default;
        JsonElement patch = default;
        var hasAnchor = select.ValueKind == JsonValueKind.Object && select.TryGetProperty("anchor", out anchor);
        var hasPatch = select.ValueKind == JsonValueKind.Object && select.TryGetProperty("patch", out patch);
        if (hasAnchor && hasPatch)
            throw new AgentToolException("ide_invalid_arguments", "The selector 'anchor' and 'patch' forms are mutually exclusive.");
        if (hasAnchor)
        {
            if (anchor.ValueKind != JsonValueKind.Array)
                throw new AgentToolException("ide_invalid_arguments", "The selector 'anchor' must be an array of context lines.");
            var occurrence = 0;
            if (select.TryGetProperty("occurrence", out var occurrenceElement))
            {
                if (occurrenceElement.ValueKind != JsonValueKind.Number ||
                    !occurrenceElement.TryGetInt32(out var parsed))
                    throw new AgentToolException("ide_invalid_arguments", "The selector 'occurrence' must be a number.");
                occurrence = parsed;
            }

            // 0 means "not given" — the locator then requires a unique match.

            var anchorLines = ImmutableArray.CreateBuilder<string>(anchor.GetArrayLength());
            foreach (var entry in anchor.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    throw new AgentToolException("ide_invalid_arguments", "Every selector 'anchor' entry must be a string.");
                anchorLines.Add(entry.GetString() ?? string.Empty);
            }

            var anchorFileLines = await IdeSelector
                .ReadLinesAsync(resolved.FullPath, cancellationToken)
                .ConfigureAwait(false);
            var anchorLine = IdeSelector.LocateAnchor(anchorFileLines, anchorLines.ToImmutable(), occurrence);
            return (anchorLine - 1, 0);
        }

        if (hasPatch)
        {
            if (patch.ValueKind != JsonValueKind.String)
                throw new AgentToolException(
                    "ide_invalid_arguments",
                    "The selector 'patch' must be a string holding a V4A or git unified diff.");
            var patchFileLines = await IdeSelector
                .ReadLinesAsync(resolved.FullPath, cancellationToken)
                .ConfigureAwait(false);
            var patchLine = IdeSelector.LocatePatch(patchFileLines, patch.GetString()!, resolved.DisplayPath);
            return (patchLine - 1, 0);
        }

        return ParseCoordinateSelect(select);
    }

    private static (int Line, int Character) ParseCoordinateSelect(JsonElement select)
    {
        if (select.ValueKind != JsonValueKind.Object)
            throw new AgentToolException(
                "ide_selector_unsupported",
                "The selector must be {line, column}, {anchor: [context lines], occurrence?}, or {patch: \"…\"}.");
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
                    // Property names carry URIs too (an LSP changes map is keyed
                    // by them), so names pass through the same translation.
                    writer.WritePropertyName(TranslateString(property.Name, outgoing));
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

    private sealed record ResolvedDocument(string FullPath, string FileUri, string WorkspaceUri, string DisplayPath);
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
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IdeApplyEditsValue))]
[JsonSerializable(typeof(WorkspaceEditDiff))]
[JsonSerializable(typeof(WriteTextResult))]
internal sealed partial class IdeFunctionJsonContext : JsonSerializerContext;

/// <summary>The ide_apply_edit / ide_format_document answer: what each file
/// became through the gated write, or — when a file failed mid-apply — what
/// changed, what failed, and what is still pending (ADR 0042 decision 6).</summary>
internal sealed record IdeApplyEditsValue(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("applied")] IReadOnlyList<WriteTextResult> Applied,
    [property: JsonPropertyName("failedUri")] string? FailedUri = null,
    [property: JsonPropertyName("failedError")] string? FailedError = null,
    [property: JsonPropertyName("pending")] IReadOnlyList<string>? Pending = null);
