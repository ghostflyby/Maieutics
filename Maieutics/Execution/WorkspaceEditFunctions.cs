using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;
using Maieutics.Permissions;
using Microsoft.Extensions.AI;

namespace Maieutics.Execution;

/// <summary>The structured workspace edit tools: create or overwrite a text
/// file, and replace exact text inside one. Both return the change as a bounded
/// unified diff so the edit stays structured tool output until a renderer
/// displays it, and both reuse the workspace read tools' safety envelope:
/// workspace://local URIs only, regular files only, no reparse points, no .git,
/// bounded sizes.</summary>
internal sealed class WorkspaceEditFunctions
{
    private const int DefaultMaximumWriteBytes = 2 * 1_024 * 1_024;
    private const int DefaultMaximumEditedFileBytes = 2 * 1_024 * 1_024;
    private const int DefaultMaximumDiffBytes = 16 * 1_024;
    private const int DefaultMaximumDiffLines = 400;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private static readonly JsonSerializerOptions SerializerOptions =
        new(WorkspaceEditJsonSerializerContext.Default.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    private readonly Workspace workspace;
    private readonly PermissionPolicyAcquirer? acquirer;
    private readonly int maximumWriteBytes;
    private readonly int maximumEditedFileBytes;
    private readonly int maximumDiffBytes;
    private readonly int maximumDiffLines;

    internal WorkspaceEditFunctions(
        Workspace workspace,
        PermissionPolicyAcquirer? acquirer = null,
        int maximumWriteBytes = DefaultMaximumWriteBytes,
        int maximumEditedFileBytes = DefaultMaximumEditedFileBytes,
        int maximumDiffBytes = DefaultMaximumDiffBytes,
        int maximumDiffLines = DefaultMaximumDiffLines)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.acquirer = acquirer;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWriteBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEditedFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDiffBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDiffLines);
        this.maximumWriteBytes = maximumWriteBytes;
        this.maximumEditedFileBytes = maximumEditedFileBytes;
        this.maximumDiffBytes = maximumDiffBytes;
        this.maximumDiffLines = maximumDiffLines;

