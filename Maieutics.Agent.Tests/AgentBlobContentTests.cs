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
    [Fact]
    public void SmallTextualContentStaysInline()
    {
        var store = new InMemoryObjectStore();

        var content = AgentContentTriage.Ingest(
            store, new MemoryStream(Encoding.UTF8.GetBytes("hello")), "text/plain");

        content.Should().BeOfType<TextContent>().Which.Text.Should().Be("hello");
        store.Objects.Should().BeEmpty();
    }

    [Fact]
    public void OversizedTextIsStoredAndReferenced()
    {
        var store = new InMemoryObjectStore();
        var text = new string('x', AgentContentTriage.DefaultInlineThresholdBytes + 1);

        var content = AgentContentTriage.Ingest(
            store, new MemoryStream(Encoding.UTF8.GetBytes(text)), "text/plain", "notes.txt");

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

    [Fact]
    public void BinaryContentIsNeverInlinedEvenUnderTheThreshold()
    {
        var store = new InMemoryObjectStore();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };

        var content = AgentContentTriage.Ingest(
            store, new MemoryStream(png), "image/png", "tiny.png", inlineThresholdBytes: 1024);

        AgentBlobContent.TryParse(content, out var descriptor).Should().BeTrue();
        descriptor.MediaType.Should().Be("image/png");
        store.Objects.Should().ContainKey(descriptor.Sha256);
    }

    [Fact]
    public void TextualMediaTypeDoesNotSaveInvalidUtf8FromTheStore()
    {
        var store = new InMemoryObjectStore();
        var bytes = new byte[] { 0xFF, 0xFE, 0x00, 0xC8 };

        var content = AgentContentTriage.Ingest(
            store, new MemoryStream(bytes), "text/plain", inlineThresholdBytes: 1024);

        AgentBlobContent.TryParse(content, out var descriptor).Should().BeTrue();
        store.Objects.Should().ContainKey(descriptor.Sha256);
    }

    [Fact]
    public async Task TurnsAcceptBlobReferencesAndSnapshotsPreserveThem()
    {
        var store = new InMemoryObjectStore();
        var reference = AgentContentTriage.Ingest(
            store, new MemoryStream(Encoding.UTF8.GetBytes(new string('y', 70_000))), "text/plain");
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

    [Fact]
    public void DetachedCopiesRoundTripTheDescriptor()
    {
        var store = new InMemoryObjectStore();
        var reference = AgentContentTriage.Ingest(
            store, new MemoryStream(Encoding.UTF8.GetBytes(new string('z', 70_000))), "application/json");
        var message = new ChatMessage(ChatRole.User, [reference]);

        var detached = AgentTranscriptCodec.DetachPrivateMessages([message]);

        AgentBlobContent.TryParse(detached[0].Contents.Single(), out var descriptor).Should().BeTrue();
        descriptor.MediaType.Should().Be("application/json");
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
}
