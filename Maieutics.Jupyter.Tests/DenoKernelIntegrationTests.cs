using System.Diagnostics;
using FluentAssertions;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

[Collection(JupyterSocketIntegrationCollection.Name)]
public sealed class DenoKernelIntegrationTests
{
    private static readonly string DenoKernelSpecPath = Path.Combine(
        AppContext.BaseDirectory,
        "TestData",
        "kernels",
        "deno",
        "kernel.json");

    [Fact(Timeout = 30_000)]
    public async Task LocalManagerConnectsToRealDenoKernel()
    {
        using var deadline = CreateDeadline();
        var cancellationToken = deadline.Token;
        var spec = await JupyterKernelSpec.ReadAsync(
            DenoKernelSpecPath,
            cancellationToken);
        await using var manager = await LocalJupyterKernelManager.StartAsync(
            spec,
            cancellationToken: cancellationToken);
        var client = manager.Client;

        var latency = await client.PingAsync(cancellationToken);
        latency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        var kernelInfo = await client.GetKernelInfoAsync(cancellationToken);
        kernelInfo.LanguageInfo.Name.Should().BeOneOf("typescript", "javascript");
        kernelInfo.Status.Should().Be("ok");

        var completion = await client.CompleteAsync(new JupyterCompleteRequest("cons", 4), cancellationToken);
        completion.Status.Should().Be("ok");
        completion.Matches.Should().Contain("console");
        completion.CursorStart.Should().Be(0);
        completion.CursorEnd.Should().Be(4);
        var completeness = await client.IsCompleteAsync(
            new JupyterIsCompleteRequest("if (true) {"),
            cancellationToken);
        completeness.Status.Should().Be("incomplete");
        var inspection = await client.InspectAsync(
            new JupyterInspectRequest("console", 7),
            cancellationToken);
        inspection.Status.Should().Be("ok");

        var execution = await client.ExecuteAsync(
            new JupyterExecuteRequest("1 + 2"),
            cancellationToken);
        var outputs = await ReadOutputsAsync(execution, cancellationToken);
        var executionCompletion = await execution.Completion.WaitAsync(cancellationToken);

        executionCompletion.Reply.Status.Should().Be("ok");
        outputs.OfType<JupyterExecuteResultOutput>()
            .Should().Contain(output => output.Data.Data.Values.Any(value => value.ToString().Contains('3')));
    }

    [Fact(Timeout = 60_000)]
    public async Task LocalManagerLifecycleAndConnectionFileCleanupAreRepeatable()
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(45));
        var runtimeDirectory = Path.Combine(Path.GetTempPath(), $"maieutics-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runtimeDirectory);
        try
        {
            var spec = await JupyterKernelSpec.ReadAsync(DenoKernelSpecPath, deadline.Token);
            for (var iteration = 0; iteration < 3; iteration++)
            {
                await using var manager = await LocalJupyterKernelManager.StartAsync(
                    spec,
                    new LocalJupyterKernelManagerOptions
                    {
                        RuntimeDirectory = runtimeDirectory,
                        StartupTimeout = TimeSpan.FromSeconds(10),
                        ShutdownTimeout = TimeSpan.FromSeconds(5)
                    },
                    deadline.Token);
                await manager.Client.PingAsync(deadline.Token);
                await manager.ShutdownAsync(deadline.Token);
                Directory.GetFiles(runtimeDirectory, "maieutics-kernel-*.json").Should().BeEmpty();
            }
        }
        finally
        {
            Directory.Delete(runtimeDirectory, recursive: true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ShutdownTimeoutKillsUnresponsiveKernelAndDeletesConnectionFile()
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(20));
        var runtimeDirectory = Path.Combine(Path.GetTempPath(), $"maieutics-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runtimeDirectory);
        try
        {
            var spec = await JupyterKernelSpec.ReadAsync(DenoKernelSpecPath, deadline.Token);
            await using var manager = await LocalJupyterKernelManager.StartAsync(
                spec,
                new LocalJupyterKernelManagerOptions
                {
                    RuntimeDirectory = runtimeDirectory,
                    StartupTimeout = TimeSpan.FromSeconds(10),
                    ShutdownTimeout = TimeSpan.FromSeconds(1)
                },
                deadline.Token);
            var forceTimeout = !OperatingSystem.IsWindows();
            var execution = await manager.Client.ExecuteAsync(
                new JupyterExecuteRequest(forceTimeout
                    ? "console.log('stopping'); Deno.kill(Deno.pid, 'SIGSTOP');"
                    : "while (true) {}"),
                deadline.Token);
            await using var outputs = execution.Outputs.GetAsyncEnumerator(deadline.Token);
            while (await outputs.MoveNextAsync())
            {
                if (forceTimeout && outputs.Current is JupyterStdout { Text: var text } &&
                    text.Contains("stopping", StringComparison.Ordinal))
                {
                    break;
                }

                if (!forceTimeout &&
                    outputs.Current is JupyterExecutionStatusChanged { State: JupyterKernelState.Busy })
                {
                    break;
                }
            }

            var elapsed = Stopwatch.StartNew();
            await manager.ShutdownAsync(deadline.Token);
            elapsed.Stop();

            if (forceTimeout)
            {
                elapsed.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(800));
            }

            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
            Directory.GetFiles(runtimeDirectory, "maieutics-kernel-*.json").Should().BeEmpty();
            await execution.Completion.Invoking(task => task).Should().ThrowAsync<Exception>();
        }
        finally
        {
            Directory.Delete(runtimeDirectory, recursive: true);
        }
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

    private static CancellationTokenSource CreateDeadline(TimeSpan? timeout = null)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        return deadline;
    }
}