        Functions =
        [
            CreateFunction(
                (Func<string, AIFunctionArguments, string, CancellationToken, ValueTask<WriteTextResult>>)WriteTextAsync,
                "write_text",
                "Creates or overwrites one workspace UTF-8 text file and returns a bounded unified diff " +
                "of the change. Missing parent directories are created. Refuses symbolic links, " +
                "directories, and .git paths."),
            CreateFunction(
                (Func<string, AIFunctionArguments, string, string, bool?, CancellationToken, ValueTask<EditTextResult>>)EditTextAsync,
                "edit_text",
                "Replaces exact text inside one workspace UTF-8 text file and returns a bounded unified " +
                "diff of the change. The target text must appear exactly once unless replaceAll is true. " +
                "Refuses binary files, symbolic links, and .git paths."),
            CreateFunction(
                (Func<string, AIFunctionArguments, CancellationToken, ValueTask<ApplyPatchResult>>)ApplyPatchAsync,
                "apply_patch",
                "Applies an OpenAI apply_patch V4A patch document to one or more workspace files: " +
                "'*** Add File:', '*** Update File:' (with @@ change sections), and '*** Delete File:'. " +
                "Operations run in order and the patch stops at the first failure. Paths are " +
                "workspace-relative and must not escape the workspace root.")
        ];
    }

    internal IReadOnlyList<AIFunction> Functions { get; }

    private static AIFunction CreateFunction(Delegate method, string name, string description)
    {
        return AIFunctionFactory.Create(
            method,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                SerializerOptions = SerializerOptions
            });
    }

    /// <summary>Gates model-initiated writes to instruction surfaces: an instruction path
    /// requires an explicit allow in the calling session's effective write policy — absence
    /// of a matching allow denies, and any matching deny denies (ADR 0032 decision 2). A
    /// host without the permission acquirer keeps the legacy containment-only behavior.</summary>
    private void GateInstructionSurface(string? relativePath, AIFunctionArguments arguments)
    {
        if (acquirer is null || relativePath is null || !InstructionSurface.IsInstructionSurface(relativePath))
        {
            return;
        }

        var sessionId = AgentToolContext.GetRequired(arguments).SessionId;
        var policy = acquirer.Acquire(PermissionLayer.Empty, sessionId);
        var rules = policy.For(PermissionKind.Write);
        var normalized = relativePath.Replace('\\', '/');
        if (rules.Deny.Any(pattern => PermissionMatching.MatchesPath(pattern, normalized)) ||
            !rules.Allow.Any(pattern => PermissionMatching.MatchesPath(pattern, normalized)))
        {
            throw new AgentToolException(
                "instructions_write_forbidden",
                $"'{normalized}' is an instruction surface: model-initiated writes require an " +
                "explicit allow in the calling session's permission policy.");
        }
    }

    [Description("Creates or overwrites one workspace UTF-8 text file.")]
    private async ValueTask<WriteTextResult> WriteTextAsync(
        [Description("The workspace://local URI of the file. Missing parent directories are created.")]
        string uri,
        AIFunctionArguments arguments,
        [Description("The complete file content, written verbatim as UTF-8 without a byte order mark.")]
        string content,
        CancellationToken cancellationToken)
    {
        GateInstructionSurface(WorkspaceRelativePath(uri), arguments);
        try
        {
            if (string.IsNullOrEmpty(uri))
                throw new WorkspaceException("workspace_invalid_arguments", "uri is required.");

            var bytes = EncodeUtf8(content);
            if (bytes.Length > maximumWriteBytes)
                throw new WorkspaceException(
                    "workspace_file_too_large",
                    $"write_text writes at most {maximumWriteBytes} UTF-8 bytes per file.");

            var snapshot = workspace.Capture();
            var target = snapshot.ResolveWriteTarget(uri);
            byte[]? beforeRaw = null;
            string? beforeText = null;
            long? beforeSize = null;
            if (target.Exists)
            {
                beforeSize = new FileInfo(target.FullPath).Length;
                if (beforeSize <= maximumEditedFileBytes)
                {
                    var bounded = await snapshot.ReadAsync(target.FullPath, target.Segments, maximumEditedFileBytes, cancellationToken)
                        .ConfigureAwait(false);
                    if (!bounded.ExceededLimit)
                    {
                        beforeRaw = bounded.Bytes;
                        beforeText = TryDecodeText(bounded.Bytes, out _);
                    }
                }
            }

            if (beforeRaw is not null && beforeRaw.AsSpan().SequenceEqual(bytes))
                return new WriteTextResult(target.Uri, "unchanged", beforeSize, bytes.Length, null);

            if (!target.Exists) WorkspaceSnapshot.EnsureParentDirectories(target);
            await WriteFileAsync(snapshot, target, bytes, create: !target.Exists, cancellationToken)
                .ConfigureAwait(false);

            // A previous file whose content could not be decoded (binary or
            // oversize) has no honest before side, so the diff is omitted
            // instead of pretending the file was empty.
            var diff = target.Exists && beforeText is null
                ? null
                : UnifiedDiff.Create(beforeText, content, target.DisplayPath, maximumDiffBytes, maximumDiffLines);
            return new WriteTextResult(
                target.Uri,
                target.Exists ? "updated" : "created",
                beforeSize,
                bytes.Length,
                diff);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw ToAgentToolException(exception);
        }
    }

    [Description("Replaces exact text inside one workspace UTF-8 text file.")]
    private async ValueTask<EditTextResult> EditTextAsync(
        [Description("The workspace://local URI of the file to edit.")]
        string uri,
        AIFunctionArguments arguments,
        [Description("The exact current text to replace, including indentation. Must appear exactly " +
                     "once unless replaceAll is true.")]
        string oldText,
        [Description("The replacement text.")]
        string newText,
        [Description("Replace every occurrence. Defaults to false.")]
        bool? replaceAll = null,
        CancellationToken cancellationToken = default)
    {
        GateInstructionSurface(WorkspaceRelativePath(uri), arguments);
        try
        {
            if (string.IsNullOrEmpty(uri))
                throw new WorkspaceException("workspace_invalid_arguments", "uri is required.");

            if (oldText.Length == 0)
                throw new WorkspaceException(
                    "workspace_invalid_arguments",
                    "oldText is required and cannot be empty.");

            if (string.Equals(oldText, newText, StringComparison.Ordinal))
                throw new WorkspaceException(
                    "workspace_invalid_arguments",
                    "newText must differ from oldText.");

            var snapshot = workspace.Capture();
            var file = snapshot.Resolve(uri, false);
            if (!file.IsRegularFile)
                throw new WorkspaceException(
                    "workspace_not_regular_file",
                    "Workspace edit tools can edit only regular files.");

            if (new FileInfo(file.FullPath).Length > maximumEditedFileBytes)
                throw new WorkspaceException(
                    "workspace_file_too_large",
                    $"edit_text edits files of at most {maximumEditedFileBytes} bytes.");

            var bounded = await snapshot.ReadAsync(
                file.FullPath,
                file.Segments,
                maximumEditedFileBytes,
                cancellationToken).ConfigureAwait(false);
            if (bounded.ExceededLimit)
                throw new WorkspaceException(
                    "workspace_file_too_large",
                    $"edit_text edits files of at most {maximumEditedFileBytes} bytes.");

            var before = TryDecodeText(bounded.Bytes, out var hadByteOrderMark) ??
                throw new WorkspaceException(
                    "workspace_binary_file",
                    "The file is not UTF-8 text, so edit_text cannot edit it.");

            var (search, replacement) = MatchLineEndings(before, oldText, newText);
            var occurrences = CountOccurrences(before, search);
            if (occurrences == 0)
                throw new WorkspaceException(
                    "workspace_edit_target_not_found",
                    "oldText does not appear in the file. Read the file again and copy the exact text, " +
                    "including indentation.");

            if (occurrences > 1 && replaceAll is not true)
                throw new WorkspaceException(
                    "workspace_edit_target_not_unique",
                    $"oldText appears {occurrences} times; pass replaceAll to replace every occurrence.");

            var after = before.Replace(search, replacement);
            var bytes = EncodeUtf8(hadByteOrderMark ? after.Insert(0, "\uFEFF") : after);
            await WriteFileAsync(snapshot, file.AsWriteTarget(), bytes, create: false, cancellationToken)
                .ConfigureAwait(false);

            var diff = UnifiedDiff.Create(
                before,
                after,
                WorkspaceSnapshot.ToDisplayPath(file.Segments),
                maximumDiffBytes,
                maximumDiffLines);
            return new EditTextResult(file.Uri, occurrences, bounded.Bytes.Length, bytes.Length, diff!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw ToAgentToolException(exception);
        }
    }

    [Description("Applies an OpenAI apply_patch V4A patch document to workspace files.")]
    private async ValueTask<ApplyPatchResult> ApplyPatchAsync(
        [Description("The complete patch text, including the '*** Begin Patch' and '*** End Patch' sentinels.")]
        string patch,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        GatePatchSurface(patch, arguments);
        try
        {
            if (string.IsNullOrWhiteSpace(patch))
                throw new WorkspaceException("workspace_invalid_arguments", "patch is required.");

            var document = ApplyPatchParser.Parse(patch);
            var snapshot = workspace.Capture();
            var files = ImmutableArray.CreateBuilder<ApplyPatchFileResult>(document.Files.Length);
            foreach (var change in document.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(await ApplyFileChangeAsync(snapshot, change, cancellationToken).ConfigureAwait(false));
            }

            return new ApplyPatchResult(files.ToImmutable());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw ToAgentToolException(exception);
        }
    }

    /// <summary>Applies one parsed file operation. Failures propagate as typed
    /// tool errors after earlier operations in the patch have been applied;
    /// like the reference harnesses, applied files are not rolled back.</summary>
    private async ValueTask<ApplyPatchFileResult> ApplyFileChangeAsync(
        WorkspaceSnapshot snapshot,
        ApplyPatchFileChange change,
        CancellationToken cancellationToken)
    {
        switch (change.Kind)
        {
            case "create_file":
            {
                var target = snapshot.ResolveWriteTarget(ToWorkspaceUri(change.Path));
                if (target.Exists)
                    throw new WorkspaceException(
                        "workspace_path_exists",
                        $"The patch creates '{change.Path}', but that file already exists.");

                WorkspaceSnapshot.EnsureParentDirectories(target);
                var bytes = EncodeUtf8(change.Diff ?? "");
                await WriteFileAsync(snapshot, target, bytes, create: true, cancellationToken)
                    .ConfigureAwait(false);
                return new ApplyPatchFileResult(
                    target.DisplayPath,
                    "created",
                    null,
                    UnifiedDiff.Create(null, change.Diff ?? "", target.DisplayPath, maximumDiffBytes, maximumDiffLines));
            }
            case "update_file":
            {
                if (change.Changes.Length == 0)
                    throw new WorkspaceException(
                        "workspace_patch_invalid",
                        $"The update for '{change.Path}' contains no change sections.");

                var file = snapshot.Resolve(ToWorkspaceUri(change.Path), allowRoot: false);
                if (!file.IsRegularFile)
                    throw new WorkspaceException(
                        "workspace_not_regular_file",
                        "Workspace edit tools can edit only regular files.");

                if (new FileInfo(file.FullPath).Length > maximumEditedFileBytes)
                    throw new WorkspaceException(
                        "workspace_file_too_large",
                        $"edit tools edit files of at most {maximumEditedFileBytes} bytes.");

                var bounded = await snapshot.ReadAsync(file.FullPath, file.Segments, maximumEditedFileBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (bounded.ExceededLimit)
                    throw new WorkspaceException(
                        "workspace_file_too_large",
                        $"edit tools edit files of at most {maximumEditedFileBytes} bytes.");

                var before = TryDecodeText(bounded.Bytes, out var hadByteOrderMark) ??
                    throw new WorkspaceException(
                        "workspace_binary_file",
                        $"The file '{change.Path}' is not UTF-8 text, so the patch cannot update it.");

                var after = ApplyChangeSections(before, change.Path, change.Changes);
                if (string.Equals(before, after, StringComparison.Ordinal))
                    return new ApplyPatchFileResult(WorkspaceSnapshot.ToDisplayPath(file.Segments), "unchanged", null, null);

                var bytes = EncodeUtf8(hadByteOrderMark ? after.Insert(0, "\uFEFF") : after);
                if (change.MoveToPath is { } moveTo)
                {
                    var moved = snapshot.ResolveWriteTarget(ToWorkspaceUri(moveTo));
                    if (moved.Exists)
                        throw new WorkspaceException(
                            "workspace_path_exists",
                            $"The patch moves '{change.Path}' to '{moveTo}', but that file already exists.");

                    WorkspaceSnapshot.EnsureParentDirectories(moved);
                    await WriteFileAsync(snapshot, moved, bytes, create: true, cancellationToken)
                        .ConfigureAwait(false);
                    snapshot.DeleteFile(file.AsWriteTarget());
                    return new ApplyPatchFileResult(
                        moved.DisplayPath,
                        "created",
                        WorkspaceSnapshot.ToDisplayPath(file.Segments),
                        UnifiedDiff.Create(before, after, moved.DisplayPath, maximumDiffBytes, maximumDiffLines));
                }

                await WriteFileAsync(snapshot, file.AsWriteTarget(), bytes, create: false, cancellationToken)
                    .ConfigureAwait(false);
                return new ApplyPatchFileResult(
                    WorkspaceSnapshot.ToDisplayPath(file.Segments),
                    "updated",
                    null,
                    UnifiedDiff.Create(before, after, WorkspaceSnapshot.ToDisplayPath(file.Segments), maximumDiffBytes, maximumDiffLines));
            }
            case "delete_file":
            {
                var file = snapshot.Resolve(ToWorkspaceUri(change.Path), allowRoot: false);
                if (!file.IsRegularFile)
                    throw new WorkspaceException(
                        "workspace_not_regular_file",
                        "Workspace edit tools can delete only regular files.");

                var beforeText = await TryReadBeforeAsync(snapshot, file, cancellationToken).ConfigureAwait(false);
                snapshot.DeleteFile(file.AsWriteTarget());
                return new ApplyPatchFileResult(
                    WorkspaceSnapshot.ToDisplayPath(file.Segments),
                    "deleted",
                    null,
                    UnifiedDiff.Create(beforeText, "", WorkspaceSnapshot.ToDisplayPath(file.Segments), maximumDiffBytes, maximumDiffLines));
            }
            default:
                throw new WorkspaceException(
                    "workspace_patch_invalid",
                    $"Unsupported patch operation '{change.Kind}'.");
        }
    }

    /// <summary>Locates and applies each change section against the current
    /// file lines. With an anchor, the pattern must start on the line after
    /// the anchor (or insert there when the section has no removed lines);
    /// without one, the pattern's first occurrence at or after the previous
    /// section's end is used. Line comparison ignores carriage returns.</summary>
    private static string ApplyChangeSections(
        string before,
        string displayPath,
        ImmutableArray<ApplyPatchChangeSection> sections)
    {
        var crlf = before.Contains('\r');
        var lines = SplitContentLines(before);
        var seek = 0;
        foreach (var section in sections)
        {
            int position;
            if (section.Anchor is { } anchor)
            {
                var anchorIndex = FindLine(lines, seek, anchor);
                if (anchorIndex < 0)
                    throw ContextNotFound(displayPath, anchor);

                position = anchorIndex + 1;
                if (section.OldLines.Length > 0 && !MatchAt(lines, position, section.OldLines))
                    throw ContextNotFound(displayPath, anchor);
            }
            else
            {
                position = FindPattern(lines, seek, section.OldLines);
                if (position < 0)
                    throw ContextNotFound(displayPath, section.OldLines.IsEmpty ? "@@" : section.OldLines[0]);
            }

            // Pure-insertion sections have no removed lines; anything else
            // replaces the matched pattern run.
            if (section.OldLines.Length > 0)
                lines.RemoveRange(position, section.OldLines.Length);

            lines.InsertRange(position, section.NewLines);
            seek = position + section.NewLines.Length;
        }

        var body = string.Join(crlf ? "\r\n" : "\n", lines);
        var trailing = endsWithNewline(before) ? (crlf ? "\r\n" : "\n") : "";
        return body + trailing;

        static bool endsWithNewline(string text)
        {
            return text.Length > 0 && (text.EndsWith('\n') || text.EndsWith('\r'));
        }
    }

    private static List<string> SplitContentLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                lines.Add(text[start..].TrimEnd('\r'));
                return lines;
            }

            lines.Add(text[start..newline].TrimEnd('\r'));
            start = newline + 1;
        }

        return lines;
    }

    private static int FindLine(List<string> lines, int start, string value)
    {
        for (var index = Math.Max(0, start); index < lines.Count; index++)
            if (string.Equals(lines[index], value, StringComparison.Ordinal))
                return index;

        return -1;
    }

    private static bool MatchAt(List<string> lines, int position, ImmutableArray<string> pattern)
    {
        if (position + pattern.Length > lines.Count) return false;

        for (var index = 0; index < pattern.Length; index++)
            if (!string.Equals(lines[position + index], pattern[index], StringComparison.Ordinal))
                return false;

        return true;
    }

    private static int FindPattern(List<string> lines, int start, ImmutableArray<string> pattern)
    {
        if (pattern.Length == 0) return Math.Max(0, Math.Min(start, lines.Count));

        for (var index = Math.Max(0, start); index + pattern.Length <= lines.Count; index++)
            if (MatchAt(lines, index, pattern))
                return index;

        return -1;
    }

    private static WorkspaceException ContextNotFound(string path, string hint)
    {
        return new WorkspaceException(
            "workspace_patch_context_not_found",
            $"Invalid context in '{path}': the section beginning '@@ {hint.Trim()}' does not match the file. " +
            "Read the file again and include more surrounding context lines.");
    }

    /// <summary>Converts a patch-relative POSIX path into a workspace URI.
    /// Absolute paths and escapes fail later with the workspace's typed
    /// URI errors.</summary>
    private void GatePatchSurface(string patch, AIFunctionArguments arguments)
    {
        var document = ApplyPatchParser.Parse(patch);
        foreach (var change in document.Files)
            GateInstructionSurface(ToWorkspaceUri(change.Path), arguments);
    }

    /// <summary>Extracts the workspace-relative path (forward slashes, no leading
    /// separator) from a workspace://local URI, or null when the URI names no local path.</summary>
    private static string? WorkspaceRelativePath(string uri)
    {
        const string localPrefix = "workspace://local/";
        return uri.StartsWith(localPrefix, StringComparison.Ordinal)
            ? uri[localPrefix.Length..]
            : null;
    }

    private static string ToWorkspaceUri(string relativePath)
    {
        var trimmed = relativePath.Trim().TrimStart('/');
        if (trimmed.Length == 0)
            throw new WorkspaceException(
                "workspace_patch_invalid",
                "Patch paths must name a file inside the workspace.");

        var builder = new StringBuilder("workspace://local");
        foreach (var segment in trimmed.Split('/'))
        {
            builder.Append('/').Append(Uri.EscapeDataString(segment));
        }

        return builder.ToString();
    }

    /// <summary>Encodes content strictly, surfacing unpaired surrogates as the
    /// typed invalid-UTF-8 failure.</summary>
    private static byte[] EncodeUtf8(string content)
    {
        try
        {
            return StrictUtf8.GetBytes(content);
        }
        catch (EncoderFallbackException exception)
        {
            throw new WorkspaceException(
                "workspace_invalid_utf8",
                "The content is not valid UTF-8 text.",
                exception);
        }
    }

    /// <summary>Reads an existing file as text for diff purposes, or null when
    /// it is binary or grew past the edit bound between the length check and
    /// the read (the caller then omits the diff).</summary>
    private async Task<string?> TryReadBeforeAsync(
        WorkspaceSnapshot snapshot,
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path.FullPath).Length > maximumEditedFileBytes) return null;

        var bounded = await snapshot.ReadAsync(
                path.FullPath,
                path.Segments,
                maximumEditedFileBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (bounded.ExceededLimit) return null;

        return TryDecodeText(bounded.Bytes, out _);
    }

    /// <summary>Decodes file bytes as editable text, or null when the file is
    /// binary (decoder failure or control characters beyond line structure).
    /// Byte order marks are stripped and reported so writes can preserve them
    /// while diffs stay on the visible text.</summary>
    private static string? TryDecodeText(byte[] bytes, out bool hadByteOrderMark)
    {
        hadByteOrderMark = false;
        string content;
        try
        {
            content = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        if (content.Length > 0 && content[0] == '\uFEFF')
        {
            hadByteOrderMark = true;
            content = content[1..];
        }

        if (content.Any(static character =>
                char.IsControl(character) && character is not '\t' and not '\n' and not '\r'))
            return null;

        return content;
    }

    /// <summary>read_text normalizes CRLF away, so models quote LF-only text
    /// even when the file uses CRLF. When the literal target is absent and the
    /// file contains carriage returns, retry the match with the file's line
    /// endings applied to both sides.</summary>
    private static (string Search, string Replacement) MatchLineEndings(string content, string oldText, string newText)
    {
        if (oldText.IndexOf('\n') < 0 || !content.Contains('\r')) return (oldText, newText);

        var crlfSearch = oldText.Replace("\r\n", "\n").Replace("\n", "\r\n");
        if (content.IndexOf(crlfSearch, StringComparison.Ordinal) < 0) return (oldText, newText);

        var crlfReplacement = newText.Replace("\r\n", "\n").Replace("\n", "\r\n");
        return (crlfSearch, crlfReplacement);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while (offset <= text.Length - value.Length)
        {
            var index = text.IndexOf(value, offset, StringComparison.Ordinal);
            if (index < 0) break;

            count++;
            offset = index + value.Length;
        }

        return count;
    }

    private static async Task WriteFileAsync(
        WorkspaceSnapshot snapshot,
        WorkspaceWriteTarget target,
        byte[] bytes,
        bool create,
        CancellationToken cancellationToken)
    {
        var stream = snapshot.OpenWrite(target, create);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRecoverable(Exception exception)
    {
        return exception is WorkspaceException or UnauthorizedAccessException or IOException;
    }

    private static AgentToolException ToAgentToolException(Exception exception)
    {
        return exception switch
        {
            WorkspaceException workspaceException =>
                new AgentToolException(workspaceException.Code, workspaceException.Message),
            FileNotFoundException or DirectoryNotFoundException => new AgentToolException(
                "workspace_path_not_found",
                "The workspace URI does not identify an existing path."),
            UnauthorizedAccessException => new AgentToolException(
                "workspace_access_denied",
                "The operating system denied access to the requested workspace path."),
            IOException => new AgentToolException(
                "workspace_io_error",
                "The workspace operation could not be completed because of an I/O error."),
            _ => throw new ArgumentOutOfRangeException(nameof(exception), exception, null)
        };
    }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(WriteTextResult))]
[JsonSerializable(typeof(EditTextResult))]
[JsonSerializable(typeof(ApplyPatchResult))]
internal sealed partial class WorkspaceEditJsonSerializerContext : JsonSerializerContext;

internal sealed record WriteTextResult(
    string Uri,
    string Operation,
    long? BeforeBytes,
    long AfterBytes,
    WorkspaceEditDiff? Diff);

internal sealed record EditTextResult(
    string Uri,
    int Replacements,
    long BeforeBytes,
    long AfterBytes,
    WorkspaceEditDiff Diff);

/// <summary>The ordered per-file outcomes of one apply_patch call.</summary>
internal sealed record ApplyPatchResult(
    ImmutableArray<ApplyPatchFileResult> Files);

/// <summary>One file's outcome: the workspace-relative display path, the
/// applied operation, and the bounded diff it produced.</summary>
internal sealed record ApplyPatchFileResult(
    string Path,
    string Operation,
    string? MovedFrom,
    WorkspaceEditDiff? Diff);
