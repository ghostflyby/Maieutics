using System.Security.Cryptography;
using System.Text;

namespace Maieutics.Plugins;

/// <summary>
///     Derives the per-plugin persistent-storage directory from the plugin identity (ADR 0022).
///     The kernel owns path derivation: the Deno host only receives the resolved directory in the
///     plugin config and never derives storage paths itself.
/// </summary>
/// <remarks>
///     The identity is the manifest package name (the specifier identity, stable across
///     directory renames) falling back to the scanned directory id. Safe names are used verbatim;
///     names that needed sanitizing always carry a short identity hash so the result does not
///     depend on scan order. Plugin removal keeps the data directory on disk.
/// </remarks>
internal static class PluginStoragePaths
{
    public const string RootFolderName = "plugin-data";

    public static string DirectoryFor(string pluginDataRoot, string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var sanitized = Sanitize(identity);
        var directoryName = sanitized == identity
            ? sanitized
            : $"{sanitized}-{HashPrefix(identity, 8)}";
        return Path.Combine(pluginDataRoot, directoryName);
    }

    /// <summary>Assigns one storage directory per distinct identity. Identities whose derived
    /// directory collides with a DIFFERENT identity are assigned <see langword="null"/> — the
    /// whole colliding group starts without storage (typed runtime errors) instead of silently
    /// sharing one store. The resident host must not fail startup over a manifest name.</summary>
    public static IReadOnlyDictionary<string, string?> Assign(
        string pluginDataRoot,
        IEnumerable<string> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var byDirectory = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var identity in identities.Distinct(StringComparer.Ordinal))
        {
            var directory = DirectoryFor(pluginDataRoot, identity);
            if (!byDirectory.TryGetValue(directory, out var owners))
            {
                owners = [];
                byDirectory.Add(directory, owners);
            }
            owners.Add(identity);
        }

        var assignment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (directory, owners) in byDirectory)
        {
            var shared = owners.Count == 1;
            foreach (var identity in owners)
            {
                assignment[identity] = shared ? directory : null;
            }
        }
        return assignment;
    }

    /// <summary>Maps the identity onto one safe directory segment; everything outside
    /// <c>[A-Za-z0-9._-]</c> collapses to <c>_</c>, and degenerate results become <c>plugin</c>.</summary>
    public static string Sanitize(string identity)
    {
        var chars = identity
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_')
            .ToArray();
        var name = new string(chars);
        if (name.Length == 0 || name is "." or "..")
        {
            name = "plugin";
        }
        return name;
    }

    private static string HashPrefix(string identity, int length)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash)[..length].ToLowerInvariant();
    }
}
