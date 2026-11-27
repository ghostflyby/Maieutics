namespace Maieutics.Processes;

internal sealed partial class PolicyProcessSandboxPolicy
{
    private static IReadOnlyList<string> RenderLinuxLaunch(
        string launcher, SandboxProfile profile, string executable, IReadOnlyList<string> arguments,
        string temporaryDirectory)
    {
        var mask = PrepareBubblewrapDenyFile(temporaryDirectory);
        var argv = RenderBubblewrap(launcher, profile, executable, arguments, mask);
        if (!profile.HostRead) return argv;

        var socketFilter = Path.Combine(temporaryDirectory, ".deny-mask", "socket-filter");
        LinuxSandboxSocketFilter.Write(socketFilter);
        // Bubblewrap reads cBPF from an inherited fd. The fixed shim preserves stdin and
        // passes both the filename and the command positionally.
        return ["/bin/sh", "-c", "filter=$1; shift; exec \"$@\" 3<\"$filter\"",
            "maieutics-sandbox", socketFilter, .. argv];
    }

    private static string PrepareBubblewrapDenyFile(string temporaryDirectory)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Bubblewrap requires Linux.");

        // The source is private to this launch and hidden inside the child's namespace.
        // A mode-000 regular file makes denied file reads fail, rather than returning EOF.
        // Prepare one per plan even without file denials so every file mask has the same
        // owned lifetime; its hidden source never becomes an extra child-visible grant.
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
        if (profile.HostRead) argv.AddRange(["--seccomp", "3", "--ro-bind", "/", "/"]);

