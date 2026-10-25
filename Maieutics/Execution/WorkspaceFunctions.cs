using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Maieutics.Agent;
using Maieutics.Permissions;
using Microsoft.Extensions.AI;

namespace Maieutics.Execution;

internal sealed class WorkspaceFunctions
{
    private const int DefaultPageSize = 100;
    private const int MaximumPageSize = 200;
    private const int DefaultMaximumLines = 200;
    private const int MaximumLines = 1_000;
    private const int MaximumUtf8Bytes = 64 * 1_024;
    private const int MaximumLineUtf8Bytes = MaximumUtf8Bytes;
    private const int MaximumReadScanBytes = 8 * 1_024 * 1_024;
    private const int MaximumResults = 200;
    private const int MaximumRegexPatternCharacters = 512;
    private const int DefaultMaximumFileBytes = 2 * 1_024 * 1_024;
    private const long DefaultMaximumSearchBytes = 64L * 1_024 * 1_024;
    private const int DefaultMaximumFiles = 10_000;
    private const int DefaultMaximumDirectoryEntries = 10_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private static readonly JsonSerializerOptions SerializerOptions =
        new(WorkspaceJsonSerializerContext.Default.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    private readonly int _maximumDirectoryEntries;
    private readonly int _maximumFileBytes;
    private readonly int _maximumFiles;
    private readonly long _maximumSearchBytes;
    private readonly Workspace _workspace;
    private readonly ResourceRegistry? _resources;
    private readonly IWorkspaceIoBridge? _bridge;
    private readonly PermissionPolicyAcquirer? _acquirer;

    internal WorkspaceFunctions(
        Workspace workspace,
        int maximumFiles = DefaultMaximumFiles,
        int maximumDirectoryEntries = DefaultMaximumDirectoryEntries,
        int maximumFileBytes = DefaultMaximumFileBytes,
        long maximumSearchBytes = DefaultMaximumSearchBytes,
        ResourceRegistry? resources = null,
        IWorkspaceIoBridge? bridge = null,
        PermissionPolicyAcquirer? acquirer = null)
    {
        this._workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this._resources = resources;
        this._bridge = bridge;
        this._acquirer = acquirer;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDirectoryEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSearchBytes);
        this._maximumFiles = maximumFiles;
        this._maximumDirectoryEntries = maximumDirectoryEntries;
        this._maximumFileBytes = maximumFileBytes;
        this._maximumSearchBytes = maximumSearchBytes;

        Functions =
        [
            CreateFunction(
                (Func<AIFunctionArguments, string?, string?, int?, CancellationToken, ValueTask<ListDirectoryResult>>)ListDirectoryAsync,
                "list_directory",
                "Lists one workspace directory without recursion. Omit uri to list the workspace root. " +
                "When an IDE frontend is attached, the listing is served through it."),
            CreateFunction(
                (Func<AIFunctionArguments, string, int?, int?, CancellationToken, ValueTask<ReadTextResult>>)ReadTextAsync,
                "read_text",
                "Reads a bounded range of lines from a UTF-8 text resource: a workspace://local " +
                "URI or any other registered resource URI (see list_resources). When an IDE frontend " +
                "is attached, workspace reads answer the editor's text state — unsaved changes included."),
            CreateFunction(
                (Func<AIFunctionArguments, string, string?, bool?, bool?, int?, string[]?, string[]?, bool?, CancellationToken, ValueTask<SearchTextResult>>)
                SearchTextAsync,
                "search_text",
                "Searches UTF-8 workspace files recursively with a literal query or bounded regular expression. " +
                "When an IDE frontend is attached and covers the searched tree, the frontend's provider-mediated " +
                "scan serves the search; otherwise the local disk scan does. Search reads saved file state either " +
                "way — even when an IDE frontend serves read_text from unsaved editor buffers — so the two may " +
                "disagree on files with unsaved changes.")
        ];
    }

    internal IReadOnlyList<AIFunction> Functions { get; }

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

