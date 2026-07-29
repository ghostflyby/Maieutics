using System.Collections.Immutable;
using System.Text.Json;
using Maieutics.Agent;

namespace Maieutics.Execution;

internal sealed class ListDirectoryTool : IAgentTool
{
    private const int DefaultPageSize = 100;
    private const int MaximumPageSize = 200;
    private const int DefaultMaximumDirectoryEntries = 10_000;
    private readonly int maximumDirectoryEntries;
    private readonly WorkspacePathResolver paths;

    public ListDirectoryTool(WorkspacePathResolver paths)
        : this(paths, DefaultMaximumDirectoryEntries)
    {
    }

    internal ListDirectoryTool(WorkspacePathResolver paths, int maximumDirectoryEntries)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDirectoryEntries);
        this.paths = paths;
        this.maximumDirectoryEntries = maximumDirectoryEntries;
    }

    public AgentToolDescriptor Descriptor { get; } = new(
        "list_directory",
        "Lists one workspace directory without recursion. Omit uri to list the workspace root.",
        WorkspaceToolJson.ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "uri": { "type": "string", "description": "A workspace://local URI." },
                "cursor": { "type": "string", "description": "An opaque cursor returned by an earlier page." },
                "pageSize": { "type": "integer", "minimum": 1, "maximum": 200, "default": 100 }
              },
              "additionalProperties": false
            }
            """));

    public ValueTask<AgentToolOutcome> InvokeAsync(
        AgentToolContext context,
        AgentToolArguments arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var paths = this.paths.Capture();
            var input = arguments.Deserialize(WorkspaceToolJsonSerializerContext.Default.ListDirectoryArguments);
            var pageSize = input.PageSize ?? DefaultPageSize;
            if (pageSize is < 1 or > MaximumPageSize)
            {
                throw new WorkspaceToolException(
                    "workspace_invalid_arguments",
                    $"pageSize must be between 1 and {MaximumPageSize}.");
            }

            var directory = paths.Resolve(input.Uri);
            if (!directory.IsDirectory)
            {
                throw new WorkspaceToolException(
                    "workspace_not_directory",
                    "The workspace URI does not identify a directory.");
            }

            var cursor = DecodeCursor(input.Cursor, directory.Uri, paths.WorkspaceVersion);
            var entries = new List<FileSystemInfo>();
            foreach (var entry in new DirectoryInfo(directory.FullPath).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(entry);
                if (entries.Count > maximumDirectoryEntries)
                {
                    throw new WorkspaceToolException(
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

            var offset = cursor is null
                ? 0
                : entries.FindIndex(entry => StringComparer.Ordinal.Compare(entry.Name, cursor.LastName) > 0);
            if (offset < 0)
            {
                offset = entries.Count;
            }

            var page = entries
                .Skip(offset)
                .Take(pageSize)
                .Select(entry => CreateEntry(paths, entry))
                .ToImmutableArray();
            var nextOffset = offset + page.Length;
            var result = new ListDirectoryResult(
                directory.Uri,
                page,
                nextOffset < entries.Count
                    ? EncodeCursor(directory.Uri, paths.WorkspaceVersion, page[^1].Name)
                    : null);
            return ValueTask.FromResult<AgentToolOutcome>(WorkspaceToolJson.Success(
                result,
                WorkspaceToolJsonSerializerContext.Default.ListDirectoryResult));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is WorkspaceToolException or JsonException or
                                              UnauthorizedAccessException or IOException)
        {
            return ValueTask.FromResult<AgentToolOutcome>(WorkspaceToolJson.Failure(exception));
        }
    }

    private static WorkspaceDirectoryEntry CreateEntry(WorkspacePathResolver paths, FileSystemInfo entry)
    {
        var attributes = entry.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null)
        {
            return new WorkspaceDirectoryEntry(entry.Name, paths.ToWorkspaceUri(entry.FullName), "symbolic_link");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            return new WorkspaceDirectoryEntry(entry.Name, paths.ToWorkspaceUri(entry.FullName), "directory");
        }

        if ((attributes & FileAttributes.Normal) != 0 || File.Exists(entry.FullName))
        {
            return new WorkspaceDirectoryEntry(
                entry.Name,
                paths.ToWorkspaceUri(entry.FullName),
                "file",
                ((FileInfo)entry).Length);
        }

        return new WorkspaceDirectoryEntry(entry.Name, paths.ToWorkspaceUri(entry.FullName), "other");
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
                WorkspaceToolJsonSerializerContext.Default.DirectoryCursor);
            if (value is null || value.LastName.Length == 0 || value.WorkspaceVersion != workspaceVersion ||
                !string.Equals(value.Uri, uri, StringComparison.Ordinal))
            {
                throw new FormatException();
            }

            return value;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or JsonException)
        {
            throw new WorkspaceToolException(
                "workspace_invalid_cursor",
                "The directory cursor is invalid for this workspace URI.",
                exception);
        }
    }

    private static string EncodeCursor(string uri, long workspaceVersion, string lastName)
    {
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new DirectoryCursor(uri, workspaceVersion, lastName),
            WorkspaceToolJsonSerializerContext.Default.DirectoryCursor));
        return encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}