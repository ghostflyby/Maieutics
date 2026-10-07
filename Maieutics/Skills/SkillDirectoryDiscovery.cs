using System.Text;

namespace Maieutics.Skills;

/// <summary>Walks one declared skill root (ADR 0039 discovery primitive). The walk is
/// recursive over the root, but the shallowest <c>SKILL.md</c> in any directory subtree
/// fixes that subtree's skill boundary: deeper <c>SKILL.md</c> files inside an accepted
/// skill directory are that skill's resources, never nested skills. The filename match is
/// exact and case-sensitive on every platform. Every failure mode is a per-skill diagnostic
/// carried in the result; discovery itself never throws for content problems.</summary>
internal static class SkillDirectoryDiscovery
{
    internal const string SkillFileName = "SKILL.md";

    /// <summary>The most skills one root contributes; the excess is ignored with one
    /// diagnostic so a runaway directory cannot flood the prompt catalog.</summary>
    internal const int MaximumSkillsPerRoot = 256;

    /// <summary>The largest <c>SKILL.md</c> read during discovery. Only the frontmatter is
    /// parsed at catalog time and the body is streamed on demand, but a body file must still
    /// be plausibly inspectable — gigabyte markdown is not a skill.</summary>
    internal const long MaximumBodyBytes = 4 * 1024 * 1024;

    /// <summary>The most directories one root's walk visits; a wide skill-less tree stops
    /// here instead of becoming a full-disk scan.</summary>
    internal const int MaximumVisitedDirectories = 4096;

    /// <summary>Discovers the skills of one root as one deterministically ordered list.
    /// This is the buffered facade over <see cref="EnumerateSubtree"/> for callers that
    /// want a whole root at once; the catalog's initial scan and differential rescans
    /// consume the streaming core instead.</summary>
    internal static IReadOnlyList<SkillDescriptor> Discover(string root, SkillSource source)
    {
        ArgumentNullException.ThrowIfNull(root);

        // Deterministic in-root order: the same-name winner is the first entry in ordinal
        // body-path order, independent of filesystem enumeration order.
        return [.. EnumerateSubtree(root, root, source)
            .OrderBy(static descriptor => descriptor.BodyPath, StringComparer.Ordinal)];
    }

    /// <summary>Lazily streams the skills of one region subtree of a declared root: every
    /// skill directory is yielded the moment it is found, so an initial scan goes live per
    /// directory and a differential rescan walks only the changed region. The region root
    /// itself is the single-directory skill case when it directly contains a
    /// <c>SKILL.md</c>: its name comes from the frontmatter (fallback: the region
    /// directory name), and its subdirectories are resources that are not walked. All
    /// boundary rules (shallowest <c>SKILL.md</c> wins, no descent into a claimed skill
    /// directory, linked directories not traversed, exact ordinal filename match) are
    /// identical to a full-root walk, and containment is evaluated against
    /// <paramref name="declaredRoot"/> — a region walk yields exactly the descriptors a
    /// full walk would yield for that subtree. The caller is responsible for never passing
    /// a region inside an already-claimed skill directory: a full walk never enters one,
    /// and neither does this.</summary>
    internal static IEnumerable<SkillDescriptor> EnumerateSubtree(
        string regionRoot,
        string declaredRoot,
        SkillSource source,
        SkillDirectoryVisitCounter? visits = null)
    {
        ArgumentNullException.ThrowIfNull(regionRoot);
        ArgumentNullException.ThrowIfNull(declaredRoot);
        var declaredFullName = Path.GetFullPath(declaredRoot);
        var regionFullName = Path.GetFullPath(regionRoot);
        if (!Directory.Exists(regionFullName)) yield break;

        if (FindSkillFile(regionFullName, visits) is { } rootSkillFile)
        {
            yield return CreateDescriptor(
                declaredFullName,
                rootSkillFile,
                source,
                fallbackName: Path.GetFileName(regionFullName.TrimEnd(Path.DirectorySeparatorChar)));
            yield break;
        }

        // Breadth-first so "shallowest SKILL.md wins" falls out of the walk order: a
        // directory claimed by a skill is never entered, and the first level is scanned
        // before any of its children. The visited bound keeps a wide skill-less tree from
        // turning the catalog build into a full-disk walk; the found bound counts inert
        // diagnostic entries too, so a broken tree floods the diagnostics, not the prompt.
        var pending = new Queue<string>();
        pending.Enqueue(regionFullName);
        var visited = 0;
        var found = 0;
        while (pending.Count > 0 && found < MaximumSkillsPerRoot)
        {
            var directory = pending.Dequeue();
            if (++visited > MaximumVisitedDirectories) break;

            string[] childDirectories;
            try
            {
                visits?.Increment();
                childDirectories = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in childDirectories)
            {
                if (found >= MaximumSkillsPerRoot) break;

                // A symlinked (or junctioned) child directory would walk and watch outside
                // the declared root — ResolveLinkTarget only resolves the final component,
                // so a later per-file check cannot see through an ancestor link (the
                // WorkspaceHome canonicalization trap). Linked directories are not
                // traversed at all; a skill set living behind a link is declared by
                // configuring that location as its own root.
                if (IsLinkedDirectory(child)) continue;

                // The skill's subdirectories are resources; every other sibling continues
                // the walk — one file probe per child decides both.
                if (FindSkillFile(child, visits) is not { } skillFile)
                {
                    pending.Enqueue(child);
                    continue;
                }

                found++;
                yield return CreateDescriptor(
                    declaredFullName, skillFile, source, fallbackName: Path.GetFileName(child));
            }
        }
    }