    [Description("Lists one workspace directory without recursion.")]
    private async ValueTask<ListDirectoryResult> ListDirectoryAsync(
        AIFunctionArguments arguments,
        [Description("A workspace://local URI. Omit to list the workspace root.")]
        string? uri = null,
        [Description("An opaque cursor returned by an earlier page.")]
        string? cursor = null,
        [Description("The number of entries to return, from 1 through 200.")]
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _workspace.Capture();
            var requestedPageSize = pageSize ?? DefaultPageSize;
            if (requestedPageSize is < 1 or > MaximumPageSize)
                throw new WorkspaceException(
                    "workspace_invalid_arguments",
                    $"pageSize must be between 1 and {MaximumPageSize}.");

            var logicalRoot = snapshot.ResolveLogical(uri, allowRoot: true);
            EnforceRead(snapshot, logicalRoot.Segments, logicalRoot.DisplayPath, arguments);
            List<WorkspaceDirectoryEntry> entries;
            var bridgedChannel = WorkspaceToolCalls.AcquireBridge(_bridge, arguments);
            if (bridgedChannel is { } channel)
            {
                try
                {
                    var listing = await channel
                        .ListAsync(logicalRoot.Segments, _maximumDirectoryEntries, cancellationToken)
                        .ConfigureAwait(false);
                    if (listing.Truncated)
                        throw new WorkspaceException(
                            "workspace_directory_too_large",
                            $"A directory cannot contain more than {_maximumDirectoryEntries} visible entries.");

                    entries = listing.Entries
                        .Select(entry => new WorkspaceDirectoryEntry(
                            entry.Name,
                            snapshot.ToWorkspaceUri([.. logicalRoot.Segments, entry.Name]),
                            entry.Kind,
                            entry.SizeBytes))
                        .ToList();
                }
                catch (WorkspaceBridgeCoverageException)
                {
                    // The frontend's folders do not cover this directory; list locally.
                    entries = EnumerateLocalDirectory(snapshot, uri, cancellationToken);
                }
            }
            else
            {
                entries = EnumerateLocalDirectory(snapshot, uri, cancellationToken);
            }

            var comparisons = 0;
            entries.Sort((left, right) =>
            {
                if ((++comparisons & 255) == 0) cancellationToken.ThrowIfCancellationRequested();

                return StringComparer.Ordinal.Compare(left.Name, right.Name);
            });

            var decodedCursor = DecodeCursor(cursor, logicalRoot.Uri, snapshot.Version);
            var offset = decodedCursor is null
                ? 0
                : entries.FindIndex(entry => StringComparer.Ordinal.Compare(entry.Name, decodedCursor.LastName) > 0);
            if (offset < 0) offset = entries.Count;

            var page = entries
                .Skip(offset)
                .Take(requestedPageSize)
                .ToImmutableArray();
            var nextOffset = offset + page.Length;
            return new ListDirectoryResult(
                logicalRoot.Uri,
                page,
                nextOffset < entries.Count
                    ? EncodeCursor(logicalRoot.Uri, snapshot.Version, page[^1].Name)
                    : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw ToAgentToolException(exception);
        }
    }

    /// <summary>Enforces the session's read policy on one workspace-plane path: the
    /// model-tool surface baseline (the workspace root) minus configuration denials, so a
    /// denial bites the same whichever path — bridge or disk — serves the call.</summary>
    private void EnforceRead(
        WorkspaceSnapshot snapshot,
        IReadOnlyList<string> segments,
        string displayPath,
        AIFunctionArguments arguments)
    {
        WorkspacePermissionSurface.Enforce(
            _acquirer,
            _workspace,
            PermissionKind.Read,
            snapshot.ToPhysicalPath(segments),
            displayPath,
            ParseSession(arguments));
    }

    private static AgentSessionId? ParseSession(AIFunctionArguments arguments)
    {
        return WorkspaceToolCalls.TryGetSessionId(arguments) is { } text &&
               AgentSessionId.TryParse(text, out var sessionId)
            ? sessionId
            : null;
    }

