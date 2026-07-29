using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Maieutics.Agent;
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

    private readonly int maximumDirectoryEntries;
    private readonly int maximumFileBytes;
    private readonly int maximumFiles;
    private readonly long maximumSearchBytes;
    private readonly Workspace workspace;

    internal WorkspaceFunctions(Workspace workspace)
        : this(
            workspace,
            DefaultMaximumFiles,
            DefaultMaximumDirectoryEntries,
            DefaultMaximumFileBytes,
            DefaultMaximumSearchBytes)
    {
    }

    internal WorkspaceFunctions(
        Workspace workspace,
        int maximumFiles,
        int maximumDirectoryEntries,
        int maximumFileBytes = DefaultMaximumFileBytes,
        long maximumSearchBytes = DefaultMaximumSearchBytes)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDirectoryEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSearchBytes);
        this.maximumFiles = maximumFiles;
        this.maximumDirectoryEntries = maximumDirectoryEntries;
        this.maximumFileBytes = maximumFileBytes;
        this.maximumSearchBytes = maximumSearchBytes;

        Functions =
        [
            CreateFunction(
                (Func<string?, string?, int?, CancellationToken, ValueTask<ListDirectoryResult>>)ListDirectoryAsync,
                "list_directory",
                "Lists one workspace directory without recursion. Omit uri to list the workspace root."),
            CreateFunction(
                (Func<string, int?, int?, CancellationToken, ValueTask<ReadTextResult>>)ReadTextAsync,
                "read_text",
                "Reads a bounded range of lines from a UTF-8 text file in the workspace."),
            CreateFunction(
                (Func<string, string?, bool?, bool?, int?, CancellationToken, ValueTask<SearchTextResult>>)
                SearchTextAsync,
                "search_text",
                "Searches UTF-8 workspace files recursively with a literal query or bounded regular expression.")
        ];
    }

    internal IReadOnlyList<AIFunction> Functions { get; }

    private static AIFunction CreateFunction(Delegate method, string name, string description) =>
        AIFunctionFactory.Create(
            method,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                SerializerOptions = SerializerOptions
            });

    [Description("Lists one workspace directory without recursion.")]
    private ValueTask<ListDirectoryResult> ListDirectoryAsync(
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
            var snapshot = workspace.Capture();
            var requestedPageSize = pageSize ?? DefaultPageSize;
            if (requestedPageSize is < 1 or > MaximumPageSize)
            {
                throw new WorkspaceException(
                    "workspace_invalid_arguments",
                    $"pageSize must be between 1 and {MaximumPageSize}.");
            }

            var directory = snapshot.Resolve(uri);
            if (!directory.IsDirectory)
            {
                throw new WorkspaceException(
                    "workspace_not_directory",
                    "The workspace URI does not identify a directory.");
            }

            var decodedCursor = DecodeCursor(cursor, directory.Uri, snapshot.Version);
            var entries = new List<FileSystemInfo>();
            foreach (var entry in new DirectoryInfo(directory.FullPath).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(entry);
                if (entries.Count > maximumDirectoryEntries)
                {
                    throw new WorkspaceException(
                        "workspace_directory_too_large",
                        $"A directory cannot contain more than {maximumDirectoryEntries} visible entries.");
                }
            }

            var comparisons = 0;
            entries.Sort((left, right) =>
            {
                if ((++comparisons & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return StringComparer.Ordinal.Compare(left.Name, right.Name);
            });

            var offset = decodedCursor is null
                ? 0
                : entries.FindIndex(entry => StringComparer.Ordinal.Compare(entry.Name, decodedCursor.LastName) > 0);
            if (offset < 0)
            {
                offset = entries.Count;
            }

            var page = entries
                .Skip(offset)
                .Take(requestedPageSize)
                .Select(entry => CreateEntry(snapshot, entry))
                .ToImmutableArray();
            var nextOffset = offset + page.Length;
            return ValueTask.FromResult(new ListDirectoryResult(
                directory.Uri,
                page,
                nextOffset < entries.Count
                    ? EncodeCursor(directory.Uri, snapshot.Version, page[^1].Name)
                    : null));
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

    [Description("Reads a bounded range of lines from a UTF-8 workspace text file.")]
    private async ValueTask<ReadTextResult> ReadTextAsync(
        [Description("The workspace://local URI of a text file.")]
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
            {
                throw new WorkspaceException("workspace_invalid_arguments", "uri is required.");
            }

            var requestedStartLine = startLine ?? 1;
            var maximumLines = maxLines ?? DefaultMaximumLines;
            if (requestedStartLine < 1 || maximumLines is < 1 or > MaximumLines)
            {
                throw new WorkspaceException(
                    "workspace_invalid_arguments",
                    $"startLine must be positive and maxLines must be between 1 and {MaximumLines}.");
            }

            var snapshot = workspace.Capture();
            var file = snapshot.Resolve(uri, allowRoot: false);
            if (file.IsDirectory)
            {
                throw new WorkspaceException(
                    "workspace_not_file",
                    "The workspace URI does not identify a regular file.");
            }

            if (!file.IsRegularFile)
            {
                throw new WorkspaceException(
                    "workspace_not_regular_file",
                    "Workspace text tools can read only regular files.");
            }

            return await ReadTextCoreAsync(
                snapshot,
                file,
                requestedStartLine,
                maximumLines,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DecoderFallbackException)
        {
            throw new AgentToolException(
                "workspace_invalid_utf8",
                "The requested file is not valid UTF-8 text.");
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw ToAgentToolException(exception);
        }
    }

    [Description("Searches UTF-8 workspace files recursively.")]
    private async ValueTask<SearchTextResult> SearchTextAsync(
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
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(query))
            {
                throw new WorkspaceException(
                    "workspace_invalid_arguments",
                    "query is required and cannot be empty.");
            }

            var requestedMaximumResults = maxResults ?? MaximumResults;
            if (requestedMaximumResults is < 1 or > MaximumResults)
            {
                throw new WorkspaceException(
                    "workspace_invalid_arguments",
                    $"maxResults must be between 1 and {MaximumResults}.");
            }

            Regex? compiledRegex = null;
            if (regex is true)
            {
                if (query.Length > MaximumRegexPatternCharacters)
                {
                    throw new WorkspaceException(
                        "workspace_invalid_arguments",
                        $"Regular expression queries cannot exceed {MaximumRegexPatternCharacters} characters.");
                }

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

            var snapshot = workspace.Capture();
            var searchRoot = snapshot.Resolve(uri);
            return await SearchCoreAsync(
                snapshot,
                searchRoot,
                query,
                compiledRegex,
                caseSensitive is not false,
                requestedMaximumResults,
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

    private static WorkspaceDirectoryEntry CreateEntry(WorkspaceSnapshot snapshot, FileSystemInfo entry)
    {
        var attributes = entry.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null)
        {
            return new WorkspaceDirectoryEntry(entry.Name, snapshot.ToWorkspaceUri(entry.FullName), "symbolic_link");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            return new WorkspaceDirectoryEntry(entry.Name, snapshot.ToWorkspaceUri(entry.FullName), "directory");
        }

        if ((attributes & FileAttributes.Normal) != 0 || File.Exists(entry.FullName))
        {
            return new WorkspaceDirectoryEntry(
                entry.Name,
                snapshot.ToWorkspaceUri(entry.FullName),
                "file",
                ((FileInfo)entry).Length);
        }

        return new WorkspaceDirectoryEntry(entry.Name, snapshot.ToWorkspaceUri(entry.FullName), "other");
    }

    private static DirectoryCursor? DecodeCursor(string? cursor, string uri, long workspaceVersion)
    {
        if (cursor is null)
        {
            return null;
        }

        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var value = JsonSerializer.Deserialize(
                Convert.FromBase64String(encoded),
                WorkspaceJsonSerializerContext.Default.DirectoryCursor);
            if (value is null || value.LastName.Length == 0 || value.WorkspaceVersion != workspaceVersion ||
                !string.Equals(value.Uri, uri, StringComparison.Ordinal))
            {
                throw new FormatException();
            }

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

    private static async Task<ReadTextResult> ReadTextCoreAsync(
        WorkspaceSnapshot snapshot,
        WorkspacePath file,
        int requestedStartLine,
        int maximumLines,
        CancellationToken cancellationToken)
    {
        await using var stream = snapshot.OpenVerifiedRead(file.FullPath);
        var reader = new BoundedUtf8LineReader(stream);
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
                    file.Uri,
                    actualStartLine,
                    actualEndLine,
                    text.ToString(),
                    hasMore,
                    hasMore ? actualEndLine + 1 : null);
            }

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return new ReadTextResult(
                    file.Uri,
                    actualStartLine,
                    actualEndLine,
                    text.ToString(),
                    Truncated: false,
                    NextStartLine: null);
            }

            lineNumber++;
            if (lineNumber < requestedStartLine)
            {
                continue;
            }

            var separatorBytes = actualStartLine.HasValue ? 1 : 0;
            var lineBytes = StrictUtf8.GetByteCount(line);
            if (outputBytes + separatorBytes + lineBytes > MaximumUtf8Bytes)
            {
                return new ReadTextResult(
                    file.Uri,
                    actualStartLine,
                    actualEndLine,
                    text.ToString(),
                    Truncated: true,
                    NextStartLine: lineNumber);
            }

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
        CancellationToken cancellationToken)
    {
        var matches = ImmutableArray.CreateBuilder<WorkspaceSearchMatch>();
        var statistics = new SearchStatistics();
        var truncated = false;

        foreach (var file in EnumerateFiles(searchRoot, statistics, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (statistics.VisitedFiles >= maximumFiles)
            {
                truncated = true;
                break;
            }

            statistics.VisitedFiles++;
            if (statistics.ScannedBytes >= maximumSearchBytes)
            {
                truncated = true;
                break;
            }

            var remainingSearchBytes = maximumSearchBytes - statistics.ScannedBytes;
            var maximumReadBytes = (int)Math.Min(maximumFileBytes, remainingSearchBytes);
            BoundedFileContent bounded;
            try
            {
                bounded = await snapshot.ReadAsync(file.FullName, maximumReadBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WorkspaceException exception) when (
                exception.Code == "workspace_not_regular_file" && searchRoot.IsDirectory)
            {
                statistics.SkippedNonRegularFiles++;
                continue;
            }

            if (bounded.ExceededLimit)
            {
                if (maximumReadBytes < maximumFileBytes)
                {
                    truncated = true;
                    break;
                }

                statistics.SkippedLargeFiles++;
                continue;
            }

            if (statistics.ScannedBytes + bounded.Bytes.Length > maximumSearchBytes)
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

            if (content.Length > 0 && content[0] == '\uFEFF')
            {
                content = content[1..];
            }

            if (content.Any(static character =>
                    char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            {
                statistics.SkippedBinaryFiles++;
                continue;
            }

            var fileUri = snapshot.ToWorkspaceUri(file.FullName);
            var lineMap = new TextLineMap(content);
            if (regex is null)
            {
                AddLiteralMatches(
                    matches,
                    content,
                    query,
                    caseSensitive,
                    fileUri,
                    lineMap,
                    maximumResults);
            }
            else
            {
                AddRegexMatches(matches, content, regex, fileUri, lineMap, maximumResults);
            }

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
            statistics.SkippedNonRegularFiles);
    }

    private IEnumerable<FileInfo> EnumerateFiles(
        WorkspacePath searchRoot,
        SearchStatistics statistics,
        CancellationToken cancellationToken)
    {
        if (!searchRoot.IsDirectory)
        {
            yield return new FileInfo(searchRoot.FullPath);
            yield break;
        }

        var directories = new Stack<DirectoryInfo>();
        directories.Push(new DirectoryInfo(searchRoot.FullPath));
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = GetSortedEntries(directory, statistics, cancellationToken);
            var childDirectories = new List<DirectoryInfo>();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = entry.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null)
                {
                    statistics.SkippedSymbolicLinks++;
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        childDirectories.Add((DirectoryInfo)entry);
                    }

                    continue;
                }

                if ((attributes & FileAttributes.Device) != 0 || entry is not FileInfo file)
                {
                    statistics.SkippedNonRegularFiles++;
                    continue;
                }

                yield return file;
            }

            for (var index = childDirectories.Count - 1; index >= 0; index--)
            {
                directories.Push(childDirectories[index]);
            }
        }
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
            if (statistics.VisitedEntries > maximumDirectoryEntries)
            {
                throw new WorkspaceException(
                    "workspace_directory_too_large",
                    $"A search cannot enumerate more than {maximumDirectoryEntries} workspace entries.");
            }

            entries.Add(entry);
        }

        var comparisons = 0;
        entries.Sort((left, right) =>
        {
            if ((++comparisons & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

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
            if (index < 0)
            {
                return;
            }

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
            if (matches.Count >= maximumResults)
            {
                return;
            }
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is WorkspaceException or UnauthorizedAccessException or IOException;

    private static AgentToolException ToAgentToolException(Exception exception) => exception switch
    {
        WorkspaceException workspaceException =>
            new AgentToolException(workspaceException.Code, workspaceException.Message),
        UnauthorizedAccessException => new AgentToolException(
            "workspace_access_denied",
            "The operating system denied access to the requested workspace path."),
        IOException => new AgentToolException(
            "workspace_io_error",
            "The workspace operation could not be completed because of an I/O error."),
        _ => throw new ArgumentOutOfRangeException(nameof(exception), exception, null)
    };

    private sealed class BoundedUtf8LineReader(FileStream stream)
    {
        private readonly byte[] buffer = new byte[4_096];
        private readonly byte[] lineBuffer = new byte[MaximumLineUtf8Bytes + 3];
        private int bufferCount;
        private int bufferOffset;
        private bool firstLine = true;
        private int scannedBytes;

        internal async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var lineLength = 0;
            while (await EnsureBufferAsync(cancellationToken).ConfigureAwait(false))
            {
                var value = buffer[bufferOffset++];
                if (value == 0)
                {
                    throw new WorkspaceException(
                        "workspace_binary_file",
                        "The requested file contains binary data.");
                }

                if (value == (byte)'\n')
                {
                    return DecodeLine(lineLength);
                }

                if (lineLength == lineBuffer.Length)
                {
                    throw LineTooLong();
                }

                lineBuffer[lineLength++] = value;
            }

            return lineLength == 0 ? null : DecodeLine(lineLength);
        }

        internal ValueTask<bool> HasMoreDataAsync(CancellationToken cancellationToken) =>
            EnsureBufferAsync(cancellationToken);

        private async ValueTask<bool> EnsureBufferAsync(CancellationToken cancellationToken)
        {
            if (bufferOffset < bufferCount)
            {
                return true;
            }

            var remaining = MaximumReadScanBytes - scannedBytes;
            var requested = Math.Min(buffer.Length, remaining + 1);
            bufferCount = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            bufferOffset = 0;
            scannedBytes += bufferCount;
            if (scannedBytes > MaximumReadScanBytes)
            {
                throw new WorkspaceException(
                    "workspace_file_too_large",
                    $"read_text scans at most {MaximumReadScanBytes} bytes per call.");
            }

            return bufferCount != 0;
        }

        private string DecodeLine(int length)
        {
            if (length > 0 && lineBuffer[length - 1] == (byte)'\r')
            {
                length--;
            }

            var line = StrictUtf8.GetString(lineBuffer, 0, length);
            if (firstLine)
            {
                firstLine = false;
                if (line.Length > 0 && line[0] == '\uFEFF')
                {
                    line = line[1..];
                }
            }

            if (StrictUtf8.GetByteCount(line) > MaximumLineUtf8Bytes)
            {
                throw LineTooLong();
            }

            if (line.Any(static character => char.IsControl(character) && character != '\t'))
            {
                throw new WorkspaceException(
                    "workspace_binary_file",
                    "The requested file contains binary control characters.");
            }

            return line;
        }

        private static WorkspaceException LineTooLong() => new(
            "workspace_line_too_long",
            $"A text line cannot exceed {MaximumLineUtf8Bytes} UTF-8 bytes.");
    }

    private sealed class TextLineMap
    {
        private const int MaximumPreviewCharacters = 512;
        private readonly string content;
        private readonly int[] lineStarts;

        internal TextLineMap(string content)
        {
            this.content = content;
            var starts = new List<int> { 0 };
            for (var index = 0; index < content.Length; index++)
            {
                if (content[index] == '\n' && index + 1 < content.Length)
                {
                    starts.Add(index + 1);
                }
            }

            lineStarts = starts.ToArray();
        }

        internal WorkspaceSearchMatch CreateMatch(int index, string uri)
        {
            var lineIndex = Array.BinarySearch(lineStarts, index);
            if (lineIndex < 0)
            {
                lineIndex = ~lineIndex - 1;
            }

            var lineStart = lineStarts[lineIndex];
            var lineEnd = lineIndex + 1 < lineStarts.Length
                ? lineStarts[lineIndex + 1] - 1
                : content.Length;
            if (lineEnd > lineStart && content[lineEnd - 1] == '\r')
            {
                lineEnd--;
            }

            var previewStart = lineStart;
            if (lineEnd - lineStart > MaximumPreviewCharacters)
            {
                previewStart = Math.Clamp(
                    index - MaximumPreviewCharacters / 4,
                    lineStart,
                    lineEnd - MaximumPreviewCharacters);
            }

            var previewEnd = Math.Min(lineEnd, previewStart + MaximumPreviewCharacters);
            if (previewStart > lineStart && char.IsLowSurrogate(content[previewStart]))
            {
                previewStart++;
            }

            if (previewEnd > previewStart && previewEnd < lineEnd && char.IsHighSurrogate(content[previewEnd - 1]))
            {
                previewEnd--;
            }

            return new WorkspaceSearchMatch(
                uri,
                lineIndex + 1,
                index - lineStart + 1,
                content[previewStart..previewEnd]);
        }
    }

    private sealed class SearchStatistics
    {
        internal long ScannedBytes { get; set; }
        internal int SkippedBinaryFiles { get; set; }
        internal int SkippedLargeFiles { get; set; }
        internal int SkippedSymbolicLinks { get; set; }
        internal int SkippedNonRegularFiles { get; set; }
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
    int SkippedNonRegularFiles);

internal sealed record WorkspaceSearchMatch(
    string Uri,
    int Line,
    int Column,
    string Preview);