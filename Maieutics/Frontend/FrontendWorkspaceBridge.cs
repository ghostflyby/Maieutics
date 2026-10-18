using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;
using Maieutics.Execution;

namespace Maieutics.Frontend;

/// <summary>
///     The frontend's workspace file-system bridge (ADR 0042 decision 12): implements
///     Execution's <see cref="IWorkspaceIoBridge" /> by pushing targeted
///     <c>ide.request</c> frames to the elected announcing socket — the earliest live
///     announcement of the calling session (process-wide for context-less script calls).
///     Workspace fs requests are never broadcast, so another attached IDE cannot answer
///     a read with a different window's buffer; an operation that loses its authority
///     mid-call fails typed instead of re-asking a different announcer. A frontend that
///     predates the local family answers <c>ide_method_unsupported</c>; the first such
///     answer is absorbed process-wide and the bridge reports itself inactive, which
///     returns every workspace tool to local I/O — the coherent pre-bridge behavior.
/// </summary>
internal sealed class FrontendWorkspaceBridge : IWorkspaceIoBridge
{
    private readonly FrontendIdeSockets sockets;
    private readonly IdeRequestRegistry requests;
    private readonly Workspace workspace;

    /// <summary>The elected authority's coverage: the physical directory prefixes of
    /// the workspace folders it answered at election time (ADR 0042 decision 12's
    /// coverage ruling — the bridge serves only files the frontend actually opens).
    /// Keyed by registration id; an entry for a dead registration is dead weight and
    /// the cache is capped by wholesale clearing. Staleness is safe in both
    /// directions: a narrow cache keeps newly opened folders local, and a wide one
    /// (folders closed since) meets the frontend's own fs_not_covered check.</summary>
    private readonly ConcurrentDictionary<Guid, string[]> coverage = new();

    private volatile bool cohortPredates;