    /// <summary>The physical path a bridged match refers to. <see cref="Path.GetFullPath" />
    /// collapses any traversal the frontend sent, and the judge then denies a path outside
    /// the workspace root, so a hostile relative path cannot pass as covered.</summary>
    private static string BridgedMatchPhysicalPath(string rootPhysical, string relativePath)
    {
        return Path.GetFullPath(Path.Combine(
            rootPhysical,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>Maps a bridged match's root-relative posix path onto the workspace
    /// URI domain; empty for a malformed path (the frontend is trusted, but a
    /// traversal segment must never reach the model as an addressable URI).</summary>
    private static string ToRootedUri(
        WorkspaceSnapshot snapshot,
        IReadOnlyList<string> rootSegments,
        string relativePath)
    {
        var segments = relativePath.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
            return string.Empty;

        return snapshot.ToWorkspaceUri([.. rootSegments, .. segments]);
    }

    /// <summary>The local directory enumeration behind the un-bridged path, shared by
    /// the no-frontend and uncovered-fallback branches.</summary>
    private List<WorkspaceDirectoryEntry> EnumerateLocalDirectory(
        WorkspaceSnapshot snapshot,
        string? uri,
        CancellationToken cancellationToken)
    {
        var directory = snapshot.Resolve(uri);
        if (!directory.IsDirectory)
            throw new WorkspaceException(
                "workspace_not_directory",
                "The workspace URI does not identify a directory.");

        var entries = new List<WorkspaceDirectoryEntry>();
        foreach (var entry in new DirectoryInfo(directory.FullPath).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(CreateEntry(snapshot, entry, directory.Segments));
            if (entries.Count > _maximumDirectoryEntries)
                throw new WorkspaceException(
                    "workspace_directory_too_large",
                    $"A directory cannot contain more than {_maximumDirectoryEntries} visible entries.");
        }

        return entries;
    }

    [Description("Reads a bounded range of lines from a UTF-8 text resource.")]
    private async ValueTask<ReadTextResult> ReadTextAsync(
        AIFunctionArguments arguments,
        [Description("The resource URI: workspace://local/... or any registered resource URI.")]
        string uri,
        [Description("The first one-based line to return.")]
        int? startLine = null,
        [Description("The maximum number of lines to return, from 1 through 1000.")]
        int? maxLines = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(uri))
                throw new WorkspaceException("workspace_invalid_arguments", "uri is required.");

            var requestedStartLine = startLine ?? 1;
            var maximumLines = maxLines ?? DefaultMaximumLines;
            if (requestedStartLine < 1 || maximumLines is < 1 or > MaximumLines)
                throw new WorkspaceException(
                    "workspace_invalid_arguments",
                    $"startLine must be positive and maxLines must be between 1 and {MaximumLines}.");

            if (_resources is not null)
            {
                var resource = await ResolveResourceAsync(
                    uri,
                    requestedStartLine,
                    maximumLines,
                    cancellationToken).ConfigureAwait(false);
                if (resource is { } result) return result;
            }

            var snapshot = _workspace.Capture();
            var logical = snapshot.ResolveLogical(uri, allowRoot: false);
            // The workspace-plane read is judged against the session's policy before the
            // bridge or the disk serves it; registered resource URIs are their own plane.
            EnforceRead(snapshot, logical.Segments, logical.DisplayPath, arguments);
            var bridgedChannel = WorkspaceToolCalls.AcquireBridge(_bridge, arguments);
            if (bridgedChannel is { } channel)
            {
                // The bridge answers the IDE's authoritative text state (dirty buffers
                // included) and the kernel windows it with the local read conventions.
                // The coverage ruling: a path outside the frontend's workspace folders
                // is served locally — the bridge never claims state it does not hold.
                try
                {
                    var text = await channel
                        .ReadTextAsync(logical.Segments, MaximumReadScanBytes, cancellationToken)
                        .ConfigureAwait(false);
                    return await ReadTextCoreAsync(
                        logical.Uri,
                        new MemoryStream(StrictUtf8.GetBytes(text)),
                        requestedStartLine,
                        maximumLines,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (WorkspaceBridgeCoverageException)
                {
                }
            }

            var file = snapshot.Resolve(uri, false);
            if (file.IsDirectory)
                throw new WorkspaceException(
                    "workspace_not_file",
                    "The workspace URI does not identify a regular file.");

            if (!file.IsRegularFile)
                throw new WorkspaceException(
                    "workspace_not_regular_file",
                    "Workspace text tools can read only regular files.");

            return await ReadTextCoreAsync(
                file.Uri,
                snapshot.OpenVerifiedRead(file),
                requestedStartLine,
                maximumLines,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ResourceException exception)
        {
            throw new AgentToolException(exception.Code, exception.Message);
        }
        catch (DecoderFallbackException)
        {
            throw new AgentToolException(
                "workspace_invalid_utf8",
                "The requested resource is not valid UTF-8 text.");
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw ToAgentToolException(exception);
        }
    }

    /// <summary>Resolves a non-workspace resource URI through the registry and reads its
    /// body into the shared bounded line reader. Returns null when the URI belongs to the
    /// built-in workspace plane (or cannot be a resource URI at all), so the original
    /// workspace path with its exact error codes stays authoritative for those URIs.</summary>
    private async ValueTask<ReadTextResult?> ResolveResourceAsync(
        string uri,
        int requestedStartLine,
        int maximumLines,
        CancellationToken cancellationToken)
    {
        if (!ResourceRegistry.TryParseUri(uri, out var parsed))
            throw new AgentToolException(
                "resource_invalid_uri",
                "The value must be an absolute resource URI without a fragment.");

        var provider = _resources!.Resolve(parsed);
        if (provider is null)
            throw new AgentToolException(
                "resource_unknown_scheme",
                $"No registered provider reads the resource URI scheme '{parsed.Scheme}://'.");

        if (provider is WorkspaceResourceProvider) return null;

        var read = await provider.ReadAsync(
            uri,
            new ResourceReadRequest(MaximumReadScanBytes),
            cancellationToken).ConfigureAwait(false);
        return await ReadTextCoreAsync(
            uri,
            read.Content,
            requestedStartLine,
            maximumLines,
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Searches UTF-8 workspace files recursively.")]
    private async ValueTask<SearchTextResult> SearchTextAsync(
        AIFunctionArguments arguments,
        [Description("The non-empty literal or regular-expression query.")]
        string query,
        [Description("A workspace://local file or directory URI. Omit to search the root.")]
        string? uri = null,
        [Description("Whether query is a regular expression. Defaults to false.")]
        bool? regex = null,
        [Description("Whether matching is case-sensitive. Defaults to true.")]
        bool? caseSensitive = null,
        [Description("The maximum number of matches to return, from 1 through 200.")]
        int? maxResults = null,
        [Description("Globs a file's relative path must match (rg style: **, *, ?; a pattern without a slash matches the file name at any depth). At most 16.")]
        string[]? include = null,
        [Description("Globs a file's relative path must not match. At most 16.")]
        string[]? exclude = null,
        [Description("Whether the frontend's fast search respects VCS ignore files (skips dependencies and build output). Defaults to true; only the frontend's fast search implements this.")]
        bool? respectIgnore = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(query))
                throw new WorkspaceException(
                    "workspace_invalid_arguments",
                    "query is required and cannot be empty.");
            ValidateSearchFilters(include, exclude);

            var requestedMaximumResults = maxResults ?? MaximumResults;
            if (requestedMaximumResults is < 1 or > MaximumResults)
                throw new WorkspaceException(
                    "workspace_invalid_arguments",
                    $"maxResults must be between 1 and {MaximumResults}.");

            Regex? compiledRegex = null;
            if (regex is true)
            {
                if (query.Length > MaximumRegexPatternCharacters)
                    throw new WorkspaceException(
                        "workspace_invalid_arguments",
                        $"Regular expression queries cannot exceed {MaximumRegexPatternCharacters} characters.");

                try
                {
                    compiledRegex = new Regex(
                        query,
                        RegexOptions.CultureInvariant |
                        (caseSensitive is false ? RegexOptions.IgnoreCase : RegexOptions.None),
                        RegexTimeout);
                }
                catch (ArgumentException exception)
                {
                    throw new WorkspaceException(
                        "workspace_invalid_regex",
                        "query is not a valid regular expression.",
                        exception);
                }
            }

            var snapshot = _workspace.Capture();
            var searchRoot = snapshot.Resolve(uri);
            // The session's read policy gates the whole search: the root is enforced once,
            // and one composed snapshot judges every file the scan visits or the frontend
            // returns, so a denied subtree cannot leak through match previews.
            var readPolicy = _acquirer is null
                ? null
                : WorkspacePermissionSurface.Acquire(_acquirer, _workspace, ParseSession(arguments));
            var searchLogicalRoot = snapshot.ResolveLogical(uri, allowRoot: true);
            if (readPolicy is not null &&
                !WorkspacePermissionSurface.IsAllowed(
                    readPolicy,
                    PermissionKind.Read,
                    snapshot.ToPhysicalPath(searchLogicalRoot.Segments)))
            {
                throw WorkspacePermissionSurface.Forbidden(PermissionKind.Read, searchLogicalRoot.DisplayPath);
            }

            // The search generalization (ADR 0042 stage F-II): like the file
            // operations, the covered search root is served by the elected frontend's
            // provider-mediated scan — one bounded contract both sides implement. A
            // frontend predating the method, a root outside its folders, or no
            // frontend at all falls back to the local disk scan.
            var bridgedChannel = WorkspaceToolCalls.AcquireBridge(_bridge, arguments);
            if (bridgedChannel is { } channel)
            {
                var bridgedRoot = snapshot.ResolveLogical(uri, allowRoot: true);
                var bridgedRootPhysical = snapshot.ToPhysicalPath(bridgedRoot.Segments);
                try
                {
                    var bridged = await channel.SearchAsync(
                        bridgedRoot.Segments,
                        query,
                        compiledRegex is not null,
                        caseSensitive is not false,
                        requestedMaximumResults,
                        include,
                        exclude,
                        respectIgnore is not false,
                        cancellationToken).ConfigureAwait(false);
                    if (bridged is not null)
                        return new SearchTextResult(
                            bridgedRoot.Uri,
                            [
                                .. bridged.Matches
                                    .Where(match => readPolicy is null || WorkspacePermissionSurface.IsAllowed(
                                        readPolicy,
                                        PermissionKind.Read,
                                        BridgedMatchPhysicalPath(bridgedRootPhysical, match.RelativePath)))
                                    .Select(match => new WorkspaceSearchMatch(
                                            ToRootedUri(snapshot, bridgedRoot.Segments, match.RelativePath),
                                            match.Line,
                                            match.Column,
                                            match.Preview))
                                    .Where(static match => match.Uri.Length > 0)
                            ],
                            bridged.Truncated,
                            bridged.ScannedBytes,
                            0,
                            0,
                            0,
                            0);
                }
                catch (WorkspaceBridgeCoverageException)
                {
                    // Uncovered root: the local channel owns this tree.
                }
                catch (AgentToolException exception) when (exception.Code == "ide_method_unsupported")
                {
                    // Defensive: SearchAsync converts this to null; kept for symmetry
                    // with the coverage fallback so any future path here degrades, too.
                }
            }

            return await SearchCoreAsync(
                snapshot,
                searchRoot,
                query,
                compiledRegex,
                caseSensitive is not false,
                requestedMaximumResults,
                include,
                exclude,
                readPolicy,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RegexMatchTimeoutException)
        {
            throw new AgentToolException(
                "workspace_regex_timeout",
                "The regular expression exceeded the per-file time limit.");
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw ToAgentToolException(exception);
        }
    }

    private static WorkspaceDirectoryEntry CreateEntry(
        WorkspaceSnapshot snapshot,
        FileSystemInfo entry,
        IReadOnlyList<string> parentSegments)
    {
        var segments = new List<string>(parentSegments.Count + 1);
        segments.AddRange(parentSegments);
        segments.Add(entry.Name);
        var uri = snapshot.ToWorkspaceUri(segments);
        var attributes = entry.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null)
            return new WorkspaceDirectoryEntry(entry.Name, uri, "symbolic_link");

        if ((attributes & FileAttributes.Directory) != 0)
            return new WorkspaceDirectoryEntry(entry.Name, uri, "directory");

        if ((attributes & FileAttributes.Normal) != 0 || File.Exists(entry.FullName))
            return new WorkspaceDirectoryEntry(
                entry.Name,
                uri,
                "file",
                ((FileInfo)entry).Length);

        return new WorkspaceDirectoryEntry(entry.Name, uri, "other");
    }

    private static DirectoryCursor? DecodeCursor(string? cursor, string uri, long workspaceVersion)
    {
        if (cursor is null) return null;

        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var value = JsonSerializer.Deserialize(
                Convert.FromBase64String(encoded),
                WorkspaceJsonSerializerContext.Default.DirectoryCursor);
            if (value is null || value.LastName.Length == 0 || value.WorkspaceVersion != workspaceVersion ||
                !string.Equals(value.Uri, uri, StringComparison.Ordinal))
                throw new FormatException();

            return value;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or JsonException)
        {
            throw new WorkspaceException(
                "workspace_invalid_cursor",
                "The directory cursor is invalid for this workspace URI.",
                exception);
        }
    }

    private static string EncodeCursor(string uri, long workspaceVersion, string lastName)
    {
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new DirectoryCursor(uri, workspaceVersion, lastName),
            WorkspaceJsonSerializerContext.Default.DirectoryCursor));
        return encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads the shared bounded line window from one open resource body.
    /// Takes ownership of <paramref name="content"/> and disposes it on every path.</summary>
    private static async Task<ReadTextResult> ReadTextCoreAsync(
        string uri,
        Stream content,
        int requestedStartLine,
        int maximumLines,
        CancellationToken cancellationToken)
    {
        await using var stream = content.ConfigureAwait(false);
        var reader = new BoundedUtf8LineReader(content);
        var text = new StringBuilder();
        var outputBytes = 0;
        var lineNumber = 0;
        var returnedLines = 0;
        int? actualStartLine = null;
        int? actualEndLine = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (returnedLines >= maximumLines)
            {
                var hasMore = await reader.HasMoreDataAsync(cancellationToken).ConfigureAwait(false);
                return new ReadTextResult(
                    uri,
                    actualStartLine,
                    actualEndLine,
                    text.ToString(),
                    hasMore,
                    hasMore ? actualEndLine + 1 : null);
            }

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                return new ReadTextResult(
                    uri,
                    actualStartLine,
                    actualEndLine,
                    text.ToString(),
                    false,
                    null);

            lineNumber++;
            if (lineNumber < requestedStartLine) continue;

            var separatorBytes = actualStartLine.HasValue ? 1 : 0;
            var lineBytes = StrictUtf8.GetByteCount(line);
            if (outputBytes + separatorBytes + lineBytes > MaximumUtf8Bytes)
                return new ReadTextResult(
                    uri,
                    actualStartLine,
                    actualEndLine,
                    text.ToString(),
                    true,
                    lineNumber);

            if (separatorBytes != 0)
            {
                text.Append('\n');
                outputBytes++;
            }

            actualStartLine ??= lineNumber;
            actualEndLine = lineNumber;
            text.Append(line);
            outputBytes += lineBytes;
            returnedLines++;
        }
    }

    private async Task<SearchTextResult> SearchCoreAsync(
        WorkspaceSnapshot snapshot,
        WorkspacePath searchRoot,
        string query,
        Regex? regex,
        bool caseSensitive,
        int maximumResults,
        string[]? include,
        string[]? exclude,
        EffectivePolicy? readPolicy,
        CancellationToken cancellationToken)
    {
        var matches = ImmutableArray.CreateBuilder<WorkspaceSearchMatch>();
        var statistics = new SearchStatistics();
        var truncated = false;

        foreach (var (file, fileSegments) in EnumerateFiles(snapshot, searchRoot, statistics, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (statistics.VisitedFiles >= _maximumFiles)
            {
                truncated = true;
                break;
            }

            // The session's read policy is a filter like the globs: a denied file is never
            // opened, so neither its content nor its preview can surface through a match.
            if (readPolicy is not null &&
                !WorkspacePermissionSurface.IsAllowed(readPolicy, PermissionKind.Read, file.FullName))
            {
                statistics.SkippedByFilter++;
                continue;
            }

            // The filter contract is shared with the frontend's fast search: the
            // globs judge the file's relative path before its bytes are read.
            var relativePath = WorkspaceSnapshot.ToDisplayPath(fileSegments);
            if (!SearchGlobs.Passes(relativePath, file.Name, include, exclude))
            {
                statistics.SkippedByFilter++;
                continue;
            }

            statistics.VisitedFiles++;
            if (statistics.ScannedBytes >= _maximumSearchBytes)
            {
                truncated = true;
                break;
            }

            var remainingSearchBytes = _maximumSearchBytes - statistics.ScannedBytes;
            var maximumReadBytes = (int)Math.Min(_maximumFileBytes, remainingSearchBytes);
            BoundedFileContent bounded;
            try
            {
                bounded = await snapshot.ReadAsync(
                    file.FullName,
                    fileSegments,
                    maximumReadBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (WorkspaceException exception) when (
                exception.Code == "workspace_not_regular_file" && searchRoot.IsDirectory)
            {
                statistics.SkippedNonRegularFiles++;
                continue;
            }

            if (bounded.ExceededLimit)
            {
                if (maximumReadBytes < _maximumFileBytes)
                {
                    truncated = true;
                    break;
                }

                statistics.SkippedLargeFiles++;
                continue;
            }

            if (statistics.ScannedBytes + bounded.Bytes.Length > _maximumSearchBytes)
            {
                truncated = true;
                break;
            }

            statistics.ScannedBytes += bounded.Bytes.Length;
            string content;
            try
            {
                content = StrictUtf8.GetString(bounded.Bytes);
            }
            catch (DecoderFallbackException)
            {
                statistics.SkippedBinaryFiles++;
                continue;
            }

            if (content.Length > 0 && content[0] == '\uFEFF') content = content[1..];

            if (content.Any(static character =>
                    char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            {
                statistics.SkippedBinaryFiles++;
                continue;
            }

            var fileUri = snapshot.ToWorkspaceUri(fileSegments);
            var lineMap = new TextLineMap(content);
            if (regex is null)
                AddLiteralMatches(
                    matches,
                    content,
                    query,
                    caseSensitive,
                    fileUri,
                    lineMap,
                    maximumResults);
            else
                AddRegexMatches(matches, content, regex, fileUri, lineMap, maximumResults);

            if (matches.Count >= maximumResults)
            {
                truncated = true;
                break;
            }
        }

        return new SearchTextResult(
            searchRoot.Uri,
            matches.DrainToImmutable(),
            truncated,
            statistics.ScannedBytes,
            statistics.SkippedBinaryFiles,
            statistics.SkippedLargeFiles,
            statistics.SkippedSymbolicLinks,
            statistics.SkippedNonRegularFiles,
            statistics.SkippedByFilter);
    }

    /// <summary>Yields regular files depth-first with the logical segments that identify
    /// each file's workspace URI. Reparse points are never descended except the registered
    /// <c>projects/&lt;name&gt;</c> hop, which is followed into its canonical target while
    /// files are reported under the link's logical path; every other link counts as a
    /// skipped symbolic link.</summary>
    private IEnumerable<(FileInfo File, IReadOnlyList<string> Segments)> EnumerateFiles(
        WorkspaceSnapshot snapshot,
        WorkspacePath searchRoot,
        SearchStatistics statistics,
        CancellationToken cancellationToken)
    {
        if (!searchRoot.IsDirectory)
        {
            yield return (new FileInfo(searchRoot.FullPath), searchRoot.Segments);
            yield break;
        }

        var directories = new Stack<(DirectoryInfo Directory, IReadOnlyList<string> Segments)>();
        directories.Push((new DirectoryInfo(searchRoot.FullPath), searchRoot.Segments));
        while (directories.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = GetSortedEntries(current.Directory, statistics, cancellationToken);
            var childDirectories = new List<(DirectoryInfo Directory, IReadOnlyList<string> Segments)>();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = entry.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null)
                {
                    var hopSegments = AppendSegment(current.Segments, entry.Name);
                    var hopTarget = snapshot.ResolveRegisteredLink(
                        hopSegments,
                        hopSegments.Count - 1,
                        Path.Combine(current.Directory.FullName, entry.Name));
                    if (hopTarget is null)
                    {
                        statistics.SkippedSymbolicLinks++;
                        continue;
                    }

                    childDirectories.Add((new DirectoryInfo(hopTarget), hopSegments));
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!IsDeniedDirectoryName(entry.Name))
                    {
                        var segments = AppendSegment(current.Segments, entry.Name);
                        childDirectories.Add(((DirectoryInfo)entry, segments));
                    }

                    continue;
                }

                if ((attributes & FileAttributes.Device) != 0 || entry is not FileInfo file)
                {
                    statistics.SkippedNonRegularFiles++;
                    continue;
                }

                yield return (file, AppendSegment(current.Segments, entry.Name));
            }

            for (var index = childDirectories.Count - 1; index >= 0; index--)
                directories.Push(childDirectories[index]);
        }
    }

    private static IReadOnlyList<string> AppendSegment(IReadOnlyList<string> segments, string name)
    {
        var appended = new List<string>(segments.Count + 1);
        appended.AddRange(segments);
        appended.Add(name);
        return appended;
    }

    /// <summary>Shape validation for the search filter knobs: bounded count and
    /// length, no empty values or NULs — the pattern semantics live in
    /// <see cref="SearchGlobs" /> (local) and the frontend's matcher (bridge).</summary>
    private static void ValidateSearchFilters(string[]? include, string[]? exclude)
    {
        foreach (var filters in new[] { include, exclude })
        {
            if (filters is null) continue;
            if (filters.Length > 16)
                throw new WorkspaceException(
                    "workspace_invalid_arguments",
                    "At most 16 include/exclude globs are accepted per search.");

            foreach (var pattern in filters)
            {
                if (pattern.Length == 0 || pattern.Length > 128 || pattern.Contains('\0'))
                    throw new WorkspaceException(
                        "workspace_invalid_arguments",
                        "Search globs must be 1-128 characters.");
            }
        }
    }

    private static bool IsDeniedDirectoryName(string name)
    {
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(WorkspaceHome.StateDirectoryName, StringComparison.OrdinalIgnoreCase);
    }

    private FileSystemInfo[] GetSortedEntries(
        DirectoryInfo directory,
        SearchStatistics statistics,
        CancellationToken cancellationToken)
    {
        var entries = new List<FileSystemInfo>();
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            statistics.VisitedEntries++;
            if (statistics.VisitedEntries > _maximumDirectoryEntries)
                throw new WorkspaceException(
                    "workspace_directory_too_large",
                    $"A search cannot enumerate more than {_maximumDirectoryEntries} workspace entries.");

            entries.Add(entry);
        }

        var comparisons = 0;
        entries.Sort((left, right) =>
        {
            if ((++comparisons & 255) == 0) cancellationToken.ThrowIfCancellationRequested();

            return StringComparer.Ordinal.Compare(left.Name, right.Name);
        });
        return entries.ToArray();
    }

    private static void AddLiteralMatches(
        ImmutableArray<WorkspaceSearchMatch>.Builder matches,
        string content,
        string query,
        bool caseSensitive,
        string uri,
        TextLineMap lineMap,
        int maximumResults)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var offset = 0;
        while (offset <= content.Length - query.Length && matches.Count < maximumResults)
        {
            var index = content.IndexOf(query, offset, comparison);
            if (index < 0) return;

            matches.Add(lineMap.CreateMatch(index, uri));
            offset = index + query.Length;
        }
    }

    private static void AddRegexMatches(
        ImmutableArray<WorkspaceSearchMatch>.Builder matches,
        string content,
        Regex regex,
        string uri,
        TextLineMap lineMap,
        int maximumResults)
    {
        foreach (Match match in regex.Matches(content))
        {
            matches.Add(lineMap.CreateMatch(match.Index, uri));
            if (matches.Count >= maximumResults) return;
        }
    }

    private static bool IsRecoverable(Exception exception)
    {
        return exception is WorkspaceException or UnauthorizedAccessException or IOException;
    }

    private static AgentToolException ToAgentToolException(Exception exception)
    {
        return exception switch
        {
            WorkspaceException workspaceException =>
                new AgentToolException(workspaceException.Code, workspaceException.Message),
            FileNotFoundException or DirectoryNotFoundException => new AgentToolException(
                "workspace_path_not_found",
                "The workspace URI does not identify an existing path."),
            UnauthorizedAccessException => new AgentToolException(
                "workspace_access_denied",
                "The operating system denied access to the requested workspace path."),
            IOException => new AgentToolException(
                "workspace_io_error",
                "The workspace operation could not be completed because of an I/O error."),
            _ => throw new ArgumentOutOfRangeException(nameof(exception), exception, null)
        };
    }

    private sealed class BoundedUtf8LineReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[4_096];
        private readonly byte[] _lineBuffer = new byte[MaximumLineUtf8Bytes + 3];
        private int _bufferCount;
        private int _bufferOffset;
        private bool _firstLine = true;
        private int _scannedBytes;

        internal async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var lineLength = 0;
            while (await EnsureBufferAsync(cancellationToken).ConfigureAwait(false))
            {
                var value = _buffer[_bufferOffset++];
                if (value == 0)
                    throw new WorkspaceException(
                        "workspace_binary_file",
                        "The requested file contains binary data.");

                if (value == (byte)'\n') return DecodeLine(lineLength);

                if (lineLength == _lineBuffer.Length) throw LineTooLong();

                _lineBuffer[lineLength++] = value;
            }

            return lineLength == 0 ? null : DecodeLine(lineLength);
        }

