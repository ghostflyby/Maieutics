namespace Maieutics.Permissions;

/// <summary>Canonical pattern matching for permission values, shared by the Deno broker resolver
/// and the process-launch enforcement seams (terminal executable, MCP command, terminal/MCP
/// environment). The match mode comes from <see cref="PermissionKinds.MatchMode"/> so every
/// enforcer judges a kind identically: path kinds and <c>run</c> match on a directory boundary so
/// granting <c>/tmp</c> never admits <c>/tmp-evil</c>, <c>net</c> and <c>import</c> keep Deno's
/// prefix semantics, and <c>env</c>/<c>sys</c> names match exactly. Empty patterns never match:
/// the layer store rejects them at build time, and the checks here are the defensive backstop for
/// patterns that reach the enforcers without a build.</summary>
internal static class PermissionMatching
{
    /// <summary>Whether any pattern matches <paramref name="value"/> under the kind's one match
    /// mode (path boundary, prefix, or exact) from <see cref="PermissionKinds.MatchMode"/>.</summary>
    internal static bool MatchesAny(PermissionKind kind, IReadOnlyList<string> patterns, string value)
    {
        var comparison = Comparison;
        var mode = PermissionKinds.MatchMode(kind);
        foreach (var pattern in patterns)
        {
            if (pattern.Length == 0) continue;

            var matched = mode switch
            {
                PermissionMatchMode.PathBoundary => MatchesPath(pattern, value, comparison),
                PermissionMatchMode.Prefix => value.StartsWith(pattern, comparison),
                PermissionMatchMode.Exact => value.Equals(pattern, comparison),
                _ => false
            };
            if (matched)
                return true;
        }

        return false;
    }

    /// <summary>Path-boundary match used by the launch enforcement seams: the value equals the
    /// pattern or lives inside the pattern directory (pattern followed by a separator). Empty
    /// patterns never match.</summary>
    internal static bool MatchesPath(string pattern, string value)
    {
        return MatchesPath(pattern, value, Comparison);
    }

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool MatchesPath(string pattern, string value, StringComparison comparison)
    {
        if (pattern.Length == 0 || value.Length == 0) return false;

        pattern = NormalizeSeparators(pattern).TrimEnd(Path.DirectorySeparatorChar);
        if (pattern.Length == 0) return false;

        var normalizedValue = NormalizeSeparators(value);
        return normalizedValue.Equals(pattern, comparison) ||
               normalizedValue.StartsWith(pattern + Path.DirectorySeparatorChar, comparison);
    }

    private static string NormalizeSeparators(string path)
    {
        return Path.AltDirectorySeparatorChar == Path.DirectorySeparatorChar
            ? path
            : path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }
}
