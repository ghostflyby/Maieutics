using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ScriptedChatClient = Maieutics.Testing.ScriptedChatClient;

namespace Maieutics.Agent.Tests;

/// <summary>The content triage point: small UTF-8 text stays inline; binary and oversized
/// content become blob references that the transcript codec accepts and preserves.</summary>
public sealed class AgentBlobContentTests
{
    [Fact(Timeout = 30_000)]
    public async Task SmallTextualContentStaysInline()
    {
        var store = new InMemoryObjectStore();

        var content = await AgentContentTriage.IngestAsync(
            store, new MemoryStream(Encoding.UTF8.GetBytes("hello")), "text/plain", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("hello", Assert.IsType<TextContent>(content).Text);
        Assert.Empty(store.Objects);
    }

    [Fact(Timeout = 30_000)]
    public async Task OversizedTextIsStoredAndReferenced()
    {
        var store = new InMemoryObjectStore();
        var text = new string('x', AgentContentTriage.DefaultInlineThresholdBytes + 1);

        var content = await AgentContentTriage.IngestAsync(
            store, new MemoryStream(Encoding.UTF8.GetBytes(text)), "text/plain", "notes.txt", cancellationToken: TestContext.Current.CancellationToken);

        var data = Assert.IsType<DataContent>(content);
        Assert.Equal(AgentBlobContent.MediaType, data.MediaType);
        Assert.True(AgentBlobContent.TryParse(content, out var descriptor));
        Assert.Equal(64, descriptor.Sha256.Length);
        Assert.Equal(text.Length, descriptor.Size);
        Assert.Equal("text/plain", descriptor.MediaType);
        Assert.Equal("notes.txt", descriptor.Name);
        Assert.True(store.Objects.ContainsKey(descriptor.Sha256));
        Assert.Equal(text, Encoding.UTF8.GetString(store.Objects[descriptor.Sha256]));
    }

    [Fact(Timeout = 30_000)]
    public async Task ContentExactlyAtTheThresholdStaysInline()
    {
        var store = new InMemoryObjectStore();
        var text = new string('x', AgentContentTriage.DefaultInlineThresholdBytes);

        var content = await AgentContentTriage.IngestAsync(
            store, new MemoryStream(Encoding.UTF8.GetBytes(text)), "text/plain", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(text, Assert.IsType<TextContent>(content).Text);
        Assert.Empty(store.Objects);
    }

    [Fact(Timeout = 30_000)]
    public async Task BinaryContentIsNeverInlinedEvenUnderTheThreshold()
    {
        var store = new InMemoryObjectStore();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };

        var content = await AgentContentTriage.IngestAsync(
            store, new MemoryStream(png), "image/png", "tiny.png", inlineThresholdBytes: 1024, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(AgentBlobContent.TryParse(content, out var descriptor));
        Assert.Equal("image/png", descriptor.MediaType);
        Assert.True(store.Objects.ContainsKey(descriptor.Sha256));
    }

    [Fact(Timeout = 30_000)]
    public async Task TextualMediaTypeDoesNotSaveInvalidUtf8FromTheStore()
    {
        var store = new InMemoryObjectStore();
        var bytes = new byte[] { 0xFF, 0xFE, 0x00, 0xC8 };

        var content = await AgentContentTriage.IngestAsync(
            store, new MemoryStream(bytes), "text/plain", inlineThresholdBytes: 1024, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(AgentBlobContent.TryParse(content, out var descriptor));
        Assert.True(store.Objects.ContainsKey(descriptor.Sha256));
    }

    [Fact(Timeout = 30_000)]
    public async Task OversizedNonSeekableStreamIsStoredWithoutMaterializingItWhole()
    {
        var store = new CapturingObjectStore();
        var bytes = Encoding.UTF8.GetBytes(new string('x', 3 * AgentContentTriage.DefaultInlineThresholdBytes));
        await using var source = new NonSeekableStream(bytes);

        var content = await AgentContentTriage.IngestAsync(
            store, source, "text/plain", inlineThresholdBytes: 4096, cancellationToken: TestContext.Current.CancellationToken);

        // The store received the content as a live non-seekable stream (a MemoryStream buffer
        // would be seekable), it observed the whole payload exactly once in order, and the
        // blob reference addresses the identical bytes.
        Assert.False(store.CapturedStreamWasSeekable);
        Assert.Equal(bytes, store.CapturedBytes);
        Assert.True(AgentBlobContent.TryParse(content, out var descriptor));
        Assert.Equal(bytes.Length, descriptor.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), descriptor.Sha256);
        Assert.Equal(bytes.Length, source.TotalBytesRead);
    }

    [Fact(Timeout = 30_000)]
    public async Task IngestHonorsCancellationBeforeReading()
    {
        var store = new CapturingObjectStore();
        await using var source = new NonSeekableStream(new byte[10_000]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await AgentContentTriage.IngestAsync(
                store, source, "text/plain", cancellationToken: cancellation.Token);
        });

        Assert.Empty(store.CapturedBytes);
        Assert.Equal(0, source.TotalBytesRead);
    }

    [Fact(Timeout = 30_000)]
    public async Task TurnsAcceptBlobReferencesAndSnapshotsPreserveThem()
    {
        var store = new InMemoryObjectStore();
        var reference = await AgentContentTriage.IngestAsync(
            store, new MemoryStream(Encoding.UTF8.GetBytes(new string('y', 70_000))), "text/plain", cancellationToken: TestContext.Current.CancellationToken);
        var client = new ScriptedChatClient((_, _) => StreamAsync("Acknowledged."));
        var session = new AgentSession(
            client,
            options: new AgentSessionOptions { MaxInputCharacters = 200_000 });

        await using var run = await session.StartTurnAsync(
            new AgentTurn([new TextContent("read this"), reference]),
            TestContext.Current.CancellationToken);
        await ReadEventsAsync(run, TestContext.Current.CancellationToken);
        await run.Completion.WaitAsync(TestContext.Current.CancellationToken);

        var snapshot = session.GetTranscriptSnapshot();
        Assert.Single(snapshot.Turns);
        var inputMessage = snapshot.Turns[0].Messages[0];
        Assert.Equal(2, inputMessage.Contents.Count());
        Assert.True(AgentBlobContent.TryParse(inputMessage.Contents[1], out var descriptor));
        Assert.Equal(64, descriptor.Sha256.Length);
    }

    [Fact]
    public void CodecRejectsMalformedBlobReferences()
    {
        var malformed = new DataContent(Encoding.UTF8.GetBytes("{\"sha256\":\"nope\"}"), AgentBlobContent.MediaType);
        var message = new ChatMessage(ChatRole.User, [malformed]);

        var detach = () => AgentTranscriptCodec.DetachPrivateMessages([message]);

        Assert.ThrowsAny<AgentUnsupportedResponseException>(() => detach.Invoke());
    }

    [Fact(Timeout = 30_000)]
    public async Task DetachedCopiesRoundTripTheDescriptor()
    {
        var store = new InMemoryObjectStore();
        var reference = await AgentContentTriage.IngestAsync(
            store, new MemoryStream(Encoding.UTF8.GetBytes(new string('z', 70_000))), "application/json", cancellationToken: TestContext.Current.CancellationToken);
        var message = new ChatMessage(ChatRole.User, [reference]);

        var detached = AgentTranscriptCodec.DetachPrivateMessages([message]);

        Assert.True(AgentBlobContent.TryParse(detached[0].Contents.Single(), out var descriptor));
        Assert.Equal("application/json", descriptor.MediaType);
    }

    [Fact(Timeout = 30_000)]
    public async Task CollectObjectReferencesFindsBlobContentAndTruncatedEnvelopes()
    {
        var store = new InMemoryObjectStore();
        var reference = await AgentContentTriage.IngestAsync(
            store, new MemoryStream(Encoding.UTF8.GetBytes(new string('y', 70_000))), "text/plain", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(AgentBlobContent.TryParse(reference, out var descriptor));

        var envelopeSha = new string('b', 64);
        var truncated = JsonSerializer.SerializeToElement(new
        {
            status = "ok",
            value = "<truncated>",
            truncated = true,
            @object = new { sha256 = envelopeSha, size = 9_000, mediaType = "application/json" }
        });
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, [new TextContent("question"), reference]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", truncated)]),
        };

        var references = AgentTranscriptCodec.CollectObjectReferences(messages);

        Assert.Equivalent(new[] { descriptor.Sha256, envelopeSha }, references);
    }

    private static async Task ReadEventsAsync(
        IAgentRun run,
        CancellationToken cancellationToken)
    {
        await foreach (var _ in run.Events.WithCancellation(cancellationToken))
        {
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(params string[] deltas)
    {
        foreach (var delta in deltas)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, delta);
        }
    }


    private sealed class InMemoryObjectStore : IAgentObjectStore
    {
        private readonly Lock gate = new();

        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public AgentObjectDescriptor Ingest(Stream content)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            lock (gate) Objects[sha256] = bytes;
            return new AgentObjectDescriptor(sha256, bytes.Length);
        }

        public Stream Open(string sha256)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Records the stream triage handed over without consuming it eagerly, so tests can
    /// observe whether the content arrived materialized (seekable) or streamed.</summary>
    private sealed class CapturingObjectStore : IAgentObjectStore
    {
        public byte[] CapturedBytes { get; private set; } = [];

        public bool CapturedStreamWasSeekable { get; private set; }

        public AgentObjectDescriptor Ingest(Stream content)
        {
            CapturedStreamWasSeekable = content.CanSeek;
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            CapturedBytes = buffer.ToArray();
            var sha256 = Convert.ToHexString(SHA256.HashData(CapturedBytes)).ToLowerInvariant();
            return new AgentObjectDescriptor(sha256, CapturedBytes.Length);
        }

        public Stream Open(string sha256)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>A forward-only single-pass stream: the shape of network or pipe content the
    /// triage point must handle without buffering it whole.</summary>
    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private int position;

        public int TotalBytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, bytes.Length - position);
            bytes.AsSpan(position, read).CopyTo(buffer.Span);
            position += read;
            TotalBytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, bytes.Length - position);
            Array.Copy(bytes, position, buffer, offset, read);
            position += read;
            TotalBytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