        internal ValueTask<bool> HasMoreDataAsync(CancellationToken cancellationToken)
        {
            return EnsureBufferAsync(cancellationToken);
        }

        private async ValueTask<bool> EnsureBufferAsync(CancellationToken cancellationToken)
        {
            if (_bufferOffset < _bufferCount) return true;

            var remaining = MaximumReadScanBytes - _scannedBytes;
            var requested = Math.Min(_buffer.Length, remaining + 1);
            _bufferCount = await stream.ReadAsync(_buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            _bufferOffset = 0;
            _scannedBytes += _bufferCount;
            if (_scannedBytes > MaximumReadScanBytes)
                throw new WorkspaceException(
                    "workspace_file_too_large",
                    $"read_text scans at most {MaximumReadScanBytes} bytes per call.");

            return _bufferCount != 0;
        }

        private string DecodeLine(int length)
        {
            if (length > 0 && _lineBuffer[length - 1] == (byte)'\r') length--;

            var line = StrictUtf8.GetString(_lineBuffer, 0, length);
            if (_firstLine)
            {
                _firstLine = false;
                if (line.Length > 0 && line[0] == '\uFEFF') line = line[1..];
            }

            if (StrictUtf8.GetByteCount(line) > MaximumLineUtf8Bytes) throw LineTooLong();

            if (line.Any(static character => char.IsControl(character) && character != '\t'))
                throw new WorkspaceException(
                    "workspace_binary_file",
                    "The requested file contains binary control characters.");

            return line;
        }

        private static WorkspaceException LineTooLong()
        {
            return new WorkspaceException(
                "workspace_line_too_long",
                $"A text line cannot exceed {MaximumLineUtf8Bytes} UTF-8 bytes.");
        }
    }

