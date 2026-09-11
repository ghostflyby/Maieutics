using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;

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

        content.Should().BeOfType<TextContent>().Which.Text.Should().Be("hello");
        store.Objects.Should().BeEmpty();
    }

    [Fact(Timeout = 30_000)]
    public async Task OversizedTextIsStoredAndReferenced()
    {
        var store = new InMemoryObjectStore();
        var text = new string('x', AgentContentTriage.DefaultInlineThresholdBytes + 1);

        var content = await AgentContentTriage.IngestAsync(
            store, new MemoryStream(Encoding.UTF8.GetBytes(text)), "text/plain", "notes.txt", cancellationToken: TestContext.Current.CancellationToken);

        var data = content.Should().BeOfType<DataContent>().Which;
        data.MediaType.Should().Be(AgentBlobContent.MediaType);
        AgentBlobContent.TryParse(content, out var descriptor).Should().BeTrue();
        descriptor.Sha256.Should().HaveLength(64);
        descriptor.Size.Should().Be(text.Length);
        descriptor.MediaType.Should().Be("text/plain");
        descriptor.Name.Should().Be("notes.txt");
        store.Objects.Should().ContainKey(descriptor.Sha256);
        Encoding.UTF8.GetString(store.Objects[descriptor.Sha256]).Should().Be(text);
    }

    [Fact(Timeout = 30_000)]
    public async Task ContentExactlyAtTheThresholdStaysInline()
    {
        var store = new InMemoryObjectStore();
        var text = new string('x', AgentContentTriage.DefaultInlineThresholdBytes);

        var content = await AgentContentTriage.IngestAsync(
            store, new MemoryStream(Encoding.UTF8.GetBytes(text)), "text/plain", cancellationToken: TestContext.Current.CancellationToken);

        content.Should().BeOfType<TextContent>().Which.Text.Should().Be(text);
        store.Objects.Should().BeEmpty();
    }

    [Fact(Timeout = 30_000)]
    public async Task BinaryContentIsNeverInlinedEvenUnderTheThreshold()
    {
        var store = new InMemoryObjectStore();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };

        var content = await AgentContentTriage.IngestAsync(
            store, new MemoryStream(png), "image/png", "tiny.png", inlineThresholdBytes: 1024, cancellationToken: TestContext.Current.CancellationToken);

        AgentBlobContent.TryParse(content, out var descriptor).Should().BeTrue();
        descriptor.MediaType.Should().Be("image/png");
        store.Objects.Should().ContainKey(descriptor.Sha256);
    }

    [Fact(Timeout = 30_000)]
    public async Task TextualMediaTypeDoesNotSaveInvalidUtf8FromTheStore()
    {
        var store = new InMemoryObjectStore();
        var bytes = new byte[] { 0xFF, 0xFE, 0x00, 0xC8 };

        var content = await AgentContentTriage.IngestAsync(
            store, new MemoryStream(bytes), "text/plain", inlineThresholdBytes: 1024, cancellationToken: TestContext.Current.CancellationToken);

        AgentBlobContent.TryParse(content, out var descriptor).Should().BeTrue();
        store.Objects.Should().ContainKey(descriptor.Sha256);
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
        store.CapturedStreamWasSeekable.Should().BeFalse();
        store.CapturedBytes.Should().Equal(bytes);
        AgentBlobContent.TryParse(content, out var descriptor).Should().BeTrue();
        descriptor.Size.Should().Be(bytes.Length);
        descriptor.Sha256.Should()
            .Be(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        source.TotalBytesRead.Should().Be(bytes.Length);
    }

    [Fact(Timeout = 30_000)]
    public async Task IngestHonorsCancellationBeforeReading()
    {
        var store = new CapturingObjectStore();
        await using var source = new NonSeekableStream(new byte[10_000]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await FluentActions.Awaiting(async () => await AgentContentTriage.IngestAsync(
                store, source, "text/plain", cancellationToken: cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        store.CapturedBytes.Should().BeEmpty();
        source.TotalBytesRead.Should().Be(0);
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
        snapshot.Turns.Should().HaveCount(1);
        var inputMessage = snapshot.Turns[0].Messages[0];
        inputMessage.Contents.Should().HaveCount(2);
        AgentBlobContent.TryParse(inputMessage.Contents[1], out var descriptor).Should().BeTrue();
        descriptor.Sha256.Should().HaveLength(64);
    }

    [Fact]
    public void CodecRejectsMalformedBlobReferences()
    {
        var malformed = new DataContent(Encoding.UTF8.GetBytes("{\"sha256\":\"nope\"}"), AgentBlobContent.MediaType);
        var message = new ChatMessage(ChatRole.User, [malformed]);

        var detach = () => AgentTranscriptCodec.DetachPrivateMessages([message]);

        detach.Should().Throw<AgentUnsupportedResponseException>();
    }

    [Fact(Timeout = 30_000)]
    public async Task DetachedCopiesRoundTripTheDescriptor()
    {
        var store = new InMemoryObjectStore();
        var reference = await AgentContentTriage.IngestAsync(
            store, new MemoryStream(Encoding.UTF8.GetBytes(new string('z', 70_000))), "application/json", cancellationToken: TestContext.Current.CancellationToken);
        var message = new ChatMessage(ChatRole.User, [reference]);

        var detached = AgentTranscriptCodec.DetachPrivateMessages([message]);

        AgentBlobContent.TryParse(detached[0].Contents.Single(), out var descriptor).Should().BeTrue();
        descriptor.MediaType.Should().Be("application/json");
    }

    [Fact(Timeout = 30_000)]
    public async Task CollectObjectReferencesFindsBlobContentAndTruncatedEnvelopes()
    {
        var store = new InMemoryObjectStore();
        var reference = await AgentContentTriage.IngestAsync(
            store, new MemoryStream(Encoding.UTF8.GetBytes(new string('y', 70_000))), "text/plain", cancellationToken: TestContext.Current.CancellationToken);
        AgentBlobContent.TryParse(reference, out var descriptor).Should().BeTrue();

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

        references.Should().BeEquivalentTo([descriptor.Sha256, envelopeSha]);
    }

    private static async Task<List<AgentEvent>> ReadEventsAsync(
        IAgentRun run,
        CancellationToken cancellationToken)
    {
        var events = new List<AgentEvent>();
        await foreach (var agentEvent in run.Events.WithCancellation(cancellationToken))
            events.Add(agentEvent);

        return events;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(params string[] deltas)
    {
        foreach (var delta in deltas)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, delta);
        }
    }

    private sealed class ScriptedChatClient(
        params Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>[] responses)
        : IChatClient
    {
        private readonly Queue<Func<IReadOnlyList<ChatMessage>, CancellationToken,
            IAsyncEnumerable<ChatResponseUpdate>>> responses = new(responses);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return responses.Dequeue()(messages.ToArray(), cancellationToken);
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<ChatResponse>(new NotSupportedException());
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
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
