using System.Text.Json;
using FluentAssertions;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

[Collection(JupyterSocketIntegrationCollection.Name)]
public sealed class SelfHostedJupyterIntegrationTests
{
    [Fact]
    public async Task ClientAndKernelCompleteCoreLifecycle()
    {
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var application = new TestKernelApplication();
        await using var host = await JupyterKernelHost.StartAsync(
            connection,
            application,
            cancellationToken: TestContext.Current.CancellationToken);
        await using var client = await JupyterClient.ConnectAsync(
            connection,
            cancellationToken: TestContext.Current.CancellationToken);

        var latency = await client.PingAsync(TestContext.Current.CancellationToken);
        latency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        var info = await client.GetKernelInfoAsync(TestContext.Current.CancellationToken);
        info.Implementation.Should().Be("maieutics-test");

        var sum = await client.ExecuteAsync(
            new JupyterExecuteRequest("sum"),
            TestContext.Current.CancellationToken);
        var sumOutputs = await ReadOutputsAsync(sum, TestContext.Current.CancellationToken);
        (await sum.Completion.WaitAsync(TestContext.Current.CancellationToken)).Reply.Status.Should().Be("ok");
        sumOutputs.Should().ContainSingle(output => output is JupyterExecuteResultOutput);

        var inputExecution = await client.ExecuteAsync(
            new JupyterExecuteRequest("input", AllowStdin: true),
            TestContext.Current.CancellationToken);
        var inputOutputs = new List<JupyterOutput>();
        await foreach (var output in inputExecution.Outputs.WithCancellation(TestContext.Current.CancellationToken))
        {
            inputOutputs.Add(output);
            if (output is JupyterInputRequest input)
            {
                await inputExecution.ReplyInputAsync(input, "Ada", TestContext.Current.CancellationToken);
            }
        }

        (await inputExecution.Completion.WaitAsync(TestContext.Current.CancellationToken)).Reply.Status.Should()
            .Be("ok");
        inputOutputs.OfType<JupyterStdout>().Should().Contain(output => output.Text == "Hello Ada");

        var waiting = await client.ExecuteAsync(
            new JupyterExecuteRequest("wait"),
            TestContext.Current.CancellationToken);
        var waitOutputs = ReadOutputsAsync(waiting, TestContext.Current.CancellationToken);
        await application.WaitStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var busyLatency = await client.PingAsync(TestContext.Current.CancellationToken);
        busyLatency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        await client.InterruptAsync(TestContext.Current.CancellationToken);
        (await waiting.Completion.WaitAsync(TestContext.Current.CancellationToken)).Reply.Status.Should().Be("aborted");
        await waitOutputs.WaitAsync(TestContext.Current.CancellationToken);

        var shutdown = await client.ShutdownAsync(false, TestContext.Current.CancellationToken);
        shutdown.Restart.Should().BeFalse();
        await host.Completion.WaitAsync(TestContext.Current.CancellationToken);
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

    private sealed class TestKernelApplication : IJupyterKernelApplication
    {
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                case "wait":
                    WaitStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    break;
            }

            return JupyterExecuteResult.Ok;
        }
    }
}