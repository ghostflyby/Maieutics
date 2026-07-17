using System.Runtime.CompilerServices;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;
using Microsoft.Extensions.AI;

namespace Maieutics.Jupyter.Tests;

[Collection(JupyterSocketIntegrationCollection.Name)]
public sealed class AgentJupyterIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task AgentKernelStreamsTrackedMarkdownAndRetainsConversation()
    {
        using var deadline = CreateDeadline();
        var timeProvider = new ManualTimeProvider();
        var chatClient = new ScriptedChatClient(
            (_, token) => TimedResponseAsync(timeProvider, token),
            (_, token) => TextResponseAsync(token, "remembered"));
        var session = new AgentSession(chatClient, new AgentSessionOptions { SystemPrompt = "Be concise." });
        var application = new MaieuticsAgentKernelApplication(
            session,
            new MaieuticsAgentKernelOptions
            {
                FlushInterval = TimeSpan.FromMilliseconds(50),
                FlushCharacters = 2
            },
            timeProvider: timeProvider);
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        await using var host = await JupyterKernelHost.StartAsync(
            connection,
            application,
            cancellationToken: deadline.Token);
        await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);

        (await client.PingAsync(deadline.Token)).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        var first = await client.ExecuteAsync(new JupyterExecuteRequest("first"), deadline.Token);
        var firstOutputs = await ReadOutputsAsync(first, deadline.Token);
        (await first.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");

        var notebookOutputs = firstOutputs
            .Where(output => output is JupyterDisplayOutput or JupyterDisplayUpdateOutput)
            .ToArray();
        notebookOutputs.Select(ReadMarkdown).Should().Equal("A", "ABC", "ABCDE");
        notebookOutputs.Select(ReadPlainText).Should().Equal("A", "ABC", "ABCDE");
        var displayId = notebookOutputs.OfType<JupyterDisplayOutput>().Single().DisplayId;
        displayId.Should().NotBeNull();
        notebookOutputs.OfType<JupyterDisplayUpdateOutput>().Should()
            .OnlyContain(update => update.DisplayId == displayId);

        var second = await client.ExecuteAsync(new JupyterExecuteRequest("second"), deadline.Token);
        var secondOutputs = await ReadOutputsAsync(second, deadline.Token);
        (await second.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");
        secondOutputs.OfType<JupyterDisplayOutput>().Single().Data.Data["text/markdown"].GetString().Should()
            .Be("remembered");
        chatClient.Requests[1].Select(message => (message.Role, message.Text)).Should().Equal(
            (ChatRole.User, "first"),
            (ChatRole.Assistant, "ABCDE"),
            (ChatRole.User, "second"));

        var whitespace = await client.ExecuteAsync(new JupyterExecuteRequest("   "), deadline.Token);
        var whitespaceOutputs = await ReadOutputsAsync(whitespace, deadline.Token);
        (await whitespace.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");
        whitespaceOutputs.OfType<JupyterDisplayOutput>().Should().BeEmpty();
        whitespaceOutputs.OfType<JupyterDisplayUpdateOutput>().Should().BeEmpty();
        chatClient.Requests.Should().HaveCount(2);

        await client.ShutdownAsync(false, deadline.Token);
        await host.Completion.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task ProviderFailureKeepsPartialOutputButRollsBackHistory()
    {
        using var deadline = CreateDeadline();
        var chatClient = new ScriptedChatClient(
            (_, token) => FailAfterTextAsync(new InvalidOperationException("provider secret"), token, "part", "ial"),
            (_, token) => TextResponseAsync(token, "recovered"));
        var session = new AgentSession(chatClient);
        var application = new MaieuticsAgentKernelApplication(session);
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        await using var host = await JupyterKernelHost.StartAsync(
            connection,
            application,
            cancellationToken: deadline.Token);
        await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);

        var failed = await client.ExecuteAsync(new JupyterExecuteRequest("fail"), deadline.Token);
        var failedOutputs = await ReadOutputsAsync(failed, deadline.Token);
        var failedCompletion = await failed.Completion.WaitAsync(deadline.Token);
        failedCompletion.Reply.Status.Should().Be("error");
        failedCompletion.Reply.ErrorName.Should().Be("AgentProviderError");
        failedCompletion.Reply.ErrorValue.Should().NotContain("provider secret");
        failedOutputs.Where(output => output is JupyterDisplayOutput or JupyterDisplayUpdateOutput)
            .Select(ReadMarkdown)
            .Should().Equal("part", "partial");
        failedOutputs.OfType<JupyterExecutionError>().Single().Name.Should().Be("AgentProviderError");
        session.GetTranscriptSnapshot().Messages.Should().BeEmpty();

        var recovered = await client.ExecuteAsync(new JupyterExecuteRequest("retry"), deadline.Token);
        await ReadOutputsAsync(recovered, deadline.Token);
        (await recovered.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");
        session.GetTranscriptSnapshot().Messages
            .Select(message => (message.Role, Text: ReadText(message)))
            .Should().Equal(
                (AgentMessageRole.User, "retry"),
                (AgentMessageRole.Assistant, "recovered"));

        await client.ShutdownAsync(false, deadline.Token);
        await host.Completion.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task InterruptAbortsStreamingTurnAndLeavesHistoryUnchanged()
    {
        using var deadline = CreateDeadline();
        var responseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chatClient =
            new ScriptedChatClient((_, token) => WaitAfterTextAsync(responseStarted, token, "part", "ial"));
        var session = new AgentSession(chatClient);
        var application = new MaieuticsAgentKernelApplication(session);
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        await using var host = await JupyterKernelHost.StartAsync(
            connection,
            application,
            cancellationToken: deadline.Token);
        await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);

        var execution = await client.ExecuteAsync(new JupyterExecuteRequest("wait"), deadline.Token);
        await using var outputs = execution.Outputs.GetAsyncEnumerator(deadline.Token);
        JupyterDisplayOutput? partialDisplay = null;
        while (await outputs.MoveNextAsync())
        {
            if (outputs.Current is JupyterDisplayOutput display)
            {
                partialDisplay = display;
                break;
            }
        }

        await responseStarted.Task.WaitAsync(deadline.Token);
        await client.InterruptAsync(deadline.Token);
        var remainingOutputs = new List<JupyterOutput>();
        while (await outputs.MoveNextAsync())
        {
            remainingOutputs.Add(outputs.Current);
        }

        partialDisplay.Should().NotBeNull();
        partialDisplay.Data.Data["text/markdown"].GetString().Should().Be("part");
        remainingOutputs.Where(output => output is JupyterDisplayOutput or JupyterDisplayUpdateOutput)
            .Select(ReadMarkdown)
            .Should().ContainSingle().Which.Should().Be("partial");
        (await execution.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("aborted");
        session.GetTranscriptSnapshot().Messages.Should().BeEmpty();
        (await client.PingAsync(deadline.Token)).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);

        await client.ShutdownAsync(false, deadline.Token);
        await host.Completion.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task InterruptAfterEventStreamCompletionDoesNotAbortCommittedRun()
    {
        using var deadline = CreateDeadline();
        var session = new CommitBoundarySession();
        var application = new MaieuticsAgentKernelApplication(session);
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        await using var host = await JupyterKernelHost.StartAsync(
            connection,
            application,
            cancellationToken: deadline.Token);
        await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);

        var execution = await client.ExecuteAsync(new JupyterExecuteRequest("commit"), deadline.Token);
        var outputsTask = ReadOutputsAsync(execution, deadline.Token);
        await session.Run.CompletionObserved.Task.WaitAsync(deadline.Token);

        await client.InterruptAsync(deadline.Token);
        session.Run.Complete();

        var outputs = await outputsTask;
        outputs.OfType<JupyterDisplayOutput>().Single().Data.Data["text/markdown"].GetString().Should()
            .Be("committed");
        (await execution.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");

        await client.ShutdownAsync(false, deadline.Token);
        await host.Completion.WaitAsync(deadline.Token);
    }

    private static string? ReadMarkdown(JupyterOutput output) => output switch
    {
        JupyterDisplayOutput display => display.Data.Data["text/markdown"].GetString(),
        JupyterDisplayUpdateOutput update => update.Data.Data["text/markdown"].GetString(),
        _ => null
    };

    private static string? ReadPlainText(JupyterOutput output) => output switch
    {
        JupyterDisplayOutput display => display.Data.Data["text/plain"].GetString(),
        JupyterDisplayUpdateOutput update => update.Data.Data["text/plain"].GetString(),
        _ => null
    };

    private static string ReadText(AgentMessage message) => string.Concat(
        message.Contents.OfType<AgentTextContent>().Select(content => content.Text));

    private static async Task<IReadOnlyList<JupyterOutput>> ReadOutputsAsync(
        IJupyterExecution execution,
        CancellationToken cancellationToken)
    {
        var outputs = new List<JupyterOutput>();
        await foreach (var output in execution.Outputs.WithCancellation(cancellationToken))
        {
            outputs.Add(output);
        }

        return outputs;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> TimedResponseAsync(
        ManualTimeProvider timeProvider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "A");
        timeProvider.Advance(TimeSpan.FromMilliseconds(51));
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "B");
        yield return new ChatResponseUpdate(ChatRole.Assistant, "C");
        yield return new ChatResponseUpdate(ChatRole.Assistant, "D");
        yield return new ChatResponseUpdate(ChatRole.Assistant, "E");
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> TextResponseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        params string[] text)
    {
        foreach (var value in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, value);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FailAfterTextAsync(
        Exception exception,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        params string[] text)
    {
        foreach (var value in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, value);
            await Task.Yield();
        }

        throw exception;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WaitAfterTextAsync(
        TaskCompletionSource responseStarted,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        params string[] text)
    {
        foreach (var value in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, value);
        }

        responseStarted.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static CancellationTokenSource CreateDeadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return deadline;
    }

    private sealed class ScriptedChatClient(
        params Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>[] responses)
        : IChatClient
    {
        private readonly Queue<Func<IReadOnlyList<ChatMessage>, CancellationToken,
            IAsyncEnumerable<ChatResponseUpdate>>> responses = new(responses);

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(new NotSupportedException());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var request = messages.Select(message => message.Clone()).ToArray();
            Requests.Add(request);
            return responses.Dequeue()(request, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref timestamp);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref timestamp, elapsed.Ticks);
    }

    private sealed class CommitBoundarySession : IAgentSession
    {
        public CommitBoundarySession()
        {
            Id = AgentSessionId.Create();
            Run = new CommitBoundaryRun(Id);
        }

        public AgentSessionId Id { get; }

        public CommitBoundaryRun Run { get; }

        public Task<IAgentRun> StartTurnAsync(
            AgentTurn turn,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IAgentRun>(Run);
        }

        public AgentTranscript GetTranscriptSnapshot() => new(Id, 0, []);
    }

    private sealed class CommitBoundaryRun : IAgentRun
    {
        private readonly TaskCompletionSource<AgentRunResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly AgentMessage user;
        private readonly AgentMessage assistant;

        public CommitBoundaryRun(AgentSessionId sessionId)
        {
            SessionId = sessionId;
            Id = AgentRunId.Create();
            user = new AgentMessage(
                AgentMessageId.Create(),
                AgentMessageRole.User,
                [new AgentTextContent("commit")]);
            assistant = new AgentMessage(
                AgentMessageId.Create(),
                AgentMessageRole.Assistant,
                [new AgentTextContent("committed")]);
        }

        public AgentRunId Id { get; }

        public AgentSessionId SessionId { get; }

        public IAsyncEnumerable<AgentEvent> Events => ReadEventsAsync();

        public Task<AgentRunResult> Completion
        {
            get
            {
                CompletionObserved.TrySetResult();
                return completion.Task;
            }
        }

        public TaskCompletionSource CompletionObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            completion.TrySetCanceled(cancellationToken);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Complete()
        {
            var transcript = new AgentTranscript(SessionId, 1, [user, assistant]);
            completion.TrySetResult(new AgentRunResult(Id, user, assistant, transcript));
        }

        private async IAsyncEnumerable<AgentEvent> ReadEventsAsync()
        {
            await Task.Yield();
            yield return new AgentTextDelta(Id, 1, assistant.Id, "committed");
            yield return new AgentMessageCompleted(Id, 2, assistant);
        }
    }
}