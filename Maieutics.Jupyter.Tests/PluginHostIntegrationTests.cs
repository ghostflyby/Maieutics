using System.Text.Json;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Control;
using Maieutics.Execution;
using Maieutics.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

public sealed class PluginHostIntegrationTests
{
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
            manager.GetStatus().State.Should().Be(PluginHostState.NotStarted);
            var readiness = manager.WaitUntilReadyAsync(canceledWait.Token);
            canceledWait.Cancel();
            await readiness
                .Invoking(static task => task)
                .Should().ThrowAsync<OperationCanceledException>();
            manager.GetStatus().State.Should().Be(PluginHostState.NotStarted);

            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            manager.GetStatus().Should().BeEquivalentTo(new PluginHostStatus(
                PluginHostState.Ready,
                0,
                0,
                false,
                false));
            await Task.WhenAll(
                manager.StopAsync(deadline.Token),
                manager.DisposeAsync().AsTask());
            await manager.DisposeAsync();
            manager.GetStatus().State.Should().Be(PluginHostState.Stopped);
        }
        finally
        {
            await manager.DisposeAsync();
            Directory.Delete(pluginsRoot, true);
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

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
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

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
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
        var application = await ReplControlTestHost.StartAsync(socketPath, controlHost, timeout.Token);
        await manager.StartAsync(timeout.Token);
        await using (application)
        await using (manager)
        {
            var registrations = await WaitForRegistrationsAsync(
                manager,
                ReplExtensionPointName.ToolPreInvoke,
                timeout.Token);
            registrations.Should().NotBeEmpty();

            var factory = new LocalDenoReplSessionFactory(
                new DenoReplOptions(),
                controlHost,
                new ReplClientModule());
            var session = new DenoReplSession(
                AgentSessionId.Create(),
                "hook-session",
                false,
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
                    "await maieutics.tools.invoke('list_directory', {}).catch((error) => ({ rejected: String(error) }))",
                    AgentToolCallId.Create(),
                    timeout.Token);
                result.ExecutionStatus.Should().Be("ok");
                result.Outputs
                    .Where(item => item is { Kind: "result", Text: not null })
                    .Select(item => item.Text)
                    .Should().Contain(value => value != null && value.Contains("denied_by_hook"));
            }
        }

        Directory.Delete(workspaceRoot, true);
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
        var root = Path.Combine(Path.GetTempPath(), $"mc-plugins-root-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, pluginName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "deno.json"),
            $$"""
              {
                "name": "@maieutics/{{pluginName}}",
                "version": "0.1.0",
                "exports": { "./main": "./mod.ts" },
                "permissions": { "default": { "read": ["./"] } },
                "maieutics": { "isolation": "auto" }
              }
              """);
        File.WriteAllText(
            Path.Combine(directory, "mod.ts"),
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
