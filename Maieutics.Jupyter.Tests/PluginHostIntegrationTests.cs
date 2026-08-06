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

    [Fact(Timeout = 120_000)]
    public async Task DiscoversAndInvokesExtensionPointsInARealDenoHost()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var modules = new PluginHostModule();
        var pluginsRoot = CreatePluginsRoot("integration");
        var managerTcs = new TaskCompletionSource<PluginHostManager>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            pluginHosts: managerTcs.Task);
        var application = await ReplControlTestHost.StartAsync(socketPath, controlHost, timeout.Token);
        var manager = await PluginHostManager.CreateAsync(
            pluginsRoot,
            socketPath,
            new DenoReplOptions { Executable = "deno" },
            modules,
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);
        managerTcs.TrySetResult(manager);
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
        var managerTcs = new TaskCompletionSource<PluginHostManager>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            functions,
            managerTcs.Task);
        var application = await ReplControlTestHost.StartAsync(socketPath, controlHost, timeout.Token);
        var manager = await PluginHostManager.CreateAsync(
            pluginsRoot,
            socketPath,
            new DenoReplOptions { Executable = "deno" },
            modules,
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);
        managerTcs.TrySetResult(manager);
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

    private static async Task<IReadOnlyList<PluginRegistration>> WaitForRegistrationsAsync(
        PluginHostManager manager,
        string extensionPoint,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var registrations = manager.GetRegistrations(extensionPoint);
            if (registrations.Count > 0) return registrations;

            await Task.Delay(250, cancellationToken);
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