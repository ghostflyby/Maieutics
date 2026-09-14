using System.Buffers;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Maieutics.Providers.OpenAI;

/// <summary>Pipeline transport that gives the OpenAI .NET SDK's Responses
/// surface the apply_patch tool it cannot model natively: outgoing request
/// bodies gain the built-in `{"type":"apply_patch"}` tool entry and replayed
/// history keeps its original wire shapes; incoming `apply_patch_call` and
/// `custom_tool_call` items are folded into plain `function_call` items so the
/// SDK and the function orchestrator see a tool they understand.</summary>
internal sealed class ApplyPatchPipelineTransport : PipelineTransport
{
    private readonly HttpClientPipelineTransport inner;
    private readonly ApplyPatchCallRegistry registry = new();

    private ApplyPatchPipelineTransport(HttpClientPipelineTransport inner)
    {
        this.inner = inner;
    }

    public static ApplyPatchPipelineTransport CreateDefault()
    {
        return new ApplyPatchPipelineTransport(new HttpClientPipelineTransport());
    }

    protected override PipelineMessage CreateMessageCore()
    {
        return inner.CreateMessage();
    }

    protected override void ProcessCore(PipelineMessage message)
    {
        Prepare(message);
        RewriteRequest(message);
        inner.Process(message);
        RewriteResponse(message);
    }

    protected override async ValueTask ProcessCoreAsync(PipelineMessage message)
    {
        Prepare(message);
        await RewriteRequestAsync(message).ConfigureAwait(false);
        await inner.ProcessAsync(message).ConfigureAwait(false);
        await RewriteResponseAsync(message).ConfigureAwait(false);
    }

    /// <summary>The transport itself owns buffering for /responses JSON
    /// bodies: the rewritten stream replaces the original one, so the default
    /// pre-buffering must not capture the untranslated body.</summary>
    private static void Prepare(PipelineMessage message)
    {
        if (IsResponsesRequest(message)) message.BufferResponse = false;
    }

