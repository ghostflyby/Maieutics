using System.Diagnostics;
using FluentAssertions;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

public sealed class DenoKernelIntegrationTests
{
    private const string DenoKernelSpecPath = "/Users/ghostflyby/Library/Jupyter/kernels/deno/kernel.json";

    [Fact]
    public async Task ClientCanConnectToRealDenoKernel()
    {
        if (!File.Exists(DenoKernelSpecPath))
        {
            return;
        }

        var connectionInfo = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-deno-{Guid.NewGuid():N}.json");
        Process? process = null;

        try
        {
            await connectionInfo.WriteFileAsync(connectionFile);
            var spec = await JupyterKernelSpec.ReadAsync(DenoKernelSpecPath);
            process = spec.Start(connectionFile);

            await using var client = new JupyterClient(connectionInfo);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var kernelInfo = await client.RequestKernelInfoAsync(timeout.Token);
            kernelInfo.MessageType.Should().Be("kernel_info_reply");
            kernelInfo.Content["language_info"]?["name"]?.GetValue<string>()
                .Should().BeOneOf("typescript", "javascript");

            var execute = await client.ExecuteAsync("1 + 2", timeout.Token);
            execute.Status.Should().Be("ok");
            execute.IopubMessages.Any(message =>
                    (message.MessageType == "execute_result" || message.MessageType == "display_data") &&
                    message.Content.ToJsonString().Contains('3', StringComparison.Ordinal))
                .Should().BeTrue();
        }
        finally
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            if (File.Exists(connectionFile))
            {
                File.Delete(connectionFile);
            }
        }
    }
}