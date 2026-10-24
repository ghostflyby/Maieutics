using System.Text;
using System.Text.Json;
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
using Microsoft.Extensions.Time.Testing;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
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
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
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
            Assert.Equal(PluginHostState.NotStarted, manager.GetStatus().State);
            var readiness = manager.WaitUntilReadyAsync(canceledWait.Token);
            await canceledWait.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await readiness; });
            Assert.Equal(PluginHostState.NotStarted, manager.GetStatus().State);

            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            // The host always starts, even with zero plugins: the plugins root is
            // a resident empty deno project and built-in functionality is planned
            // to ship as plugins. ControlConnected is not asserted because the
            // host's control-channel attach races the status read.
            var status = manager.GetStatus();
            Assert.Equal(PluginHostState.Ready, status.State);
            Assert.Equal(0, status.PluginCount);
            Assert.Equal(0, status.RegistrationCount);
            Assert.True(status.HostProcessRequired);
            await Task.WhenAll(
                manager.StopAsync(deadline.Token),
                manager.DisposeAsync().AsTask());
            await manager.DisposeAsync();
            Assert.Equal(PluginHostState.Stopped, manager.GetStatus().State);
        }
        finally
        {
            await manager.DisposeAsync();
            await application.DisposeAsync();
            await TempArea.DeleteAsync(pluginsRoot, CancellationToken.None);
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
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions(),
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);

        try
        {
            Assert.False(Directory.Exists(pluginsRoot));
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);

            // The skeleton is an empty deno project: deno.json + maieutics.json
            // with no entrypoints, so the root project carries zero workers.
            Assert.True(Directory.Exists(pluginsRoot));
            Assert.True(File.Exists(Path.Combine(pluginsRoot, "deno.json")));
            Assert.True(File.Exists(Path.Combine(pluginsRoot, "maieutics.json")));
            Assert.Equal(1, manager.GetStatus().PluginCount); // the skeleton project itself
            Assert.Equal(0, manager.GetStatus().RegistrationCount);
            Assert.True(manager.GetStatus().HostProcessRequired);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(pluginsRoot)) await TempArea.DeleteAsync(pluginsRoot, CancellationToken.None);
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
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = $"missing-deno-{Guid.NewGuid():N}" },
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);

        try
        {
            var startupFailure = await Assert.ThrowsAnyAsync<Exception>(async () => { await manager.StartAsync(deadline.Token); });
            var readinessFailure = await Assert.ThrowsAnyAsync<Exception>(async () => { await manager.WaitUntilReadyAsync(deadline.Token); });

            Assert.Same(startupFailure, readinessFailure);
            Assert.Equal(PluginHostState.Failed, manager.GetStatus().State);
        }
        finally
        {
            await manager.DisposeAsync();
            await TempArea.DeleteAsync(pluginsRoot, CancellationToken.None);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task DiscoversAndInvokesExtensionPointsInARealDenoHost()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The plugin host harness attaches over a Unix-socket control channel.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var modules = new PluginHostModule();
        var pluginsRoot = CreatePluginsRoot("integration");
        var manager = new PluginHostManager(
            pluginsRoot,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            socketPath,
            new DenoReplOptions { Executable = "deno" },
            modules,
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            pluginApprovalsPath: PluginApprovalSeeds.SeedLocalPlugins(pluginsRoot));
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
            Assert.NotEmpty(registrations);

            var outcome = await manager.InvokeExtensionPointAsync(
                registrations[0].PluginId,
                registrations[0].ExportName,
                ReplExtensionPointName.McpDiscover,
                null,
                timeout.Token);
            Assert.False(outcome.IsError, outcome.Message);
            Assert.NotNull(outcome.Value);

            var discovery = JsonSerializer.Deserialize<McpDiscoveryTestShape[]>(
                outcome.Value is { } discoveryValue
                    ? discoveryValue.GetRawText()
                    : throw new InvalidOperationException("Expected a discovery result."),
                JsonSerializerOptionsCaseInsensitive);
            Assert.NotNull(discovery);
            Assert.Single(discovery);
            Assert.Equal("npm:@maieutics/probe-server", discovery[0].Module);
        }
    }

    // Regression gate (docs/plugin-import-resolution.md §5.1): every import form a
    // plugin can declare — the SDK via links only, a bare alias through the merged
    // process import map, and a direct registry specifier — must reach a ready
    // worker whose aliased dependency executes. The blocker that once skipped this
    // suite was the worker's denied import grant, not the merge.
    [Theory(Timeout = 45_000)]
    [InlineData("sdk-only")]
    [InlineData("sdk-alias")]
    [InlineData("sdk-direct-jsr")]
    public async Task WorkerReadinessAcrossImportForms(string variant)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The plugin host harness attaches over a Unix-socket control channel.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var registry = new ReplControlSessionRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var modules = new PluginHostModule();
        var pluginsRoot = CreateAliasedPluginsRoot(variant);
        var manager = new PluginHostManager(
            pluginsRoot,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            socketPath,
            new DenoReplOptions { Executable = "deno" },
            modules,
            registry,
            new DebugConsoleLogger<PluginHostManager>(),
            new DebugConsoleLoggerFactory(),
            TimeProvider.System,
            pluginApprovalsPath: PluginApprovalSeeds.SeedLocalPlugins(pluginsRoot));
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
            // The merged entry is present in every variant: only the worker's import
            // form differs, so the variants isolate the failing resolution path.
            Assert.Contains("\"@std/bytes\"", File.ReadAllText(modules.ConfigFile));

            var registrations = await WaitForRegistrationsAsync(
                manager,
                ReplExtensionPointName.McpDiscover,
                timeout.Token);
            Assert.NotEmpty(registrations);

            var outcome = await manager.InvokeExtensionPointAsync(
                registrations[0].PluginId,
                registrations[0].ExportName,
                ReplExtensionPointName.McpDiscover,
                null,
                timeout.Token);
            Assert.False(outcome.IsError, outcome.Message);
            if (variant != "sdk-only")
                Assert.Contains("1,2,3,4", outcome.Value!.Value.GetRawText());
        }
        Console.WriteLine($"[readiness] {variant}: READY");
    }

    [Fact(Timeout = 120_000)]
    public async Task RejectsToolCallsThroughThePreInvokeHookChain()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The plugin host harness attaches over a Unix-socket control channel.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var credentials = new ReplControlCredentialRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var modules = new PluginHostModule();
        var pluginsRoot = CreatePluginsRoot("rejecting");
        var pluginsApprovals = PluginApprovalSeeds.SeedLocalPlugins(pluginsRoot);
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"mc-hook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);
        var functions = new WorkspaceFunctions(Workspace.Create(workspaceRoot, workspaceRoot)).Functions;
        var manager = new PluginHostManager(
            pluginsRoot,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            socketPath,
            new DenoReplOptions { Executable = "deno" },
            modules,
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            pluginApprovalsPath: pluginsApprovals);
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
            Assert.NotEmpty(registrations);

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
                SharedBroker,
                TestPermissionPolicies.Unconfigured());
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
                Assert.Equal("ok", result.ExecutionStatus);
                Assert.Contains(result.Outputs
                    .Where(static item => item is { Kind: "result", Value: not null })
                    .Select(static item => item.Value?.GetRawText()), value => value != null && value.Contains("denied_by_hook"));
            }
        }

        await TempArea.DeleteAsync(workspaceRoot, CancellationToken.None);
    }

    [Fact(Timeout = 120_000)]
    public async Task PluginHookCallsBackIntoTheKernelThroughTheCapabilityApi()
    {
        // The full two-sided cooperation loop: a real plugin worker's ToolPreInvoke
        // hook requests the tools.invoke capability through the SDK; the host relays
        // it over the control bus with the plugin identity derived from its worker
        // mapping; the kernel gates it (catalog + manifest grants), executes the real
        // script tool, and returns the result. The hook observes the answer by
        // replacing the outer tool's arguments — so the outer result proves whether
        // the callback succeeded.
        if (OperatingSystem.IsWindows())
            Assert.Skip("The plugin host harness attaches over a Unix-socket control channel.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var credentials = new ReplControlCredentialRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var modules = new PluginHostModule();
        var pluginsRoot = CreateCapabilityPluginsRoot();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"mc-capability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);
        var functions = new WorkspaceFunctions(Workspace.Create(workspaceRoot, workspaceRoot)).Functions;
        var manager = new PluginHostManager(
            pluginsRoot,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            socketPath,
            new DenoReplOptions { Executable = "deno" },
            modules,
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            pluginApprovalsPath: PluginApprovalSeeds.SeedLocalPlugins(pluginsRoot));
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            functions,
            manager);
        manager.CapabilityExecutor = (tool, arguments, token) => controlHost.InvokeScriptToolAsync(tool, arguments, token);
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
            Assert.NotEmpty(registrations);

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
                SharedBroker,
                TestPermissionPolicies.Unconfigured());
            var session = new DenoReplSession(
                AgentSessionId.Create(),
                "capability-session",
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
                    "await maieutics.tools.invoke('list_directory', { path: 'missing-without-capability-callback' })",
                    AgentToolCallId.Create(),
                    timeout.Token);
                var resultValues = result.Outputs
                    .Where(static item => item is { Kind: "result", Value: not null })
                    .Select(static item => item.Value?.GetRawText())
                    .Where(static text => text is not null)
                    .ToArray();
                _ = string.Join("\n", result.Outputs.Select(static item =>
                        $"{item.Kind}:{(item.Value is { } value ? value.GetRawText() : item.Text)}")) +
                    $"\nstatus={result.ExecutionStatus}";
                // The hook replaced the bogus outer path with the real root listing only
                // if the capability callback executed the kernel tool end to end; a failed
                // callback leaves the bogus path in place and the tool errors on it.
                Assert.Equal("ok", result.ExecutionStatus);
                Assert.Contains(resultValues, text => text!.Contains("workspace://local/") && text.Contains("entries"));
                Assert.DoesNotContain(resultValues, text => text!.Contains("missing-without-capability-callback"));
            }
        }

        await TempArea.DeleteAsync(workspaceRoot, CancellationToken.None);
    }

    private static string CreateCapabilityPluginsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-plugins-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/capability-e2e",
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
              "entrypoints": { "worker": { "main": ["./mod.ts"] } },
              "capabilities": ["tools.invoke"]
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "mod.ts"),
            """
            import { capabilities, defineExtensionPoint } from "jsr:@maieutics/plugin-sdk@^0.1";
            export const pre = defineExtensionPoint("ToolPreInvoke", {
              handler: async () => {
                try {
                  const result = await capabilities.invokeTool("list_directory", {});
                  if (result.status === "ok") {
                    // Replace the bogus outer path with the real root listing: the
                    // outer result only turns "ok" when the callback executed.
                    return { action: "replace", arguments: {} };
                  }
                  return { action: "reject", error: { code: "cap_status", message: JSON.stringify(result) } };
                } catch (error) {
                  return { action: "reject", error: { code: "cap_threw", message: String(error) } };
                }
              },
            });
            """);
        return root;
    }

    [Fact(Timeout = 60_000)]
    public async Task DeclarativeOnlyPluginsContributeWithoutSpawningWorkers()
    {
        // A real plugin host process, a real control bus — but a plugin that is pure
        // manifest data: no entrypoint, no worker, just a declarative mcp.discover
        // section. The kernel publishes its synthetic registration into the same
        // registry the host's own worker registrations land in.
        if (OperatingSystem.IsWindows())
            Assert.Skip("The plugin host harness attaches over a Unix-socket control channel.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var registry = new ReplControlSessionRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var modules = new PluginHostModule();
        var root = Path.Combine(Path.GetTempPath(), $"mc-decl-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/declarative-e2e",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "extensions": {
                "McpDiscover": [
                  { "module": "npm:@maieutics/probe-server", "transport": { "type": "stdio", "command": "deno" } }
                ]
              }
            }
            """);
        var manager = new PluginHostManager(
            root,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            socketPath,
            new DenoReplOptions { Executable = "deno" },
            modules,
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            pluginApprovalsPath: PluginApprovalSeeds.SeedLocalPlugins(root));
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            pluginHosts: manager);
        var application = await ReplControlTestHost.StartAsync(socketPath, controlHost, timeout.Token);

        try
        {
            await manager.StartAsync(timeout.Token);
            await manager.WaitUntilReadyAsync(timeout.Token);

            var registration = Assert.Single(await WaitForRegistrationsAsync(
                manager,
                ReplExtensionPointName.McpDiscover,
                timeout.Token));
            Assert.Equal(Path.GetFileName(root), registration.PluginId);
            Assert.Equal(PluginHostManager.ManifestExportName, registration.ExportName);

            var discovery = manager.DiscoverManifestMcpAsync(registration);
            Assert.True(discovery.IsSuccess, discovery.Failure);
            Assert.Equal($"plugin:{Path.GetFileName(root)}::npm:@maieutics/probe-server", Assert.Single(discovery.Definitions).Id);

            // No worker was ever spawned for a data-only plugin.
            Assert.Equal(1, manager.GetStatus().PluginCount);
        }
        finally
        {
            await manager.DisposeAsync();
            await application.DisposeAsync();
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task McpDataFilePluginsContributeWithoutSpawningWorkers()
    {
        // A real plugin host process with a pure mcp.json data file (ADR 0033): the
        // synthetic registration rides the same registry the host's worker
        // registrations land in, and no worker is ever spawned for the data-only plugin.
        if (OperatingSystem.IsWindows())
            Assert.Skip("The plugin host harness attaches over a Unix-socket control channel.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var registry = new ReplControlSessionRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var modules = new PluginHostModule();
        var root = Path.Combine(Path.GetTempPath(), $"mc-data-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/mcp-data-e2e",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "capabilities": ["tools.invoke"],
              "entrypoints": { "mcp": "mcp.json" }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "mcp.json"),
            """
            {
              "mcpServers": {
                "probe": { "command": "deno", "args": ["info"] }
              }
            }
            """);
        var manager = new PluginHostManager(
            root,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            socketPath,
            new DenoReplOptions { Executable = "deno" },
            modules,
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            pluginApprovalsPath: PluginApprovalSeeds.SeedLocalPlugins(root));
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            pluginHosts: manager);
        var application = await ReplControlTestHost.StartAsync(socketPath, controlHost, timeout.Token);

        try
        {
            await manager.StartAsync(timeout.Token);
            await manager.WaitUntilReadyAsync(timeout.Token);

            var registration = Assert.Single(await WaitForRegistrationsAsync(
                manager,
                ReplExtensionPointName.McpDiscover,
                timeout.Token));
            Assert.Equal(Path.GetFileName(root), registration.PluginId);
            Assert.Equal(PluginHostManager.ManifestExportName, registration.ExportName);

            var discovery = manager.DiscoverManifestMcpAsync(registration);
            Assert.True(discovery.IsSuccess, discovery.Failure);
            Assert.Equal($"plugin:{Path.GetFileName(root)}::probe", Assert.Single(discovery.Definitions).Id);

            Assert.Equal(1, manager.GetStatus().PluginCount);
        }
        finally
        {
            await manager.DisposeAsync();
            await application.DisposeAsync();
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
        }
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

    [Fact(Timeout = 120_000)]
    public async Task CrossPluginActorCallsResolveThroughDeclaredDependencies()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The plugin host harness attaches over a Unix-socket control channel.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var modules = new PluginHostModule();
        var pluginsRoot = CreateTwoPluginRoot();
        var manager = new PluginHostManager(
            pluginsRoot,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            socketPath,
            new DenoReplOptions { Executable = "deno" },
            modules,
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            pluginApprovalsPath: PluginApprovalSeeds.SeedLocalPlugins(pluginsRoot));
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
            Assert.NotEmpty(registrations);

            // The consumer's handler crosses workers: dep.math.double(21) executes
            // in the sub plugin's worker through the load-hook stub redirect.
            var outcome = await manager.InvokeExtensionPointAsync(
                registrations[0].PluginId,
                registrations[0].ExportName,
                ReplExtensionPointName.McpDiscover,
                null,
                timeout.Token);
            Assert.False(outcome.IsError, outcome.Message);
            Assert.Contains("42", outcome.Value!.Value.GetRawText());
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task RevokingAnApprovedPluginStopsItsWorkersOnTheLiveHost()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The plugin host harness attaches over a Unix-socket control channel.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var pluginsRoot = CreateRootWithoutExtraPlugin();
        var manager = new PluginHostManager(
            pluginsRoot,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            socketPath,
            new DenoReplOptions { Executable = "deno" },
            new PluginHostModule(),
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            pluginApprovalsPath: PluginApprovalSeeds.SeedLocalPlugins(pluginsRoot));
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
            var rootPluginId = (await WaitForRegistrationsAsync(
                    manager,
                    ReplExtensionPointName.McpDiscover,
                    timeout.Token))
                .Single().PluginId;

            // Revocation drives the stop form of plugin.reload end to end (ADR 0037):
            // the live host closes the worker, its post-stop registry frame withdraws
            // the registration, and the kernel classifies the plugin pending.
            await manager.RevokeAsync(rootPluginId, timeout.Token);
            await WaitForRegistryConditionAsync(
                manager,
                ReplExtensionPointName.McpDiscover,
                registrations => registrations.Count == 0,
                timeout.Token);
            Assert.Empty(manager.GetRegistrations(ReplExtensionPointName.McpDiscover));
            Assert.Equal(1, manager.GetStatus().PendingApprovals);

            // Re-approval upserts the worker back without a host-process restart. The
            // approve snapshot's registration copy derives from the STALE pre-revoke
            // hostRegistrations (the revoke path filters the derived list, not the
            // source), so the pre-frame state is non-empty but not the truth: the
            // re-activated worker's registration is only real once its post-boot
            // registry frame replaces the set (frame handler clears and re-adds from
            // the live host payload). Waiting on the frame — the completion
            // notification for the worker's actual registration — closes the window
            // where the frame's transient clear could race a bare state assert.
            await manager.ApproveAsync(rootPluginId, timeout.Token);
            await WaitForRegistryConditionAfterFrameAsync(
                manager,
                ReplExtensionPointName.McpDiscover,
                registrations => registrations.Count > 0,
                timeout.Token);
            Assert.NotEmpty(manager.GetRegistrations(ReplExtensionPointName.McpDiscover));
            Assert.Equal(0, manager.GetStatus().PendingApprovals);
        }

        if (Directory.Exists(pluginsRoot)) await TempArea.DeleteAsync(pluginsRoot, CancellationToken.None);
    }

    [Fact(Timeout = 60_000)]
    public async Task ReloadWarnsWhenThePluginImportMapChanges()
    {
        var logger = new CollectingLogger<PluginHostManager>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(55));
        var pluginsRoot = CreateAliasedPluginsRoot("sdk-alias");
        var clock = new FakeTimeProvider();
        var manager = new PluginHostManager(
            pluginsRoot,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = "deno" },
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            logger,
            logger,
            clock,
            pluginApprovalsPath: PluginApprovalSeeds.SeedLocalPlugins(pluginsRoot));

        try
        {
            await manager.StartAsync(timeout.Token);
            await manager.WaitUntilReadyAsync(timeout.Token);

            // Change the plugin's import map; the process map is fixed at host start. The
            // watched change's debounce rides the fake clock: advance past it (and any
            // re-armed burst event) until the reload applies, then read the warned state.
            var applied = manager.ReloadApplied;
            var denoJsonPath = Path.Combine(pluginsRoot, "deno.json");
            var updated = File.ReadAllText(denoJsonPath).Replace(
                "\"imports\":",
                "\"imports\": { \"@std/bytes\": \"jsr:@std/bytes@1\", \"@std/path\": \"jsr:@std/path@^1\" },\n    \"imports-old\":");
            File.WriteAllText(denoJsonPath, updated);
            await PluginWatcherTestWaits.AwaitReloadAppliedByAdvancingAsync(
                manager, clock, applied, timeout.Token);

            Assert.True(logger.Lines.Any(line => line.Contains("Restart the host process")), "the reload must warn that the host process restart is required");
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(pluginsRoot)) await TempArea.DeleteAsync(pluginsRoot, CancellationToken.None);
        }
    }

    [Fact(Timeout = 150_000)]
    public async Task WatchedSetChangesRecomputeThePluginSetThroughAHostRestart()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The plugin host harness attaches over a Unix-socket control channel.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(140));
        var registry = new ReplControlSessionRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var pluginsRoot = CreateRootWithoutExtraPlugin();
        var manager = new PluginHostManager(
            pluginsRoot,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            socketPath,
            new DenoReplOptions { Executable = "deno" },
            new PluginHostModule(),
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            pluginApprovalsPath: PluginApprovalSeeds.SeedLocalPlugins(pluginsRoot));
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
            // The root project is the only plugin at first; its id is the root
            // directory name (the temp directory's unique suffix).
            await WaitForRegistrationsAsync(
                manager,
                ReplExtensionPointName.McpDiscover,
                timeout.Token);
            var rootPluginId = manager.GetRegistrations(ReplExtensionPointName.McpDiscover).Single().PluginId;
            Assert.StartsWith("mc-plugins-root-", rootPluginId);

            // Adding a sibling plugin plus its local import entry changes the plugin set:
            // the new module entry belongs to the process import map, so the manager must
            // restart the host instead of attempting an in-process reload. The new plugin
            // arrives unapproved (ADR 0037): the restart discovers it, and approving it
            // activates its registration without another restart.
            WriteExtraPlugin(pluginsRoot);
            AddLocalImport(pluginsRoot, "@acme/extra/main", "./extra/mod.ts");
            await WaitForApprovalAsync(manager, "extra", timeout.Token);
            await manager.ApproveAsync("extra", timeout.Token);

            // Both plugins must be live before asserting: `extra` alone can show up while the
            // restart's fallback generation is still tearing the registry down (its approval
            // upsert lands undelivered and triggers another generation), which is not the
            // convergence this phase asserts.
            await WaitForRegistryConditionAsync(
                manager,
                ReplExtensionPointName.McpDiscover,
                registrations =>
                    registrations.Any(registration => registration.PluginId == rootPluginId) &&
                    registrations.Any(registration => registration.PluginId == "extra"),
                timeout.Token);
            Assert.Contains(manager.GetRegistrations(ReplExtensionPointName.McpDiscover), registration => registration.PluginId == rootPluginId);
            Assert.Contains(manager.GetRegistrations(ReplExtensionPointName.McpDiscover), registration => registration.PluginId == "extra");

            // Removing the plugin (directory and import entry) recomputes the set the
            // other way: the removed plugin's registrations disappear after the restart.
            // The wait targets the NEW generation's convergence (root re-registered,
            // extra gone): the teardown phase clears every registration, so a negative
            // condition alone would "pass" mid-restart with the root still missing.
            await TempArea.DeleteAsync(Path.Combine(pluginsRoot, "extra"), timeout.Token);
            RemoveLocalImport(pluginsRoot, "@acme/extra/main");

            await WaitForRegistryConditionAsync(
                manager,
                ReplExtensionPointName.McpDiscover,
                registrations =>
                    registrations.Any(registration => registration.PluginId == rootPluginId) &&
                    registrations.All(registration => registration.PluginId != "extra"),
                timeout.Token);
            Assert.Contains(manager.GetRegistrations(ReplExtensionPointName.McpDiscover), registration => registration.PluginId == rootPluginId);
        }

        if (Directory.Exists(pluginsRoot)) await TempArea.DeleteAsync(pluginsRoot, CancellationToken.None);
    }

    [Fact(Timeout = 150_000)]
    public async Task DisposeDuringASetChangeRestartStopsCleanlyWithoutResurrection()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The plugin host harness attaches over a Unix-socket control channel.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(140));
        var registry = new ReplControlSessionRegistry();
        var socketPath = ReplControlHost.CreateSocketPath();
        var pluginsRoot = CreateRootWithoutExtraPlugin();
        var manager = new PluginHostManager(
            pluginsRoot,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            socketPath,
            new DenoReplOptions { Executable = "deno" },
            new PluginHostModule(),
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            pluginApprovalsPath: PluginApprovalSeeds.SeedLocalPlugins(pluginsRoot));
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            pluginHosts: manager);
        var application = await ReplControlTestHost.StartAsync(socketPath, controlHost, timeout.Token);
        await manager.StartAsync(timeout.Token);
        await using (application)
        {
            // Trigger a set change and wait until the restart has actually torn the old
            // generation down and installed its successor (the channel swap is reported at
            // that point, with readiness still pending), then dispose mid-restart. Dispose
            // must complete deterministically: the aborted restart's StartAsync faults
            // readiness and cleans up, and the manager ends terminal Stopped with nothing
            // resurrected.
            var restarted = manager.RegistryChangesReplaced;
            WriteExtraPlugin(pluginsRoot);
            AddLocalImport(pluginsRoot, "@acme/extra/main", "./extra/mod.ts");
            await restarted.WaitAsync(timeout.Token);

            await manager.DisposeAsync();
            // The terminal state depends on where the race landed (a startup cancelled by the
            // stop reports Canceled, a faulted one Failed); what it must never be is a live
            // generation again.
            Assert.Contains(manager.GetStatus().State, new[] { PluginHostState.Stopped, PluginHostState.Canceled, PluginHostState.Failed });
        }

        if (Directory.Exists(pluginsRoot)) await TempArea.DeleteAsync(pluginsRoot, CancellationToken.None);
    }

    /// <summary>
    ///     Waits until the plugin host's live registration set for <paramref name="extensionPoint"/>
    ///     satisfies <paramref name="condition"/>, waking on the manager's registry frames instead
    ///     of polling: every channel frame is the complete registration set written when the host
    ///     publishes a registry payload. Only the live <c>GetRegistrations</c> set decides — a
    ///     satisfying frame of a superseded generation must not pass, because the restart that
    ///     supersedes it (e.g. the fallback restart when an approval upsert cannot be delivered)
    ///     wipes the live registry right after. A restart also swaps the channel itself, so each
    ///     pass re-captures it and additionally wakes on <c>RegistryChangesReplaced</c> rather
    ///     than blocking on frames the superseded channel will never produce.
    /// </summary>
    private static async Task WaitForRegistryConditionAsync(
        PluginHostManager manager,
        string extensionPoint,
        Func<IReadOnlyList<PluginRegistration>, bool> condition,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (condition(manager.GetRegistrations(extensionPoint))) return;

            var channel = manager.RegistryChanges;
            Task replaced = manager.RegistryChangesReplaced;
            Task winner = await Task.WhenAny(
                channel.Reader.WaitToReadAsync(cancellationToken).AsTask(),
                replaced.WaitAsync(cancellationToken)).ConfigureAwait(false);
            if (winner == replaced) continue;

            // A frame was pending or arrived: drain it and re-check the live set next pass.
            while (channel.Reader.TryRead(out _)) { }
        }
    }

    /// <summary>Waits for the condition to hold AFTER at least one registry frame has
    /// been observed — for transitions whose approve snapshot pre-populates the
    /// registration set from the stale pre-transition host payload. The bare state
    /// check would return on that stale copy; the frame that replaces it is the
    /// completion notification for the transition the test is waiting on.</summary>
    private static async Task WaitForRegistryConditionAfterFrameAsync(
        PluginHostManager manager,
        string extensionPoint,
        Func<IReadOnlyList<PluginRegistration>, bool> condition,
        CancellationToken cancellationToken)
    {
        // Consume one frame unconditionally: the transition's snapshot may already
        // satisfy the condition, but the frame that follows can transiently clear the
        // set while the live host reports the transition's actual outcome.
        while (true)
        {
            var channel = manager.RegistryChanges;
            Task replaced = manager.RegistryChangesReplaced;
            Task winner = await Task.WhenAny(
                channel.Reader.WaitToReadAsync(cancellationToken).AsTask(),
                replaced.WaitAsync(cancellationToken)).ConfigureAwait(false);
            if (winner == replaced) continue;

            while (channel.Reader.TryRead(out _)) { }
            if (condition(manager.GetRegistrations(extensionPoint))) return;
        }
    }

    /// <summary>Waits for an approval classification to appear after a plugin-set change,
    /// waking on the manager's approval-recompute signal instead of polling. Approval state
    /// never writes a registry frame, so this signal is the only completion edge; a missing
    /// classification surfaces as a timeout here instead of an unrelated later assertion.</summary>
    private static async Task WaitForApprovalAsync(
        PluginHostManager manager,
        string pluginId,
        CancellationToken cancellationToken)
    {
        while (!manager.ListPluginApprovals().Any(approval => approval.PluginId == pluginId))
        {
            var changed = manager.ApprovalsChanged;
            await changed.WaitAsync(TimeSpan.FromSeconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string CreateRootWithoutExtraPlugin()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-plugins-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/root-plugin",
              "version": "0.1.0",
              "exports": { "./main": "./mod.ts" },
              "permissions": { "default": { "read": ["./"] } },
              "imports": {}
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "isolation": "auto",
              "entrypoints": { "worker": { "main": ["./mod.ts"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "mod.ts"),
            """
            import { defineExtensionPoint } from "jsr:@maieutics/plugin-sdk@^0.1";
            export const discover = defineExtensionPoint("McpDiscover", {
              handler: () => [{ module: "npm:@maieutics/probe-server", transport: { type: "stdio", command: "deno" } }],
            });
            """);
        return root;
    }

    private static void WriteExtraPlugin(string pluginsRoot)
    {
        var extra = Path.Combine(pluginsRoot, "extra");
        Directory.CreateDirectory(extra);
        File.WriteAllText(
            Path.Combine(extra, "deno.json"),
            """
            {
              "name": "@acme/extra",
              "version": "0.1.0",
              "exports": { "./main": "./mod.ts" },
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(extra, "maieutics.json"),
            """
            {
              "isolation": "auto",
              "entrypoints": { "worker": { "main": ["./mod.ts"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(extra, "mod.ts"),
            """
            import { defineExtensionPoint } from "jsr:@maieutics/plugin-sdk@^0.1";
            export const discover = defineExtensionPoint("McpDiscover", {
              handler: () => [{ module: "npm:@maieutics/probe-server", transport: { type: "stdio", command: "deno" } }],
            });
            """);
    }

    private static void AddLocalImport(string pluginsRoot, string key, string target)
    {
        var path = Path.Combine(pluginsRoot, "deno.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var imports = new StringBuilder("{");
        var first = true;
        foreach (var property in document.RootElement.GetProperty("imports").EnumerateObject())
        {
            if (!first) imports.Append(',');
            first = false;
            imports.Append(JsonSerializer.Serialize(property.Name)).Append(':')
                .Append(JsonSerializer.Serialize(property.Value.GetString()));
        }

        if (!first) imports.Append(',');
        imports.Append(JsonSerializer.Serialize(key)).Append(':')
            .Append(JsonSerializer.Serialize(target));
        imports.Append('}');

        // Plain concatenation: no raw-string interpolation to mis-escape.
        var json = "{\n" +
                   "  \"name\": \"@maieutics/root-plugin\",\n" +
                   "  \"version\": \"0.1.0\",\n" +
                   "  \"exports\": { \"./main\": \"./mod.ts\" },\n" +
                   "  \"permissions\": { \"default\": { \"read\": [\"./\"] } },\n" +
                   "  \"imports\": " + imports + "\n" +
                   "}";
        File.WriteAllText(path, json);
    }

    private static void RemoveLocalImport(string pluginsRoot, string key)
    {
        var path = Path.Combine(pluginsRoot, "deno.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var imports = new StringBuilder("{");
        var first = true;
        foreach (var property in document.RootElement.GetProperty("imports").EnumerateObject())
        {
            if (property.Name == key) continue;
            if (!first) imports.Append(',');
            first = false;
            imports.Append(JsonSerializer.Serialize(property.Name)).Append(':')
                .Append(JsonSerializer.Serialize(property.Value.GetString()));
        }

        imports.Append('}');

        var json = "{\n" +
                   "  \"name\": \"@maieutics/root-plugin\",\n" +
                   "  \"version\": \"0.1.0\",\n" +
                   "  \"exports\": { \"./main\": \"./mod.ts\" },\n" +
                   "  \"permissions\": { \"default\": { \"read\": [\"./\"] } },\n" +
                   "  \"imports\": " + imports + "\n" +
                   "}";
        File.WriteAllText(path, json);
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
              "entrypoints": { "worker": { "main": ["./mod.ts"] } }
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

    /// <summary>Same layout as CreatePluginsRoot plus a deno.json alias import
    /// whose runtime resolution the merged process import map must provide
    /// (docs/plugin-import-resolution.md §8). The <paramref name="variant"/> selects
    /// the worker's dependency import form; the declared deno.json imports (and thus
    /// the merged process map) are identical in every variant. concat takes a single
    /// iterable: current @std/bytes@1 dropped the variadic overload.</summary>
    private static string CreateAliasedPluginsRoot(string variant)
    {
        // deno.json imports are identical in every variant: the process import map is
        // constant, and only the worker's import FORM differs (§5.1 variants).
        var importLine = variant switch
        {
            "sdk-alias" => "import { concat } from \"@std/bytes/concat\";",
            "sdk-direct-jsr" => "import { concat } from \"jsr:@std/bytes@1/concat\";",
            _ => "",
        };
        var sdkImport = "import { defineExtensionPoint } from \"jsr:@maieutics/plugin-sdk@^0.1\";";
        var moduleBody = variant == "sdk-only"
            ? $"{sdkImport}\nexport const discover = defineExtensionPoint(\"McpDiscover\", {{\n  handler: () => [{{ module: \"npm:@maieutics/probe-server\", transport: {{ type: \"stdio\", command: \"deno\" }} }}],\n}});"
            : $"{sdkImport}\n{importLine}\nexport const discover = defineExtensionPoint(\"McpDiscover\", {{\n  handler: () => [{{ module: \"npm:@maieutics/probe-server\", transport: {{ type: \"stdio\", command: \"deno\", args: [String(concat([new Uint8Array([1, 2]), new Uint8Array([3, 4])]))] }} }}],\n}});";

        var root = Path.Combine(Path.GetTempPath(), $"mc-plugins-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/aliased",
              "version": "0.1.0",
              "exports": { "./main": "./mod.ts" },
              "permissions": { "default": { "read": ["./"] } },
              "imports": { "@std/bytes": "jsr:@std/bytes@1" }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "isolation": "auto",
              "entrypoints": { "worker": { "main": ["./mod.ts"] } }
            }
            """);
        File.WriteAllText(Path.Combine(root, "mod.ts"), moduleBody + "\n");
        return root;
    }

    /// <summary>Two-package layout: the root project consumes a sub-package plugin
    /// through a declared dependency and a bare-alias actor import.</summary>
    private static string CreateTwoPluginRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-plugins-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/composite",
              "version": "0.1.0",
              "exports": { "./main": "./mod.ts" },
              "permissions": { "default": { "read": ["./"] } },
              "imports": { "@acme/sub/main": "./sub/mod.ts" }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "isolation": "auto",
              "dependencies": ["sub"],
              "entrypoints": { "worker": { "main": ["./mod.ts"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "mod.ts"),
            """
            import { defineExtensionPoint } from "jsr:@maieutics/plugin-sdk@^0.1";
            import dep from "@acme/sub/main";
            export const discover = defineExtensionPoint("McpDiscover", {
              handler: async () => {
                const doubled = await dep.math.double(21);
                return [{ module: "npm:@maieutics/probe-server", transport: { type: "stdio", command: "deno", args: [String(doubled)] } }];
              },
            });
            """);
        File.WriteAllText(
            Path.Combine(root, "sub", "deno.json"),
            """
            {
              "name": "@acme/sub",
              "version": "0.1.0",
              "exports": { "./main": "./mod.ts" },
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "sub", "maieutics.json"),
            """
            {
              "isolation": "auto",
              "entrypoints": { "worker": { "main": ["./mod.ts"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "sub", "mod.ts"),
            """
            import { defineActor } from "jsr:@maieutics/plugin-sdk@^0.1";
            export const math = defineActor({ double(n: number): number { return n * 2; } });
            """);
        return root;
    }

    private sealed class CollectingLogger<T> : ILogger<T>, ILoggerFactory
    {
        private readonly object gate = new();
        private readonly List<string> lines = [];
        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (gate) return [.. lines];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            lock (gate) lines.Add($"{logLevel}: {line}");
        }
    }

    private sealed class DebugConsoleLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine($"[host:{logLevel}] {line}");
            if (exception is not null) Console.WriteLine(exception);
        }
    }

    private sealed class DebugConsoleLoggerFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new DebugConsoleLogger<object>();
        public void Dispose() { }
    }


    private sealed record McpDiscoveryTestShape(string Module, object Transport);
}
