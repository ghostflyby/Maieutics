namespace Maieutics.Execution;

/// <summary>How a managed link last fared against the file system (ADR 0027 amendment).
/// Runtime state rebuilt at startup and open; never persisted.</summary>
internal enum WorkspaceLinkState
{
    /// <summary>The registered object was found at the registered path (fingerprint match,
    /// or path-only for a fingerprint-less record).</summary>
    Verified,

    /// <summary>The fingerprint re-located the project at a new path this startup (or the
    /// record was re-pointed by an explicit open); the link follows the object.</summary>
    Relocated,

    /// <summary>The registered path is gone and no fingerprint match was found; the entry
    /// stays dangling until the target reappears.</summary>
    TargetMissing,

    /// <summary>The registered path now holds a different directory. The physical link is
    /// withheld so tools cannot read the wrong project; the name stays reserved for the
    /// registered object. A relocation candidate found at a disallowed location is ignored
    /// (it falls through to <see cref="TargetMissing"/> or this state as appropriate).</summary>
    IdentityChanged,

    /// <summary>The remount itself failed (link creation, I/O); the error detail is
    /// carried by the health entry.</summary>
    Failed
}

/// <summary>The remount outcome for one link, with an optional human-readable detail.</summary>
internal sealed record WorkspaceLinkHealth(WorkspaceLinkState State, string? Detail = null)
{
    internal static readonly WorkspaceLinkHealth Verified = new(WorkspaceLinkState.Verified);
}

/// <summary>Immutable link view handed to each workspace snapshot: the registered targets
/// by link name (case-insensitive) drive managed-hop validation and
/// <c>${var.project.*}</c> permission variables (ADR 0027 §7).</summary>
internal sealed class WorkspaceLinks
{
    private WorkspaceLinks(
        IReadOnlyDictionary<string, string> targetsByName,
        IReadOnlyList<WorkspaceLinkRecord> records,
        IReadOnlyDictionary<string, WorkspaceLinkHealth> healthByName)
    {
        TargetsByName = targetsByName;
        Records = records;
        HealthByName = healthByName;
    }

    internal IReadOnlyDictionary<string, string> TargetsByName { get; }

    internal IReadOnlyList<WorkspaceLinkRecord> Records { get; }

    internal IReadOnlyDictionary<string, WorkspaceLinkHealth> HealthByName { get; }

