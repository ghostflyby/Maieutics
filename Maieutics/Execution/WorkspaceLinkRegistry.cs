using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Execution;

/// <summary>One open managed project link (ADR 0027). <paramref name="Target"/> is the
/// canonical final target path; the link name is bound to it for the lifetime of the
/// registry. <paramref name="Identity"/> is the rename-surviving file fingerprint captured
/// at open; null on capture failure or a pre-amendment registry, which keeps path-only
/// semantics.</summary>
internal sealed record WorkspaceLinkRecord(
    string Name,
    string Target,
    string? Alias,
    DateTimeOffset OpenedUtc,
    WorkspaceFileIdentity? Identity = null);

/// <summary>A closed link. The name is never reused for a different target, so persisted
/// <c>workspace://local/projects/&lt;name&gt;/...</c> URIs keep pointing at the same project
/// or fail loudly — they never silently read a different project. When both sides carry a
/// fingerprint, "same target" means the same file-system object, wherever it now lives.</summary>
internal sealed record WorkspaceRetiredLink(string Name, string Target, WorkspaceFileIdentity? Identity = null);

internal sealed record WorkspaceLinkRegistryState(
    int Version,
    List<WorkspaceLinkRecord>? Links,
    List<WorkspaceRetiredLink>? Retired);

/// <summary>The authoritative record of managed workspace links, persisted at
/// <c>.maieutics/registry.json</c> inside the workspace home (ADR 0027 §4). Physical links
/// are derived state repaired against this registry at startup; resolution validates every
/// managed traversal against it.</summary>
internal sealed class WorkspaceLinkRegistry
{
    internal const int FormatVersion = 1;
    private const int HashCharacters = 6;
    private const int MaximumNameCharacters = 200;

    private static readonly HashSet<string> WindowsReservedNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly Lock gate = new();
    private readonly string path;
    private readonly List<WorkspaceLinkRecord> links;
    private readonly List<WorkspaceRetiredLink> retired;

    private WorkspaceLinkRegistry(
        string path,
        List<WorkspaceLinkRecord> links,
        List<WorkspaceRetiredLink> retired)
    {
        this.path = path;
        this.links = links;
        this.retired = retired;
    }

    /// <summary>Loads the registry or starts a fresh one. A file that cannot be parsed is
    /// moved aside (kept for inspection) instead of failing process startup; an unparseable
    /// registry carries no safe link identities, and recreating links is always possible by
    /// opening the projects again.</summary>
    internal static WorkspaceLinkRegistry Load(string homePath)
    {
        var path = Path.Combine(homePath, WorkspaceHome.StateDirectoryName, "registry.json");
        if (!File.Exists(path)) return new WorkspaceLinkRegistry(path, [], []);

        WorkspaceLinkRegistryState? state;
        try
        {
            state = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                WorkspaceLinkJsonContext.Default.WorkspaceLinkRegistryState);
        }
        catch (JsonException)
        {
            // Corrupt but readable: keep the bytes for diagnosis and start empty. Retired names
            // are intentionally NOT recovered from a damaged file, so an operator can inspect it
            // before the registry is rebuilt.
            TryMoveAside(path);
            return new WorkspaceLinkRegistry(path, [], []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A transient read failure (a locked or unreadable file) must not take the process
            // down during composition; the link plane simply starts empty for this boot.
            return new WorkspaceLinkRegistry(path, [], []);
        }

        return new WorkspaceLinkRegistry(path, state?.Links ?? [], state?.Retired ?? []);
    }

