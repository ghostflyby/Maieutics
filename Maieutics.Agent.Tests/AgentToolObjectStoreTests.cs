using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent.Tests;

/// <summary>Tool results exceeding the inline envelope limit are stored whole in the object
/// store and replaced by a truncated preview envelope; without an object store the original
/// typed limit failure applies.</summary>
public sealed class AgentToolObjectStoreTests
{
    [Fact(Timeout = 30_000)]
    public async Task OversizedSuccessResultIsStoredWholeAndReplacedByATruncatedEnvelope()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var store = new InMemoryObjectStore();
        var tool = CreateTool("big", static () =>
            JsonSerializer.SerializeToElement(new string('x', 4_096)));
        var session = new AgentSession(
            new ScriptedChatClient(
                (_, _) => StreamAsync(ToolCallUpdate("call-1", "big")),
                (_, _) => StreamAsync(Text("The oversized result was stored."))),
            options: new AgentSessionOptions { MaxToolResultBytes = 512 },
            tools: [tool],
            objectStore: store);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("call big"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        store.Objects.Should().HaveCount(1);
        var finished = events.OfType<AgentToolFinished>().Single();
        finished.Result.GetProperty("truncated").GetBoolean().Should().BeTrue();
        var sha = finished.Result.GetProperty("object").GetProperty("sha256").GetString()!;
        store.Objects.Should().ContainKey(sha);
        store.Objects[sha].Should().HaveCountGreaterThan(512);
        Encoding.UTF8.GetString(store.Objects[sha]).Should().Contain(new string('x', 4_096));
        finished.Result.GetProperty("value").GetString().Should().Contain("truncated");
    }

    [Fact(Timeout = 30_000)]
    public async Task OversizedResultWithoutObjectStoreFailsWithTheTypedLimit()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var tool = CreateTool("big", static () =>
            JsonSerializer.SerializeToElement(new string('x', 4_096)));
        var session = new AgentSession(
            new ScriptedChatClient((_, _) => StreamAsync(ToolCallUpdate("call-1", "big"))),
            options: new AgentSessionOptions { MaxToolResultBytes = 512 },
            tools: [tool]);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("call big"), deadline.Token);
        await ReadEventsAsync(run, deadline.Token);

        await run.Completion.Awaiting(static completion => completion.WaitAsync(TestContext.Current.CancellationToken))
            .Should().ThrowAsync<AgentToolLimitExceededException>();
    }

    [Fact(Timeout = 30_000)]
    public async Task SmallResultsStayInline()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var store = new InMemoryObjectStore();
        var tool = CreateTool("echo", static () => JsonSerializer.SerializeToElement("small"));
        var session = new AgentSession(
            new ScriptedChatClient(
                (_, _) => StreamAsync(ToolCallUpdate("call-1", "echo")),
                (_, _) => StreamAsync(Text("Done."))),
            options: new AgentSessionOptions { MaxToolResultBytes = 512 },
            tools: [tool],
            objectStore: store);

        await using var run = await session.StartTurnAsync(AgentTurn.FromText("call echo"), deadline.Token);
        var events = await ReadEventsAsync(run, deadline.Token);
        await run.Completion.WaitAsync(deadline.Token);

        store.Objects.Should().BeEmpty();
        var finished = events.OfType<AgentToolFinished>().Single();
        finished.Result.GetProperty("value").GetString().Should().Be("small");
        finished.Result.TryGetProperty("truncated", out _).Should().BeFalse();
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken testToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        return deadline;
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

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(params ChatResponseUpdate[] updates)
    {
        foreach (var update in updates)
        {
            await Task.Yield();
            yield return update;
        }
    }

    private static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text);

    private static ChatResponseUpdate ToolCallUpdate(string callId, string name)
    {
        return new ChatResponseUpdate(
            ChatRole.Assistant,
            [new FunctionCallContent(callId, name, new Dictionary<string, object?>())]);
    }

    private static AIFunction CreateTool(string name, Func<JsonElement?> invoke)
    {
        return AIFunctionFactory.Create(
            (CancellationToken cancellationToken) =>
            {
                _ = cancellationToken;
                return new ValueTask<JsonElement?>(invoke());
            },
            new AIFunctionFactoryOptions { Name = name });
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
    }
}
