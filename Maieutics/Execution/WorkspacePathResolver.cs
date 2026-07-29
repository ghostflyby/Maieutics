using System.Text;

namespace Maieutics.Execution;

internal sealed class WorkspacePathResolver
{
    private const string UriPrefix = "workspace://local";
    private readonly WorkspaceContext? context;
    private readonly WorkspaceRoot root;

    public WorkspacePathResolver(WorkspaceContext context)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        var snapshot = context.GetSnapshot();
        root = snapshot.Root;
        WorkspaceVersion = snapshot.Version;
    }

    internal WorkspacePathResolver(WorkspaceRoot root)
    {
        this.root = root ?? throw new ArgumentNullException(nameof(root));
    }

    internal long WorkspaceVersion { get; }

    internal WorkspacePathResolver Capture()
    {
        if (context is null)
        {
            return this;
        }

        var snapshot = context.GetSnapshot();
        return new WorkspacePathResolver(snapshot.Root, snapshot.Version);
    }

    private WorkspacePathResolver(WorkspaceRoot root, long workspaceVersion)
    {
        this.root = root;
        WorkspaceVersion = workspaceVersion;
    }

    internal WorkspacePath Resolve(string? uri, bool allowRoot = true)
    {
        var segments = ParseSegments(uri, allowRoot);
        var current = root.Path;

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException exception)
            {
                throw new WorkspaceToolException(
                    "workspace_path_not_found",
                    "The workspace URI does not identify an existing path.",
                    exception);
            }
            catch (DirectoryNotFoundException exception)
            {
                throw new WorkspaceToolException(
                    "workspace_path_not_found",
                    "The workspace URI does not identify an existing path.",
                    exception);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new WorkspaceToolException(
                    "workspace_symbolic_link_not_allowed",
                    "Workspace tools cannot read or traverse symbolic links.");
            }
        }

        return new WorkspacePath(current, ToWorkspaceUri(current), File.GetAttributes(current));
    }

    internal string ToWorkspaceUri(string fullPath)
    {
        var relative = Path.GetRelativePath(root.Path, fullPath);
        if (relative == ".")
        {
            return $"{UriPrefix}/";
        }

        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A workspace path escaped its configured root.");
        }

        var builder = new StringBuilder(UriPrefix);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append('/').Append(Uri.EscapeDataString(segment));
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> ParseSegments(string? uri, bool allowRoot)
    {
        if (uri is null)
        {
            if (allowRoot)
            {
                return [];
            }

            throw InvalidUri("A workspace URI is required.");
        }

        if (uri.Length == 0 || uri.IndexOf('\0') >= 0 ||
            uri.IndexOfAny(['?', '#']) >= 0 ||
            !uri.StartsWith(UriPrefix, StringComparison.Ordinal))
        {
            throw InvalidUri("The value must be a workspace://local URI.");
        }

        var suffix = uri[UriPrefix.Length..];
        if (suffix.Length == 0 || suffix == "/")
        {
            if (allowRoot)
            {
                return [];
            }

            throw InvalidUri("The workspace root is not valid for this operation.");
        }

        if (suffix[0] != '/')
        {
            throw InvalidUri("The value must be a workspace://local URI.");
        }

        var rawSegments = suffix[1..].Split('/', StringSplitOptions.None);
        var segments = new List<string>(rawSegments.Length);
        for (var index = 0; index < rawSegments.Length; index++)
        {
            var rawSegment = rawSegments[index];
            if (rawSegment.Length == 0)
            {
                if (index == rawSegments.Length - 1)
                {
                    continue;
                }

                throw InvalidUri("Workspace URIs cannot contain empty path segments.");
            }

            ValidatePercentEscaping(rawSegment);

            string segment;
            try
            {
                segment = Uri.UnescapeDataString(rawSegment);
            }
            catch (UriFormatException exception)
            {
                throw InvalidUri("The workspace URI contains invalid escaping.", exception);
            }

            if (segment.Length == 0 || segment is "." or ".." ||
                segment.IndexOf('\0') >= 0 ||
                segment.IndexOf('/') >= 0 ||
                segment.IndexOf('\\') >= 0)
            {
                throw InvalidUri("The workspace URI contains an invalid path segment.");
            }

            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkspaceToolException(
                    "workspace_path_denied",
                    "Workspace tools cannot access .git metadata.");
            }

            segments.Add(segment);
        }

        if (segments.Count == 0 && !allowRoot)
        {
            throw InvalidUri("The workspace root is not valid for this operation.");
        }

        return segments;
    }

    private static void ValidatePercentEscaping(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length || !IsHexDigit(value[index + 1]) || !IsHexDigit(value[index + 2]))
            {
                throw InvalidUri("The workspace URI contains invalid escaping.");
            }

            index += 2;
        }
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static WorkspaceToolException InvalidUri(string message, Exception? innerException = null) =>
        new("workspace_invalid_uri", message, innerException);
}

internal sealed record WorkspacePath(string FullPath, string Uri, FileAttributes Attributes)
{
    internal bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    internal bool IsRegularFile =>
        (Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
}

internal sealed class WorkspaceToolException : Exception
{
    internal WorkspaceToolException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    internal string Code { get; }
}