    private static bool IsResponsesRequest(PipelineMessage message)
    {
        var uri = message.Request.Uri;
        return message.Request.Method == "POST" &&
               uri is not null &&
               (uri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal) ||
                uri.AbsolutePath.EndsWith("/responses/", StringComparison.Ordinal));
    }

    private void RewriteRequest(PipelineMessage message)
    {
        if (!IsResponsesRequest(message)) return;
        if (message.Request.Content is not { } content) return;

        if (ReadJson(content) is not JsonObject body) return;

        // The parse and the rewrite share one instance, so the comparison must
        // be against a snapshot taken before the rewrite.
        var before = body.ToJsonString();
        ApplyPatchWire.RewriteRequest(body, registry);
        var after = body.ToJsonString();
        if (after != before)
            message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(Encoding.UTF8.GetBytes(after)));
    }

    private async ValueTask RewriteRequestAsync(PipelineMessage message)
    {
        if (!IsResponsesRequest(message)) return;
        if (message.Request.Content is not { } content) return;

        if (await ReadJsonAsync(content).ConfigureAwait(false) is not JsonObject body) return;

        var before = body.ToJsonString();
        ApplyPatchWire.RewriteRequest(body, registry);
        var after = body.ToJsonString();
        if (after != before)
            message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(Encoding.UTF8.GetBytes(after)));
    }

    private async ValueTask RewriteResponseAsync(PipelineMessage message)
    {
        if (!IsResponsesRequest(message) || message.Response is not { } response) return;

        var contentType = response.Headers.TryGetValue("Content-Type", out var value) ? value : null;
        if (contentType is not null &&
            contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            if (response.ContentStream is { } stream)
                response.ContentStream = new ApplyPatchSseStream(stream, registry);

            return;
        }

        await RewriteBufferedResponseAsync(response).ConfigureAwait(false);
    }

    private void RewriteResponse(PipelineMessage message)
    {
        if (!IsResponsesRequest(message) || message.Response is not { } response) return;

        var contentType = response.Headers.TryGetValue("Content-Type", out var value) ? value : null;
        if (contentType is not null &&
            contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            if (response.ContentStream is { } stream)
                response.ContentStream = new ApplyPatchSseStream(stream, registry);

            return;
        }

        RewriteBufferedResponse(response);
    }

    /// <summary>Buffered JSON bodies are read, translated, and put back as the
    /// response's replacement stream; the pipeline's own buffering is off for
    /// /responses requests, so nothing has captured the original body.</summary>
    private async ValueTask RewriteBufferedResponseAsync(PipelineResponse response)
    {
        if (response.ContentStream is not { } stream) return;

        string body;
        using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false))
            body = await reader.ReadToEndAsync().ConfigureAwait(false);

        if (TryRewriteBody(body, out var rewritten))
            response.ContentStream = new MemoryStream(Encoding.UTF8.GetBytes(rewritten), writable: false);
    }

    private void RewriteBufferedResponse(PipelineResponse response)
    {
        if (response.ContentStream is not { } stream) return;

        string body;
        using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false))
            body = reader.ReadToEndAsync().GetAwaiter().GetResult();

        if (TryRewriteBody(body, out var rewritten))
            response.ContentStream = new MemoryStream(Encoding.UTF8.GetBytes(rewritten), writable: false);
    }

    private bool TryRewriteBody(string body, out string rewritten)
    {
        rewritten = body;
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        if (json is null) return false;

        ApplyPatchWire.RewriteResponse(json, registry);
        var translated = json.ToJsonString();
        if (translated == body) return false;

        rewritten = translated;
        return true;
    }

    private static JsonNode? ReadJson(BinaryContent content)
    {
        using var stream = new MemoryStream();
        content.WriteTo(stream, CancellationToken.None);
        try
        {
            return JsonNode.Parse(stream.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async ValueTask<JsonNode?> ReadJsonAsync(BinaryContent content)
    {
        using var stream = new MemoryStream();
        await content.WriteToAsync(stream, CancellationToken.None).ConfigureAwait(false);
        stream.Position = 0;
        try
        {
            return await JsonNode.ParseAsync(stream, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>SSE stream that rewrites event payloads on the fly: apply_patch
/// call items become function_call items, and custom-tool input delta events
/// are dropped entirely. Events are processed as complete blocks (an event
/// ends at its blank line), which keeps suppression decisions atomic.</summary>
internal sealed class ApplyPatchSseStream : Stream
{
    private const int MaximumEventBytes = 4 * 1_024 * 1_024;
    private readonly Stream inner;
    private readonly ApplyPatchCallRegistry registry;
    private readonly MemoryStream pending = new();
    private byte[]? emitted;
    private int emittedOffset;
    private int scanned;
    private bool innerCompleted;

    public ApplyPatchSseStream(Stream inner, ApplyPatchCallRegistry registry)
    {
        this.inner = inner;
        this.registry = registry;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (emitted is not null && emittedOffset < emitted.Length)
            {
                var take = Math.Min(buffer.Length, emitted.Length - emittedOffset);
                emitted.AsSpan(emittedOffset, take).CopyTo(buffer.Span);
                emittedOffset += take;
                if (emittedOffset >= emitted.Length) emitted = null;

                return take;
            }

            var block = await ReadEventBlockAsync(cancellationToken).ConfigureAwait(false);
            emitted = block.Length == 0 ? block : ProcessBlock(block);
            emittedOffset = 0;
            if (emitted.Length == 0 && innerCompleted) return 0;
        }
    }

    /// <summary>Returns the next complete event block (lines plus the blank
    /// boundary), an empty array at clean end-of-stream, or the trailing
    /// unterminated block.</summary>
    private async ValueTask<byte[]> ReadEventBlockAsync(CancellationToken cancellationToken)
    {
        var block = new MemoryStream();
        while (true)
        {
            var buffer = pending.GetBuffer();
            var length = (int)pending.Length;
            var boundary = false;
            for (var index = scanned; index < length; index++)
            {
                if (buffer[index] != (byte)'\n') continue;

                var contentStart = scanned;
                var lineLength = index - scanned;
                var endsWithCarriageReturn = lineLength > 0 && buffer[contentStart + lineLength - 1] == (byte)'\r';
                var contentLength = endsWithCarriageReturn ? lineLength - 1 : lineLength;
                scanned = index + 1;
                if (contentLength == 0)
                {
                    // A blank line is the event boundary.
                    boundary = true;
                    break;
                }

                block.Write(buffer, contentStart, contentLength);
                block.WriteByte((byte)'\n');
            }

            if (boundary) break;

            if (innerCompleted)
            {
                if (pending.Length > scanned)
                {
                    // A truncated trailing line is forwarded untouched.
                    var tail = new byte[length - scanned];
                    Array.Copy(buffer, scanned, tail, 0, tail.Length);
                    DiscardPending();
                    return tail;
                }

                DiscardPending();
                return [];
            }

            var rented = ArrayPool<byte>.Shared.Rent(16 * 1_024);
            try
            {
                var read = await inner.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    innerCompleted = true;
                    continue;
                }

                if (pending.Length + read > MaximumEventBytes)
                    throw new IOException("The Responses event stream produced an oversized event block.");

                pending.Position = pending.Length;
                pending.Write(rented, 0, read);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        DiscardPending();
        return block.Length > 0 ? block.ToArray() : [];
    }

    /// <summary>Compacts the buffer, keeping only bytes after the scan
    /// position: the boundary-terminated event was consumed, but the bytes of
    /// following events in the same chunk must survive.</summary>
    private void DiscardPending()
    {
        var remaining = (int)pending.Length - scanned;
        if (remaining > 0)
        {
            var buffer = pending.GetBuffer();
            Array.Copy(buffer, scanned, buffer, 0, remaining);
        }

        pending.SetLength(remaining);
        scanned = 0;
    }

    private byte[] ProcessBlock(byte[] block)
    {
        // A block is "event: <name>\n" plus "data: <payload>\n" lines. The
        // payload decides suppression, so both lines are filtered together.
        string? dataPayload = null;
        var hasData = false;
        var lines = Encoding.UTF8.GetString(block).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                hasData = true;
                dataPayload = line[5..].TrimStart();
            }
        }

        if (!hasData) return block;

        JsonObject? payload;
        try
        {
            payload = dataPayload is { Length: > 0 } ? JsonNode.Parse(dataPayload) as JsonObject : null;
        }
        catch (JsonException)
        {
            return block;
        }

        if (payload is null) return block;

        var rewritten = ApplyPatchWire.RewriteStreamEvent(payload, registry);
        if (rewritten.Count == 0) return [];

        // Rebuild the block: the first rewritten event keeps every non-data
        // line (the "event:" name), and any synthesized events follow as
        // data-only blocks.
        var output = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            output.Append(line).Append('\n');
        }

        foreach (var evt in rewritten)
            output.Append("data: ").Append(evt.ToJsonString()).Append("\n\n");

        return Encoding.UTF8.GetBytes(output.ToString());
    }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
