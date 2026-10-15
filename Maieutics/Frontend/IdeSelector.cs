using System.Collections.Immutable;
using System.Text;
using Maieutics.Agent;
using Maieutics.Execution;

namespace Maieutics.Frontend;

/// <summary>
///     The read-side selector locator (ADR 0042 stage B, decision 8): resolves
///     anchor and patch selectors to a 1-based file line. The semantics are
///     deliberately stricter than the apply path's first-match-after-seek — a
///     locator that silently picked the first of several lookalike blocks
///     would point a hover or a navigate at the wrong symbol — so matching is
///     unique unless <c>occurrence</c> disambiguates, ambiguity is a typed
///     failure listing bounded candidate positions, and the budgets sit at
///     <c>search_text</c> scale. Patch selectors locate through pre-image
///     context lines: a V4A patch's first change section
///     (<see cref="ApplyPatchParser" />) or a git unified diff's first hunk
///     (<see cref="GitDiffParser" />) supplies the exact lines to find, and a
///     patch whose paths do not name the addressed file is a typed mismatch —
///     context is never borrowed from an unrelated patch.
/// </summary>
internal static class IdeSelector
{
    internal const int MaxAnchorLines = 64;
    internal const int MaxAnchorLineCharacters = 512;
    internal const int MaxScanBytes = 8 * 1024 * 1024;
    internal const int MaxCandidates = 5;
    internal const int MaxOccurrence = 1_000;

    /// <summary>Locates the unique occurrence of one consecutive anchor block
    /// (ordinal line equality, CRLF-normalized, no whitespace drift) and
    /// returns its 1-based start line. Zero matches fail
    /// <c>ide_selector_no_match</c>; more than one with no or an
    /// out-of-range <paramref name="occurrence" /> fails
    /// <c>ide_selector_ambiguous</c> listing bounded candidates.</summary>
    internal static int LocateAnchor(IReadOnlyList<string> fileLines, ImmutableArray<string> anchor, int occurrence)
    {
        if (anchor.IsDefaultOrEmpty)
            throw new AgentToolException("ide_invalid_arguments", "The selector 'anchor' needs at least one context line.");
        if (anchor.Length > MaxAnchorLines)
            throw new AgentToolException("ide_invalid_arguments", $"The selector 'anchor' is bounded at {MaxAnchorLines} lines.");
        foreach (var line in anchor)
            if (line.Length > MaxAnchorLineCharacters)
                throw new AgentToolException("ide_invalid_arguments", $"Anchor lines are bounded at {MaxAnchorLineCharacters} characters.");
        if (occurrence is < 0 or > MaxOccurrence)
            throw new AgentToolException(
                "ide_invalid_arguments",
                $"The selector 'occurrence' must be between 0 (unique match required) and {MaxOccurrence}.");

        var matches = new List<int>();
        for (var start = 0; start + anchor.Length <= fileLines.Count; start++)
        {
            var found = true;
            for (var offset = 0; offset < anchor.Length; offset++)
            {
                if (!EqualsNormalized(fileLines[start + offset], anchor[offset]))
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                matches.Add(start);
                // With an explicit occurrence the scan can stop at the nth
                // match; without one every match is counted so the ambiguity
                // failure reports the real total.
                if (occurrence >= 1 && matches.Count >= occurrence) break;
            }
        }

        if (matches.Count == 0)
            throw new AgentToolException(
                "ide_selector_no_match",
                $"The anchor block ({anchor.Length} line(s)) does not appear in the file; re-read the region with read_text and anchor on its exact lines.");
        if (matches.Count > 1 && (occurrence == 0 || occurrence > matches.Count))
        {
            var candidates = string.Join(", ", matches.Take(MaxCandidates)
                .Select(static match => $"line {match + 1}"));
            if (matches.Count > MaxCandidates) candidates += ", …";
            throw new AgentToolException(
                "ide_selector_ambiguous",
                $"The anchor block matches {matches.Count} positions ({candidates}); narrow the anchor or pass 'occurrence'.");
        }

        // Ambiguity already threw; occurrence 0 (unspecified) picks the single
        // match, an explicit occurrence the nth — both are in range here.
        var picked = occurrence >= 1 ? Math.Min(occurrence, matches.Count) : 1;
        return matches[picked - 1] + 1;
    }

