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

    [Fact]
    public async Task LocalManagerConnectsToRealDenoKernel()
    {
        var spec = await JupyterKernelSpec.ReadAsync(
            DenoKernelSpecPath,
            TestContext.Current.CancellationToken);
        await using var manager = await LocalJupyterKernelManager.StartAsync(
            spec,
            cancellationToken: TestContext.Current.CancellationToken);
        var client = manager.Client;

        var latency = await client.PingAsync(TestContext.Current.CancellationToken);
        latency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        var kernelInfo = await client.GetKernelInfoAsync(TestContext.Current.CancellationToken);
        kernelInfo.LanguageInfo.Name.Should().BeOneOf("typescript", "javascript");

        var execution = await client.ExecuteAsync(
            new JupyterExecuteRequest("1 + 2"),
            TestContext.Current.CancellationToken);
        var outputs = await ReadOutputsAsync(execution, TestContext.Current.CancellationToken);
        var completion = await execution.Completion.WaitAsync(TestContext.Current.CancellationToken);

        completion.Reply.Status.Should().Be("ok");
        outputs.OfType<JupyterExecuteResultOutput>()
            .Should().Contain(output => output.Data.Data.Values.Any(value => value.ToString().Contains('3')));
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