using FluentAssertions;
using Maieutics.Control;
using Maieutics.Execution;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

public sealed class ReplControlChannelIntegrationTests
{
    [Fact(Timeout = 120_000)]
    public async Task RealDenoChildTalksToItsOwnControlChannel()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows fails explicitly until the named-pipe bootstrap milestone.
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var controlHost = new ReplControlHost(registry, NullLogger<ReplControlHost>.Instance);
        await controlHost.StartAsync(timeout.Token);
        await using (controlHost)
        {
            var factory = new LocalDenoReplSessionFactory(new DenoReplOptions(), controlHost);
            var manager = await factory.StartAsync(Directory.GetCurrentDirectory(), timeout.Token);
            await using (manager)
            {
                manager.ProcessId.Should().NotBeNull();
                registry.Register(manager.ProcessId!.Value, "integration-session");

                var execution = await manager.Client.ExecuteAsync(
                    new JupyterExecuteRequest(ReplChildProbeScript),
                    timeout.Token);
                var outputs = await ReadOutputsAsync(execution, timeout.Token);
                (await execution.Completion.WaitAsync(timeout.Token)).Reply.Status.Should().Be("ok");
                outputs.OfType<JupyterExecuteResultOutput>()
                    .Select(output => output.Data.Data["text/plain"].GetString())
                    .Should().Contain(value => value != null && value.Contains("control-ok"));
            }
        }
    }

    private const string ReplChildProbeScript = """
        const ipc = Deno.env.get("MAIEUTICS_REPL_IPC");
        if (!ipc) throw new Error("MAIEUTICS_REPL_IPC is missing");
        const client = Deno.createHttpClient({ proxy: { transport: "unix", path: ipc } });
        const res = await fetch("http://localhost/health", { client });
        if (!res.ok) throw new Error(`health status ${res.status}`);
        if ((await res.text()) !== "ok") throw new Error("unexpected health body");
        const ws = new WebSocket("ws://localhost/ws", { client });
        await new Promise((resolve, reject) => {
          ws.onopen = () => resolve(undefined);
          ws.onerror = () => reject(new Error("websocket failed to open"));
        });
        const echo = new Promise((resolve, reject) => {
          ws.onmessage = (event) => resolve(String(event.data));
          ws.onerror = () => reject(new Error("websocket error"));
        });
        ws.send("ping");
        const received = await echo;
        if (received !== "ping") throw new Error(`unexpected echo: ${received}`);
        ws.close();
        client.close();
        "control-ok";
        """;

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