    /// <summary>Revalidates one already-known skill directory after a watched change inside
    /// it: returns the fresh descriptor when its <c>SKILL.md</c> is still present (usable or
    /// inert), or <see langword="null"/> when the skill is gone and the caller must widen
    /// to a region walk of the directory (its subtree may contain skills that were
    /// resources a moment ago).</summary>
    internal static SkillDescriptor? RediscoverSkillDirectory(
        string skillDirectory,
        string declaredRoot,
        SkillSource source,
        SkillDirectoryVisitCounter? visits = null)
    {
        ArgumentNullException.ThrowIfNull(skillDirectory);
        ArgumentNullException.ThrowIfNull(declaredRoot);
        var directoryFullName = Path.GetFullPath(skillDirectory);
        if (!Directory.Exists(directoryFullName)) return null;
        if (FindSkillFile(directoryFullName, visits) is not { } skillFile) return null;
        return CreateDescriptor(
            Path.GetFullPath(declaredRoot),
            skillFile,
            source,
            fallbackName: Path.GetFileName(directoryFullName.TrimEnd(Path.DirectorySeparatorChar)));
    }

    /// <summary>Finds a directory's skill file by its stored name, compared ordinally. A
    /// case-insensitive filesystem would satisfy <c>File.Exists</c> for <c>skill.md</c>;
    /// enumerating and comparing the on-disk name keeps the exact-filename rule meaningful
    /// on every platform.</summary>
    private static string? FindSkillFile(string directory, SkillDirectoryVisitCounter? visits)
    {
        try
        {
            visits?.Increment();
            foreach (var file in Directory.EnumerateFiles(directory))
                if (string.Equals(Path.GetFileName(file), SkillFileName, StringComparison.Ordinal))
                    return file;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>Whether the directory is a reparse point (symlink or junction). Swallowing
    /// the probe failure reads as "linked": an unprobeable entry is not walked.</summary>
    private static bool IsLinkedDirectory(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Builds one skill's descriptor: usable, or inert with the first failure as
    /// its diagnostic. Pure — the caller decides whether to yield, store, or diff it.</summary>
    private static SkillDescriptor CreateDescriptor(
        string rootFullName,
        string skillFilePath,
        SkillSource source,
        string fallbackName)
    {
        try
        {
            var info = new FileInfo(skillFilePath);
            if (info.Length > MaximumBodyBytes)
            {
                return new SkillDescriptor(fallbackName, string.Empty, source, rootFullName, skillFilePath,
                    $"The skill body '{skillFilePath}' exceeds {MaximumBodyBytes} bytes.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SkillDescriptor(fallbackName, string.Empty, source, rootFullName, skillFilePath,
                $"The skill body '{skillFilePath}' cannot be inspected: {exception.Message}");
        }

        string body;
        try
        {
            body = File.ReadAllText(skillFilePath, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SkillDescriptor(fallbackName, string.Empty, source, rootFullName, skillFilePath,
                $"The skill body '{skillFilePath}' cannot be read: {exception.Message}");
        }

        if (!SkillFrontmatter.TryRead(body, out var frontmatterName, out var description, out var parseError))
            return new SkillDescriptor(fallbackName, string.Empty, source, rootFullName, skillFilePath, parseError);

        var name = frontmatterName ?? fallbackName;
        if (!SkillDescriptor.IsValidName(name))
        {
            return new SkillDescriptor(fallbackName, string.Empty, source, rootFullName, skillFilePath,
                $"The skill name '{name}' is not a valid catalog name.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return new SkillDescriptor(name, string.Empty, source, rootFullName, skillFilePath,
                "The skill has no description; a catalog entry requires one.");
        }

        // Containment is checked last so the diagnostic for an escaping path is the one
        // reported even when the file also parses.
        var fullPath = Path.GetFullPath(skillFilePath);
        if (!IsWithinRoot(rootFullName, fullPath))
        {
            return new SkillDescriptor(name, description, source, rootFullName, skillFilePath,
                $"The skill path '{skillFilePath}' resolves outside its root.");
        }

        // A symlinked (or junctioned) skill file that finally resolves outside its root is
        // rejected: its skill:// body would read outside the declared discovery scope.
        try
        {
            var link = new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true);
            if (link is not null && !IsWithinRoot(rootFullName, link.FullName))
            {
                return new SkillDescriptor(name, description, source, rootFullName, skillFilePath,
                    $"The skill path '{skillFilePath}' links outside its root.");
            }
        }
        catch (IOException exception)
        {
            return new SkillDescriptor(name, description, source, rootFullName, skillFilePath,
                $"The skill path '{skillFilePath}' cannot be resolved: {exception.Message}");
        }

        return new SkillDescriptor(name, description, source, rootFullName, fullPath);
    }

    /// <summary>Ordinal containment check over resolved paths, matching the plugin
    /// manifest's root rule. Lexical only — link-target resolution is a separate,
    /// explicit check at discovery time.</summary>
    internal static bool IsWithinRoot(string rootFullName, string path)
    {
        var relative = Path.GetRelativePath(rootFullName, path);
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith("../", StringComparison.Ordinal) &&
               relative.Length > 0;
    }

    /// <summary>Containment check over resolved paths under an explicit comparison, for
    /// the catalog's differential matching on platforms whose default filesystems are
    /// case-insensitive: <see cref="Path.GetRelativePath" /> matches the common prefix
    /// with the platform's own case rules (ordinal on Unix), so a case-insensitive caller
    /// gets a direct directory-boundary prefix check instead. Strictly inside — the
    /// boundary equality is the caller's to handle.</summary>
    internal static bool IsWithinRoot(string rootFullName, string path, StringComparison comparison)
    {
        if (string.Equals(rootFullName, path, comparison)) return false;
        var prefix = rootFullName.EndsWith(Path.DirectorySeparatorChar)
            ? rootFullName
            : rootFullName + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison) && path.Length > prefix.Length;
    }
}
