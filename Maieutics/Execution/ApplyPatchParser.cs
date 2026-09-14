using System.Collections.Immutable;
using System.Text;

namespace Maieutics.Execution;

/// <summary>A parsed V4A apply_patch document: the ordered file operations the
/// model asked for. Paths are workspace-relative; <see cref="ApplyPatchFileChange.Changes"/>
/// is populated for update operations only.</summary>
internal sealed record ApplyPatchDocument(
    ImmutableArray<ApplyPatchFileChange> Files);

/// <summary>One file operation inside a patch.</summary>
/// <param name="Kind">create_file, update_file, or delete_file.</param>
/// <param name="Path">The workspace-relative target path.</param>
/// <param name="MoveToPath">The rename target of an update operation, when given.</param>
/// <param name="Diff">For create_file the decoded file content; for update_file
/// the raw section body verbatim; null for delete_file.</param>
/// <param name="Changes">The parsed change sections of an update operation.</param>
internal sealed record ApplyPatchFileChange(
    string Kind,
    string Path,
    string? MoveToPath,
    string? Diff,
    ImmutableArray<ApplyPatchChangeSection> Changes);

/// <summary>One `@@` change section of an update operation.</summary>
/// <param name="Anchor">The optional context line text following the `@@` marker.</param>
/// <param name="OldLines">Context and removed lines, in order: the pattern to locate.</param>
/// <param name="NewLines">Context and added lines, in order: the replacement.</param>
internal sealed record ApplyPatchChangeSection(
    string? Anchor,
    ImmutableArray<string> OldLines,
    ImmutableArray<string> NewLines);

/// <summary>Parses the V4A patch format used by OpenAI's apply_patch tool:
/// `*** Begin Patch` / `*** End Patch` sentinels around `*** Add File:`,
/// `*** Update File:` (optionally `*** Move to:`), and `*** Delete File:`
/// headers whose bodies are `@@`-delimited change sections of space
/// (context), `-` (removed), and `+` (added) lines. Blank lines are
/// formatting artifacts: an empty context line is encoded as a lone space.</summary>
internal static class ApplyPatchParser
{
    private const string BeginMarker = "*** Begin Patch";
    private const string EndMarker = "*** End Patch";
    private const string AddFileMarker = "*** Add File: ";
    private const string UpdateFileMarker = "*** Update File: ";
    private const string DeleteFileMarker = "*** Delete File: ";
    private const string MoveToMarker = "*** Move to: ";
    private const string EndOfFileMarker = "*** End of File";

    /// <summary>Parses a complete patch document (with sentinels).</summary>
    /// <exception cref="WorkspaceException">With code workspace_patch_invalid
    /// when the document violates the format.</exception>
    internal static ApplyPatchDocument Parse(string patch)
    {
        var lines = SplitLines(patch);
        var first = SkipBlank(lines, 0);
        if (first >= lines.Count || lines[first] != BeginMarker)
            throw InvalidPatch("The patch must start with '*** Begin Patch'.");

        var body = CollectBody(lines, first + 1);
        return ParseBody(body);
    }

    /// <summary>Parses the bare diff body of one structured apply_patch_call
    /// operation: all-added lines for create_file, change sections for
    /// update_file, and an empty body for delete_file.</summary>
    internal static ApplyPatchDocument ParseOperationDiff(string kind, string path, string? diff)
    {
        var body = Numbered(SplitLines(diff ?? ""));
        return kind switch
        {
            "create_file" => new ApplyPatchDocument([ParseCreateFile(path, body)]),
            "update_file" => new ApplyPatchDocument([ParseUpdateFile(path, null, body)]),
            "delete_file" => new ApplyPatchDocument([new ApplyPatchFileChange("delete_file", path, null, null, [])]),
            _ => throw InvalidPatch($"Unsupported apply_patch operation '{kind}'.")
        };
    }

