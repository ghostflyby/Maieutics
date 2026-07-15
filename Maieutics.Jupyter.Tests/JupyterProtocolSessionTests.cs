using System.Text.Json.Nodes;
using FluentAssertions;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Client.Protocol;
using Maieutics.Jupyter.Client.Transport;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

public sealed class JupyterProtocolSessionTests
{
    [Fact]
    public async Task RequestMatchesReplyByParentMessageId()
    {
        var transport = new FakeJupyterTransport();
        await using var session =
            new JupyterProtocolSession(transport, new JupyterSessionIdentity("session", "tester"));

        var requestTask = session.GetKernelInfoAsync(TestContext.Current.CancellationToken);
        var request = transport.SentMessages.Single().Message;

        transport.Receive(
            JupyterTransportChannel.Shell,
            JupyterMessage.Create(
                "kernel_info_reply",
                new JsonObject
                {
                    ["implementation"] = "deno",
                    ["implementation_version"] = "1",
                    ["language_info"] = new JsonObject { ["name"] = "typescript" }
                },
                new JupyterSessionIdentity("kernel", "kernel"),
                request.Header));

        var reply = await requestTask.WaitAsync(TestContext.Current.CancellationToken);

        reply.Implementation.Should().Be("deno");
        reply.LanguageName.Should().Be("typescript");
    }

    [Fact]
    public async Task ExecutionRoutesParentedOutputAndCompletesAfterReplyAndIdle()
    {
        var transport = new FakeJupyterTransport();
        await using var session =
            new JupyterProtocolSession(transport, new JupyterSessionIdentity("session", "tester"));

        var execution = await session.StartExecutionAsync(
            new ExecuteRequest("1 + 2"),
            TestContext.Current.CancellationToken);
        var request = transport.SentMessages.Single().Message;
        var outputs = new List<KernelOutput>();
        var outputReader = Task.Run(async () =>
        {
            await foreach (var output in execution.Outputs.WithCancellation(TestContext.Current.CancellationToken))
            {
                outputs.Add(output);
            }
        }, TestContext.Current.CancellationToken);

        transport.Receive(
            JupyterTransportChannel.Iopub,
            JupyterMessage.Create(
                "execute_result",
                new JsonObject
                {
                    ["data"] = new JsonObject
                    {
                        ["text/plain"] = "3"
                    },
                    ["metadata"] = new JsonObject(),
                    ["execution_count"] = 1
                },
                new JupyterSessionIdentity("kernel", "kernel"),
                request.Header));
        transport.Receive(
            JupyterTransportChannel.Shell,
            JupyterMessage.Create(
                "execute_reply",
                new JsonObject
                {
                    ["status"] = "ok",
                    ["execution_count"] = 1
                },
                new JupyterSessionIdentity("kernel", "kernel"),
                request.Header));
        transport.Receive(
            JupyterTransportChannel.Iopub,
            JupyterMessage.Create(
                "status",
                new JsonObject
                {
                    ["execution_state"] = "idle"
                },
                new JupyterSessionIdentity("kernel", "kernel"),
                request.Header));

        var completion = await execution.Completion.WaitAsync(TestContext.Current.CancellationToken);
        await outputReader.WaitAsync(TestContext.Current.CancellationToken);

        completion.Status.Should().Be("ok");
        outputs.Should().ContainSingle(output => output is ExecuteResultOutput);
    }

    [Fact]
    public async Task UnknownParentMessageBecomesUnhandledEvent()
    {
        var transport = new FakeJupyterTransport();
        await using var session =
            new JupyterProtocolSession(transport, new JupyterSessionIdentity("session", "tester"));
        await using var enumerator = session.Events.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.Should().BeOfType<Connected>();

        transport.Receive(
            JupyterTransportChannel.Iopub,
            JupyterMessage.Create(
                "display_data",
                new JsonObject(),
                new JupyterSessionIdentity("kernel", "kernel"),
                JupyterMessageHeader.Create("execute_request", new JupyterSessionIdentity("missing", "tester"))));

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.Should().BeOfType<UnhandledMessage>();
    }

    [Fact]
    public async Task IdleBeforeReplyDoesNotCloseExecutionStream()
    {
        var transport = new FakeJupyterTransport();
        await using var session =
            new JupyterProtocolSession(transport, new JupyterSessionIdentity("session", "tester"));

        var execution = await session.StartExecutionAsync(
            new ExecuteRequest("1 + 2"),
            TestContext.Current.CancellationToken);
        var request = transport.SentMessages.Single().Message;
        var outputs = new List<KernelOutput>();
        var outputReader = Task.Run(async () =>
        {
            await foreach (var output in execution.Outputs.WithCancellation(TestContext.Current.CancellationToken))
            {
                outputs.Add(output);
            }
        }, TestContext.Current.CancellationToken);

        transport.Receive(
            JupyterTransportChannel.Iopub,
            JupyterMessage.Create(
                "status",
                new JsonObject
                {
                    ["execution_state"] = "idle"
                },
                new JupyterSessionIdentity("kernel", "kernel"),
                request.Header));
        transport.Receive(
            JupyterTransportChannel.Iopub,
            JupyterMessage.Create(
                "execute_result",
                new JsonObject
                {
                    ["data"] = new JsonObject
                    {
                        ["text/plain"] = "3"
                    },
                    ["metadata"] = new JsonObject(),
                    ["execution_count"] = 1
                },
                new JupyterSessionIdentity("kernel", "kernel"),
                request.Header));
        transport.Receive(
            JupyterTransportChannel.Shell,
            JupyterMessage.Create(
                "execute_reply",
                new JsonObject
                {
                    ["status"] = "ok",
                    ["execution_count"] = 1
                },
                new JupyterSessionIdentity("kernel", "kernel"),
                request.Header));

        var completion = await execution.Completion.WaitAsync(TestContext.Current.CancellationToken);
        await outputReader.WaitAsync(TestContext.Current.CancellationToken);

        completion.Status.Should().Be("ok");
        outputs.Should().ContainSingle(output => output is ExecuteResultOutput);
    }

    [Fact]
    public async Task DisposeFailsPendingRequest()
    {
        var transport = new FakeJupyterTransport();
        var session = new JupyterProtocolSession(transport, new JupyterSessionIdentity("session", "tester"));

        var requestTask = session.GetKernelInfoAsync(TestContext.Current.CancellationToken);
        await session.DisposeAsync();

        await requestTask.Invoking(task => task).Should()
            .ThrowAsync<ObjectDisposedException>();
    }
}