    private static void TryMoveAside(string path)
    {
        try
        {
            File.Move(path, $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: an unmovable corrupt file still leaves the registry usable.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal IReadOnlyList<WorkspaceLinkRecord> Entries()
    {
        lock (gate)
        {
            return [.. links];
        }
    }

    internal WorkspaceLinkRecord? FindByTarget(string canonicalTarget)
    {
        lock (gate)
        {
            return links.FirstOrDefault(link => PathsEqual(link.Target, canonicalTarget));
        }
    }

    /// <summary>The open link bound to this file-system object, wherever it currently
    /// lives; null when no open record carries a matching fingerprint.</summary>
    internal WorkspaceLinkRecord? FindByFingerprint(WorkspaceFileIdentity identity)
    {
        lock (gate)
        {
            return links.FirstOrDefault(link => link.Identity?.Matches(identity) == true);
        }
    }

    internal WorkspaceLinkRecord? FindByName(string name)
    {
        lock (gate)
        {
            return links.FirstOrDefault(link =>
                string.Equals(link.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Re-points an open record to the current location of the same object,
    /// keeping the name, alias, opening time, and fingerprint (ADR 0027 amendment). The
    /// fingerprint is what makes this a move and not a re-binding.</summary>
    internal WorkspaceLinkRecord UpdateTarget(string name, string target)
    {
        lock (gate)
        {
            var index = links.FindIndex(link =>
                string.Equals(link.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new ArgumentException($"No open workspace link is named '{name}'.");

            links[index] = links[index] with { Target = target };
            Persist();
            return links[index];
        }
    }

    /// <summary>Chooses the link name for a new target: an explicit alias, else the target
    /// basename; a name already bound to a different target gets a path-hash suffix. A name
    /// retired while bound to this same object is reused, keeping identities stable across
    /// close/reopen cycles — including a close, a rename, and a reopen at the new path —
    /// where "same object" is the fingerprint when both sides carry one, and the path alone
    /// for legacy fingerprint-less tombstones.</summary>
    internal string AllocateName(
        string canonicalTarget,
        string? alias,
        WorkspaceFileIdentity? identity = null)
    {
        lock (gate)
        {
            var boundName = retired.FirstOrDefault(retiredLink =>
                    RetiredNameMatches(retiredLink, canonicalTarget, identity) &&
                    !IsOccupied(retiredLink.Name, excludeTarget: canonicalTarget, identity))
                ?.Name;
            if (boundName is not null) return boundName;

            var baseName = SanitizeName(alias ?? Basename(canonicalTarget));
            if (baseName.Length == 0)
                throw new ArgumentException(
                    "The workspace link name derived from the target path is empty or " +
                    "reserved; open the project with an explicit alias.");

            if (!IsOccupied(baseName, excludeTarget: null, identity)) return baseName;

            for (var length = HashCharacters; length <= HashCharacters * 3; length++)
            {
                var candidate = $"{baseName}-{TargetHash(canonicalTarget, length)}";
                if (!IsOccupied(candidate, excludeTarget: null, identity)) return candidate;
            }

            throw new InvalidOperationException(
                "The workspace link name space for this target is exhausted; " +
                "open the project with an explicit alias.");
        }
    }

    private static bool RetiredNameMatches(
        WorkspaceRetiredLink retiredLink,
        string canonicalTarget,
        WorkspaceFileIdentity? identity)
    {
        if (identity is not null && retiredLink.Identity is not null)
            return identity.Matches(retiredLink.Identity);

        return PathsEqual(retiredLink.Target, canonicalTarget);
    }

    internal void CommitNew(WorkspaceLinkRecord record)
    {
        lock (gate)
        {
            links.Add(record);
            Persist();
        }
    }

    internal WorkspaceLinkRecord Remove(string name)
    {
        lock (gate)
        {
            var index = links.FindIndex(link =>
                string.Equals(link.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new ArgumentException($"No open workspace link is named '{name}'.");

            var record = links[index];
            links.RemoveAt(index);
            retired.Add(new WorkspaceRetiredLink(record.Name, record.Target, record.Identity));
            Persist();
            return record;
        }
    }

    /// <summary>Windows-invalid characters become underscores, trailing dots and spaces are
    /// stripped, and reserved device names are rejected; the result must stay unique
    /// case-insensitively, which <see cref="IsOccupied"/> enforces at allocation.</summary>
    internal static string SanitizeName(string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        var builder = new StringBuilder(candidate.Length);
        foreach (var character in candidate.Trim())
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 ||
                           char.IsControl(character)
                ? '_'
                : character);

        var name = builder.ToString().TrimEnd('.', ' ');
        if (name.Length is 0 or > MaximumNameCharacters ||
            WindowsReservedNames.Contains(name))
            return string.Empty;

        return name;
    }

    private static string Basename(string canonicalTarget)
    {
        var name = Path.GetFileName(canonicalTarget.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        return name;
    }

    private static string TargetHash(string canonicalTarget, int characters)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalTarget)));
        return hex[..characters].ToLowerInvariant();
    }

    private bool IsOccupied(
        string name,
        string? excludeTarget,
        WorkspaceFileIdentity? excludeIdentity = null)
    {
        return links.Any(link =>
                   string.Equals(link.Name, name, StringComparison.OrdinalIgnoreCase) &&
                   (excludeTarget is null || !PathsEqual(link.Target, excludeTarget))) ||
               retired.Any(link =>
                   string.Equals(link.Name, name, StringComparison.OrdinalIgnoreCase) &&
                   (excludeTarget is null || !PathsEqual(link.Target, excludeTarget)) &&
                   !IsOwnFormerBinding(link, excludeIdentity));
    }

    /// <summary>Whether this tombstone is the same object reclaiming its old name at a new
    /// path: an identity match means the name is not occupied, it is waiting.</summary>
    private static bool IsOwnFormerBinding(WorkspaceRetiredLink link, WorkspaceFileIdentity? identity)
    {
        return identity is not null && link.Identity is not null && identity.Matches(link.Identity);
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
            throw new InvalidOperationException(
                "The workspace link registry path has no directory component.");

        Directory.CreateDirectory(directory);
        // A per-write unique temporary keeps two hosts sharing one DataRoot from interleaving
        // into the same scratch file, and the flush before the rename keeps a crash from
        // publishing a truncated registry (which would drop the retired-name tombstones and
        // let a retired name be reallocated to a different target).
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(
                    stream,
                    new WorkspaceLinkRegistryState(FormatVersion, links, retired),
                    WorkspaceLinkJsonContext.Default.WorkspaceLinkRegistryState);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var leftPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var rightPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(
            leftPath,
            rightPath,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(WorkspaceLinkRegistryState))]
internal sealed partial class WorkspaceLinkJsonContext : JsonSerializerContext;
