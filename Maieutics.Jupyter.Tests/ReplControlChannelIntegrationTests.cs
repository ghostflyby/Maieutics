using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Control;
using Maieutics.DenoExecution;
using Maieutics.DenoRepl;
using Maieutics.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

public sealed class ReplControlChannelIntegrationTests
{
    private static readonly DenoPermissionBroker SharedBroker =
        DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);

    private const string ReplChildProbeScript = """
                                                if (!Deno.env.get("MAIEUTICS_REPL_IPC")) {
                                                  throw new Error("MAIEUTICS_REPL_IPC is missing");
                                                }
                                                const clientUrl = Deno.env.get("MAIEUTICS_REPL_CLIENT");
                                                if (!clientUrl) throw new Error("MAIEUTICS_REPL_CLIENT is missing");
                                                const maieutics = await import(clientUrl);
                                                const healthText = await maieutics.health();
                                                if (healthText !== "ok") throw new Error(`unexpected health: ${healthText}`);
                                                const task = maieutics.tools.start("list_directory", {});
                                                const listing = await task;
                                                if (listing === null || typeof listing !== "object") {
                                                  throw new Error("tool result is not a structured object");
                                                }
                                                await maieutics.comm.open("probe-comm", "test");
                                                await maieutics.comm.msg("probe-comm", { value: 42 });
                                                await maieutics.comm.close("probe-comm");
                                                "control-ok";
                                                """;

    [Fact(Timeout = 120_000)]
    public async Task BootstrapBindingIsUsableFromUserCells()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var credentials = new ReplControlCredentialRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            credentials: credentials);
        await using var evalHost = new ReplEvalWebSocketHost(registry, credentials);
        await using var outputHost = new ReplOutputWebSocketHost(registry, credentials);
        var application = await StartHostAsync(socketPath, controlHost, evalHost, outputHost, timeout.Token);
        await using (application)
        {
            var options = new DenoReplOptions();
            var factory = new LocalDenoReplSessionFactory(
                options,
                controlHost,
                new DenoReplModule(),
                evalHost,
                outputHost,
                registry,
                credentials,
                NullLogger<DenoReplProcess>.Instance,
                SharedBroker);
            var session = new DenoReplSession(
                AgentSessionId.Create(),
                "verify",
                false,
                Directory.GetCurrentDirectory(),
                options,
                factory,
                new DenoReplSessionTests.ImmediatePresentationRouter(),
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
                    .Where(static item => item is { Kind: "result", Value: not null })
                    .Select(static item => item.Value?.GetString())
                    .Should().Contain("ok");
            }
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task RealDenoChildTalksToItsOwnControlChannel()
    {
        if (OperatingSystem.IsWindows())
            // Windows fails explicitly until the named-pipe bootstrap milestone.
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var credentials = new ReplControlCredentialRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"mc-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);
        var functions = new WorkspaceFunctions(Workspace.Create(workspaceRoot, workspaceRoot)).Functions;
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            functions,
            credentials: credentials);
        await using var evalHost = new ReplEvalWebSocketHost(registry, credentials);
        await using var outputHost = new ReplOutputWebSocketHost(registry, credentials);
        var application = await StartHostAsync(socketPath, controlHost, evalHost, outputHost, timeout.Token);
        await using (application)
        {
            var options = new DenoReplOptions();
            var factory = new LocalDenoReplSessionFactory(
                options,
                controlHost,
                new DenoReplModule(),
                evalHost,
                outputHost,
                registry,
                credentials,
                NullLogger<DenoReplProcess>.Instance,
                SharedBroker);
            var generation = await factory.StartAsync(
                Directory.GetCurrentDirectory(),
                "integration-session",
                1,
                timeout.Token);
            await using (generation)
            {
                var execution = await generation.Connection.ExecuteAsync(ReplChildProbeScript, timeout.Token);
                await foreach (var _ in execution.Events.WithCancellation(timeout.Token))
                {
                }
                var terminal = await execution.Completion.WaitAsync(timeout.Token);
                terminal.Should().BeOfType<ReplEvalResultTerminal>().Which.Value?.GetString()
                    .Should().Contain("control-ok");
            }
        }

        Directory.Delete(workspaceRoot, true);
    }

    [Fact(Timeout = 120_000)]
    public async Task RealDenoChildPromptInputRoundTrips()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var credentials = new ReplControlCredentialRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"mc-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            credentials: credentials);
        await using var evalHost = new ReplEvalWebSocketHost(registry, credentials);
        await using var outputHost = new ReplOutputWebSocketHost(registry, credentials);
        var application = await StartHostAsync(socketPath, controlHost, evalHost, outputHost, timeout.Token);
        await using (application)
        {
            var options = new DenoReplOptions();
            var factory = new LocalDenoReplSessionFactory(
                options,
                controlHost,
                new DenoReplModule(),
                evalHost,
                outputHost,
                registry,
                credentials,
                NullLogger<DenoReplProcess>.Instance,
                SharedBroker);
            var generation = await factory.StartAsync(
                Directory.GetCurrentDirectory(),
                "input-session",
                1,
                timeout.Token);
            await using (generation)
            {
                var execution = await generation.Connection.ExecuteAsync(
                    "const name = prompt('Name: '); console.error('shared-stderr'); name;",
                    timeout.Token);
                var received = new List<string>();
                await foreach (var item in execution.Events.WithCancellation(timeout.Token))
                {
                    received.Add(item.GetType().Name);
                    if (item is ReplEvalInputRequestEvent input)
                    {
                        input.Prompt.Should().Be("Name: ");
                        await generation.Connection.ReplyInputAsync(input, "Ada", timeout.Token);
                    }
                }

                received.Should().Equal(
                    nameof(ReplEvalInputRequestEvent));
                var terminal = await execution.Completion.WaitAsync(timeout.Token);
                terminal.Should().BeOfType<ReplEvalResultTerminal>().Which.Value?.GetString()
                    .Should().Be("Ada");
            }
        }

        Directory.Delete(workspaceRoot, true);
    }

    private static async Task<WebApplication> StartHostAsync(
        string socketPath,
        ReplControlHost controlHost,
        ReplEvalWebSocketHost evalHost,
        ReplOutputWebSocketHost outputHost,
        CancellationToken cancellationToken)
    {
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "maieutics-repl-integration-test"
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MinRequestBodyDataRate = null;
            options.Limits.MinResponseDataRate = null;
            options.ListenUnixSocket(socketPath, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });
        var application = builder.Build();
        evalHost.MapEndpoint(application);
        // The REPL child now opens the dedicated binary output endpoint as part of its startup;
        // the test kernel must serve it or the child fails to start.
        outputHost.MapEndpoint(application);
        controlHost.MapEndpoints(application);
        await application.StartAsync(cancellationToken);
        return application;
    }
}
