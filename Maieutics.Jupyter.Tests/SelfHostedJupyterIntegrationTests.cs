using System.Text.Json;
using FluentAssertions;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;
using NetMQ;
using NetMQ.Sockets;

namespace Maieutics.Jupyter.Tests;

[Collection(JupyterSocketIntegrationCollection.Name)]
public sealed class SelfHostedJupyterIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task ClientAndKernelCompleteCoreLifecycle()
    {
        using var deadline = CreateDeadline();
        var cancellationToken = deadline.Token;
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var application = new TestKernelApplication();
        await using var host = await JupyterKernelHost.StartAsync(
            connection,
            application,
            cancellationToken: cancellationToken);
        await using var client = await JupyterClient.ConnectAsync(
            connection,
            cancellationToken: cancellationToken);

        var latency = await client.PingAsync(cancellationToken);
        latency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        var info = await client.GetKernelInfoAsync(cancellationToken);
        info.Implementation.Should().Be("maieutics-test");
        info.ProtocolVersion.Should().Be("5.5");
        info.Status.Should().Be("ok");

        var completion = await client.CompleteAsync(new JupyterCompleteRequest("cons", 4), cancellationToken);
        completion.Matches.Should().Contain("console");
        completion.CursorStart.Should().Be(0);
        completion.CursorEnd.Should().Be(4);
        var inspection = await client.InspectAsync(new JupyterInspectRequest("console", 7), cancellationToken);
        inspection.Found.Should().BeTrue();
        inspection.Data["text/plain"].GetString().Should().Contain("console");
        var completeness = await client.IsCompleteAsync(
            new JupyterIsCompleteRequest("if (true) {"),
            cancellationToken);
        completeness.Status.Should().Be("incomplete");
        completeness.Indent.Should().Be("  ");
        (await (Client: client, Token: cancellationToken)
                .Awaiting(static state => state.Client.CompleteAsync(
                    new JupyterCompleteRequest("fail", 4),
                    state.Token))
                .Should().ThrowAsync<JupyterRequestException>()).Which.ErrorName.Should()
            .Be("CompletionFailure");
        (await (Client: client, Token: cancellationToken)
                .Awaiting(static state => state.Client.InspectAsync(
                    new JupyterInspectRequest("fail", 4),
                    state.Token))
                .Should().ThrowAsync<JupyterRequestException>()).Which.ErrorName.Should()
            .Be("InspectionFailure");
        (await client.IsCompleteAsync(new JupyterIsCompleteRequest("fail"), cancellationToken)).Status.Should()
            .Be("unknown");

        var sum = await client.ExecuteAsync(
            new JupyterExecuteRequest("sum"),
            cancellationToken);
        var sumOutputs = await ReadOutputsAsync(sum, cancellationToken);
        (await sum.Completion.WaitAsync(cancellationToken)).Reply.Status.Should().Be("ok");
        sumOutputs.Should().ContainSingle(output => output is JupyterExecuteResultOutput);

        var displayExecution = await client.ExecuteAsync(
            new JupyterExecuteRequest("display"),
            cancellationToken);
        var displayOutputs = await ReadOutputsAsync(displayExecution, cancellationToken);
        (await displayExecution.Completion.WaitAsync(cancellationToken)).Reply.Status.Should().Be("ok");
        displayOutputs.Where(IsNotebookDisplayOutput).Select(output => output.GetType()).Should().Equal(
            typeof(JupyterDisplayOutput),
            typeof(JupyterDisplayUpdateOutput),
            typeof(JupyterClearOutput));
        var display = displayOutputs.OfType<JupyterDisplayOutput>().Single();
        var update = displayOutputs.OfType<JupyterDisplayUpdateOutput>().Single();
        display.DisplayId.Should().NotBeNull();
        update.DisplayId.Should().Be(display.DisplayId);
        displayOutputs.OfType<JupyterClearOutput>().Single().Wait.Should().BeTrue();

        var specifiedExecution = await client.ExecuteAsync(
            new JupyterExecuteRequest("specified-display"),
            cancellationToken);
        var specifiedOutputs = await ReadOutputsAsync(specifiedExecution, cancellationToken);
        await specifiedExecution.Completion.WaitAsync(cancellationToken);
        specifiedOutputs.OfType<JupyterDisplayOutput>().Single().DisplayId.Should()
            .Be(new JupyterDisplayId("specified-display"));

        var silentExecution = await client.ExecuteAsync(
            new JupyterExecuteRequest("display", Silent: true),
            cancellationToken);
        var silentOutputs = await ReadOutputsAsync(silentExecution, cancellationToken);
        await silentExecution.Completion.WaitAsync(cancellationToken);
        silentOutputs.Should()
            .NotContain(output => output is JupyterExecuteInputOutput || IsNotebookDisplayOutput(output));

        var invalidDisplayExecution = await client.ExecuteAsync(
            new JupyterExecuteRequest("invalid-display"),
            cancellationToken);
        var invalidDisplayOutputs = await ReadOutputsAsync(invalidDisplayExecution, cancellationToken);
        var invalidDisplayCompletion = await invalidDisplayExecution.Completion.WaitAsync(cancellationToken);
        invalidDisplayCompletion.Reply.Status.Should().Be("error");
        invalidDisplayOutputs.OfType<JupyterExecutionError>().Should().ContainSingle();

        var publishedErrorExecution = await client.ExecuteAsync(
            new JupyterExecuteRequest("published-error"),
            cancellationToken);
        var publishedErrorOutputs = await ReadOutputsAsync(publishedErrorExecution, cancellationToken);
        (await publishedErrorExecution.Completion.WaitAsync(cancellationToken)).Reply.Status.Should().Be("ok");
        publishedErrorOutputs.OfType<JupyterExecutionError>().Should().ContainSingle().Which.Should().BeEquivalentTo(
            new
            {
                Name = "InnerError",
                Value = "inner failure",
                Traceback = new[] { "frame" }
            });

        var inputExecution = await client.ExecuteAsync(
            new JupyterExecuteRequest("input", AllowStdin: true),
            cancellationToken);
        var inputOutputs = new List<JupyterOutput>();
        await foreach (var output in inputExecution.Outputs.WithCancellation(cancellationToken))
        {
            inputOutputs.Add(output);
            if (output is JupyterInputRequest input)
            {
                await inputExecution.ReplyInputAsync(input, "Ada", cancellationToken);
            }
        }

        (await inputExecution.Completion.WaitAsync(cancellationToken)).Reply.Status.Should()
            .Be("ok");
        inputOutputs.OfType<JupyterStdout>().Should().Contain(output => output.Text == "Hello Ada");

        var deniedInputExecution = await client.ExecuteAsync(
            new JupyterExecuteRequest("input", AllowStdin: false),
            cancellationToken);
        var deniedInputOutputs = await ReadOutputsAsync(deniedInputExecution, cancellationToken);
        (await deniedInputExecution.Completion.WaitAsync(cancellationToken)).Reply.Status.Should().Be("error");
        deniedInputOutputs.Should().NotContain(output => output is JupyterInputRequest);
        deniedInputOutputs.OfType<JupyterExecutionError>().Should().ContainSingle();

        var waiting = await client.ExecuteAsync(
            new JupyterExecuteRequest("wait"),
            cancellationToken);
        var waitOutputs = ReadOutputsAsync(waiting, cancellationToken);
        await application.WaitStarted.Task.WaitAsync(cancellationToken);
        var busyLatency = await client.PingAsync(cancellationToken);
        busyLatency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        await client.InterruptAsync(cancellationToken);
        (await waiting.Completion.WaitAsync(cancellationToken)).Reply.Status.Should().Be("aborted");
        await waitOutputs.WaitAsync(cancellationToken);

        var shutdown = await client.ShutdownAsync(false, cancellationToken);
        shutdown.Restart.Should().BeFalse();
        shutdown.Status.Should().Be("ok");
        await host.Completion.WaitAsync(cancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task DisplayUpdateAfterIdleBecomesTypedLateOutput()
    {
        using var deadline = CreateDeadline();
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var application = new TestKernelApplication();
        await using var host = await JupyterKernelHost.StartAsync(
            connection,
            application,
            cancellationToken: deadline.Token);
        await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
        await using var events = client.WatchEventsAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
        (await events.MoveNextAsync()).Should().BeTrue();

        var execution = await client.ExecuteAsync(
            new JupyterExecuteRequest("late-display"),
            deadline.Token);
        var outputs = await ReadOutputsAsync(execution, deadline.Token);
        await execution.Completion.WaitAsync(deadline.Token);
        outputs.OfType<JupyterDisplayOutput>().Should().ContainSingle();

        await application.PublishLateDisplayUpdateAsync(deadline.Token);

        JupyterLateOutput? late = null;
        while (await events.MoveNextAsync())
        {
            if (events.Current is JupyterLateOutput lateOutput)
            {
                late = lateOutput;
                break;
            }
        }

        late.Should().NotBeNull();
        var lateEvent = late;
        lateEvent.Message.MessageType.Should().Be("update_display_data");
        lateEvent.Output.Should().BeOfType<JupyterDisplayUpdateOutput>()
            .Which.DisplayId.Should().Be(application.LateDisplayId);
        await client.ShutdownAsync(false, deadline.Token);
        await host.Completion.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task MissingProviderReturnsTypedError()
    {
        using var deadline = CreateDeadline();
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        await using var host = await JupyterKernelHost.StartAsync(
            connection,
            new ExecuteOnlyKernelApplication(),
            cancellationToken: deadline.Token);
        await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);

        var assertion = await (Client: client, deadline.Token)
            .Awaiting(static state => state.Client.CompleteAsync(
                new JupyterCompleteRequest("cons", 4),
                state.Token))
            .Should().ThrowAsync<JupyterRequestException>();
        assertion.Which.ErrorName.Should().Be("NotSupported");
        (await (Client: client, deadline.Token)
            .Awaiting(static state => state.Client.InspectAsync(
                new JupyterInspectRequest("console", 7),
                state.Token))
            .Should().ThrowAsync<JupyterRequestException>()).Which.ErrorName.Should().Be("NotSupported");
        await client.ShutdownAsync(false, deadline.Token);
        await host.Completion.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 60_000)]
    public async Task HostLifecycleAndConcurrentDisposeAreRepeatable()
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(45));
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var connection = JupyterConnectionInfo.CreateLocalTcp();
            await using var host = await JupyterKernelHost.StartAsync(
                connection,
                new ExecuteOnlyKernelApplication(),
                cancellationToken: deadline.Token);
            var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            await client.PingAsync(deadline.Token);

            await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask())
                .WaitAsync(deadline.Token);
            await host.StopAsync(deadline.Token);
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task FirstIopubSubscriptionPublishesInitializationAndRequestLifecycle()
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(10));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        await using var host = await JupyterKernelHost.StartAsync(
            connection,
            new ExecuteOnlyKernelApplication(),
            cancellationToken: deadline.Token);
        using var subscriber = new SubscriberSocket();
        subscriber.Connect(connection.Endpoint(JupyterChannel.Iopub));
        subscriber.SubscribeToAnyTopic();
        var serializer = new JupyterMessageSerializer(connection.Key, connection.SignatureScheme);

        var welcome = ReceiveWireMessage(subscriber, serializer);
        var starting = ReceiveWireMessage(subscriber, serializer);

        welcome.Message.MessageType.Should().Be("iopub_welcome");
        welcome.Message.ParentHeader.Should().BeNull();
        starting.Message.MessageType.Should().Be("status");
        starting.Message.ParentHeader.Should().BeNull();
        starting.Message.GetContent(JupyterJsonContext.Default.JupyterStatus).ExecutionState.Should().Be("starting");

        using var shell = new DealerSocket();
        shell.Connect(connection.Endpoint(JupyterChannel.Shell));
        var request = JupyterMessage.Create(
            "kernel_info_request",
            new JupyterEmptyContent(),
            JupyterJsonContext.Default.JupyterEmptyContent,
            new JupyterSessionIdentity("raw-session", "tester"));
        SendWireMessage(shell, serializer, JupyterWireMessage.Create(request));

        var busy = ReceiveWireMessage(subscriber, serializer);
        var reply = ReceiveWireMessage(shell, serializer);
        var idle = ReceiveWireMessage(subscriber, serializer);
        busy.Message.MessageType.Should().Be("status");
        busy.Message.ParentHeader?.MessageId.Should().Be(request.Header.MessageId);
        busy.Message.GetContent(JupyterJsonContext.Default.JupyterStatus).ExecutionState.Should().Be("busy");
        reply.Message.MessageType.Should().Be("kernel_info_reply");
        reply.Message.ParentHeader?.MessageId.Should().Be(request.Header.MessageId);
        idle.Message.MessageType.Should().Be("status");
        idle.Message.ParentHeader?.MessageId.Should().Be(request.Header.MessageId);
        idle.Message.GetContent(JupyterJsonContext.Default.JupyterStatus).ExecutionState.Should().Be("idle");
        await host.StopAsync(deadline.Token);
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

    private static bool IsNotebookDisplayOutput(JupyterOutput output) =>
        output is JupyterDisplayOutput or JupyterDisplayUpdateOutput or JupyterClearOutput;

    private static CancellationTokenSource CreateDeadline(TimeSpan? timeout = null)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        return deadline;
    }

    private static JupyterWireMessage ReceiveWireMessage(
        SubscriberSocket subscriber,
        JupyterMessageSerializer serializer)
    {
        var message = new NetMQMessage();
        if (!subscriber.TryReceiveMultipartMessage(TimeSpan.FromSeconds(5), ref message))
        {
            throw new TimeoutException("Timed out while waiting for the Kernel IOPub initialization messages.");
        }

        return serializer.Deserialize(message.Select(frame => frame.ToByteArray()).ToArray());
    }

    private static JupyterWireMessage ReceiveWireMessage(
        DealerSocket dealer,
        JupyterMessageSerializer serializer)
    {
        var message = new NetMQMessage();
        if (!dealer.TryReceiveMultipartMessage(TimeSpan.FromSeconds(5), ref message))
        {
            throw new TimeoutException("Timed out while waiting for the Kernel shell reply.");
        }

        return serializer.Deserialize(message.Select(frame => frame.ToByteArray()).ToArray());
    }

    private static void SendWireMessage(
        DealerSocket dealer,
        JupyterMessageSerializer serializer,
        JupyterWireMessage wireMessage)
    {
        var message = new NetMQMessage();
        foreach (var frame in serializer.Serialize(wireMessage))
        {
            message.Append(frame);
        }

        dealer.SendMultipartMessage(message);
    }

    private sealed class TestKernelApplication : IJupyterKernelApplication, IJupyterCompletionProvider,
        IJupyterInspectionProvider, IJupyterCodeCompletenessProvider
    {
        private readonly TaskCompletionSource<(JupyterExecutionContext Context, JupyterDisplayId DisplayId)>
            lateDisplay = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JupyterDisplayId LateDisplayId { get; private set; }

        public JupyterKernelInfo KernelInfo { get; } = new(
            "5.5",
            "maieutics-test",
            "1.0",
            new JupyterLanguageInfo("test", "1.0"));

        public async ValueTask<JupyterExecuteResult> ExecuteAsync(
            JupyterExecutionContext context,
            JupyterExecuteRequest request,
            CancellationToken cancellationToken)
        {
            switch (request.Code)
            {
                case "sum":
                    await context.PublishResultAsync(
                        new MimeBundle(new Dictionary<string, JsonElement>
                        {
                            ["text/plain"] = JsonSerializer.SerializeToElement("3")
                        }),
                        cancellationToken: cancellationToken);
                    break;
                case "input":
                    var name = await context.RequestInputAsync("Name: ", cancellationToken: cancellationToken);
                    await context.WriteStdoutAsync($"Hello {name}", cancellationToken);
                    break;
                case "display":
                    var displayId = await context.DisplayTrackedAsync(
                        TextBundle("initial"),
                        cancellationToken: cancellationToken);
                    await context.UpdateDisplayAsync(
                        displayId,
                        TextBundle("updated"),
                        cancellationToken: cancellationToken);
                    await context.ClearOutputAsync(true, cancellationToken);
                    break;
                case "specified-display":
                    await context.DisplayTrackedAsync(
                        TextBundle("specified"),
                        new JupyterDisplayId("specified-display"),
                        cancellationToken: cancellationToken);
                    break;
                case "late-display":
                    LateDisplayId = await context.DisplayTrackedAsync(
                        TextBundle("initial"),
                        cancellationToken: cancellationToken);
                    lateDisplay.TrySetResult((context, LateDisplayId));
                    break;
                case "invalid-display":
                    await context.UpdateDisplayAsync(
                        default,
                        TextBundle("invalid"),
                        cancellationToken: cancellationToken);
                    break;
                case "published-error":
                    await context.PublishErrorAsync(
                        "InnerError",
                        "inner failure",
                        ["frame"],
                        cancellationToken);
                    break;
                case "wait":
                    WaitStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    break;
            }

            return JupyterExecuteResult.Ok;
        }

        public async Task PublishLateDisplayUpdateAsync(CancellationToken cancellationToken)
        {
            var state = await lateDisplay.Task.WaitAsync(cancellationToken);
            await state.Context.UpdateDisplayAsync(
                state.DisplayId,
                TextBundle("late"),
                cancellationToken: cancellationToken);
        }

        public ValueTask<JupyterCompletionResult> CompleteAsync(
            JupyterCompleteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Code == "fail")
            {
                throw new JupyterKernelExecutionException("CompletionFailure", "completion failed", ["trace"]);
            }

            return ValueTask.FromResult(new JupyterCompletionResult(["console"], 0, request.CursorPosition));
        }

        public ValueTask<JupyterInspectionResult> InspectAsync(
            JupyterInspectRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Code == "fail")
            {
                throw new JupyterKernelExecutionException("InspectionFailure", "inspection failed", ["trace"]);
            }

            return ValueTask.FromResult(new JupyterInspectionResult(
                true,
                new MimeBundle(new Dictionary<string, JsonElement>
                {
                    ["text/plain"] = JsonSerializer.SerializeToElement($"Documentation for {request.Code}")
                })));
        }

        public ValueTask<JupyterCodeCompletenessResult> IsCompleteAsync(
            JupyterIsCompleteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Code == "fail")
            {
                throw new JupyterKernelExecutionException("CompletenessFailure", "completeness failed", ["trace"]);
            }

            var incomplete = request.Code.EndsWith('{');
            return ValueTask.FromResult(new JupyterCodeCompletenessResult(
                incomplete ? JupyterCodeCompletenessStatus.Incomplete : JupyterCodeCompletenessStatus.Complete,
                incomplete ? "  " : null));
        }

        private static MimeBundle TextBundle(string text) => new(new Dictionary<string, JsonElement>
        {
            ["text/plain"] = JsonSerializer.SerializeToElement(text)
        });
    }

    private sealed class ExecuteOnlyKernelApplication : IJupyterKernelApplication
    {
        public JupyterKernelInfo KernelInfo { get; } = new(
            "5.5",
            "maieutics-test",
            "1.0",
            new JupyterLanguageInfo("test", "1.0"));

        public ValueTask<JupyterExecuteResult> ExecuteAsync(
            JupyterExecutionContext context,
            JupyterExecuteRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(JupyterExecuteResult.Ok);
    }
}
