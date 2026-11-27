namespace Maieutics.Processes;

/// <summary>Resolves the executable before policy checks or one-time approval.</summary>
internal static class ProcessExecutable
{
    private const UnixFileMode ExecuteBits =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    internal static bool IsBareName(string executable) =>
        !Path.IsPathRooted(executable) &&
        !executable.Contains(Path.DirectorySeparatorChar) &&
        !executable.Contains(Path.AltDirectorySeparatorChar);

    internal static string? Resolve(
        string executable,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?> environment)
    {
        var root = workingDirectory ?? Directory.GetCurrentDirectory();
        var pathExtensions = OperatingSystem.IsWindows() ? environment.GetValueOrDefault("PATHEXT") : null;
        if (!IsBareName(executable))
            return FindExecutable(executable, root, pathExtensions);

        if (environment.GetValueOrDefault("PATH") is not { Length: > 0 } path) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (FindExecutable(Path.Combine(directory, executable), root, pathExtensions) is { } resolved)
                return resolved;
        }

        return null;
    }

    private static string? FindExecutable(string name, string root, string? pathExtensions)
    {
        try
        {
            var path = Path.GetFullPath(name, root);
            if (OperatingSystem.IsWindows())
            {
                if (Path.HasExtension(path)) return File.Exists(path) ? path : null;

                // The launchers start programs directly. PATHEXT shell associations are
                // outside this lookup and require an explicitly named interpreter.
                foreach (var extension in (pathExtensions ?? ".COM;.EXE").Split(
                             ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!extension.Equals(".COM", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".EXE", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var candidate = path + extension;
                    if (File.Exists(candidate)) return candidate;
                }
                return null;
            }

            return File.Exists(path) && (File.GetUnixFileMode(path) & ExecuteBits) != 0 ? path : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? ResolvePhysicalPath(string path)
    {
        if (path.Contains('\0')) return null;
        if (!OperatingSystem.IsWindows()) return PolicyProcessSandboxPolicy.ResolveRealPath(path);
        try
        {
            // Resolve junctions and symlinked parents as well as a symlinked executable.
            var root = Path.GetPathRoot(path);
            if (root is null) return null;
            var components = path[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (components.Length == 0) return null;
            var directory = root;
            foreach (var component in components[..^1])
            {
                directory = Path.Combine(directory, component);
                if (Directory.ResolveLinkTarget(directory, returnFinalTarget: true) is { } target)
                    directory = target.FullName;
            }
            var file = Path.Combine(directory, components[^1]);
            return File.ResolveLinkTarget(file, returnFinalTarget: true)?.FullName ?? file;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