        var requiredReadDenials = ResolveLinuxDenials(profile.ReadDeniedPaths, includeAliases: profile.HostRead);
        var optionalReadDenials = ResolveLinuxDenials(profile.OptionalProtectedPaths ?? [], includeAliases: true);
        var readDenials = requiredReadDenials.Concat(optionalReadDenials).Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path.Length).ToArray();
        var writeDenials = ResolveLinuxDenials(profile.WriteDeniedPaths ?? []);
        if (readDenials.Contains("/", StringComparer.Ordinal))
            throw new NotSupportedException("The Linux sandbox policy denies the filesystem root.");

        var trusted = new HashSet<string>(profile.TrustedPaths.Select(SandboxFullPath), StringComparer.Ordinal);
        var bindings = new Dictionary<string, bool>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!profile.HostRead) AddGrants(profile.ReadOnlyPaths, writable: false);
        AddGrants(profile.WritablePaths, writable: true);

        var projected = profile.HostRead
            ? ProjectReadOnlyDenialParents(argv, bindings, readDenials)
            : new HashSet<string>(StringComparer.Ordinal);

        // Recreate trusted symlink spellings before their resolved mounts (e.g. /bin -> usr/bin).
        foreach (var (path, target) in aliases.OrderBy(static pair => pair.Key.Length))
            argv.AddRange(["--symlink", target, path]);

        var mounted = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (profile.HostRead) mounted.Add("/", false);
        foreach (var (path, writable) in bindings.OrderBy(static pair => pair.Key.Length).ThenBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (mounted.Any(parent => SandboxContains(parent.Key, path) && (parent.Value || !writable)))
                continue;
            argv.AddRange([writable ? "--bind" : "--ro-bind", path, path]);
            mounted[path] = writable;
        }

        argv.AddRange(["--dev", "/dev", "--remount-ro", "/dev",
            "--proc", "/proc", "--remount-ro", "/proc", "--dir", "/tmp"]);
        var exposedRoots = mounted.Keys.Concat(["/dev", "/proc", "/tmp"]).ToArray();

        foreach (var path in writeDenials)
        {
            if (readDenials.Any(parent => SandboxContains(parent, path)) ||
                !mounted.Any(grant => grant.Value && SandboxContains(grant.Key, path)))
                continue;
            if (!Directory.Exists(path) && !File.Exists(path))
                throw new NotSupportedException(
                    $"The Linux sandbox cannot make the missing path '{path}' read-only inside a writable directory.");
            argv.AddRange(["--ro-bind", path, path]);
        }

        var masked = new List<string>();
        foreach (var path in readDenials)
        {
            if (projected.Contains(path)) continue;
            if (masked.Any(parent => SandboxContains(parent, path))) continue;
            // An ancestor denial must hide descendant grants too, including private temp.
            // Synthetic mounts participate even when no host binding exposes their parent.
            if (!exposedRoots.Any(root => SandboxContains(root, path) || SandboxContains(path, root)))
                continue; // The denied tree has no overlap with the exposed namespace.
            if (Directory.Exists(path))
                MaskDirectory(path);
            else if (File.Exists(path))
            {
                if (deniedFileSource is null)
                    throw new NotSupportedException("The Linux sandbox needs a launch-owned file denial mask.");
                argv.AddRange(["--ro-bind", deniedFileSource, path]);
            }
            else if (requiredReadDenials.Contains(path, StringComparer.Ordinal) ||
                     !optionalReadDenials.Contains(path, StringComparer.Ordinal) ||
                     !mounted.Any(grant => grant.Value && SandboxContains(grant.Key, path)) ||
                     !IsMissing(path))
                throw new NotSupportedException(
                    $"The Linux sandbox cannot mask the missing denied path '{path}' inside an exposed directory.");
            else
                continue; // There is no existing credential to hide, and masking it would require a host entry.
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
                if (writable && (writeDenials.Any(path => SandboxContains(path, resolved)) ||
                                 (profile.HostRead && readDenials.Any(path => SandboxContains(path, resolved)))))
                    continue;
                if (trusted.Contains(lexical) && !profile.HostRead)
                {
                    foreach (var ancestor in Ancestors(lexical).Reverse().Append(lexical))
                    {
                        var info = new FileInfo(ancestor);
                        if (info.LinkTarget is { } target && Path.GetDirectoryName(ancestor) is { } parent)
                        {
                            // A lexical parent may itself be a symlink. Create this alias
                            // in its physical parent so setup never walks a dangling alias.
                            var physicalParent = ResolveRealPath(parent) ?? parent;
                            aliases[Path.Combine(physicalParent, Path.GetFileName(ancestor))] = target;
                        }
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

    private static string[] ResolveLinuxDenials(IReadOnlyList<string> paths, bool includeAliases = false)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var lexical = SandboxFullPath(path);
            var alias = ResolveMissingSandboxPath(lexical);
            result.Add(ResolveRealPath(lexical) ?? alias);
            if (includeAliases) result.Add(alias);
        }

        return result.OrderBy(static path => path.Length).ToArray();
    }

    private static bool IsMissing(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    /// <summary>A read-only synthetic parent cannot reveal an excluded child later, even
    /// when the host creates or replaces it after the sandbox starts.</summary>
    private static HashSet<string> ProjectReadOnlyDenialParents(
        List<string> argv, Dictionary<string, bool> bindings, IReadOnlyList<string> denials)
    {
        var excludedByParent = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var projected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in denials)
        {
            if (projected.Any(parent => SandboxContains(parent, path)))
            {
                projected.Add(path);
                continue;
            }

            var child = path;
            var parent = Path.GetDirectoryName(path);
            while (parent is not null && !Directory.Exists(parent))
            {
                child = parent;
                parent = Path.GetDirectoryName(parent);
            }

            // /dev and /proc are replaced after projection. Their own masks operate on
            // the synthetic namespaces, and / can also contain mountpoints needed by bwrap.
            if (parent is null || parent == "/" || SandboxContains("/dev", parent) || SandboxContains("/proc", parent) ||
                bindings.Any(grant => grant.Value && SandboxContains(grant.Key, parent)))
                continue;
            if (!excludedByParent.TryGetValue(parent, out var excluded))
                excludedByParent.Add(parent, excluded = new HashSet<string>(StringComparer.Ordinal));
            excluded.Add(child);
            projected.Add(path);
        }

        foreach (var (parent, excluded) in excludedByParent.OrderBy(static entry => entry.Key.Length))
        {
            argv.AddRange(["--tmpfs", parent]);
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
            {
                if (excluded.Contains(entry)) continue;
                var target = new FileInfo(entry).LinkTarget;
                if (target is not null)
                    argv.AddRange(["--symlink", target, entry]);
                else
                    argv.AddRange(["--ro-bind-try", entry, entry]);
            }
            argv.AddRange(["--remount-ro", parent]);
        }
        return projected;
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
