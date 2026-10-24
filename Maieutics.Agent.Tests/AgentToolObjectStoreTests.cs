using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ScriptedChatClient = Maieutics.Testing.ScriptedChatClient;

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

        Assert.Single(store.Objects);
        var finished = events.OfType<AgentToolFinished>().Single();
        Assert.True(finished.Result.GetProperty("truncated").GetBoolean());
        var sha = finished.Result.GetProperty("object").GetProperty("sha256").GetString();
        Assert.NotNull(sha);
        Assert.True(store.Objects.ContainsKey(sha));
        Assert.True(store.Objects[sha].Count() > 512);
        Assert.Contains(new string('x', 4_096), Encoding.UTF8.GetString(store.Objects[sha]));
        Assert.Contains("truncated", finished.Result.GetProperty("value").GetString());
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

        await Assert.ThrowsAnyAsync<AgentToolLimitExceededException>(async () => { await run.Completion.WaitAsync(TestContext.Current.CancellationToken); });
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

        Assert.Empty(store.Objects);
        var finished = events.OfType<AgentToolFinished>().Single();
        Assert.Equal("small", finished.Result.GetProperty("value").GetString());
        Assert.False(finished.Result.TryGetProperty("truncated", out _));
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken testToken)
        => TestDeadlines.Create(testToken, TimeSpan.FromSeconds(25));

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
            lock (gate)
            {
                if (!Objects.TryGetValue(sha256, out var bytes))
                    throw new FileNotFoundException($"Object '{sha256}' is not present.", sha256);

                return new MemoryStream(bytes, writable: false);
            }
        }
    }
}
