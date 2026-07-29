namespace Maieutics.Execution;

internal sealed class WorkspaceRoot
{
    private WorkspaceRoot(string path)
    {
        Path = path;
    }

    internal string Path { get; }

    internal static WorkspaceRoot Create(string? configuredPath, string startupCurrentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startupCurrentDirectory);
        if (configuredPath is not null && string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new ArgumentException(
                "The configured Maieutics workspace root cannot be empty.",
                nameof(configuredPath));
        }

        var candidate = configuredPath ?? startupCurrentDirectory;
        var fullPath = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(candidate, startupCurrentDirectory));

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("The configured Maieutics workspace root does not exist.");
        }

        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException(
                "The configured Maieutics workspace root cannot be a symbolic link.",
                nameof(configuredPath));
        }

        return new WorkspaceRoot(fullPath);
    }
}