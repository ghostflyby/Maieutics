using System.Text.RegularExpressions;

namespace Maieutics.Execution;

/// <summary>
///     The rg-style glob the search filters accept (ADR 0042 stage F-II): `**`
///     crosses separators, `*` and `?` stay within one segment, and a glob
///     without a separator matches the file name at any depth (the gitignore
///     rule). Matching is case-insensitive — the extension host's matcher uses
///     the same rule, so the two search surfaces agree.
/// </summary>
internal static partial class SearchGlobs
{
    public static bool Passes(
        string relativePath,
        string fileName,
        IReadOnlyList<string>? include,
        IReadOnlyList<string>? exclude)
    {
        if (exclude is { Count: > 0 })
        {
            foreach (var pattern in exclude)
            {
                if (Matches(pattern, relativePath, fileName)) return false;
            }
        }

        if (include is { Count: > 0 })
        {
            foreach (var pattern in include)
            {
                if (Matches(pattern, relativePath, fileName)) return true;
            }

            return false;
        }

        return true;
    }

    private static bool Matches(string pattern, string relativePath, string fileName)
    {
        var regex = GlobRegex(pattern);
        if (pattern.Contains('/')) return regex.IsMatch(relativePath);

        // The gitignore rule: a separator-free glob names a segment — it matches
        // the file's own name and every ancestor directory, so `node_modules`
        // reaches the files inside without a trailing `/**`.
        foreach (var segment in relativePath.Split('/', '\\'))
        {
            if (regex.IsMatch(segment)) return true;
        }

        return regex.IsMatch(fileName);
    }

    [GeneratedRegex(@"[.+^${}()|\[\]\\]")]
    private static partial Regex MetaCharacters();

    private static Regex GlobRegex(string pattern)
    {
        var source = "^" + MetaCharacters()
            .Replace(pattern, "\\$&")
            .Replace("**", "\u0000")
            .Replace("*", "[^/]*")
            .Replace("?", "[^/]")
            .Replace("\u0000", ".*") + "$";
        return new Regex(source, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
