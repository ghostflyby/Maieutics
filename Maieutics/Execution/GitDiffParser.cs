using System.Collections.Immutable;

namespace Maieutics.Execution;

/// <summary>One file's change inside a git unified diff. <see cref="OldPath" />
/// is null for created files and <see cref="NewPath" /> null for deleted ones;
/// <see cref="Binary" /> marks a binary patch, which carries no hunks and
/// cannot be located in by context.</summary>
internal sealed record GitDiffFile(string? OldPath, string? NewPath, bool Binary, ImmutableArray<GitDiffHunk> Hunks);

/// <summary>One `@@` hunk: the header's old-side start (1-based) and the
/// ordered body lines. <see cref="GitDiffLine.Origin" /> is ' ' (context),
/// '-' (removed), or '+' (added) — the hunk's pre-image is the ' ' and '-'
/// lines in order.</summary>
internal sealed record GitDiffHunk(int OldStart, ImmutableArray<GitDiffLine> Lines)
{
    /// <summary>The context plus removed lines — what the pre-image side of
    /// the hunk expects to find in the file.</summary>
    internal int PreImageCount => Lines.Count(static line => line.Origin is ' ' or '-');
}

internal readonly record struct GitDiffLine(char Origin, string Text);

/// <summary>The parsed form of one git unified diff document.</summary>
internal sealed record GitDiffDocument(ImmutableArray<GitDiffFile> Files);

/// <summary>
///     Bounded parser for the git unified diff format (ADR 0042 stage B):
///     the read-side selector addresses files through hunk context lines, and
///     later stages reuse the parsed form to apply model-provided git patches
///     through the gated path (<see cref="UnifiedDiff" /> stays the generator).
///     Tolerates the decorations git emits (`diff --git` headers, `index` /
///     mode / similarity lines, rename from/to, `\ No newline at end of
///     file`); every structural violation is a typed
///     <see cref="WorkspaceException" /> with code
///     <c>ide_selector_patch_malformed</c>, every size overflow
///     <c>ide_selector_patch_too_large</c>. Paths are the `a/`- and `b/`
///     -stripped forms with any trailing timestamp tab removed;
///     `/dev/null` maps to a null path side.
/// </summary>
internal static partial class GitDiffParser
{
    internal const int MaxFiles = 64;
    internal const int MaxDiffLines = 16_000;
    internal const int MaxHunkLines = 4_000;

    private const string DiffHeader = "diff --git ";
    private const string OldFileMarker = "--- ";
    private const string NewFileMarker = "+++ ";
    private const string HunkMarker = "@@ -";

    internal static GitDiffDocument Parse(string diff)
    {
        var lines = SplitLines(diff);
        var index = 0;
        var files = ImmutableArray.CreateBuilder<GitDiffFile>();
        while (index < lines.Count)
        {
            if (lines[index].Length == 0 ||
                !(lines[index].StartsWith(DiffHeader, StringComparison.Ordinal) ||
                  lines[index].StartsWith(OldFileMarker, StringComparison.Ordinal)))
            {
                // Decoration between files (mail banners, commit messages):
                // skip and keep scanning rather than failing the parse.
                index++;
                continue;
            }

            if (files.Count >= MaxFiles)
                throw TooLarge($"the diff carries more than {MaxFiles} files.");

            index = ParseFileSection(lines, index, files);
        }

        if (files.Count == 0)
            throw Malformed("no 'diff --git' or '--- /+++' file section was found.");

        return new GitDiffDocument(files.ToImmutable());
    }

    /// <summary>Parses one file's section starting at <paramref name="start" />;
    /// returns the index of the first line after it. Path resolution order: the
    /// `--- /+++` pair, then the rename headers, then the `diff --git` banner
    /// (mode-change-only and binary sections carry paths only there).</summary>
    private static int ParseFileSection(List<string> lines, int start, ImmutableArray<GitDiffFile>.Builder files)
    {
        var hasBanner = lines[start].StartsWith(DiffHeader, StringComparison.Ordinal);
        var index = start + (hasBanner ? 1 : 0);
        var (bannerOld, bannerNew) = hasBanner ? ParseBannerPaths(lines[start]) : (null, null);
        string? oldPath = null;
        string? newPath = null;
        var sawPathPair = false;
        string? renameFrom = null;
        string? renameTo = null;
        var binary = false;
        var hunks = ImmutableArray.CreateBuilder<GitDiffHunk>();

        while (index < lines.Count &&
               !lines[index].StartsWith(DiffHeader, StringComparison.Ordinal) &&
               !lines[index].StartsWith(HunkMarker, StringComparison.Ordinal))
        {
            var line = lines[index];
            if (line.StartsWith(OldFileMarker, StringComparison.Ordinal))
            {
                oldPath = ParsePath(line[OldFileMarker.Length..]);
                index++;
                if (index < lines.Count && lines[index].StartsWith(NewFileMarker, StringComparison.Ordinal))
                {
                    newPath = ParsePath(lines[index][NewFileMarker.Length..]);
                    index++;
                    sawPathPair = true;
                    continue;
                }

                throw Malformed("the '+++' path line is missing after the '---' line.");
            }

            if (line.StartsWith("rename from ", StringComparison.Ordinal)) renameFrom = line["rename from ".Length..];
            else if (line.StartsWith("rename to ", StringComparison.Ordinal)) renameTo = line["rename to ".Length..];
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                     line.StartsWith("GIT binary patch", StringComparison.Ordinal)) binary = true;
            index++;
        }