    private static ApplyPatchDocument ParseBody(List<(string Line, int Number)> body)
    {
        var files = ImmutableArray.CreateBuilder<ApplyPatchFileChange>();
        var index = 0;
        while (index < body.Count)
        {
            var (line, number) = body[index];
            if (line.StartsWith(AddFileMarker, StringComparison.Ordinal))
            {
                var path = RequirePath(line, number);
                var (content, next) = CollectPrefixed(body, index + 1, '+');
                index = next;
                files.Add(new ApplyPatchFileChange("create_file", path, null, RenderLines(content), []));
            }
            else if (line.StartsWith(UpdateFileMarker, StringComparison.Ordinal))
            {
                var path = RequirePath(line, number);
                index++;
                var (sectionLines, next) = CollectUntilFileHeader(body, index);
                index = next;
                string? moveTo = null;
                if (index < body.Count && body[index].Line.StartsWith(MoveToMarker, StringComparison.Ordinal))
                {
                    moveTo = RequirePath(body[index].Line, body[index].Number);
                    index++;
                }

                files.Add(ParseUpdateFile(path, moveTo, sectionLines));
            }
            else if (line.StartsWith(DeleteFileMarker, StringComparison.Ordinal))
            {
                var path = RequirePath(line, number);
                index++;
                files.Add(new ApplyPatchFileChange("delete_file", path, null, null, []));
            }
            else if (line.Length == 0)
            {
                index++;
            }
            else
            {
                throw InvalidPatch($"Line {number}: expected a file header, found '{Truncate(line)}'.");
            }
        }

        if (files.Count == 0)
            throw InvalidPatch("The patch does not contain any file operations.");

        return new ApplyPatchDocument(files.ToImmutable());
    }

    private static (List<(string Line, int Number)> Lines, int Next) CollectUntilFileHeader(
        List<(string Line, int Number)> body,
        int start)
    {
        var collected = new List<(string Line, int Number)>();
        var index = start;
        while (index < body.Count)
        {
            var line = body[index].Line;
            if (line.StartsWith(AddFileMarker, StringComparison.Ordinal) ||
                line.StartsWith(UpdateFileMarker, StringComparison.Ordinal) ||
                line.StartsWith(DeleteFileMarker, StringComparison.Ordinal) ||
                line.StartsWith(MoveToMarker, StringComparison.Ordinal) ||
                line == EndMarker)
                break;

            collected.Add(body[index]);
            index++;
        }

        return (collected, index);
    }

    private static (List<(string Line, int Number)> Lines, int Next) CollectPrefixed(
        List<(string Line, int Number)> body,
        int start,
        char prefix)
    {
        var collected = new List<(string Line, int Number)>();
        var index = start;
        while (index < body.Count)
        {
            var (line, number) = body[index];
            if (line.StartsWith(AddFileMarker, StringComparison.Ordinal) ||
                line.StartsWith(UpdateFileMarker, StringComparison.Ordinal) ||
                line.StartsWith(DeleteFileMarker, StringComparison.Ordinal) ||
                line == EndMarker)
                break;

            if (line == EndOfFileMarker)
            {
                index++;
                break;
            }

            if (line.Length == 0)
                throw InvalidPatch($"Line {number}: create-file content lines must start with '{prefix}'.");
            if (line[0] != prefix)
                throw InvalidPatch($"Line {number}: create-file content lines must start with '{prefix}', found '{Truncate(line)}'.");

            collected.Add((line[1..], number));
            index++;
        }

        return (collected, index);
    }

