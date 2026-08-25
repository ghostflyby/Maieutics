using System.Text.Json;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Control;
using Maieutics.DenoExecution;
using Maieutics.DenoRepl;
using Maieutics.Execution;
using Maieutics.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

public sealed class PluginHostIntegrationTests
{
    private static readonly DenoPermissionBroker SharedBroker =
        DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);

    private static readonly JsonSerializerOptions JsonSerializerOptionsCaseInsensitive =
        new() { PropertyNameCaseInsensitive = true };

    [Fact(Timeout = 30_000)]
    public async Task ReadinessWaitCancellationDoesNotCancelTheManagerAndShutdownIsIdempotent()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var canceledWait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var pluginsRoot = Path.Combine(Path.GetTempPath(), $"mc-empty-plugins-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pluginsRoot);
        var registry = new ReplControlSessionRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var manager = new PluginHostManager(
            pluginsRoot,
            socketPath,
            new DenoReplOptions(),
            new PluginHostModule(),
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            pluginHosts: manager);
        var application = await ReplControlTestHost.StartAsync(socketPath, controlHost, deadline.Token);

        try
        {
            manager.GetStatus().State.Should().Be(PluginHostState.NotStarted);
            var readiness = manager.WaitUntilReadyAsync(canceledWait.Token);
            canceledWait.Cancel();
            await readiness
                .Invoking(static task => task)
                .Should().ThrowAsync<OperationCanceledException>();
            manager.GetStatus().State.Should().Be(PluginHostState.NotStarted);

            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            // The host always starts, even with zero plugins: the plugins root is
            // a resident empty deno project and built-in functionality is planned
            // to ship as plugins. ControlConnected is not asserted because the
            // host's control-channel attach races the status read.
            var status = manager.GetStatus();
            status.State.Should().Be(PluginHostState.Ready);
            status.PluginCount.Should().Be(0);
            status.RegistrationCount.Should().Be(0);
            status.HostProcessRequired.Should().BeTrue();
            await Task.WhenAll(
                manager.StopAsync(deadline.Token),
                manager.DisposeAsync().AsTask());
            await manager.DisposeAsync();
            manager.GetStatus().State.Should().Be(PluginHostState.Stopped);
        }
        finally
        {
            await manager.DisposeAsync();
            await application.DisposeAsync();
            Directory.Delete(pluginsRoot, true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task StartCreatesTheEmptyDenoProjectSkeletonAndRunsTheHost()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var pluginsRoot = Path.Combine(Path.GetTempPath(), $"mc-scaffold-{Guid.NewGuid():N}");
        var manager = new PluginHostManager(
            pluginsRoot,
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions(),
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);

        try
        {
            Directory.Exists(pluginsRoot).Should().BeFalse();
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);

            // The skeleton is an empty deno project: deno.json + maieutics.json
            // with no entrypoints, so the root project carries zero workers.
            Directory.Exists(pluginsRoot).Should().BeTrue();
            File.Exists(Path.Combine(pluginsRoot, "deno.json")).Should().BeTrue();
            File.Exists(Path.Combine(pluginsRoot, "maieutics.json")).Should().BeTrue();
            manager.GetStatus().PluginCount.Should().Be(1); // the skeleton project itself
            manager.GetStatus().RegistrationCount.Should().Be(0);
            manager.GetStatus().HostProcessRequired.Should().BeTrue();
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(pluginsRoot)) Directory.Delete(pluginsRoot, true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task StartupFailureIsPublishedToReadinessAndCleansUp()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var pluginsRoot = CreatePluginsRoot("integration");
        var manager = new PluginHostManager(
            pluginsRoot,
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = $"missing-deno-{Guid.NewGuid():N}" },
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);

        try
        {
            var startupFailure = (await manager.StartAsync(deadline.Token)
                .Invoking(static task => task)
                .Should().ThrowAsync<Exception>()).Which;
            var readinessFailure = (await manager.WaitUntilReadyAsync(deadline.Token)
                .Invoking(static task => task)
                .Should().ThrowAsync<Exception>()).Which;

            readinessFailure.Should().BeSameAs(startupFailure);
            manager.GetStatus().State.Should().Be(PluginHostState.Failed);
        }
        finally
        {
            await manager.DisposeAsync();
            Directory.Delete(pluginsRoot, true);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task DiscoversAndInvokesExtensionPointsInARealDenoHost()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var modules = new PluginHostModule();
        var pluginsRoot = CreatePluginsRoot("integration");
        var manager = new PluginHostManager(
            pluginsRoot,
            socketPath,
            new DenoReplOptions { Executable = "deno" },
            modules,
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            pluginHosts: manager);
        var application = await ReplControlTestHost.StartAsync(socketPath, controlHost, timeout.Token);
        await manager.StartAsync(timeout.Token);
        await using (application)
        await using (manager)
        {
            var registrations = await WaitForRegistrationsAsync(
                manager,
                ReplExtensionPointName.McpDiscover,
                timeout.Token);
            registrations.Should().NotBeEmpty();

            var outcome = await manager.InvokeExtensionPointAsync(
                registrations[0].PluginId,
                registrations[0].ExportName,
                ReplExtensionPointName.McpDiscover,
                null,
                timeout.Token);
            outcome.IsError.Should().BeFalse(outcome.Message);
            outcome.Value.Should().NotBeNull();

            var discovery = JsonSerializer.Deserialize<McpDiscoveryTestShape[]>(
                outcome.Value is { } discoveryValue
                    ? discoveryValue.GetRawText()
                    : throw new InvalidOperationException("Expected a discovery result."),
                JsonSerializerOptionsCaseInsensitive);
            discovery.Should().HaveCount(1);
            discovery[0].Module.Should().Be("npm:@maieutics/probe-server");
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task RejectsToolCallsThroughThePreInvokeHookChain()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var credentials = new ReplControlCredentialRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var modules = new PluginHostModule();
        var pluginsRoot = CreatePluginsRoot("rejecting");
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"mc-hook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);
        var functions = new WorkspaceFunctions(Workspace.Create(workspaceRoot, workspaceRoot)).Functions;
        var manager = new PluginHostManager(
            pluginsRoot,
            socketPath,
            new DenoReplOptions { Executable = "deno" },
            modules,
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            functions,
            manager);
        await using var evalHost = new ReplEvalWebSocketHost(registry, credentials);
        await using var outputHost = new ReplOutputWebSocketHost(registry, credentials);
        var application = await StartHostAsync(socketPath, controlHost, evalHost, outputHost, timeout.Token);
        await manager.StartAsync(timeout.Token);
        await using (application)
        await using (manager)
        {
            var registrations = await WaitForRegistrationsAsync(
                manager,
                ReplExtensionPointName.ToolPreInvoke,
                timeout.Token);
            registrations.Should().NotBeEmpty();

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
                "hook-session",
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
                    "await maieutics.tools.invoke('list_directory', {}).catch((error) => ({ rejected: String(error) }))",
                    AgentToolCallId.Create(),
                    timeout.Token);
                result.ExecutionStatus.Should().Be("ok");
                result.Outputs
                    .Where(static item => item is { Kind: "result", Value: not null })
                    .Select(static item => item.Value?.GetRawText())
                    .Should().Contain(value => value != null && value.Contains("denied_by_hook"));
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
            ApplicationName = "maieutics-plugin-repl-integration-test"
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
        outputHost.MapEndpoint(application);
        controlHost.MapEndpoints(application);
        await application.StartAsync(cancellationToken);
        return application;
    }

    /// <summary>
    ///     Waits for a plugin registration to appear. Relies on registry snapshots being additive
    ///     (the plugin host only publishes registration sets that grow monotonically), so a
    ///     registration present in a dropped channel snapshot is still present in every later one.
    /// </summary>
    private static async Task<IReadOnlyList<PluginRegistration>> WaitForRegistrationsAsync(
        PluginHostManager manager,
        string extensionPoint,
        CancellationToken cancellationToken)
    {
        if (manager.GetRegistrations(extensionPoint) is { Count: > 0 } existing) return existing;

        await foreach (var registrations in manager.RegistryChanges.Reader.ReadAllAsync(cancellationToken))
        {
            var matches = registrations
                .Where(registration => registration.ExtensionPoint == extensionPoint)
                .ToArray();
            if (matches.Length > 0) return matches;
        }

        return [];
    }

    private static string CreatePluginsRoot(string pluginName)
    {
        // The plugins root IS the plugin project: deno.json + maieutics.json +
        // mod.ts sit directly at the root.
        var root = Path.Combine(Path.GetTempPath(), $"mc-plugins-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            $$"""
              {
                "name": "@maieutics/{{pluginName}}",
                "version": "0.1.0",
                "exports": { "./main": "./mod.ts" },
                "permissions": { "default": { "read": ["./"] } }
              }
              """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "isolation": "auto",
              "entrypoints": { "main": ["./mod.ts"] }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "mod.ts"),
            pluginName == "rejecting"
                ? """
                  import { defineExtensionPoint } from "jsr:@maieutics/plugin-sdk@^0.1";
                  export const pre = defineExtensionPoint("ToolPreInvoke", {
                    handler: () => ({ action: "reject", error: { code: "denied_by_hook", message: "the hook denied this call" } }),
                  });
                  """
                : """
                  import { defineExtensionPoint } from "jsr:@maieutics/plugin-sdk@^0.1";
                  export const discover = defineExtensionPoint("McpDiscover", {
                    handler: () => [{ module: "npm:@maieutics/probe-server", transport: { type: "stdio", command: "deno" } }],
                  });
                  """);
        return root;
    }

    private sealed record McpDiscoveryTestShape(string Module, object Transport);
}