    public FrontendWorkspaceBridge(FrontendIdeSockets sockets, IdeRequestRegistry requests, Workspace workspace)
    {
        this.sockets = sockets ?? throw new ArgumentNullException(nameof(sockets));
        this.requests = requests ?? throw new ArgumentNullException(nameof(requests));
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>How long one bridge request waits for the frontend's answer before the
    /// typed timeout. Internal-settable so integration tests shrink the wait.</summary>
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public WorkspaceBridgeChannel? Acquire(string? sessionId)
    {
        if (cohortPredates) return null;

        return sockets.TryGetAuthority(sessionId) is { } authority
            ? new Channel(this, authority)
            : null;
    }

    /// <summary>The coverage ruling's kernel half: the physical path must sit under
    /// (or be) one of the authority's workspace-folder roots; anything else falls
    /// back to the local channel per call.</summary>
    private async Task EnsureCoveredAsync(IdeAnnouncerHandle authority, IReadOnlyList<string> segments, CancellationToken cancellationToken)
    {
        var roots = await GetCoverageAsync(authority, cancellationToken).ConfigureAwait(false);
        var target = workspace.Capture().ToPhysicalPath(segments);
        if (!IsCovered(target, roots))
            throw new WorkspaceBridgeCoverageException(target);
    }

    private async Task<string[]> GetCoverageAsync(IdeAnnouncerHandle authority, CancellationToken cancellationToken)
    {
        if (coverage.TryGetValue(authority.RegistrationId, out var known)) return known;

        // The coverage set is the authority's own folder answer — the idefs root
        // listing ({folderId: null}), the same shape the folder registry serves.
        var parameters = JsonSerializer.SerializeToElement(
            new IdeFsListParams(null, [], null),
            IdeFunctionJsonContext.Default.IdeFsListParams);
        var answer = await SendAsync(authority, "maieutics/fs/list", parameters, cancellationToken)
            .ConfigureAwait(false);
        var roots = ParseCoverageRoots(answer);
        if (coverage.Count > 64) coverage.Clear();

        coverage[authority.RegistrationId] = roots;
        return roots;
    }

    private string[] ParseCoverageRoots(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object ||
            !answer.TryGetProperty("folders", out var folders) ||
            folders.ValueKind != JsonValueKind.Array)
            throw new AgentToolException("ide_result_invalid", "The folder listing is not the documented shape.");

        var roots = new List<string>();
        foreach (var folder in folders.EnumerateArray())
        {
            if (folder.ValueKind != JsonValueKind.Object ||
                !folder.TryGetProperty("uri", out var uri) ||
                uri.ValueKind != JsonValueKind.String ||
                uri.GetString() is not { Length: > 0 } raw ||
                !Uri.TryCreate(raw, UriKind.Absolute, out var parsed) ||
                parsed.Scheme != "file")
                continue;

            try
            {
                roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parsed.LocalPath)));
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return [.. roots];
    }

    private static bool IsCovered(string target, string[] roots)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in roots)
        {
            if (string.Equals(normalized, root, comparison)) return true;
            if (normalized.Length > root.Length + 1 &&
                normalized[root.Length] is '/' or '\\' &&
                normalized.StartsWith(root, comparison))
                return true;
        }

        return false;
    }

    private sealed class Channel(
        FrontendWorkspaceBridge owner,
        IdeAnnouncerHandle authority) : WorkspaceBridgeChannel
    {
        public override async Task<string> ReadTextAsync(
            IReadOnlyList<string> segments,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            await owner.EnsureCoveredAsync(authority, segments, cancellationToken).ConfigureAwait(false);
            var parameters = JsonSerializer.SerializeToElement(
                new FsLocalReadParams(PhysicalUri(owner.workspace, segments)),
                BridgeJsonContext.Default.FsLocalReadParams);
            var answer = await owner
                .SendAsync(authority, "maieutics/fs/local/read", parameters, cancellationToken)
                .ConfigureAwait(false);
            return ReadString(answer, "content") ??
                throw new AgentToolException("ide_result_invalid", "The read answer carries no text content.");
        }

        public override async ValueTask<WorkspaceBridgeListing> ListAsync(
            IReadOnlyList<string> segments,
            int maximumEntries,
            CancellationToken cancellationToken)
        {
            await owner.EnsureCoveredAsync(authority, segments, cancellationToken).ConfigureAwait(false);
            var parameters = JsonSerializer.SerializeToElement(
                new FsLocalListParams(PhysicalUri(owner.workspace, segments), maximumEntries),
                BridgeJsonContext.Default.FsLocalListParams);
            var answer = await owner
                .SendAsync(authority, "maieutics/fs/local/list", parameters, cancellationToken)
                .ConfigureAwait(false);
            if (answer.ValueKind != JsonValueKind.Object ||
                !answer.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
                throw new AgentToolException("ide_result_invalid", "The listing answer is not the documented shape.");

            var builder = ImmutableArray.CreateBuilder<WorkspaceBridgeEntry>(entries.GetArrayLength());
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("name", out var name) ||
                    name.ValueKind != JsonValueKind.String)
                    throw new AgentToolException("ide_result_invalid", "A listing entry carries no name.");

                builder.Add(new WorkspaceBridgeEntry(
                    name.GetString()!,
                    entry.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                        ? kind.GetString()!
                        : "other",
                    entry.TryGetProperty("sizeBytes", out var size) &&
                        size.ValueKind == JsonValueKind.Number &&
                        size.TryGetInt64(out var sizeValue)
                        ? sizeValue
                        : null));
            }

            var truncated = answer.TryGetProperty("truncated", out var truncatedElement) &&
                            truncatedElement.ValueKind == JsonValueKind.True;
            return new WorkspaceBridgeListing(builder.ToImmutable(), truncated);
        }

        public override async Task WriteAsync(
            IReadOnlyList<string> segments,
            string content,
            string? expectedContentHash,
            bool createExclusive,
            CancellationToken cancellationToken)
        {
            await owner.EnsureCoveredAsync(authority, segments, cancellationToken).ConfigureAwait(false);
            var parameters = JsonSerializer.SerializeToElement(
                new FsLocalWriteParams(
                    PhysicalUri(owner.workspace, segments),
                    content,
                    expectedContentHash,
                    createExclusive),
                BridgeJsonContext.Default.FsLocalWriteParams);
            await owner
                .SendAsync(authority, "maieutics/fs/local/write", parameters, cancellationToken)
                .ConfigureAwait(false);
        }

        public override async Task DeleteAsync(
            IReadOnlyList<string> segments,
            CancellationToken cancellationToken)
        {
            await owner.EnsureCoveredAsync(authority, segments, cancellationToken).ConfigureAwait(false);
            var parameters = JsonSerializer.SerializeToElement(
                new FsLocalDeleteParams(PhysicalUri(owner.workspace, segments)),
                BridgeJsonContext.Default.FsLocalDeleteParams);
            await owner
                .SendAsync(authority, "maieutics/fs/local/delete", parameters, cancellationToken)
                .ConfigureAwait(false);
        }

        public override async Task<WorkspaceBridgeSearch?> SearchAsync(
            IReadOnlyList<string> segments,
            string query,
            bool regex,
            bool caseSensitive,
            int maximumMatches,
            IReadOnlyList<string>? include,
            IReadOnlyList<string>? exclude,
            bool respectIgnore,
            CancellationToken cancellationToken)
        {
            await owner.EnsureCoveredAsync(authority, segments, cancellationToken).ConfigureAwait(false);
            var parameters = JsonSerializer.SerializeToElement(
                new FsLocalSearchParams(
                    PhysicalUri(owner.workspace, segments),
                    query,
                    regex,
                    caseSensitive,
                    maximumMatches,
                    include?.ToArray(),
                    exclude?.ToArray(),
                    respectIgnore),
                BridgeJsonContext.Default.FsLocalSearchParams);
            JsonElement answer;
            try
            {
                answer = await owner
                    .SendAsync(authority, "maieutics/fs/local/search", parameters, cancellationToken, tolerateUnsupported: true)
                    .ConfigureAwait(false);
            }
            catch (IdeMethodUnsupportedException)
            {
                return null;
            }

            if (answer.ValueKind != JsonValueKind.Object ||
                !answer.TryGetProperty("matches", out var matches) ||
                matches.ValueKind != JsonValueKind.Array)
                throw new AgentToolException("ide_result_invalid", "The search answer is not the documented shape.");

            var builder = ImmutableArray.CreateBuilder<WorkspaceBridgeSearchMatch>(matches.GetArrayLength());
            foreach (var match in matches.EnumerateArray())
            {
                if (match.ValueKind != JsonValueKind.Object ||
                    !match.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
                    !match.TryGetProperty("line", out var line) || line.ValueKind != JsonValueKind.Number ||
                    !line.TryGetInt32(out var lineValue) ||
                    !match.TryGetProperty("column", out var column) || column.ValueKind != JsonValueKind.Number ||
                    !column.TryGetInt32(out var columnValue) ||
                    !match.TryGetProperty("preview", out var preview) || preview.ValueKind != JsonValueKind.String)
                    throw new AgentToolException("ide_result_invalid", "A search match is not the documented shape.");

                builder.Add(new WorkspaceBridgeSearchMatch(
                    path.GetString()!,
                    lineValue,
                    columnValue,
                    preview.GetString()!));
            }

            var truncated = answer.TryGetProperty("truncated", out var truncatedElement) &&
                            truncatedElement.ValueKind == JsonValueKind.True;
            var scanned = answer.TryGetProperty("scannedBytes", out var scannedElement) &&
                          scannedElement.ValueKind == JsonValueKind.Number &&
                          scannedElement.TryGetInt64(out var scannedValue)
                ? scannedValue
                : 0;
            return new WorkspaceBridgeSearch(builder.ToImmutable(), truncated, scanned);
        }

        public override async Task EnsureCleanAsync(
            IReadOnlyList<string> segments,
            CancellationToken cancellationToken)
        {
            if (segments.Count == 0) return;

            await owner.EnsureCoveredAsync(authority, segments, cancellationToken).ConfigureAwait(false);
            var fileUri = new Uri(owner.workspace.Capture().ToPhysicalPath(segments)).AbsoluteUri;
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("uris");
                writer.WriteStringValue(fileUri);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            buffer.Position = 0;
            using var document = JsonDocument.Parse(buffer);
            var answer = await owner
                .SendAsync(authority, "maieutics/dirty", document.RootElement.Clone(), cancellationToken)
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
                throw new WorkspaceException(
                    "ide_document_dirty",
                    "These documents have unsaved changes; save or revert them before writing: " +
                    string.Join(", ", dirty));
        }

        private static string PhysicalUri(Workspace workspace, IReadOnlyList<string> segments)
        {
            return new Uri(workspace.Capture().ToPhysicalPath(segments)).AbsoluteUri;
        }

        private static string? ReadString(JsonElement answer, string property)
        {
            return answer.ValueKind == JsonValueKind.Object &&
                   answer.TryGetProperty(property, out var element) &&
                   element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }
    }

    /// <summary>Raised when the answering frontend predates the requested method and
    /// the caller asked to tolerate that: a per-call degradation signal, never the
    /// cohort flip.</summary>
    internal sealed class IdeMethodUnsupportedException : Exception
    {
        internal IdeMethodUnsupportedException()
            : base("The IDE frontend does not support this method.")
        {
        }
    }

    private async Task<JsonElement> SendAsync(
        IdeAnnouncerHandle authority,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken,
        bool tolerateUnsupported = false)
    {
        var registration = requests.Begin();
        try
        {
            var envelope = JsonSerializer.SerializeToElement(
                new IdeRequestEnvelope(
                    new IdeRequestPayload(registration.RequestId, method, parameters)),
                IdeFunctionJsonContext.Default.IdeRequestEnvelope);
            var sent = await sockets
                .TrySendToAsync(
                    authority.RegistrationId,
                    new FrontendEventFrame("ide.request", Data: envelope.GetProperty("ideRequest").Clone()),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!sent)
                throw new AgentToolException(
                    "ide_unavailable",
                    "The elected IDE frontend serving the workspace file system is gone. Retry the call; " +
                    "a remaining announcer takes over as the authority.");

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
            {
                if (code == "fs_not_covered")
                {
                    // Defense in depth: the cached coverage said covered, the frontend's
                    // own folder check disagreed. Same verdict as the kernel-side ruling.
                    var uncovered = parameters.ValueKind == JsonValueKind.Object &&
                                    parameters.TryGetProperty("uri", out var coveredUri) &&
                                    coveredUri.ValueKind == JsonValueKind.String
                        ? coveredUri.GetString() ?? "unknown"
                        : "unknown";
                    throw new WorkspaceBridgeCoverageException(uncovered);
                }

                if (code == "ide_method_unsupported")
                {
                    if (tolerateUnsupported)
                    {
                        // Search (and any later per-call-degradable method): the caller
                        // has a full local implementation, so an old frontend degrades
                        // this one call instead of retiring the whole bridge.
                        throw new IdeMethodUnsupportedException();
                    }

                    // The announcing frontend predates the local family; the coherent
                    // degradation is the pre-bridge behavior, process-wide, not a typed
                    // error on every read. Absorbed once; the next acquire returns null.
                    cohortPredates = true;
                    throw new AgentToolException(
                        "workspace_bridge_inactive",
                        "The connected IDE frontend does not serve the workspace file system (it predates " +
                        "the maieutics/fs/local family); the caller falls back to direct file I/O.");
                }

                throw MapFsError(code, answer.ErrorMessage);
            }

            return answer.Value ??
                throw new AgentToolException("ide_result_invalid", "The IDE frontend answered without a value.");
        }
        finally
        {
            requests.Abandon(registration.RequestId);
        }
    }

    private static WorkspaceException MapFsError(string code, string? message)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? string.Empty : $" {message}";
        return code switch
        {
            "fs_not_found" => new WorkspaceException(
                "workspace_path_not_found", "The workspace URI does not identify an existing path."),
            "fs_is_directory" => new WorkspaceException(
                "workspace_not_regular_file", "The workspace URI is a directory, not a regular file."),
            "fs_not_directory" => new WorkspaceException(
                "workspace_not_directory", "The workspace URI does not identify a directory."),
            "fs_not_text" => new WorkspaceException(
                "workspace_invalid_utf8", "The requested resource is not valid UTF-8 text."),
            "fs_too_large" => new WorkspaceException(
                "workspace_file_too_large", "The file exceeds the bridge's read bound."),
            "fs_access_denied" => new WorkspaceException(
                "workspace_access_denied", "The operating system denied access to the requested workspace path."),
            "fs_exists" => new WorkspaceException(
                "workspace_path_exists", "The workspace path already exists."),
            "fs_conflict" => new WorkspaceException(
                "workspace_write_conflict",
                "The file changed on the IDE side since it was read; re-read it and retry the write."),
            _ => new WorkspaceException(
                "workspace_io_error",
                "The workspace operation could not be completed through the IDE bridge." + detail),
        };
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FsLocalReadParams))]
[JsonSerializable(typeof(FsLocalListParams))]
[JsonSerializable(typeof(FsLocalWriteParams))]
[JsonSerializable(typeof(FsLocalDeleteParams))]
[JsonSerializable(typeof(FsLocalSearchParams))]
internal sealed partial class BridgeJsonContext : JsonSerializerContext;

internal sealed record FsLocalReadParams(string Uri);

internal sealed record FsLocalListParams(string Uri, int MaximumEntries);

internal sealed record FsLocalWriteParams(
    string Uri,
    string Content,
    [property: JsonPropertyName("expectedHash")] string? ExpectedHash,
    [property: JsonPropertyName("createExclusive")] bool CreateExclusive);

internal sealed record FsLocalDeleteParams(string Uri);

internal sealed record FsLocalSearchParams(
    string Uri,
    string Query,
    bool Regex,
    bool CaseSensitive,
    int MaximumMatches,
    string[]? Include,
    string[]? Exclude,
    bool RespectIgnore);
