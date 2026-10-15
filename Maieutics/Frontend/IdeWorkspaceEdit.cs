using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Execution;

namespace Maieutics.Frontend;

/// <summary>One target file of an LSP-shaped workspace edit, addressed by its
/// agent-domain <see cref="WorkspaceUri" /> with the model's 1-based positions
/// (decision 5) still attached — validation converts them while applying.</summary>
internal sealed record IdeEditTarget(string WorkspaceUri, ImmutableArray<IdeRawEdit> Edits);

/// <summary>One text edit: an inclusive 1-based source range and the text that
/// replaces it.</summary>
internal sealed record IdeRawEdit(
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter,
    string NewText);

/// <summary>
///     Pure component behind the IDE bridge's apply step (ADR 0042 stage C,
///     decision 6): parses the agent-side edit envelope
    ///     — <c>{"changes": {"workspace://local/…": [{"range": …, "newText": …}]}}</c>
///     — into validated per-file targets and computes each file's new content
///     with LSP non-overlap semantics. Range validation converts the 1-based
///     agent positions to offsets over the file's real line table (original
///     CRLF/LF delimiters preserved verbatim — a CRLF file stays CRLF), and
///     every structural or range failure is a typed
///     <c>ide_edit_rejected</c> carrying the target path, so the caller can
///     reject the whole edit before any mutation. No disk access happens
///     here; the gated write lives in <see cref="WorkspaceEditFunctions" />.
/// </summary>
internal static class IdeWorkspaceEdit
{
    internal const int MaxTargets = 64;
    internal const int MaxEditsPerTarget = 256;

    /// <summary>Parses the edit envelope into per-file targets. The URI domain
    /// check is policy, not structure: a key outside <c>workspace://local</c>
    /// is an <c>ide_edit_rejected</c> (the domain equals the kernel write
    /// domain), while shape problems are <c>ide_invalid_arguments</c>.</summary>
    internal static ImmutableArray<IdeEditTarget> ParseEnvelope(JsonElement edit)
    {
        if (edit.ValueKind != JsonValueKind.Object ||
            !edit.TryGetProperty("changes", out var changes) ||
            changes.ValueKind != JsonValueKind.Object)
            throw new AgentToolException(
                "ide_invalid_arguments",
                "The edit must be {\"changes\": {\"workspace://local/…\": [{\"range\": …, \"newText\": …}]}}.");

        var targets = ImmutableArray.CreateBuilder<IdeEditTarget>();
        foreach (var property in changes.EnumerateObject())
        {
            if (targets.Count >= MaxTargets)
                throw new AgentToolException(
                    "ide_edit_rejected",
                    $"The edit is bounded at {MaxTargets} files per apply.");

            if (property.Value.ValueKind != JsonValueKind.Array ||
                !property.Name.StartsWith("workspace://local/", StringComparison.Ordinal))
                throw new AgentToolException(
                    "ide_edit_rejected",
                    $"The edit target '{property.Name}' is outside the workspace domain (workspace://local).");

            var edits = ImmutableArray.CreateBuilder<IdeRawEdit>();
            foreach (var entry in property.Value.EnumerateArray())
            {
                if (edits.Count >= MaxEditsPerTarget)
                    throw new AgentToolException(
                        "ide_edit_rejected",
                        $"The edit for '{property.Name}' is bounded at {MaxEditsPerTarget} edits per file.");

                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("newText", out var newTextElement) ||
                    newTextElement.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("range", out var rangeElement) ||
                    rangeElement.ValueKind != JsonValueKind.Object ||
                    !TryParsePosition(rangeElement, "start", out var startLine, out var startCharacter) ||
                    !TryParsePosition(rangeElement, "end", out var endLine, out var endCharacter))
                    throw new AgentToolException(
                        "ide_invalid_arguments",
                        $"Every edit needs a range (start/end line+character) and a string 'newText' (target '{property.Name}').");

                edits.Add(new IdeRawEdit(startLine, startCharacter, endLine, endCharacter, newTextElement.GetString()!));
            }

            targets.Add(new IdeEditTarget(property.Name, edits.ToImmutable()));
        }

