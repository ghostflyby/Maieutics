using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;
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
    private readonly int maximumWriteBytes;
    private readonly int maximumEditedFileBytes;
    private readonly int maximumDiffBytes;
    private readonly int maximumDiffLines;

    internal WorkspaceEditFunctions(
        Workspace workspace,
        int maximumWriteBytes = DefaultMaximumWriteBytes,
        int maximumEditedFileBytes = DefaultMaximumEditedFileBytes,
        int maximumDiffBytes = DefaultMaximumDiffBytes,
        int maximumDiffLines = DefaultMaximumDiffLines)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
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
                (Func<string, string, CancellationToken, ValueTask<WriteTextResult>>)WriteTextAsync,
                "write_text",
                "Creates or overwrites one workspace UTF-8 text file and returns a bounded unified diff " +
                "of the change. Missing parent directories are created. Refuses symbolic links, " +
                "directories, and .git paths."),
            CreateFunction(
                (Func<string, string, string, bool?, CancellationToken, ValueTask<EditTextResult>>)EditTextAsync,
                "edit_text",
                "Replaces exact text inside one workspace UTF-8 text file and returns a bounded unified " +
                "diff of the change. The target text must appear exactly once unless replaceAll is true. " +
                "Refuses binary files, symbolic links, and .git paths.")
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

    [Description("Creates or overwrites one workspace UTF-8 text file.")]
    private async ValueTask<WriteTextResult> WriteTextAsync(
        [Description("The workspace://local URI of the file. Missing parent directories are created.")]
        string uri,
        [Description("The complete file content, written verbatim as UTF-8 without a byte order mark.")]
        string content,
        CancellationToken cancellationToken)
    {
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
                    var bounded = await snapshot.ReadAsync(target.FullPath, maximumEditedFileBytes, cancellationToken)
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

            if (!target.Exists) snapshot.EnsureParentDirectories(target.FullPath);
            await WriteFileAsync(snapshot, target.FullPath, bytes, create: !target.Exists, cancellationToken)
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
        [Description("The exact current text to replace, including indentation. Must appear exactly " +
                     "once unless replaceAll is true.")]
        string oldText,
        [Description("The replacement text.")]
        string newText,
        [Description("Replace every occurrence. Defaults to false.")]
        bool? replaceAll = null,
        CancellationToken cancellationToken = default)
    {
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
            await WriteFileAsync(snapshot, file.FullPath, bytes, create: false, cancellationToken)
                .ConfigureAwait(false);

            var diff = UnifiedDiff.Create(
                before,
                after,
                snapshot.ToDisplayPath(file.FullPath),
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
        string path,
        byte[] bytes,
        bool create,
        CancellationToken cancellationToken)
    {
        var stream = snapshot.OpenWrite(path, create);
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
