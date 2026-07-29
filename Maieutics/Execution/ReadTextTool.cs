using System.Text;
using System.Text.Json;
using Maieutics.Agent;

namespace Maieutics.Execution;

internal sealed class ReadTextTool(WorkspacePathResolver paths) : IAgentTool
{
    private const int DefaultMaximumLines = 200;
    private const int MaximumLines = 1_000;
    private const int MaximumUtf8Bytes = 64 * 1_024;
    private const int MaximumLineUtf8Bytes = MaximumUtf8Bytes;
    private const int MaximumScanBytes = 8 * 1_024 * 1_024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public AgentToolDescriptor Descriptor { get; } = new(
        "read_text",
        "Reads a bounded range of lines from a UTF-8 text file in the workspace.",
        WorkspaceToolJson.ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "uri": { "type": "string", "description": "The workspace://local URI of a text file." },
                "startLine": { "type": "integer", "minimum": 1, "default": 1 },
                "maxLines": { "type": "integer", "minimum": 1, "maximum": 1000, "default": 200 }
              },
              "required": ["uri"],
              "additionalProperties": false
            }
            """));

    public async ValueTask<AgentToolOutcome> InvokeAsync(
        AgentToolContext context,
        AgentToolArguments arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        try
        {
            var pathSnapshot = paths.Capture();
            var input = arguments.Deserialize(WorkspaceToolJsonSerializerContext.Default.ReadTextArguments);
            if (string.IsNullOrWhiteSpace(input.Uri))
            {
                throw new WorkspaceToolException(
                    "workspace_invalid_arguments",
                    "uri is required.");
            }

            var startLine = input.StartLine ?? 1;
            var maxLines = input.MaxLines ?? DefaultMaximumLines;
            if (startLine < 1 || maxLines is < 1 or > MaximumLines)
            {
                throw new WorkspaceToolException(
                    "workspace_invalid_arguments",
                    $"startLine must be positive and maxLines must be between 1 and {MaximumLines}.");
            }

            var file = pathSnapshot.Resolve(input.Uri, allowRoot: false);
            if (file.IsDirectory)
            {
                throw new WorkspaceToolException(
                    "workspace_not_file",
                    "The workspace URI does not identify a regular file.");
            }

            if (!file.IsRegularFile)
            {
                throw new WorkspaceToolException(
                    "workspace_not_regular_file",
                    "Workspace text tools can read only regular files.");
            }

            var result = await ReadAsync(file, startLine, maxLines, cancellationToken);
            return WorkspaceToolJson.Success(result, WorkspaceToolJsonSerializerContext.Default.ReadTextResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DecoderFallbackException)
        {
            return new AgentToolFailure(
                "workspace_invalid_utf8",
                "The requested file is not valid UTF-8 text.");
        }
        catch (Exception exception) when (exception is WorkspaceToolException or JsonException or
                                              UnauthorizedAccessException or IOException)
        {
            return WorkspaceToolJson.Failure(exception);
        }
    }

    private static async Task<ReadTextResult> ReadAsync(
        WorkspacePath file,
        int requestedStartLine,
        int maximumLines,
        CancellationToken cancellationToken)
    {
        await using var stream = WorkspaceFileReader.OpenVerifiedRead(file.FullPath);
        var reader = new BoundedUtf8LineReader(stream);
        var text = new StringBuilder();
        var outputBytes = 0;
        var lineNumber = 0;
        var returnedLines = 0;
        int? actualStartLine = null;
        int? actualEndLine = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (returnedLines >= maximumLines)
            {
                var hasMore = await reader.HasMoreDataAsync(cancellationToken);
                return new ReadTextResult(
                    file.Uri,
                    actualStartLine,
                    actualEndLine,
                    text.ToString(),
                    hasMore,
                    hasMore ? actualEndLine + 1 : null);
            }

            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return new ReadTextResult(
                    file.Uri,
                    actualStartLine,
                    actualEndLine,
                    text.ToString(),
                    Truncated: false,
                    NextStartLine: null);
            }

            lineNumber++;
            if (lineNumber < requestedStartLine)
            {
                continue;
            }

            var separatorBytes = actualStartLine.HasValue ? 1 : 0;
            var lineBytes = StrictUtf8.GetByteCount(line);
            if (outputBytes + separatorBytes + lineBytes > MaximumUtf8Bytes)
            {
                return new ReadTextResult(
                    file.Uri,
                    actualStartLine,
                    actualEndLine,
                    text.ToString(),
                    Truncated: true,
                    NextStartLine: lineNumber);
            }

            if (separatorBytes != 0)
            {
                text.Append('\n');
                outputBytes++;
            }

            actualStartLine ??= lineNumber;
            actualEndLine = lineNumber;
            text.Append(line);
            outputBytes += lineBytes;
            returnedLines++;
        }
    }

    private sealed class BoundedUtf8LineReader(FileStream stream)
    {
        private readonly byte[] buffer = new byte[4_096];
        private readonly byte[] lineBuffer = new byte[MaximumLineUtf8Bytes + 3];
        private int bufferCount;
        private int bufferOffset;
        private bool firstLine = true;
        private int scannedBytes;

        internal async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var lineLength = 0;
            while (await EnsureBufferAsync(cancellationToken))
            {
                var value = buffer[bufferOffset++];
                if (value == 0)
                {
                    throw new WorkspaceToolException(
                        "workspace_binary_file",
                        "The requested file contains binary data.");
                }

                if (value == (byte)'\n')
                {
                    return DecodeLine(lineLength);
                }

                if (lineLength == lineBuffer.Length)
                {
                    throw LineTooLong();
                }

                lineBuffer[lineLength++] = value;
            }

            return lineLength == 0 ? null : DecodeLine(lineLength);
        }

        internal ValueTask<bool> HasMoreDataAsync(CancellationToken cancellationToken) =>
            EnsureBufferAsync(cancellationToken);

        private async ValueTask<bool> EnsureBufferAsync(CancellationToken cancellationToken)
        {
            if (bufferOffset < bufferCount)
            {
                return true;
            }

            var remaining = MaximumScanBytes - scannedBytes;
            var requested = Math.Min(buffer.Length, remaining + 1);
            bufferCount = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            bufferOffset = 0;
            scannedBytes += bufferCount;
            if (scannedBytes > MaximumScanBytes)
            {
                throw new WorkspaceToolException(
                    "workspace_file_too_large",
                    $"read_text scans at most {MaximumScanBytes} bytes per call.");
            }

            return bufferCount != 0;
        }

        private string DecodeLine(int length)
        {
            if (length > 0 && lineBuffer[length - 1] == (byte)'\r')
            {
                length--;
            }

            var line = StrictUtf8.GetString(lineBuffer, 0, length);
            if (firstLine)
            {
                firstLine = false;
                if (line.Length > 0 && line[0] == '\uFEFF')
                {
                    line = line[1..];
                }
            }

            if (StrictUtf8.GetByteCount(line) > MaximumLineUtf8Bytes)
            {
                throw LineTooLong();
            }

            if (line.Any(static character => char.IsControl(character) && character != '\t'))
            {
                throw new WorkspaceToolException(
                    "workspace_binary_file",
                    "The requested file contains binary control characters.");
            }

            return line;
        }

        private static WorkspaceToolException LineTooLong() => new(
            "workspace_line_too_long",
            $"A text line cannot exceed {MaximumLineUtf8Bytes} UTF-8 bytes.");
    }
}