        if (targets.Count == 0)
            throw new AgentToolException("ide_invalid_arguments", "The edit carries no changes.");

        return targets.ToImmutable();
    }

    /// <summary>Computes one target's new content over
    /// <paramref name="currentText" />: ranges convert from 1-based agent
    /// positions to offsets over the file's real line table, overlaps are
    /// rejected, and edits apply last-to-first. Failures are typed
    /// <c>ide_edit_rejected</c> naming the target.</summary>
    internal static string ComputeContent(IdeEditTarget target, string currentText)
    {
        var (lineTexts, lineDelimiters) = SplitLines(currentText);
        var lineStarts = new int[lineTexts.Length + 1];
        for (var i = 0; i < lineTexts.Length; i++)
            lineStarts[i + 1] = lineStarts[i] + lineTexts[i].Length + lineDelimiters[i].Length;

        var converted = new List<(int Start, int End, string Text)>(target.Edits.Length);
        foreach (var edit in target.Edits)
        {
            var startLine = edit.StartLine - 1;
            var endLine = edit.EndLine - 1;
            if (startLine < 0 || startLine >= lineTexts.Length ||
                edit.StartCharacter < 1 || edit.StartCharacter > lineTexts[startLine].Length + 1 ||
                endLine < 0 || endLine >= lineTexts.Length ||
                edit.EndCharacter < 1 || edit.EndCharacter > lineTexts[endLine].Length + 1)
                throw Rejected(target, $"a range falls outside the file (start {edit.StartLine}:{edit.StartCharacter}, end {edit.EndLine}:{edit.EndCharacter}, file has {lineTexts.Length} lines).");

            var start = lineStarts[startLine] + (edit.StartCharacter - 1);
            var end = lineStarts[endLine] + (edit.EndCharacter - 1);
            if (end < start)
                throw Rejected(target, "an edit's end precedes its start.");
            converted.Add((start, end, edit.NewText));
        }

        converted.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        for (var i = 1; i < converted.Count; i++)
            if (converted[i].Start < converted[i - 1].End)
                throw Rejected(target, "two edits overlap.");

        var builder = new StringBuilder(currentText.Length);
        var cursor = 0;
        foreach (var (start, end, text) in converted)
        {
            builder.Append(currentText, cursor, start - cursor);
            builder.Append(text);
            cursor = end;
        }
        builder.Append(currentText, cursor, currentText.Length - cursor);
        return builder.ToString();
    }

    private static bool TryParsePosition(JsonElement range, string side, out int line, out int character)
    {
        line = 0;
        character = 0;
        if (!range.TryGetProperty(side, out var position) || position.ValueKind != JsonValueKind.Object ||
            !position.TryGetProperty("line", out var lineElement) || lineElement.ValueKind != JsonValueKind.Number ||
            !lineElement.TryGetInt32(out line) ||
            !position.TryGetProperty("character", out var characterElement) ||
            characterElement.ValueKind != JsonValueKind.Number ||
            !characterElement.TryGetInt32(out character))
            return false;
        return true;
    }

    /// <summary>Splits into per-line text and the exact delimiter that ended it
    /// (CRLF kept as one delimiter; the final line's delimiter is empty), so a
    /// reconstructed file preserves its original line endings.</summary>
    private static (string[] Texts, string[] Delimiters) SplitLines(string text)
    {
        var texts = new List<string>();
        var delimiters = new List<string>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\n')
            {
                texts.Add(text[start..i]);
                delimiters.Add("\n");
                i++;
                start = i;
            }
            else if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                texts.Add(text[start..i]);
                delimiters.Add("\r\n");
                i += 2;
                start = i;
            }
            else i++;
        }

        texts.Add(text[start..]);
        delimiters.Add(string.Empty);
        return (texts.ToArray(), delimiters.ToArray());
    }

    private static AgentToolException Rejected(IdeEditTarget target, string reason) =>
        new("ide_edit_rejected", $"'{target.WorkspaceUri}': {reason}");
}
