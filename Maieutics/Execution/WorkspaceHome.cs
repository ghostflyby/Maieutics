namespace Maieutics.Execution;

/// <summary>Immutable link view handed to each workspace snapshot: the registered targets
/// by link name (case-insensitive) drive managed-hop validation and
/// <c>${var.project.*}</c> permission variables (ADR 0027 §7).</summary>
internal sealed class WorkspaceLinks
{
    private WorkspaceLinks(
        IReadOnlyDictionary<string, string> targetsByName,
        IReadOnlyList<WorkspaceLinkRecord> records)
    {
        TargetsByName = targetsByName;
        Records = records;
    }

    internal IReadOnlyDictionary<string, string> TargetsByName { get; }

    internal IReadOnlyList<WorkspaceLinkRecord> Records { get; }

    internal static WorkspaceLinks FromRecords(IReadOnlyList<WorkspaceLinkRecord> records)
    {
        var targets = new Dictionary<string, string>(records.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var record in records) targets[record.Name] = record.Target;

        return new WorkspaceLinks(targets, records);
    }
}

/// <summary>Owns the fixed workspace home: the product-owned directory that is the
/// workspace root, holding managed project links under <c>projects/</c>, the agent-owned
/// <c>scratch/</c> space, and product state under <c>.maieutics/</c> (ADR 0027). The
/// registry is authoritative; the physical links on disk are derived state this class
/// creates, repairs at startup, and removes on close.</summary>
internal sealed class WorkspaceHome
{
    internal const string ProjectsDirectoryName = "projects";
    internal const string ScratchDirectoryName = "scratch";
    internal const string StateDirectoryName = ".maieutics";

    private readonly WorkspaceLinkRegistry registry;

    private WorkspaceHome(string homePath, string projectsRoot, WorkspaceLinkRegistry registry)
    {
        HomePath = homePath;
        ProjectsRoot = projectsRoot;
        this.registry = registry;
    }

    internal string HomePath { get; }

    internal string ProjectsRoot { get; }

