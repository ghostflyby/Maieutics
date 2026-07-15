using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Client.Protocol;
using Maieutics.Jupyter.Client.Transport;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

public sealed class JupyterProtocolSessionTests
{
    private static readonly JupyterSessionIdentity KernelSession = new("kernel", "kernel");

    [Fact]
    public async Task RequestRequiresExpectedReplyTypeAndChannel()
    {
        var transport = new FakeJupyterTransport();
        await using var session = new JupyterProtocolSession(
            transport,
            new JupyterSessionIdentity("session", "tester"));

        var requestTask = session.GetKernelInfoAsync(TestContext.Current.CancellationToken);
        var request = transport.SentMessages.Single().Message;
        transport.Receive(
            JupyterTransportChannel.Shell,
            Reply("execute_reply", new JupyterExecuteReply("ok"), JupyterJsonContext.Default.JupyterExecuteReply,
                request));
        requestTask.IsCompleted.Should().BeFalse();

        transport.Receive(
            JupyterTransportChannel.Shell,
            Reply("kernel_info_reply", KernelInfo(), JupyterJsonContext.Default.JupyterKernelInfo, request));

        var reply = await requestTask.WaitAsync(TestContext.Current.CancellationToken);
        reply.Implementation.Should().Be("test-kernel");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecutionCompletesOnlyAfterReplyAndParentedIdle(bool replyFirst)
    {
        var transport = new FakeJupyterTransport();
        await using var session = new JupyterProtocolSession(transport);
        var execution = await session.StartExecutionAsync(
            new JupyterExecuteRequest("1 + 2"),
            TestContext.Current.CancellationToken);
        var request = transport.SentMessages.Single().Message;
        var outputs = ReadOutputsAsync(execution, TestContext.Current.CancellationToken);

        var reply = Reply(
            "execute_reply",
            new JupyterExecuteReply("ok", 1),
            JupyterJsonContext.Default.JupyterExecuteReply,
            request);
        var idle = Reply(
            "status",
            new JupyterStatus("idle"),
            JupyterJsonContext.Default.JupyterStatus,
            request);
        var result = Reply(
            "execute_result",
            new JupyterExecuteResultData(
                new Dictionary<string, JsonElement>
                {
                    ["text/plain"] = JsonSerializer.SerializeToElement("3")
                },
                new Dictionary<string, JsonElement>(),
                1),
            JupyterJsonContext.Default.JupyterExecuteResultData,
            request);

        transport.Receive(JupyterTransportChannel.Iopub, result);
        transport.Receive(replyFirst ? JupyterTransportChannel.Shell : JupyterTransportChannel.Iopub,
            replyFirst ? reply : idle);
        execution.Completion.IsCompleted.Should().BeFalse();
        transport.Receive(replyFirst ? JupyterTransportChannel.Iopub : JupyterTransportChannel.Shell,
            replyFirst ? idle : reply);

        var completion = await execution.Completion.WaitAsync(TestContext.Current.CancellationToken);
        var collected = await outputs.WaitAsync(TestContext.Current.CancellationToken);
        completion.Reply.Status.Should().Be("ok");
        collected.Should().ContainSingle(output => output is JupyterExecuteResultOutput);
    }

    [Fact]
    public async Task LateParentedOutputBecomesClientEvent()
    {
        var transport = new FakeJupyterTransport();
        await using var session = new JupyterProtocolSession(transport);
        await using var events = session.WatchEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await events.MoveNextAsync()).Should().BeTrue();

        var execution = await session.StartExecutionAsync(
            new JupyterExecuteRequest("1"),
            TestContext.Current.CancellationToken);
        var request = transport.SentMessages.Single().Message;
        var outputReader = ReadOutputsAsync(execution, TestContext.Current.CancellationToken);
        transport.Receive(
            JupyterTransportChannel.Shell,
            Reply("execute_reply", new JupyterExecuteReply("ok", 1), JupyterJsonContext.Default.JupyterExecuteReply,
                request));
        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply("status", new JupyterStatus("idle"), JupyterJsonContext.Default.JupyterStatus, request));
        await execution.Completion.WaitAsync(TestContext.Current.CancellationToken);
        await outputReader.WaitAsync(TestContext.Current.CancellationToken);

        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply("stream", new JupyterStream("stdout", "late"), JupyterJsonContext.Default.JupyterStream, request));

        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.Should().BeOfType<JupyterLateOutput>();
    }

    [Fact]
    public async Task InputReplyUsesInputRequestAsParent()
    {
        var transport = new FakeJupyterTransport();
        await using var session = new JupyterProtocolSession(transport);
        var execution = await session.StartExecutionAsync(
            new JupyterExecuteRequest("prompt()", AllowStdin: true),
            TestContext.Current.CancellationToken);
        var executeRequest = transport.SentMessages.Single().Message;
        var inputRequest = Reply(
            "input_request",
            new JupyterInputRequestContent("Name: ", false),
            JupyterJsonContext.Default.JupyterInputRequestContent,
            executeRequest);
        transport.Receive(JupyterTransportChannel.Stdin, inputRequest);

        await using var outputs = execution.Outputs.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await outputs.MoveNextAsync()).Should().BeTrue();
        var input = outputs.Current.Should().BeOfType<JupyterInputRequest>().Subject;
        await execution.ReplyInputAsync(input, "Ada", TestContext.Current.CancellationToken);

        var reply = transport.SentMessages.Last();
        reply.Channel.Should().Be(JupyterTransportChannel.Stdin);
        reply.Message.MessageType.Should().Be("input_reply");
        reply.Message.ParentHeader.Should().Be(inputRequest.Header);
    }

    [Fact]
    public async Task DisposeFailsPendingRequest()
    {
        var transport = new FakeJupyterTransport();
        var session = new JupyterProtocolSession(transport);
        var request = session.GetKernelInfoAsync(TestContext.Current.CancellationToken);

        await session.DisposeAsync();

        await request.Invoking(task => task).Should().ThrowAsync<ObjectDisposedException>();
    }

    private static JupyterKernelInfo KernelInfo() => new(
        "5.5",
        "test-kernel",
        "1.0",
        new JupyterLanguageInfo("test", "1.0"));

    private static JupyterMessage Reply<TContent>(
        string messageType,
        TContent content,
        JsonTypeInfo<TContent> typeInfo,
        JupyterMessage parent)
    {
        return JupyterMessage.Create(messageType, content, typeInfo, KernelSession, parent.Header);
    }

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
}