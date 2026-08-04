using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Control;
using Maieutics.Execution;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

public sealed class ReplControlChannelIntegrationTests
{
    [Fact(Timeout = 120_000)]
    public async Task BootstrapBindingIsUsableFromUserCells()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance);
        var application = await ReplControlTestHost.StartAsync(socketPath, controlHost, timeout.Token);
        await using (application)
        {
            var factory = new LocalDenoReplSessionFactory(
                new DenoReplOptions(),
                controlHost,
                new ReplClientModule());
            var session = new DenoReplSession(
                AgentSessionId.Create(),
                "verify",
                isDefault: false,
                Directory.GetCurrentDirectory(),
                new DenoReplOptions(),
                factory,
                new DenoReplSessionTests.ImmediatePresentationRouter(),
                registry,
                NullLogger<DenoReplSession>.Instance);
            await using (session)
            {
                await session.StartAsync(timeout.Token);
                var result = await session.ExecuteAsync(
                    "await globalThis.maieutics.health()",
                    AgentToolCallId.Create(),
                    timeout.Token);
                result.ExecutionStatus.Should().Be("ok");
                result.Outputs
                    .Where(item => item.Kind == "result" && item.Text is not null)
                    .Select(item => item.Text)
                    .Should().Contain(value => value != null && value.Contains("ok"));
            }
        }
    }

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
        var socketPath = ReplControlHost.CreateSocketPath();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"mc-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);
        var functions = new WorkspaceFunctions(Workspace.Create(workspaceRoot, workspaceRoot)).Functions;
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            functions);
        var application = await ReplControlTestHost.StartAsync(socketPath, controlHost, timeout.Token);
        await using (application)
        {
            var factory = new LocalDenoReplSessionFactory(
                new DenoReplOptions(),
                controlHost,
                new ReplClientModule());
            var manager = await factory.StartAsync(
                Directory.GetCurrentDirectory(),
                "integration-session",
                timeout.Token);
            await using (manager)
            {
                var processId = manager.ProcessId;
                processId.Should().NotBeNull();
                registry.Register(processId.Value, "integration-session");

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

        Directory.Delete(workspaceRoot, recursive: true);
    }

    private const string ReplChildProbeScript = """
                                                if (!Deno.env.get("MAIEUTICS_REPL_IPC")) {
                                                  throw new Error("MAIEUTICS_REPL_IPC is missing");
                                                }
                                                const clientUrl = Deno.env.get("MAIEUTICS_REPL_CLIENT");
                                                if (!clientUrl) throw new Error("MAIEUTICS_REPL_CLIENT is missing");
                                                const maieutics = await import(clientUrl);
                                                const healthText = await maieutics.health();
                                                if (healthText !== "ok") throw new Error(`unexpected health: ${healthText}`);
                                                const listing = await maieutics.tools.invoke("list_directory", {});
                                                if (listing === null || typeof listing !== "object") {
                                                  throw new Error("tool result is not a structured object");
                                                }
                                                await maieutics.comm.open("probe-comm", "test");
                                                await maieutics.comm.msg("probe-comm", { value: 42 });
                                                await maieutics.comm.close("probe-comm");
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
