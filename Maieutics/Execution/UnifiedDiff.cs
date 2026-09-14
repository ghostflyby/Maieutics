using System.Text;

namespace Maieutics.Execution;

/// <summary>A bounded line-oriented unified diff over two UTF-8 texts.</summary>
/// <param name="Unified">The diff body: the file header plus hunks, LF separated.</param>
/// <param name="Additions">Inserted lines across the full edit script.</param>
/// <param name="Deletions">Deleted lines across the full edit script.</param>
/// <param name="Truncated">Whether the rendered body stopped at an output bound.</param>
internal sealed record WorkspaceEditDiff(string Unified, int Additions, int Deletions, bool Truncated);

/// <summary>Computes bounded unified diffs for the workspace edit tools. Line
/// comparison is ordinal; the alignment is a Myers shortest-edit pass whose
/// distance and comparison budgets fall back to a whole-range replacement
/// hunk, so worst-case inputs stay bounded rather than minimal.</summary>
internal static class UnifiedDiff
{
    internal const int ContextLines = 3;
    private const int MaximumEditDistance = 512;
    private const long MaximumLineComparisons = 4_000_000;
    private const string NoNewlineMarker = "\\ No newline at end of file";

    /// <summary>Computes the diff between two texts. Returns null when the texts
    /// are equal. <paramref name="displayPath"/> labels the file header; the
    /// body is bounded by <paramref name="maximumUtf8Bytes"/> and
    /// <paramref name="maximumLines"/>, reported through
    /// <see cref="WorkspaceEditDiff.Truncated"/>.</summary>
    internal static WorkspaceEditDiff? Create(
        string? before,
        string after,
        string displayPath,
        int maximumUtf8Bytes,
        int maximumLines)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumUtf8Bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLines);
        var beforeText = before ?? "";
        if (string.Equals(beforeText, after, StringComparison.Ordinal)) return null;

        var beforeLines = SplitLines(beforeText, out var beforeEndsNewline);
        var afterLines = SplitLines(after, out var afterEndsNewline);
        var script = BuildScript(beforeLines, afterLines);
        if (!HasChanges(script))
            // The texts differ only in their trailing newline, so the line
            // arrays align completely. The caller's byte-level change is still
            // real; surface the final line as replaced instead of an empty diff.
            script = FinalLineReplacementScript(beforeLines, afterLines);

        return Render(
            script,
            beforeLines,
            afterLines,
            beforeEndsNewline,
            afterEndsNewline,
            displayPath,
            maximumUtf8Bytes,
            maximumLines);
    }

    private static string[] SplitLines(string text, out bool endsNewline)
    {
        endsNewline = text.Length > 0 && text[^1] == '\n';
        if (text.Length == 0) return [];

        var lines = text.Split('\n');
        if (endsNewline) lines = lines[..^1];
        return lines;
    }

    private enum OpKind : byte
    {
        Equal,
        Delete,
        Insert
    }

    private readonly record struct ScriptOp(OpKind Kind, int OldIndex, int NewIndex);

    /// <summary>Builds the forward edit script over the full line arrays, ordered
    /// by position. Delete and insert indices carry the cursor position at the
    /// op, so hunk headers number without a second pass.</summary>
    private static List<ScriptOp> BuildScript(string[] before, string[] after)
    {
        var start = 0;
        while (start < before.Length && start < after.Length &&
               string.Equals(before[start], after[start], StringComparison.Ordinal))
            start++;

        var beforeEnd = before.Length;
        var afterEnd = after.Length;
        while (beforeEnd > start && afterEnd > start &&
               string.Equals(before[beforeEnd - 1], after[afterEnd - 1], StringComparison.Ordinal))
        {
            beforeEnd--;
            afterEnd--;
        }

        var script = new List<ScriptOp>(before.Length + after.Length);
        for (var index = 0; index < start; index++)
            script.Add(new ScriptOp(OpKind.Equal, index, index));

        if (beforeEnd > start || afterEnd > start)
        {
            if (beforeEnd == start || afterEnd == start)
                AppendRange(script, start, beforeEnd, start, afterEnd);
            else
                AppendMyersScript(script, before, after, start, beforeEnd, start, afterEnd);
        }

        for (var index = beforeEnd; index < before.Length; index++)
            script.Add(new ScriptOp(OpKind.Equal, index, index - beforeEnd + afterEnd));

        return script;
    }

    private static void AppendRange(List<ScriptOp> script, int oldStart, int oldEnd, int newStart, int newEnd)
    {
        for (var index = oldStart; index < oldEnd; index++)
            script.Add(new ScriptOp(OpKind.Delete, index, newStart));

        for (var index = newStart; index < newEnd; index++)
            script.Add(new ScriptOp(OpKind.Insert, oldEnd, index));
    }

    /// <summary>Myers greedy shortest-edit search over the trimmed ranges,
    /// including the matched diagonal runs as equal ops. Falls back to a
    /// full-range replacement when the edit distance exceeds
    /// <see cref="MaximumEditDistance"/> or the comparison budget runs out.</summary>
    private static void AppendMyersScript(
        List<ScriptOp> script,
        string[] before,
        string[] after,
        int oldStart,
        int oldEnd,
        int newStart,
        int newEnd)
    {
        var oldCount = oldEnd - oldStart;
        var newCount = newEnd - newStart;
        var maximumDistance = Math.Min(MaximumEditDistance, oldCount + newCount);
        var offset = maximumDistance;
        var vectors = new int[(2 * offset) + 1];
        var trace = new List<int[]>(maximumDistance + 1);
        var comparisons = 0L;
        var distance = -1;
        for (var d = 0; d <= maximumDistance; d++)
        {
            trace.Add((int[])vectors.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                int previousK;
                if (k == -d || (k != d && vectors[(k - 1) + offset] < vectors[(k + 1) + offset]))
                    previousK = k + 1;
                else
                    previousK = k - 1;

                // A down move arrives from diagonal k+1 unchanged; a right move
                // consumes one old line, so it starts one past diagonal k-1's x.
                var x = previousK == k + 1
                    ? vectors[previousK + offset]
                    : vectors[previousK + offset] + 1;
                var y = x - k;
                while (x < oldCount && y < newCount)
                {
                    if (++comparisons > MaximumLineComparisons) break;

                    if (!string.Equals(before[oldStart + x], after[newStart + y], StringComparison.Ordinal)) break;

                    x++;
                    y++;
                }

                vectors[k + offset] = x;
                if (x >= oldCount && y >= newCount)
                {
                    distance = d;
                    break;
                }
            }

            if (distance >= 0) break;
        }

        if (distance < 0)
        {
            AppendRange(script, oldStart, oldEnd, newStart, newEnd);
            return;
        }

        // Walk the trace backwards: each distance step is one delete (right) or
        // insert (down) plus the diagonal run that follows it; the remaining
        // diagonal at the front is the distance-zero baseline.
        var moves = new List<ScriptOp>(distance + oldCount + newCount);
        var x0 = oldCount;
        var y0 = newCount;
        for (var d = distance; d >= 1; d--)
        {
            var vector = trace[d];
            var k = x0 - y0;
            int previousK;
            if (k == -d || (k != d && vector[(k - 1) + offset] < vector[(k + 1) + offset]))
                previousK = k + 1;
            else
                previousK = k - 1;

            var previousX = vector[previousK + offset];
            var previousY = previousX - previousK;
            while (x0 > previousX && y0 > previousY)
            {
                x0--;
                y0--;
                moves.Add(new ScriptOp(OpKind.Equal, oldStart + x0, newStart + y0));
            }

            if (x0 == previousX)
            {
                y0--;
                moves.Add(new ScriptOp(OpKind.Insert, oldStart + x0, newStart + y0));
            }
            else
            {
                x0--;
                moves.Add(new ScriptOp(OpKind.Delete, oldStart + x0, newStart + y0));
            }
        }

        while (x0 > 0 && y0 > 0)
        {
            x0--;
            y0--;
            moves.Add(new ScriptOp(OpKind.Equal, oldStart + x0, newStart + y0));
        }

        moves.Reverse();
        script.AddRange(moves);
    }

    private static bool HasChanges(List<ScriptOp> script)
    {
        foreach (var op in script)
            if (op.Kind != OpKind.Equal)
                return true;

        return false;
    }

    /// <summary>A whole-final-line replacement for texts that differ only in
    /// their trailing newline (the line arrays align completely).</summary>
    private static List<ScriptOp> FinalLineReplacementScript(string[] before, string[] after)
    {
        var script = new List<ScriptOp>();
        var lastBefore = Math.Max(0, before.Length - 1);
        var lastAfter = Math.Max(0, after.Length - 1);
        var equalCount = Math.Max(lastBefore, lastAfter);
        for (var index = 0; index < equalCount; index++)
            script.Add(new ScriptOp(OpKind.Equal, Math.Min(index, lastBefore), Math.Min(index, lastAfter)));

        script.Add(new ScriptOp(OpKind.Delete, lastBefore, lastAfter));
        script.Add(new ScriptOp(OpKind.Insert, lastBefore, lastAfter));
        return script;
    }

    private static WorkspaceEditDiff Render(
        List<ScriptOp> script,
        string[] before,
        string[] after,
        bool beforeEndsNewline,
        bool afterEndsNewline,
        string displayPath,
        int maximumUtf8Bytes,
        int maximumLines)
    {
        var additions = 0;
        var deletions = 0;
        foreach (var op in script)
        {
            if (op.Kind == OpKind.Insert) additions++;
            else if (op.Kind == OpKind.Delete) deletions++;
        }

        var builder = new StringBuilder();
        builder.Append("--- a/").Append(displayPath).Append('\n');
        builder.Append("+++ b/").Append(displayPath).Append('\n');
        var renderedLines = 2;
        var renderedBytes = Encoding.UTF8.GetByteCount(displayPath) * 2 + 16;
        var truncated = false;

        var index = 0;
        while (index < script.Count && !truncated)
        {
            if (script[index].Kind == OpKind.Equal)
            {
                index++;
                continue;
            }

            FindHunk(script, index, out var hunkStart, out var hunkEnd, out var nextIndex);
            var oldCount = 0;
            var newCount = 0;
            for (var scan = hunkStart; scan < hunkEnd; scan++)
            {
                if (script[scan].Kind != OpKind.Insert) oldCount++;
                if (script[scan].Kind != OpKind.Delete) newCount++;
            }

            AppendHeader(builder, script[hunkStart].OldIndex, oldCount, script[hunkStart].NewIndex, newCount);
            renderedLines++;
            renderedBytes += 20;

            for (var scan = hunkStart; scan < hunkEnd; scan++)
            {
                var op = script[scan];
                switch (op.Kind)
                {
                    case OpKind.Equal:
                        AppendBodyLine(builder, ' ', before[op.OldIndex]);
                        renderedLines++;
                        renderedBytes += Encoding.UTF8.GetByteCount(before[op.OldIndex]) + 2;
                        if (op.OldIndex == before.Length - 1 && !beforeEndsNewline)
                        {
                            builder.Append(NoNewlineMarker).Append('\n');
                            renderedLines++;
                            renderedBytes += NoNewlineMarker.Length + 1;
                        }

                        break;
                    case OpKind.Delete:
                        AppendBodyLine(builder, '-', before[op.OldIndex]);
                        renderedLines++;
                        renderedBytes += Encoding.UTF8.GetByteCount(before[op.OldIndex]) + 2;
                        if (op.OldIndex == before.Length - 1 && !beforeEndsNewline)
                        {
                            builder.Append(NoNewlineMarker).Append('\n');
                            renderedLines++;
                            renderedBytes += NoNewlineMarker.Length + 1;
                        }

                        break;
                    case OpKind.Insert:
                        AppendBodyLine(builder, '+', after[op.NewIndex]);
                        renderedLines++;
                        renderedBytes += Encoding.UTF8.GetByteCount(after[op.NewIndex]) + 2;
                        if (op.NewIndex == after.Length - 1 && !afterEndsNewline)
                        {
                            builder.Append(NoNewlineMarker).Append('\n');
                            renderedLines++;
                            renderedBytes += NoNewlineMarker.Length + 1;
                        }

                        break;
                }

                if (renderedLines >= maximumLines || renderedBytes >= maximumUtf8Bytes)
                {
                    truncated = true;
                    break;
                }
            }

            index = nextIndex;
        }

        if (truncated) builder.Append("… diff output bound reached\n");

        return new WorkspaceEditDiff(builder.ToString(), additions, deletions, truncated);
    }

    /// <summary>Finds the hunk covering the changed run starting at
    /// <paramref name="start"/>: the run plus <see cref="ContextLines"/> context
    /// on both sides, merged with following runs whose separating context would
    /// overlap. Outputs the rendered range and the scan resume position.</summary>
    private static void FindHunk(
        List<ScriptOp> script,
        int start,
        out int hunkStart,
        out int hunkEnd,
        out int nextIndex)
    {
        var runEnd = start + 1;
        while (runEnd < script.Count && script[runEnd].Kind != OpKind.Equal)
            runEnd++;

        while (true)
        {
            var scan = runEnd;
            while (scan < script.Count && script[scan].Kind == OpKind.Equal)
                scan++;

            if (scan == script.Count || scan - runEnd > 2 * ContextLines) break;

            runEnd = scan;
            while (runEnd < script.Count && script[runEnd].Kind != OpKind.Equal)
                runEnd++;
        }

        hunkStart = Math.Max(0, start - ContextLines);
        hunkEnd = Math.Min(script.Count, runEnd + ContextLines);
        nextIndex = runEnd;
    }

    private static void AppendHeader(StringBuilder builder, int oldStart, int oldCount, int newStart, int newCount)
    {
        builder.Append("@@ -");
        builder.Append(oldCount == 0 ? oldStart : oldStart + 1);
        builder.Append(',');
        builder.Append(oldCount);
        builder.Append(" +");
        builder.Append(newCount == 0 ? newStart : newStart + 1);
        builder.Append(',');
        builder.Append(newCount);
        builder.Append(" @@\n");
    }

    private static void AppendBodyLine(StringBuilder builder, char marker, string line)
    {
        // Carriage returns are line-ending residue; keeping them inside the
        // body would corrupt the line-oriented display of CRLF files.
        if (line.Length > 0 && line[^1] == '\r') line = line[..^1];

        builder.Append(marker).Append(line).Append('\n');
    }
}