    internal static WorkspaceLinks FromRecords(
        IReadOnlyList<WorkspaceLinkRecord> records,
        IReadOnlyDictionary<string, WorkspaceLinkHealth>? healthByName = null)
    {
        var targets = new Dictionary<string, string>(records.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var record in records) targets[record.Name] = record.Target;

        var health = new Dictionary<string, WorkspaceLinkHealth>(records.Count, StringComparer.OrdinalIgnoreCase);
        if (healthByName is not null)
        {
            foreach (var (name, entry) in healthByName) health[name] = entry;
        }
        else
        {
            foreach (var record in records) health[record.Name] = WorkspaceLinkHealth.Verified;
        }

        return new WorkspaceLinks(targets, records, health);
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
    private readonly Lock healthGate = new();
    private readonly Dictionary<string, WorkspaceLinkHealth> healthByName =
        new(StringComparer.OrdinalIgnoreCase);

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

    internal WorkspaceLinks LinksView() =>
        WorkspaceLinks.FromRecords(registry.Entries(), SnapshotHealth());

    /// <summary>Opens an external directory as a managed project link. Opening the same
    /// target twice is idempotent and returns the existing record; opening the same
    /// <em>object</em> at a path it moved to re-points the existing record to the new
    /// path (ADR 0027 amendment). The physical link and the registry entry are written
    /// only after the target passed validation; a stray physical link at the allocated
    /// name is removed because the registry is authoritative for the name.</summary>
    internal WorkspaceLinkRecord Open(string path, string? alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var candidate = Path.GetFullPath(path);
        RejectUnsupportedTargetRoot(candidate);
        if (!Directory.Exists(candidate))
            throw new DirectoryNotFoundException(
                $"The workspace link target does not exist: {candidate}");

        // The recorded target keeps the caller's path form (the rest of the workspace compares
        // against it); escape detection runs on the fully resolved form, which is what catches a
        // target reached through a symlinked ancestor.
        var target = Path.TrimEndingDirectorySeparator(candidate);
        RejectUnsupportedTargetCycle(CanonicalizeTarget(target));

        var identity = WorkspaceFileIdentityReader.TryRead(target);

        var existing = registry.FindByTarget(target);
        if (existing is not null)
        {
            if (existing.Identity is not null && identity is not null &&
                !existing.Identity.Matches(identity))
                throw new InvalidOperationException(
                    $"The workspace link '{existing.Name}' is bound to a different project " +
                    $"that was opened at this path; the directory now here is not that " +
                    $"project. Close '{existing.Name}' and open again to adopt the new one.");

            EnsurePhysicalLink(existing);
            SetHealth(existing.Name, WorkspaceLinkHealth.Verified);
            return existing;
        }

        var moved = identity is null ? null : registry.FindByFingerprint(identity);
        if (moved is not null)
        {
            // The same project object, opened where it now lives after a rename or move:
            // the record follows it, and transcripts keep citing projects/<name>. The
            // physical link is repaired first so a failure here leaves the registry
            // describing the state the file system is actually in.
            var record = moved with { Target = target };
            EnsurePhysicalLink(record);
            registry.UpdateTarget(moved.Name, target);
            SetHealth(moved.Name, new WorkspaceLinkHealth(
                WorkspaceLinkState.Relocated,
                $"relocated to {target}"));
            return record;
        }

        var name = registry.AllocateName(target, alias, identity);
        var linkPath = LinkPath(name);
        RemoveStrayLink(linkPath);
        ManagedWorkspaceLink.Create(linkPath, target);
        var created = new WorkspaceLinkRecord(name, target, alias, DateTimeOffset.UtcNow, identity);
        registry.CommitNew(created);
        SetHealth(name, WorkspaceLinkHealth.Verified);
        return created;
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
        ForgetHealth(record.Name);
        return record;
    }

    internal string LinkPath(string name) => Path.Combine(ProjectsRoot, name);

    private void Remount()
    {
        foreach (var record in registry.Entries())
        {
            try
            {
                RemountRecord(record);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SetHealth(record.Name, new WorkspaceLinkHealth(
                    WorkspaceLinkState.Failed,
                    exception.Message));
            }
        }
    }

    /// <summary>Reconciles one registry entry with the file system. The fingerprint can
    /// tell a renamed project (the same object elsewhere) from a replaced one (a different
    /// object at the registered path) — the two cases the path-only model confused.</summary>
    private void RemountRecord(WorkspaceLinkRecord record)
    {
        var targetExists = Directory.Exists(record.Target);
        var identityHere = targetExists
            ? WorkspaceFileIdentityReader.TryRead(record.Target)
            : null;

        if (targetExists &&
            (record.Identity is null || identityHere is null || record.Identity.Matches(identityHere)))
        {
            EnsurePhysicalLink(record);
            SetHealth(record.Name, WorkspaceLinkHealth.Verified);
            return;
        }

        if (record.Identity is not null && TryRelocate(record) is { } relocated)
        {
            registry.UpdateTarget(record.Name, relocated);
            EnsurePhysicalLink(record with { Target = relocated });
            SetHealth(record.Name, new WorkspaceLinkHealth(
                WorkspaceLinkState.Relocated,
                $"relocated from {record.Target} to {relocated}"));
            return;
        }

        if (targetExists)
        {
            // The registered path validates against the record, so a physical link left
            // from an earlier session would let tools silently read the wrong project.
            // The link is withheld; the entry and its name stay reserved.
            WithholdLink(record, "the registered path now holds a different directory");
            return;
        }

        SetHealth(record.Name, new WorkspaceLinkHealth(WorkspaceLinkState.TargetMissing));
    }

    /// <summary>Searches the registered target's former parent directory for the recorded
    /// object — bounded on purpose: it catches the common rename-inside-its-folder case
    /// without ever scanning a whole volume. Returns the candidate's path in the record's
    /// own path form, or null when the object is not found or its new location is not an
    /// allowed target.</summary>
    private string? TryRelocate(WorkspaceLinkRecord record)
    {
        var registered = record.Identity;
        var formerParent = Path.GetDirectoryName(record.Target);
        if (registered is null || formerParent is null || !Directory.Exists(formerParent))
            return null;

        foreach (var entry in new DirectoryInfo(formerParent).EnumerateDirectories(
                     "*",
                     new EnumerationOptions
                     {
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint
                     }))
        {
            if (WorkspaceFileIdentityReader.TryRead(entry.FullName) is not { } identity ||
                !registered.Matches(identity))
                continue;

            var candidate = Path.Combine(formerParent, entry.Name);
            try
            {
                RejectUnsupportedTargetRoot(candidate);
                RejectUnsupportedTargetCycle(CanonicalizeTarget(candidate));
            }
            catch (ArgumentException)
            {
                return null;
            }

            return candidate;
        }

        return null;
    }

    private void WithholdLink(WorkspaceLinkRecord record, string detail)
    {
        var linkPath = LinkPath(record.Name);
        if (TryGetAttributes(linkPath, out var attributes) &&
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(linkPath, false);
        }

        SetHealth(record.Name, new WorkspaceLinkHealth(WorkspaceLinkState.IdentityChanged, detail));
    }

    private void SetHealth(string name, WorkspaceLinkHealth entry)
    {
        lock (healthGate)
        {
            healthByName[name] = entry;
        }
    }

    private void ForgetHealth(string name)
    {
        lock (healthGate)
        {
            healthByName.Remove(name);
        }
    }

    private IReadOnlyDictionary<string, WorkspaceLinkHealth> SnapshotHealth()
    {
        lock (healthGate)
        {
            return new Dictionary<string, WorkspaceLinkHealth>(
                healthByName,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private void EnsurePhysicalLink(WorkspaceLinkRecord record)
    {
        var linkPath = LinkPath(record.Name);
        if (TryGetAttributes(linkPath, out var attributes))
        {
            if ((attributes & FileAttributes.ReparsePoint) == 0) return;

            if (PathsEqual(FinalTarget(linkPath), record.Target)) return;
        }

        // Both a mismatched link and a dangling one (stat follows the link and fails) are
        // replaced; a real directory at the name is never touched (handled above).
        ManagedWorkspaceLink.Delete(linkPath);
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
        // The caller passes the resolved target, so the home must be resolved the same way:
        // comparing a resolved path against an unresolved one fails whenever the home sits under
        // a platform alias (macOS /var -> /private/var), which would disable this guard entirely.
        var home = CanonicalizeTarget(HomePath);
        if (Contains(target, home) || Contains(home, target) || PathsEqual(target, home))
            throw new ArgumentException(
                "A workspace link target must not contain, or be contained by, the workspace home.",
                nameof(target));
    }

    /// <summary>Whether <paramref name="candidate"/> is <paramref name="ancestor"/> or lies
    /// beneath it, compared on the canonical forms of both.</summary>
    private static bool Contains(string candidate, string ancestor)
    {
        var relative = Path.GetRelativePath(ancestor, candidate);
        return relative.Length == 0 ||
               (relative != "." &&
                !Path.IsPathRooted(relative) &&
                !relative.StartsWith("..", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    /// <summary>Resolves a candidate path to its physical location for validation purposes.
    /// <see cref="DirectoryInfo.ResolveLinkTarget"/> walks only the final component, so a path
    /// reaching the home through a symlinked ancestor would otherwise pass validation while
    /// physically living inside the home. Each ancestor is therefore probed for a link and the
    /// walk continues from its target.
    /// <para>
    /// The walk stops as soon as no further link is found rather than re-resolving the whole
    /// chain: system-level aliases such as macOS's <c>/var</c> → <c>/private/var</c> are
    /// deliberately preserved, because the unfollowed path the caller supplied is what the rest
    /// of the workspace compares against.
    /// </para></summary>
    internal static string CanonicalizeTarget(string candidate)
    {
        var full = Path.GetFullPath(candidate);
        var components = full.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var resolved = full.StartsWith(Path.DirectorySeparatorChar)
            ? Path.DirectorySeparatorChar.ToString()
            : string.Empty;
        for (var index = 0; index < components.Length; index++)
        {
            var current = Path.Combine(resolved, components[index]);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not { } linkTarget)
            {
                resolved = current;
                continue;
            }

            // A link resolved mid-path replaces the prefix; the remaining components are then
            // re-appended, which is what detects a target that escapes into the home.
            var target = Path.GetFullPath(Path.IsPathRooted(linkTarget)
                ? linkTarget
                : Path.Combine(Path.GetDirectoryName(current) ?? resolved, linkTarget));
            var rest = components[(index + 1)..];
            return rest.Length == 0
                ? Path.TrimEndingDirectorySeparator(target)
                : CanonicalizeTarget(Path.Combine([target, .. rest]));
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }

    private static string FinalTarget(string linkPath)
    {
        return CanonicalizeTarget(linkPath);
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
internal static partial class ManagedWorkspaceLink
{
    internal static void Create(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsWindows()) WindowsJunction.Create(linkPath, targetPath);
        else Directory.CreateSymbolicLink(linkPath, targetPath);
    }

    internal static void Delete(string linkPath)
    {
        try
        {
            Directory.Delete(linkPath, false);
        }
        catch (DirectoryNotFoundException)
        {
            // A link whose target has vanished cannot be stat'd (stat follows the link),
            // so Directory.Delete reports the whole path missing even though the reparse
            // point is there. Unlinking removes it, and File.Delete ignores an
            // already-absent path.
            File.Delete(linkPath);
        }
    }
}
