using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maieutics.Agent;

namespace Maieutics.Execution;

internal sealed class SearchTextTool : IAgentTool
{
    private const int MaximumResults = 200;
    private const int MaximumRegexPatternCharacters = 512;
    private const int DefaultMaximumFileBytes = 2 * 1_024 * 1_024;
    private const long DefaultMaximumScanBytes = 64L * 1_024 * 1_024;
    private const int DefaultMaximumFiles = 10_000;
    private const int DefaultMaximumDirectoryEntries = 10_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly int maximumDirectoryEntries;
    private readonly int maximumFileBytes;
    private readonly int maximumFiles;
    private readonly long maximumScanBytes;
    private readonly WorkspacePathResolver paths;

    public SearchTextTool(WorkspacePathResolver paths)
        : this(
            paths,
            DefaultMaximumFiles,
            DefaultMaximumDirectoryEntries,
            DefaultMaximumFileBytes,
            DefaultMaximumScanBytes)
    {
    }

    internal SearchTextTool(
        WorkspacePathResolver paths,
        int maximumFiles,
        int maximumDirectoryEntries,
        int maximumFileBytes = DefaultMaximumFileBytes,
        long maximumScanBytes = DefaultMaximumScanBytes)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDirectoryEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumScanBytes);
        this.paths = paths;
        this.maximumFiles = maximumFiles;
        this.maximumDirectoryEntries = maximumDirectoryEntries;
        this.maximumFileBytes = maximumFileBytes;
        this.maximumScanBytes = maximumScanBytes;
    }

    public AgentToolDescriptor Descriptor { get; } = new(
        "search_text",
        "Searches UTF-8 workspace files recursively with a literal query or bounded regular expression.",
        WorkspaceToolJson.ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "minLength": 1 },
                "uri": { "type": "string", "description": "A workspace://local file or directory URI. Defaults to the root." },
                "regex": { "type": "boolean", "default": false },
                "caseSensitive": { "type": "boolean", "default": true },
                "maxResults": { "type": "integer", "minimum": 1, "maximum": 200, "default": 200 }
              },
              "required": ["query"],
              "additionalProperties": false
            }
            """));

    public async ValueTask<AgentToolOutcome> InvokeAsync(
        AgentToolContext context,
        AgentToolArguments arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        try
        {
            var paths = this.paths.Capture();
            var input = arguments.Deserialize(WorkspaceToolJsonSerializerContext.Default.SearchTextArguments);
            if (string.IsNullOrEmpty(input.Query))
            {
                throw new WorkspaceToolException(
                    "workspace_invalid_arguments",
                    "query is required and cannot be empty.");
            }

            var maximumResults = input.MaxResults ?? MaximumResults;
            if (maximumResults is < 1 or > MaximumResults)
            {
                throw new WorkspaceToolException(
                    "workspace_invalid_arguments",
                    $"maxResults must be between 1 and {MaximumResults}.");
            }

            Regex? regex = null;
            if (input.Regex is true)
            {
                if (input.Query.Length > MaximumRegexPatternCharacters)
                {
                    throw new WorkspaceToolException(
                        "workspace_invalid_arguments",
                        $"Regular expression queries cannot exceed {MaximumRegexPatternCharacters} characters.");
                }

                try
                {
                    regex = new Regex(
                        input.Query,
                        RegexOptions.CultureInvariant |
                        (input.CaseSensitive is false ? RegexOptions.IgnoreCase : RegexOptions.None),
                        RegexTimeout);
                }
                catch (ArgumentException exception)
                {
                    throw new WorkspaceToolException(
                        "workspace_invalid_regex",
                        "query is not a valid regular expression.",
                        exception);
                }
            }

            var searchRoot = paths.Resolve(input.Uri);
            var result = await SearchAsync(
                paths,
                searchRoot,
                input.Query,
                regex,
                input.CaseSensitive is not false,
                maximumResults,
                cancellationToken);
            return WorkspaceToolJson.Success(result, WorkspaceToolJsonSerializerContext.Default.SearchTextResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RegexMatchTimeoutException)
        {
            return new AgentToolFailure(
                "workspace_regex_timeout",
                "The regular expression exceeded the per-file time limit.");
        }
        catch (Exception exception) when (exception is WorkspaceToolException or JsonException or
                                              UnauthorizedAccessException or IOException)
        {
            return WorkspaceToolJson.Failure(exception);
        }
    }

    private async Task<SearchTextResult> SearchAsync(
        WorkspacePathResolver paths,
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
            if (statistics.ScannedBytes >= maximumScanBytes)
            {
                truncated = true;
                break;
            }

            var remainingScanBytes = maximumScanBytes - statistics.ScannedBytes;
            var maximumReadBytes = (int)Math.Min(maximumFileBytes, remainingScanBytes);
            BoundedFileContent bounded;
            try
            {
                bounded = await WorkspaceFileReader.ReadAsync(file.FullName, maximumReadBytes, cancellationToken);
            }
            catch (WorkspaceToolException exception) when (
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

            if (statistics.ScannedBytes + bounded.Bytes.Length > maximumScanBytes)
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

            var fileUri = paths.ToWorkspaceUri(file.FullName);
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
                throw new WorkspaceToolException(
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