    private sealed class TextLineMap
    {
        private const int MaximumPreviewCharacters = 512;
        private readonly string _content;
        private readonly int[] _lineStarts;

        internal TextLineMap(string content)
        {
            this._content = content;
            var starts = new List<int> { 0 };
            for (var index = 0; index < content.Length; index++)
                if (content[index] == '\n' && index + 1 < content.Length)
                    starts.Add(index + 1);

            _lineStarts = starts.ToArray();
        }

        internal WorkspaceSearchMatch CreateMatch(int index, string uri)
        {
            var lineIndex = Array.BinarySearch(_lineStarts, index);
            if (lineIndex < 0) lineIndex = ~lineIndex - 1;

            var lineStart = _lineStarts[lineIndex];
            var lineEnd = lineIndex + 1 < _lineStarts.Length
                ? _lineStarts[lineIndex + 1] - 1
                : _content.Length;
            if (lineEnd > lineStart && _content[lineEnd - 1] == '\r') lineEnd--;

            var previewStart = lineStart;
            if (lineEnd - lineStart > MaximumPreviewCharacters)
                previewStart = Math.Clamp(
                    index - MaximumPreviewCharacters / 4,
                    lineStart,
                    lineEnd - MaximumPreviewCharacters);

            var previewEnd = Math.Min(lineEnd, previewStart + MaximumPreviewCharacters);
            if (previewStart > lineStart && char.IsLowSurrogate(_content[previewStart])) previewStart++;

            if (previewEnd > previewStart && previewEnd < lineEnd &&
                char.IsHighSurrogate(_content[previewEnd - 1])) previewEnd--;

            return new WorkspaceSearchMatch(
                uri,
                lineIndex + 1,
                index - lineStart + 1,
                _content[previewStart..previewEnd]);
        }
    }

