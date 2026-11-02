namespace Maieutics.Processes;

internal sealed partial class PolicyProcessSandboxPolicy
{
    private static string PrepareBubblewrapDenyFile(string temporaryDirectory)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Bubblewrap requires Linux.");

        // The source is private to this launch and hidden inside the child's namespace.
        // A mode-000 regular file makes denied file reads fail, rather than returning EOF.
        var directory = Directory.CreateDirectory(Path.Combine(temporaryDirectory, ".deny-mask")).FullName;
        var path = Path.Combine(directory, "file");
        File.WriteAllBytes(path, []);
        File.SetUnixFileMode(path, UnixFileMode.None);
        return path;
    }

    /// <summary>Constructs a Linux mount namespace from the same scope as seatbelt. Parent
    /// mounts precede children; denial masks are last and cannot be modified by the child.</summary>
    internal static IReadOnlyList<string> RenderBubblewrap(
        string launcher,
        SandboxProfile profile,
        string executable,
        IReadOnlyList<string> arguments,
        string? deniedFileSource = null)
    {
        var argv = new List<string>
        {
            launcher, "--unshare-user", "--unshare-pid", "--unshare-ipc", "--unshare-uts",
            "--disable-userns", "--cap-drop", "ALL", "--die-with-parent", "--new-session"
        };
        if (!profile.NetworkAllowed) argv.Add("--unshare-net");

        var trusted = new HashSet<string>(profile.TrustedPaths.Select(SandboxFullPath), StringComparer.Ordinal);
        var bindings = new Dictionary<string, bool>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        AddGrants(profile.ReadOnlyPaths, writable: false);
        AddGrants(profile.WritablePaths, writable: true);

        // Recreate trusted symlink spellings before their resolved mounts (e.g. /bin -> usr/bin).
        foreach (var (path, target) in aliases.OrderBy(static pair => pair.Key.Length))
            argv.AddRange(["--symlink", target, path]);

        var mounted = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (path, writable) in bindings.OrderBy(static pair => pair.Key.Length).ThenBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (mounted.Any(parent => SandboxContains(parent.Key, path) && (parent.Value || !writable)))
                continue;
            argv.AddRange([writable ? "--bind" : "--ro-bind", path, path]);
            mounted[path] = writable;
        }

        argv.AddRange(["--dev", "/dev", "--remount-ro", "/dev",
            "--proc", "/proc", "--remount-ro", "/proc", "--dir", "/tmp"]);

        var denials = profile.DeniedPaths.Select(path =>
            ResolveRealPath(path) ?? ResolveMissingSandboxPath(SandboxFullPath(path)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path.Length)
            .ToArray();
        var masked = new List<string>();
        foreach (var path in denials)
        {
            if (masked.Any(parent => SandboxContains(parent, path))) continue;
            if (!mounted.Keys.Any(parent => SandboxContains(parent, path)) &&
                !SandboxContains("/dev", path) && !SandboxContains("/proc", path))
                continue; // The path is absent from the namespace already.
            if (path == "/")
                throw new NotSupportedException("The Linux sandbox policy denies the filesystem root.");
            if (Directory.Exists(path))
                MaskDirectory(path);
            else if (File.Exists(path))
            {
                if (deniedFileSource is null)
                    throw new NotSupportedException("The Linux sandbox needs a launch-owned file denial mask.");
                argv.AddRange(["--ro-bind", deniedFileSource, path]);
            }
            else
                throw new NotSupportedException(
                    $"The Linux sandbox cannot mask the missing denied path '{path}' inside an exposed directory.");
            masked.Add(path);
        }

        if (deniedFileSource is not null && Path.GetDirectoryName(deniedFileSource) is { } sourceDirectory)
        {
            var resolvedSource = ResolveRealPath(sourceDirectory) ?? SandboxFullPath(sourceDirectory);
            if (!masked.Any(parent => SandboxContains(parent, resolvedSource))) MaskDirectory(resolvedSource);
        }

        // Synthetic ancestors (including shared /tmp) are not additional write grants.
        // Remounting this mount leaves the explicitly writable child bind mounts writable.
        argv.AddRange(["--remount-ro", "/", "--", executable]);
        argv.AddRange(arguments);
        return argv;

        void AddGrants(IReadOnlyList<string> paths, bool writable)
        {
            foreach (var path in paths)
            {
                var lexical = SandboxFullPath(path);
                if (!Directory.Exists(lexical) && !File.Exists(lexical)) continue;
                var resolved = ResolveRealPath(lexical) ?? lexical;
                if (!trusted.Contains(lexical) && !SandboxContains(lexical, resolved)) continue;
                if (trusted.Contains(lexical))
                {
                    foreach (var ancestor in Ancestors(lexical).Reverse().Append(lexical))
                    {
                        var info = new FileInfo(ancestor);
                        if (info.LinkTarget is { } target) aliases[ancestor] = target;
                    }
                }
                bindings[resolved] = writable || bindings.GetValueOrDefault(resolved);
            }
        }

        void MaskDirectory(string path)
        {
            argv.AddRange(["--perms", "000", "--tmpfs", path, "--remount-ro", path]);
        }
    }

    private static string SandboxFullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SandboxContains(string parent, string path) =>
        parent == "/" || string.Equals(parent, path, StringComparison.Ordinal) ||
        path.StartsWith(parent + "/", StringComparison.Ordinal);

    private static string ResolveMissingSandboxPath(string path)
    {
        var suffix = new Stack<string>();
        var current = path;
        while (Path.GetDirectoryName(current) is { } parent && parent != current)
        {
            suffix.Push(Path.GetFileName(current));
            current = parent;
            if (ResolveRealPath(current) is { } resolved)
                return Path.Combine([resolved, .. suffix]);
        }
        return path;
    }
}