    private static ApplyPatchFileChange ParseUpdateFile(
        string path,
        string? moveTo,
        List<(string Line, int Number)> sectionLines)
    {
        var sections = ImmutableArray.CreateBuilder<ApplyPatchChangeSection>();
        var index = 0;
        while (index < sectionLines.Count)
        {
            while (index < sectionLines.Count && sectionLines[index].Line.Length == 0) index++;
            if (index >= sectionLines.Count) break;

            var (line, number) = sectionLines[index];
            if (!line.StartsWith("@@", StringComparison.Ordinal))
                throw InvalidPatch($"Line {number}: an update section must begin with '@@', found '{Truncate(line)}'.");

            var anchor = line.Length > 2 ? line[2..].Trim() : null;
            if (anchor is { Length: 0 }) anchor = null;
            index++;

            var oldLines = ImmutableArray.CreateBuilder<string>();
            var newLines = ImmutableArray.CreateBuilder<string>();
            while (index < sectionLines.Count)
            {
                var (body, bodyNumber) = sectionLines[index];
                if (body.Length == 0 || body.StartsWith("@@", StringComparison.Ordinal) ||
                    body.StartsWith("*** ", StringComparison.Ordinal))
                    break;

                switch (body[0])
                {
                    case ' ':
                        oldLines.Add(body[1..]);
                        newLines.Add(body[1..]);
                        break;
                    case '-':
                        oldLines.Add(body[1..]);
                        break;
                    case '+':
                        newLines.Add(body[1..]);
                        break;
                    default:
                        throw InvalidPatch(
                            $"Line {bodyNumber}: update lines must start with ' ', '-', or '+', found '{Truncate(body)}'.");
                }

                index++;
            }

            if (oldLines.Count == 0 && newLines.Count == 0)
                throw InvalidPatch($"Line {number}: the update section starting here contains no change lines.");

            sections.Add(new ApplyPatchChangeSection(anchor, oldLines.ToImmutable(), newLines.ToImmutable()));
        }

        return new ApplyPatchFileChange("update_file", path, moveTo, RenderNumbered(sectionLines), sections.ToImmutable());
    }

    private static ApplyPatchFileChange ParseCreateFile(string path, List<(string Line, int Number)> body)
    {
        var content = new List<(string Line, int Number)>();
        foreach (var (line, number) in body)
        {
            if (line.Length == 0) continue;
            if (line[0] != '+')
                throw InvalidPatch($"Line {number}: create-file content lines must start with '+', found '{Truncate(line)}'.");

            content.Add((line[1..], number));
        }

        return new ApplyPatchFileChange("create_file", path, null, RenderLines(content), []);
    }

    private static List<string> SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                lines.Add(text[start..].TrimEnd('\r'));
                break;
            }

            lines.Add(text[start..newline].TrimEnd('\r'));
            start = newline + 1;
        }

        return lines;
    }

    private static List<(string Line, int Number)> Numbered(List<string> lines)
    {
        var numbered = new List<(string, int)>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
            numbered.Add((lines[index], index + 1));

        return numbered;
    }

    private static List<(string Line, int Number)> CollectBody(List<string> lines, int start)
    {
        var body = new List<(string, int)>();
        var index = start;
        var ended = false;
        for (; index < lines.Count; index++)
        {
            if (lines[index] == EndMarker)
            {
                ended = true;
                index++;
                break;
            }

            body.Add((lines[index], index + 1));
        }

        if (!ended) throw InvalidPatch("The patch must end with '*** End Patch'.");

        while (index < lines.Count && lines[index].Length == 0) index++;
        if (index < lines.Count) throw InvalidPatch($"Line {index + 1}: unexpected content after '*** End Patch'.");

        return body;
    }

    private static int SkipBlank(List<string> lines, int start)
    {
        var index = start;
        while (index < lines.Count && lines[index].Length == 0) index++;
        return index;
    }

    private static string RequirePath(string line, int number)
    {
        var path = line[line.IndexOf(':')..][1..].Trim();
        if (path.Length == 0) throw InvalidPatch($"Line {number}: the file header is missing a path.");

        return path;
    }

    private static string RenderLines(List<(string Line, int Number)> body)
    {
        var builder = new StringBuilder();
        foreach (var (line, _) in body)
        {
            builder.Append(line);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string RenderNumbered(List<(string Line, int Number)> body)
    {
        return RenderLines(body);
    }

    private static string Truncate(string line)
    {
        return line.Length <= 60 ? line : line[..60] + "…";
    }

    private static WorkspaceException InvalidPatch(string message)
    {
        return new WorkspaceException("workspace_patch_invalid", message);
    }
}