        // /dev/null legitimately makes one side null, so the pair's presence is
        // tracked separately from the parsed values. Fallbacks: rename headers,
        // then the banner (mode-change-only and binary sections carry their
        // paths only there).
        if (!sawPathPair)
        {
            if (renameFrom is not null && renameTo is not null)
            {
                (oldPath, newPath) = (renameFrom, renameTo);
            }
            else if (hasBanner)
            {
                (oldPath, newPath) = (bannerOld, bannerNew);
            }
            else
            {
                throw Malformed("the file section carries no '--- /+++' path pair and no rename headers.");
            }
        }

        while (index < lines.Count && lines[index].StartsWith(HunkMarker, StringComparison.Ordinal))
        {
            if (hunks.Count >= MaxHunkLines)
                throw TooLarge($"file '{newPath}' carries more than {MaxHunkLines} hunks.");
            var (oldStart, expectedOld, expectedNew) = ParseHunkHeader(lines[index], newPath ?? "(null path)");
            index++;
            var body = ImmutableArray.CreateBuilder<GitDiffLine>();
            var seenOld = 0;
            var seenNew = 0;
            while (seenOld < expectedOld || seenNew < expectedNew)
            {
                if (index >= lines.Count)
                    throw Malformed($"the hunk of '{newPath}' ends before its header counts are met.");
                if (lines[index].StartsWith('\\'))
                {
                    // "\ No newline at end of file" — a formatting marker, not a diff line.
                    index++;
                    continue;
                }

                if (lines[index].Length == 0)
                    throw Malformed($"the hunk of '{newPath}' has a blank line where a diff line is required.");
                var origin = lines[index][0];
                if (origin is not (' ' or '-' or '+'))
                    throw Malformed($"the hunk of '{newPath}' has an unexpected line origin '{origin}'.");
                if (origin != '+') seenOld++;
                if (origin != '-') seenNew++;
                body.Add(new GitDiffLine(origin, lines[index][1..]));
                index++;
                if (body.Count > MaxHunkLines)
                    throw TooLarge($"a hunk of '{newPath}' exceeds {MaxHunkLines} lines.");
            }

            hunks.Add(new GitDiffHunk(oldStart, body.ToImmutable()));
        }

        // A ---/+++ pair promises content: a pair with no hunks is broken, while
        // a banner-only section without hunks (mode change, binary, pure rename)
        // is git's normal output.
        if (sawPathPair && hunks.Count == 0 && !binary)
            throw Malformed($"the section for '{newPath}' carries a path pair but no hunks.");

        files.Add(new GitDiffFile(oldPath, newPath, binary, hunks.ToImmutable()));
        return index;
    }

    private static (int OldStart, int ExpectedOld, int ExpectedNew) ParseHunkHeader(string line, string path)
    {
        // @@ -oldStart[,oldCount] +newStart[,newCount] @@ optional section heading
        var match = HunkHeader().Match(line);
        if (!match.Success)
            throw Malformed($"the hunk header for '{path}' is malformed: {line}");
        var oldStart = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var expectedOld = match.Groups[2].Success
            ? int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 1;
        var expectedNew = match.Groups[4].Success
            ? int.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 1;
        return (oldStart, expectedOld, expectedNew);
    }

    /// <summary>Parses the banner's a/old b/new paths — the only place a
    /// mode-change-only or binary section names its files. Naive on purpose:
    /// paths containing " b/" are outside the selector's scope.</summary>
    private static (string? Old, string? New) ParseBannerPaths(string line)
    {
        var rest = line[DiffHeader.Length..];
        if (rest.StartsWith("a/", StringComparison.Ordinal)) rest = rest[2..];
        var separator = rest.IndexOf(" b/", StringComparison.Ordinal);
        if (separator < 0) return (null, null);
        var oldPath = rest[..separator];
        var newPath = rest[(separator + 3)..];
        var tab = newPath.IndexOf('\t');
        if (tab >= 0) newPath = newPath[..tab];
        return (
            oldPath == "/dev/null" ? null : oldPath,
            newPath == "/dev/null" ? null : newPath);
    }

    private static string? ParsePath(string path)
    {
        path = path.TrimEnd();
        // Tab-separated timestamp: "b/src/a.ts\t2026-10-14 …" — keep the path.
        var tab = path.IndexOf('\t');
        if (tab >= 0) path = path[..tab];
        if (path == "/dev/null") return null;
        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
            path = path[2..];
        return path;
    }

    private static List<string> SplitLines(string diff)
    {
        var normalized = diff.Replace("\r\n", "\n");
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] != '\n') continue;
            lines.Add(normalized[start..i]);
            start = i + 1;
            if (lines.Count > MaxDiffLines) throw TooLarge($"the diff exceeds {MaxDiffLines} lines.");
        }

        if (start < normalized.Length) lines.Add(normalized[start..]);
        return lines;
    }

    private static WorkspaceException Malformed(string detail) =>
        new("ide_selector_patch_malformed", $"The patch is not a parsable git unified diff: {detail}");

    private static WorkspaceException TooLarge(string detail) =>
        new("ide_selector_patch_too_large", $"The patch exceeds the selector budget: {detail}");

    [System.Text.RegularExpressions.GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial System.Text.RegularExpressions.Regex HunkHeader();
}
