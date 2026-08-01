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

    [Fact]
    public async Task ReadinessProbeAcceptsDelayedIdleFromAnEarlierRequest()
    {
        var transport = new FakeJupyterTransport();
        await using var session = new JupyterProtocolSession(transport);
        var readyTask = session.WaitForReadyAsync(TestContext.Current.CancellationToken);
        var firstRequest = transport.SentMessages.Single().Message;
        transport.Receive(
            JupyterTransportChannel.Shell,
            Reply("kernel_info_reply", KernelInfo(), JupyterJsonContext.Default.JupyterKernelInfo, firstRequest));

        await WaitForSentMessageCountAsync(transport, 2, TestContext.Current.CancellationToken);
        var secondRequest = transport.SentMessages[1].Message;
        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply("status", new JupyterStatus("idle"), JupyterJsonContext.Default.JupyterStatus, firstRequest));
        transport.Receive(
            JupyterTransportChannel.Shell,
            Reply("kernel_info_reply", KernelInfo(), JupyterJsonContext.Default.JupyterKernelInfo, secondRequest));

        var reply = await readyTask.WaitAsync(TestContext.Current.CancellationToken);
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
    public async Task ExecutionOutputsPreserveNotebookMessageOrderAndDisplayMetadata()
    {
        var transport = new FakeJupyterTransport();
        await using var session = new JupyterProtocolSession(transport);
        var execution = await session.StartExecutionAsync(
            new JupyterExecuteRequest("display()"),
            TestContext.Current.CancellationToken);
        var request = transport.SentMessages.Single().Message;
        var outputsTask = ReadOutputsAsync(execution, TestContext.Current.CancellationToken);
        var displayId = new JupyterDisplayId("display-1");
        var transient = new Dictionary<string, JsonElement>
        {
            [JupyterDisplayTransient.DisplayIdPropertyName] = JsonSerializer.SerializeToElement(displayId.Value),
            ["future"] = JsonSerializer.SerializeToElement("preserved")
        };

        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply(
                "execute_input",
                new JupyterExecuteInput("display()", 1),
                JupyterJsonContext.Default.JupyterExecuteInput,
                request));
        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply(
                "display_data",
                DisplayData("initial", transient),
                JupyterJsonContext.Default.JupyterDisplayData,
                request));
        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply(
                "update_display_data",
                new JupyterUpdateDisplayData(
                    DisplayData("updated", transient).Data,
                    new Dictionary<string, JsonElement>(),
                    transient),
                JupyterJsonContext.Default.JupyterUpdateDisplayData,
                request));
        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply(
                "clear_output",
                new JupyterClearOutputContent(true),
                JupyterJsonContext.Default.JupyterClearOutputContent,
                request));
        transport.Receive(
            JupyterTransportChannel.Shell,
            Reply(
                "execute_reply",
                new JupyterExecuteReply("ok", 1),
                JupyterJsonContext.Default.JupyterExecuteReply,
                request));
        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply("status", new JupyterStatus("idle"), JupyterJsonContext.Default.JupyterStatus, request));

        await execution.Completion.WaitAsync(TestContext.Current.CancellationToken);
        var outputs = await outputsTask.WaitAsync(TestContext.Current.CancellationToken);
        outputs.Take(4).Select(output => output.GetType()).Should().Equal(
            typeof(JupyterExecuteInputOutput),
            typeof(JupyterDisplayOutput),
            typeof(JupyterDisplayUpdateOutput),
            typeof(JupyterClearOutput));
        var input = outputs.OfType<JupyterExecuteInputOutput>().Single();
        input.Code.Should().Be("display()");
        input.ExecutionCount.Should().Be(1);
        var display = outputs.OfType<JupyterDisplayOutput>().Single();
        display.DisplayId.Should().Be(displayId);
        display.Transient?["future"].GetString().Should().Be("preserved");
        outputs.OfType<JupyterDisplayUpdateOutput>().Single().DisplayId.Should().Be(displayId);
        outputs.OfType<JupyterClearOutput>().Single().Wait.Should().BeTrue();
    }

    [Theory]
    [InlineData("null", "missing_display_id")]
    [InlineData("missing", "missing_display_id")]
    [InlineData("empty", "invalid_display_id")]
    [InlineData("number", "invalid_display_id")]
    public async Task MalformedDisplayUpdateIsOrderedAndDoesNotTerminateSession(
        string displayIdKind,
        string expectedErrorCode)
    {
        var transport = new FakeJupyterTransport();
        await using var session = new JupyterProtocolSession(transport);
        var execution = await session.StartExecutionAsync(
            new JupyterExecuteRequest("display()"),
            TestContext.Current.CancellationToken);
        var request = transport.SentMessages.Single().Message;
        var outputsTask = ReadOutputsAsync(execution, TestContext.Current.CancellationToken);
        Dictionary<string, JsonElement>? transient = displayIdKind == "null" ? null : [];
        if (displayIdKind == "empty")
        {
            transient![JupyterDisplayTransient.DisplayIdPropertyName] = JsonSerializer.SerializeToElement("");
        }
        else if (displayIdKind == "number")
        {
            transient![JupyterDisplayTransient.DisplayIdPropertyName] = JsonSerializer.SerializeToElement(42);
        }

        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply(
                "update_display_data",
                new JupyterUpdateDisplayData(
                    DisplayData("updated").Data,
                    new Dictionary<string, JsonElement>(),
                    transient),
                JupyterJsonContext.Default.JupyterUpdateDisplayData,
                request));
        transport.Receive(
            JupyterTransportChannel.Shell,
            Reply(
                "execute_reply",
                new JupyterExecuteReply("ok", 1),
                JupyterJsonContext.Default.JupyterExecuteReply,
                request));
        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply("status", new JupyterStatus("idle"), JupyterJsonContext.Default.JupyterStatus, request));

        (await execution.Completion.WaitAsync(TestContext.Current.CancellationToken)).Reply.Status.Should().Be("ok");
        var malformed = (await outputsTask.WaitAsync(TestContext.Current.CancellationToken))
            .OfType<JupyterMalformedOutput>().Should().ContainSingle().Which;
        malformed.MessageType.Should().Be("update_display_data");
        malformed.ErrorCode.Should().Be(expectedErrorCode);

        var followUp = await session.StartExecutionAsync(
            new JupyterExecuteRequest("1 + 1"),
            TestContext.Current.CancellationToken);
        var followUpRequest = transport.SentMessages.Last().Message;
        var followUpOutputs = ReadOutputsAsync(followUp, TestContext.Current.CancellationToken);
        transport.Receive(
            JupyterTransportChannel.Shell,
            Reply(
                "execute_reply",
                new JupyterExecuteReply("ok", 2),
                JupyterJsonContext.Default.JupyterExecuteReply,
                followUpRequest));
        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply("status", new JupyterStatus("idle"), JupyterJsonContext.Default.JupyterStatus, followUpRequest));

        (await followUp.Completion.WaitAsync(TestContext.Current.CancellationToken)).Reply.Status.Should().Be("ok");
        (await followUpOutputs.WaitAsync(TestContext.Current.CancellationToken))
            .OfType<JupyterMalformedOutput>().Should().BeEmpty();
    }

    [Fact]
    public async Task MalformedOptionalDisplayIdFallsBackToUntrackedDisplay()
    {
        var transport = new FakeJupyterTransport();
        await using var session = new JupyterProtocolSession(transport);
        var execution = await session.StartExecutionAsync(
            new JupyterExecuteRequest("display()"),
            TestContext.Current.CancellationToken);
        var request = transport.SentMessages.Single().Message;
        var outputsTask = ReadOutputsAsync(execution, TestContext.Current.CancellationToken);
        var transient = new Dictionary<string, JsonElement>
        {
            [JupyterDisplayTransient.DisplayIdPropertyName] = JsonSerializer.SerializeToElement(42)
        };

        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply(
                "display_data",
                DisplayData("still visible", transient),
                JupyterJsonContext.Default.JupyterDisplayData,
                request));
        transport.Receive(
            JupyterTransportChannel.Shell,
            Reply(
                "execute_reply",
                new JupyterExecuteReply("ok", 1),
                JupyterJsonContext.Default.JupyterExecuteReply,
                request));
        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply("status", new JupyterStatus("idle"), JupyterJsonContext.Default.JupyterStatus, request));

        (await execution.Completion.WaitAsync(TestContext.Current.CancellationToken)).Reply.Status.Should().Be("ok");
        var display = (await outputsTask.WaitAsync(TestContext.Current.CancellationToken))
            .OfType<JupyterDisplayOutput>().Should().ContainSingle().Which;
        display.Data.Data["text/plain"].GetString().Should().Be("still visible");
        display.DisplayId.Should().BeNull();
        display.Transient.Should().ContainKey(JupyterDisplayTransient.DisplayIdPropertyName)
            .WhoseValue.GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task LateParentedNotebookOutputsBecomeTypedClientEvents()
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
            Reply(
                "update_display_data",
                new JupyterUpdateDisplayData(
                    DisplayData("malformed").Data,
                    new Dictionary<string, JsonElement>(),
                    new Dictionary<string, JsonElement>()),
                JupyterJsonContext.Default.JupyterUpdateDisplayData,
                request));
        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply(
                "update_display_data",
                new JupyterUpdateDisplayData(
                    DisplayData("late").Data,
                    new Dictionary<string, JsonElement>(),
                    JupyterDisplayTransient.Create(new JupyterDisplayId("late-display"))),
                JupyterJsonContext.Default.JupyterUpdateDisplayData,
                request));
        transport.Receive(
            JupyterTransportChannel.Iopub,
            Reply(
                "clear_output",
                new JupyterClearOutputContent(true),
                JupyterJsonContext.Default.JupyterClearOutputContent,
                request));

        (await events.MoveNextAsync()).Should().BeTrue();
        var malformed = events.Current.Should().BeOfType<JupyterLateOutput>().Subject;
        malformed.Message.MessageType.Should().Be("update_display_data");
        malformed.Output.Should().BeOfType<JupyterMalformedOutput>()
            .Which.ErrorCode.Should().Be("missing_display_id");
        (await events.MoveNextAsync()).Should().BeTrue();
        var late = events.Current.Should().BeOfType<JupyterLateOutput>().Subject;
        late.Message.MessageType.Should().Be("update_display_data");
        late.Output.Should().BeOfType<JupyterDisplayUpdateOutput>()
            .Which.DisplayId.Should().Be(new JupyterDisplayId("late-display"));
        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.Should().BeOfType<JupyterLateOutput>()
            .Which.Output.Should().BeOfType<JupyterClearOutput>()
            .Which.Wait.Should().BeTrue();
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
    public async Task LanguageServiceRequestsCanCompleteConcurrentlyOutOfOrder()
    {
        var transport = new FakeJupyterTransport();
        await using var session = new JupyterProtocolSession(transport);
        var completeTask = session.CompleteAsync(
            new JupyterCompleteRequest("cons", 4),
            TestContext.Current.CancellationToken);
        var completeRequest = transport.SentMessages.Single().Message;
        var inspectTask = session.InspectAsync(
            new JupyterInspectRequest("console", 7),
            TestContext.Current.CancellationToken);
        var inspectRequest = transport.SentMessages.Last().Message;

        transport.Receive(
            JupyterTransportChannel.Shell,
            Reply(
                "inspect_reply",
                new JupyterInspectReply { Status = "ok", Found = false },
                JupyterJsonContext.Default.JupyterInspectReply,
                inspectRequest));
        transport.Receive(
            JupyterTransportChannel.Shell,
            Reply(
                "complete_reply",
                new JupyterCompleteReply
                {
                    Status = "ok",
                    Matches = ["console"],
                    CursorStart = 0,
                    CursorEnd = 4
                },
                JupyterJsonContext.Default.JupyterCompleteReply,
                completeRequest));

        (await completeTask.WaitAsync(TestContext.Current.CancellationToken)).Matches.Should().Contain("console");
        (await inspectTask.WaitAsync(TestContext.Current.CancellationToken)).Found.Should().BeFalse();
    }

    [Fact]
    public async Task CompleteErrorBecomesTypedRequestException()
    {
        var transport = new FakeJupyterTransport();
        await using var session = new JupyterProtocolSession(transport);
        var task = session.CompleteAsync(
            new JupyterCompleteRequest("bad", 3),
            TestContext.Current.CancellationToken);
        var request = transport.SentMessages.Single().Message;
        transport.Receive(
            JupyterTransportChannel.Shell,
            Reply(
                "complete_reply",
                new JupyterCompleteReply
                {
                    Status = "error",
                    ErrorName = "CompletionError",
                    ErrorValue = "failed",
                    Traceback = ["trace"]
                },
                JupyterJsonContext.Default.JupyterCompleteReply,
                request));

        var assertion = await task.Invoking(static value => value).Should().ThrowAsync<JupyterRequestException>();
        assertion.Which.RequestId.Should().Be(request.Header.MessageId);
        assertion.Which.RequestMessageType.Should().Be("complete_request");
        assertion.Which.ReplyMessageType.Should().Be("complete_reply");
        assertion.Which.ErrorName.Should().Be("CompletionError");
        assertion.Which.ErrorValue.Should().Be("failed");
        assertion.Which.Traceback.Should().Equal("trace");
    }

    [Fact]
    public async Task IsCompleteValidatesReplyStatus()
    {
        var transport = new FakeJupyterTransport();
        await using var session = new JupyterProtocolSession(transport);
        var task = session.IsCompleteAsync(
            new JupyterIsCompleteRequest("{}"),
            TestContext.Current.CancellationToken);
        var request = transport.SentMessages.Single().Message;
        transport.Receive(
            JupyterTransportChannel.Shell,
            Reply(
                "is_complete_reply",
                new JupyterIsCompleteReply("unexpected"),
                JupyterJsonContext.Default.JupyterIsCompleteReply,
                request));

        await task.Invoking(static value => value).Should().ThrowAsync<JupyterProtocolException>();
    }

    [Fact]
    public async Task InvalidCursorIsRejectedBeforeSending()
    {
        var transport = new FakeJupyterTransport();
        await using var session = new JupyterProtocolSession(transport);

        await session
            .Awaiting(static value => value.CompleteAsync(
                new JupyterCompleteRequest("a😀b", 4),
                TestContext.Current.CancellationToken))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        transport.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task CancellingLanguageRequestDoesNotSendInterrupt()
    {
        var transport = new FakeJupyterTransport();
        await using var session = new JupyterProtocolSession(transport);
        using var cancellation = new CancellationTokenSource();
        var task = session.CompleteAsync(new JupyterCompleteRequest("cons", 4), cancellation.Token);

        await cancellation.CancelAsync();

        await task.Invoking(static value => value).Should().ThrowAsync<OperationCanceledException>();
        transport.SentMessages.Should().ContainSingle(message => message.Message.MessageType == "complete_request");
    }

    [Fact]
    public async Task DisposeFailsPendingRequest()
    {
        var transport = new FakeJupyterTransport();
        var session = new JupyterProtocolSession(transport);
        var request = session.GetKernelInfoAsync(TestContext.Current.CancellationToken);

        await session.DisposeAsync();

        await request.Invoking(static task => task).Should().ThrowAsync<ObjectDisposedException>();
    }

    private static JupyterKernelInfo KernelInfo() => new(
        "5.5",
        "test-kernel",
        "1.0",
        new JupyterLanguageInfo("test", "1.0"));

    private static JupyterDisplayData DisplayData(
        string text,
        IReadOnlyDictionary<string, JsonElement>? transient = null) =>
        new(
            new Dictionary<string, JsonElement>
            {
                ["text/plain"] = JsonSerializer.SerializeToElement(text)
            },
            new Dictionary<string, JsonElement>(),
            transient);

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

    private static async Task WaitForSentMessageCountAsync(
        FakeJupyterTransport transport,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        while (transport.SentMessages.Count < expectedCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
