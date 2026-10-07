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

    /// <summary>Discovers the skills of one root. The root itself is the single-directory
    /// skill case when it directly contains a <c>SKILL.md</c>: its name comes from the
    /// frontmatter (fallback: the root directory name), and its subdirectories are resources
    /// that are not walked.</summary>
    internal static IReadOnlyList<SkillDescriptor> Discover(string root, SkillSource source)
    {
        ArgumentNullException.ThrowIfNull(root);
        var results = new List<SkillDescriptor>();
        var rootFullName = Path.GetFullPath(root);
        if (!Directory.Exists(rootFullName)) return results;

        if (FindSkillFile(rootFullName) is { } rootSkillFile)
        {
            AddSkill(results, rootFullName, rootSkillFile, source,
                fallbackName: Path.GetFileName(rootFullName.TrimEnd(Path.DirectorySeparatorChar)));
            return results;
        }

        // Breadth-first so "shallowest SKILL.md wins" falls out of the walk order: a
        // directory claimed by a skill is never entered, and the first level is scanned
        // before any of its children. The visited bound keeps a wide skill-less tree from
        // turning the catalog build into a full-disk walk; the results bound counts inert
        // diagnostic entries too, so a broken tree floods the diagnostics, not the prompt.
        var pending = new Queue<string>();
        pending.Enqueue(rootFullName);
        var visited = 0;
        while (pending.Count > 0 && results.Count < MaximumSkillsPerRoot)
        {
            var directory = pending.Dequeue();
            if (++visited > MaximumVisitedDirectories) break;

            string[] childDirectories;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in childDirectories)
            {
                if (results.Count >= MaximumSkillsPerRoot) break;

                // A symlinked (or junctioned) child directory would walk and watch outside
                // the declared root — ResolveLinkTarget only resolves the final component,
                // so a later per-file check cannot see through an ancestor link (the
                // WorkspaceHome canonicalization trap). Linked directories are not
                // traversed at all; a skill set living behind a link is declared by
                // configuring that location as its own root.
                if (IsLinkedDirectory(child)) continue;

                // The skill's subdirectories are resources; only siblings continue the walk.
                if (FindSkillFile(child) is not { } skillFile) continue;
                AddSkill(results, rootFullName, skillFile, source, fallbackName: Path.GetFileName(child));
            }

            foreach (var child in childDirectories)
            {
                if (IsLinkedDirectory(child)) continue;
                if (FindSkillFile(child) is not null) continue;
                pending.Enqueue(child);
            }
        }

        // Deterministic in-root order: the same-name winner is the first entry in ordinal
        // body-path order, independent of filesystem enumeration order.
        return [.. results.OrderBy(static descriptor => descriptor.BodyPath, StringComparer.Ordinal)];
    }

    /// <summary>Finds a directory's skill file by its stored name, compared ordinally. A
    /// case-insensitive filesystem would satisfy <c>File.Exists</c> for <c>skill.md</c>;
    /// enumerating and comparing the on-disk name keeps the exact-filename rule meaningful
    /// on every platform.</summary>
    private static string? FindSkillFile(string directory)
    {
        try
        {
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

    private static void AddSkill(
        List<SkillDescriptor> results,
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
                results.Add(new SkillDescriptor(fallbackName, string.Empty, source, rootFullName, skillFilePath,
                    $"The skill body '{skillFilePath}' exceeds {MaximumBodyBytes} bytes."));
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            results.Add(new SkillDescriptor(fallbackName, string.Empty, source, rootFullName, skillFilePath,
                $"The skill body '{skillFilePath}' cannot be inspected: {exception.Message}"));
            return;
        }

        string body;
        try
        {
            body = File.ReadAllText(skillFilePath, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            results.Add(new SkillDescriptor(fallbackName, string.Empty, source, rootFullName, skillFilePath,
                $"The skill body '{skillFilePath}' cannot be read: {exception.Message}"));
            return;
        }

        if (!SkillFrontmatter.TryRead(body, out var frontmatterName, out var description, out var parseError))
        {
            results.Add(new SkillDescriptor(fallbackName, string.Empty, source, rootFullName, skillFilePath, parseError));
            return;
        }

        var name = frontmatterName ?? fallbackName;
        if (!SkillDescriptor.IsValidName(name))
        {
            results.Add(new SkillDescriptor(fallbackName, string.Empty, source, rootFullName, skillFilePath,
                $"The skill name '{name}' is not a valid catalog name."));
            return;
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            results.Add(new SkillDescriptor(name, string.Empty, source, rootFullName, skillFilePath,
                "The skill has no description; a catalog entry requires one."));
            return;
        }

        // Containment is checked last so the diagnostic for an escaping path is the one
        // reported even when the file also parses.
        var fullPath = Path.GetFullPath(skillFilePath);
        if (!IsWithinRoot(rootFullName, fullPath))
        {
            results.Add(new SkillDescriptor(name, description, source, rootFullName, skillFilePath,
                $"The skill path '{skillFilePath}' resolves outside its root."));
            return;
        }

        // A symlinked (or junctioned) skill file that finally resolves outside its root is
        // rejected: its skill:// body would read outside the declared discovery scope.
        try
        {
            var link = new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true);
            if (link is not null && !IsWithinRoot(rootFullName, link.FullName))
            {
                results.Add(new SkillDescriptor(name, description, source, rootFullName, skillFilePath,
                    $"The skill path '{skillFilePath}' links outside its root."));
                return;
            }
        }
        catch (IOException exception)
        {
            results.Add(new SkillDescriptor(name, description, source, rootFullName, skillFilePath,
                $"The skill path '{skillFilePath}' cannot be resolved: {exception.Message}"));
            return;
        }

        results.Add(new SkillDescriptor(name, description, source, rootFullName, fullPath));
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
}
