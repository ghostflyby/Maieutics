using System.Diagnostics;
using FluentAssertions;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

public sealed class DenoKernelIntegrationTests
{
    private static readonly string DenoKernelSpecPath = Path.Combine(
        AppContext.BaseDirectory,
        "TestData",
        "kernels",
        "deno",
        "kernel.json");

    [Fact]
    public async Task ClientCanConnectToRealDenoKernel()
    {
        var connectionInfo = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-deno-{Guid.NewGuid():N}.json");
        Process? process = null;

        try
        {
            await connectionInfo.WriteFileAsync(connectionFile, TestContext.Current.CancellationToken);
            var spec = await JupyterKernelSpec.ReadAsync(DenoKernelSpecPath, TestContext.Current.CancellationToken);
            process = spec.Start(connectionFile);

            await using var client = new JupyterClient(connectionInfo);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token,
                TestContext.Current.CancellationToken);

            var kernelInfo = await client.GetKernelInfoAsync(cancellation.Token);
            kernelInfo.Message.MessageType.Should().Be("kernel_info_reply");
            kernelInfo.LanguageName
                .Should().BeOneOf("typescript", "javascript");

            var execution = await client.ExecuteAsync(new ExecuteRequest("1 + 2"), cancellation.Token);
            var outputs = new List<KernelOutput>();
            var outputReader = Task.Run(async () =>
            {
                await foreach (var output in execution.Outputs.WithCancellation(cancellation.Token))
                {
                    outputs.Add(output);
                }
            }, cancellation.Token);

            var result = await execution.Completion.WaitAsync(cancellation.Token);
            await outputReader.WaitAsync(cancellation.Token);
            result.Status.Should().Be("ok");
            outputs.OfType<ExecuteResultOutput>()
                .Any(output => output.Data.Data["text/plain"]?.ToString().Contains('3') == true)
                .Should().BeTrue();
        }
        finally
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }

            if (File.Exists(connectionFile))
            {
                File.Delete(connectionFile);
            }
        }
    }
}