    private sealed class SearchStatistics
    {
        internal long ScannedBytes { get; set; }
        internal int SkippedBinaryFiles { get; set; }
        internal int SkippedLargeFiles { get; set; }
        internal int SkippedSymbolicLinks { get; set; }
        internal int SkippedNonRegularFiles { get; set; }
        internal int SkippedByFilter { get; set; }
        internal int VisitedFiles { get; set; }
        internal int VisitedEntries { get; set; }
    }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(ListDirectoryResult))]
[JsonSerializable(typeof(DirectoryCursor))]
[JsonSerializable(typeof(ReadTextResult))]
[JsonSerializable(typeof(SearchTextResult))]
internal sealed partial class WorkspaceJsonSerializerContext : JsonSerializerContext;

internal sealed record ListDirectoryResult(
    string Uri,
    ImmutableArray<WorkspaceDirectoryEntry> Entries,
    string? NextCursor);

internal sealed record WorkspaceDirectoryEntry(
    string Name,
    string Uri,
    string Kind,
    long? SizeBytes = null);

internal sealed record DirectoryCursor(string Uri, long WorkspaceVersion, string LastName);

internal sealed record ReadTextResult(
    string Uri,
    int? StartLine,
    int? EndLine,
    string Text,
    bool Truncated,
    int? NextStartLine);

internal sealed record SearchTextResult(
    string Uri,
    ImmutableArray<WorkspaceSearchMatch> Matches,
    bool Truncated,
    long ScannedBytes,
    int SkippedBinaryFiles,
    int SkippedLargeFiles,
    int SkippedSymbolicLinks,
    int SkippedNonRegularFiles,
    int SkippedByFilter = 0);

internal sealed record WorkspaceSearchMatch(
    string Uri,
    int Line,
    int Column,
    string Preview);
