using System.Text.Json;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Frontend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

public sealed class FrontendRunStreamTests
{
    [Fact(Timeout = 30_000)]
    public async Task InputRequestPresentationIsFlattenedOntoTheWireFrame()
    {
        var stream = FrontendRunStream.Create(
            AgentSessionId.Create(),
            new NeverCompletingRun(),
            presentationRouter: null,
            NullLogger<FrontendRunStream>.Instance);

        stream.PublishPresentation(
            "input.request",
            displayId: null,
            JsonSerializer.SerializeToElement(
                new FrontendInputRequest("input-abc-1", "Name:", true),
                FrontendJsonContext.Default.FrontendInputRequest),
            TestContext.Current.CancellationToken);

        var (frames, channel) = stream.Subscribe(sinceSequence: 0);
        var frame = frames.Single(entry => entry.Type == "input.request");
        frame.RequestId.Should().Be("input-abc-1");
        frame.Prompt.Should().Be("Name:");
        frame.Password.Should().BeTrue();
        frame.Data.Should().BeNull();
        stream.Unsubscribe(channel);
        await Task.CompletedTask;
    }

    [Fact(Timeout = 30_000)]
    public async Task OtherPresentationFramesKeepTheDisplayIdAndBundleData()
    {
        var stream = FrontendRunStream.Create(
            AgentSessionId.Create(),
            new NeverCompletingRun(),
            presentationRouter: null,
            NullLogger<FrontendRunStream>.Instance);
        var bundle = JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonElement>
            {
                ["text/plain"] = JsonSerializer.SerializeToElement("hi")
            });

        stream.PublishPresentation(
            "repl.display",
            "display-1",
            bundle,
            TestContext.Current.CancellationToken);

        var (frames, channel) = stream.Subscribe(sinceSequence: 0);
        var frame = frames.Single(entry => entry.Type == "repl.display");
        frame.DisplayId.Should().Be("display-1");
        frame.Data.Should().NotBeNull();
        frame.Data!.Value.GetProperty("text/plain").GetString().Should().Be("hi");
        stream.Unsubscribe(channel);
        await Task.CompletedTask;
    }

    /// <summary>A run whose pump is never started; presentation publishing only needs
    /// the identity and the replay buffer.</summary>
    private sealed class NeverCompletingRun : IAgentRun
    {
        public AgentRunId Id { get; } = AgentRunId.Create();

        public AgentSessionId SessionId { get; } = AgentSessionId.Create();

        public IAsyncEnumerable<AgentEvent> Events => AsyncEnumerableEmpty();

        // Never completed: the fake never starts a pump, so nobody observes it.
        public Task<AgentRunResult> Completion { get; } = new TaskCompletionSource<AgentRunResult>().Task;

        public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<AgentEvent> AsyncEnumerableEmpty()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
