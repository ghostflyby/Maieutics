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

        pattern = NormalizeLexically(NormalizeSeparators(pattern)).TrimEnd(Path.DirectorySeparatorChar);
        if (pattern.Length == 0) return false;

        var normalizedValue = NormalizeLexically(NormalizeSeparators(value));
        return normalizedValue.Equals(pattern, comparison) ||
               normalizedValue.StartsWith(pattern + Path.DirectorySeparatorChar, comparison);
    }

    private static string NormalizeSeparators(string path)
    {
        return Path.AltDirectorySeparatorChar == Path.DirectorySeparatorChar
            ? path
            : path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    /// <summary>Lexically normalizes <c>.</c> and <c>..</c> segments without resolving symlinks
    /// or anchoring to any working directory. Deno sends broker request paths exactly as the
    /// caller wrote them (verified 2.9.8: <c>/tmp/x/../y</c> arrives verbatim, and aliases like
    /// <c>/tmp</c> are never rewritten to their realpath), so a raw prefix match would let a
    /// request phrased <c>/granted/../outside</c> prefix-match the grant and open outside it.
    /// Collapsing the lexical segments closes that escape; a leading root anchor is never
    /// popped, a leading climb keeps its <c>..</c>s (it cannot accidentally match a shorter
    /// grant), and strings without separators (bare command names) pass through unchanged.</summary>
    private static string NormalizeLexically(string path)
    {
        if (!path.Contains('.')) return path;

        var segments = path.Split(Path.DirectorySeparatorChar);
        var stack = new List<string>(segments.Length);
        var rooted = segments.Length > 0 && segments[0].Length == 0;
        if (rooted) stack.Add(string.Empty);
        var minimumDepth = rooted ? 1 : 0;
        foreach (var segment in segments.Skip(rooted ? 1 : 0))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (stack.Count > minimumDepth && stack[^1] != "..")
                    stack.RemoveAt(stack.Count - 1);
                else
                    stack.Add("..");
                continue;
            }

            stack.Add(segment);
        }

        return string.Join(Path.DirectorySeparatorChar, stack);
    }
}