    /// <summary>Creates the home structure under the configured path or
    /// <c>&lt;dataRoot&gt;/workspaces</c>, loads the registry, and remounts open links.
    /// The default home is created on first boot; an explicitly configured home must
    /// already exist (a missing override path is a configuration mistake) and neither
    /// may be a reparse point. Performs bounded synchronous file system work, like
    /// <c>ApplicationPaths.EnsureAgentRoot</c>.</summary>
    internal static WorkspaceHome Ensure(string? configuredHome, string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        string homePath;
        if (configuredHome is null)
        {
            homePath = Path.Combine(dataRoot, "workspaces");
            Directory.CreateDirectory(homePath);
        }
        else
        {
            homePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredHome));
            if (!Directory.Exists(homePath))
                throw new DirectoryNotFoundException(
                    "The configured Maieutics workspace home does not exist.");
        }

        if ((File.GetAttributes(homePath) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException(
                "The workspace home cannot be a symbolic link.",
                nameof(configuredHome));

        var projectsRoot = Path.Combine(homePath, ProjectsDirectoryName);
        Directory.CreateDirectory(projectsRoot);
        Directory.CreateDirectory(Path.Combine(homePath, ScratchDirectoryName));
        Directory.CreateDirectory(Path.Combine(homePath, StateDirectoryName));

        var home = new WorkspaceHome(homePath, projectsRoot, WorkspaceLinkRegistry.Load(homePath));
        home.Remount();
        return home;
    }

    internal WorkspaceLinks LinksView() => WorkspaceLinks.FromRecords(registry.Entries());

    /// <summary>Opens an external directory as a managed project link. Opening the same
    /// canonical target twice is idempotent and returns the existing record. The physical
    /// link and the registry entry are written only after the target passed validation;
    /// a stray physical link at the allocated name is removed because the registry is
    /// authoritative for the name.</summary>
    internal WorkspaceLinkRecord Open(string path, string? alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var candidate = Path.GetFullPath(path);
        RejectUnsupportedTargetRoot(candidate);
        if (!Directory.Exists(candidate))
            throw new DirectoryNotFoundException(
                $"The workspace link target does not exist: {candidate}");

        var target = CanonicalizeTarget(candidate);
        RejectUnsupportedTargetCycle(target);

        var existing = registry.FindByTarget(target);
        if (existing is not null)
        {
            EnsurePhysicalLink(existing);
            return existing;
        }

        var name = registry.AllocateName(target, alias);
        var linkPath = LinkPath(name);
        RemoveStrayLink(linkPath);
        ManagedWorkspaceLink.Create(linkPath, target);
        var record = new WorkspaceLinkRecord(name, target, alias, DateTimeOffset.UtcNow);
        registry.CommitNew(record);
        return record;
    }

    /// <summary>Closes a link: removes the physical reparse point (never a real directory)
    /// and retires the name↔target binding so the name is never reused for a different
    /// target.</summary>
    internal WorkspaceLinkRecord Close(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var record = registry.FindByName(name)
                     ?? throw new ArgumentException($"No open workspace link is named '{name}'.");

        var linkPath = LinkPath(record.Name);
        if (TryGetAttributes(linkPath, out var attributes))
        {
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                throw new IOException(
                    $"The workspace link path '{linkPath}' is not a link; refusing to delete it.");
            Directory.Delete(linkPath, false);
        }

        registry.Remove(record.Name);
        return record;
    }

    internal string LinkPath(string name) => Path.Combine(ProjectsRoot, name);

    private void Remount()
    {
        foreach (var record in registry.Entries())
        {
            if (!Directory.Exists(record.Target)) continue;

            EnsurePhysicalLink(record);
        }
    }

    private void EnsurePhysicalLink(WorkspaceLinkRecord record)
    {
        var linkPath = LinkPath(record.Name);
        if (TryGetAttributes(linkPath, out var attributes))
        {
            if ((attributes & FileAttributes.ReparsePoint) == 0) return;

            if (PathsEqual(FinalTarget(linkPath), record.Target)) return;

            Directory.Delete(linkPath, false);
        }

        ManagedWorkspaceLink.Create(linkPath, record.Target);
    }

    private void RemoveStrayLink(string linkPath)
    {
        if (!TryGetAttributes(linkPath, out var attributes)) return;

        if ((attributes & FileAttributes.ReparsePoint) == 0)
            throw new IOException(
                $"The workspace link path '{linkPath}' exists and is not a link; " +
                "refusing to replace it. Open the project with an explicit alias.");

        Directory.Delete(linkPath, false);
    }

    /// <summary>Rejects a target whose volume root junctions cannot express (UNC on
    /// Windows). No file system access, so it fires before existence checks and any
    /// nonexistent network path still fails with the typed error.</summary>
    private void RejectUnsupportedTargetRoot(string target)
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.GetPathRoot(target);
        if (root is not null && root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException(
                "A workspace link target cannot be a UNC network path; " +
                "junctions support local paths only.");
    }

    private void RejectUnsupportedTargetCycle(string target)
    {
        var relative = Path.GetRelativePath(target, HomePath);
        if (relative == "." ||
            (!Path.IsPathRooted(relative) &&
             !relative.StartsWith("..", StringComparison.Ordinal)))
            throw new ArgumentException(
                "A workspace link target cannot contain the workspace home.",
                nameof(target));
    }

    internal static string CanonicalizeTarget(string candidate)
    {
        var info = new DirectoryInfo(candidate);
        var final = info.ResolveLinkTarget(returnFinalTarget: true);
        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(final?.FullName ?? info.FullName));
    }

    private static string FinalTarget(string linkPath)
    {
        var final = new DirectoryInfo(linkPath).ResolveLinkTarget(returnFinalTarget: true);
        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(final?.FullName ?? linkPath));
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }

        attributes = 0;
        return false;
    }
}

/// <summary>Creates and deletes managed directory links: a symbolic link on POSIX, a
/// directory junction on Windows (ADR 0027 §3). Deletion removes the reparse point only —
/// never the target.</summary>
internal static class ManagedWorkspaceLink
{
    internal static void Create(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsWindows()) WindowsJunction.Create(linkPath, targetPath);
        else Directory.CreateSymbolicLink(linkPath, targetPath);
    }

    internal static void Delete(string linkPath) => Directory.Delete(linkPath, false);
}