    /// <summary>Locates the patch's first change inside the addressed file and
    /// returns the 1-based start line of its pre-image block. The patch's
    /// target path must name the addressed file (ordinal, `a/`/`b/`-stripped
    /// for git) — a mismatch is typed, never borrowed.</summary>
    internal static int LocatePatch(IReadOnlyList<string> fileLines, string patch, string displayPath)
    {
        if (patch.TrimStart().StartsWith("*** Begin Patch", StringComparison.Ordinal))
        {
            ApplyPatchDocument document;
            try
            {
                document = ApplyPatchParser.Parse(patch);
            }
            catch (WorkspaceException exception)
            {
                throw new AgentToolException("ide_selector_patch_malformed", exception.Message);
            }

            var change = document.Files.FirstOrDefault(candidate =>
                string.Equals(candidate.Path, displayPath, StringComparison.Ordinal) && candidate.Changes.Length > 0);
            if (change is null)
                throw PathMismatch(displayPath, document.Files.Select(static candidate => candidate.Path));
            var section = change.Changes[0];
            var anchor = section.OldLines.IsEmpty
                ? section.Anchor is { } anchorLine ? ImmutableArray.Create(anchorLine) : default
                : section.OldLines;
            if (anchor.IsDefaultOrEmpty)
                throw new AgentToolException(
                    "ide_selector_patch_malformed",
                    $"The patch section for '{displayPath}' carries no context lines to locate; use the coordinate selector.");
            return LocateAnchor(fileLines, anchor, 1);
        }

        GitDiffDocument document2;
        try
        {
            document2 = GitDiffParser.Parse(patch);
        }
        catch (WorkspaceException exception)
        {
            throw new AgentToolException(exception.Code, exception.Message);
        }

        var file = document2.Files.FirstOrDefault(candidate =>
            string.Equals(candidate.NewPath ?? candidate.OldPath, displayPath, StringComparison.Ordinal));
        if (file is null)
            throw PathMismatch(displayPath, document2.Files.Select(static candidate => candidate.NewPath ?? candidate.OldPath ?? "(none)"));
        if (file.Binary || file.Hunks.IsEmpty)
            throw new AgentToolException(
                "ide_selector_patch_malformed",
                $"The patch section for '{displayPath}' carries no hunks to locate (binary or metadata-only); use the coordinate selector.");
        var hunk = file.Hunks[0];
        var preImage = hunk.Lines.Where(static line => line.Origin is ' ' or '-')
            .Select(static line => line.Text)
            .ToImmutableArray();
        if (preImage.IsEmpty)
            throw new AgentToolException(
                "ide_selector_patch_malformed",
                $"The first hunk of '{displayPath}' is a pure insertion with no pre-image context; use the coordinate selector.");
        return LocateAnchor(fileLines, preImage, 1);
    }

    /// <summary>Reads the whole file for the selector scan, bounded at
    /// <see cref="MaxScanBytes" /> (the <c>search_text</c> scan scale) with
    /// strict UTF-8 and BOM stripping; line endings normalize to '\n'.</summary>
    internal static async ValueTask<IReadOnlyList<string>> ReadLinesAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        var buffer = new byte[MaxScanBytes + 1];
        var read = 0;
        int chunk;
        while ((chunk = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken)) > 0)
        {
            read += chunk;
            if (read > MaxScanBytes)
                throw new AgentToolException(
                    "ide_selector_file_too_large",
                    $"The selector scan is bounded at {MaxScanBytes / (1024 * 1024)} MiB; the file is larger. Use read_text windows instead.");
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(buffer, 0, read);
        }
        catch (DecoderFallbackException)
        {
            throw new AgentToolException(
                "workspace_binary_file",
                "The selector needs decodable text; the file is not valid UTF-8.");
        }

        if (text.StartsWith('\uFEFF')) text = text[1..];
        return text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
    }

    private static AgentToolException PathMismatch(string displayPath, IEnumerable<string?> available)
    {
        var named = string.Join(", ", available
            .Where(static path => path is not null)
            .Take(MaxCandidates));
        return new AgentToolException(
            "ide_selector_path_mismatch",
            $"The patch does not name the addressed file '{displayPath}' (patch targets: {named}). Anchor on the right patch for this file.");
    }

    private static bool EqualsNormalized(string fileLine, string anchorLine)
    {
        // Ordinal equality; the file side is already \n-normalized at the split,
        // so the anchor side only sheds a trailing \r (a CRLF paste artifact).
        return string.Equals(fileLine, anchorLine.EndsWith('\r') ? anchorLine[..^1] : anchorLine, StringComparison.Ordinal);
    }
}
