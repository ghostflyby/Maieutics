using System.Text.Json;
using FluentAssertions;
using Maieutics.Control;
using Maieutics.DenoRepl;
using Maieutics.Mcp;
using Maieutics.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

/// <summary>
///     Declarative (manifest-only) extensions: a plugin with zero entrypoints but an
///     `extensions` section contributes to the kernel without spawning any worker. The
///     kernel parses the manifest, exposes the entries as synthetic registrations, and
///     serves discovery straight from the manifest snapshot.
/// </summary>
public sealed class PluginDeclarativeExtensionsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact(Timeout = 60_000)]
    public async Task DeclarativeOnlyPluginsPublishSyntheticRegistrations()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateDeclarativePluginsRoot("declarative");
        var pluginName = Path.GetFileName(root);
        var manager = new PluginHostManager(
            root,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = CreateFakeDenoExecutable() },
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);

            // The synthetic registration exists without any worker ever spawning:
            // the manifest snapshot is the discovery source.
            var registrations = manager.GetRegistrations(PluginExtensionKind.McpDiscover);
            var registration = registrations.Should().ContainSingle().Which;
            registration.PluginId.Should().Be(pluginName);
            registration.ExportName.Should().Be(PluginHostManager.ManifestExportName);

            var discovery = manager.DiscoverManifestMcpAsync(registration);
            discovery.IsSuccess.Should().BeTrue(discovery.Failure);
            discovery.Definitions.Should().ContainSingle().Which.Id.Should()
                .Be($"plugin:{pluginName}::npm:@maieutics/probe-server");
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task UndeclaredCapabilityStyleEntriesAndUnknownKindsStayInert()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateDeclarativePluginsRoot("declarative-unknown", includeUnknownKind: true);
        var pluginName = Path.GetFileName(root);
        var manager = new PluginHostManager(
            root,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = CreateFakeDenoExecutable() },
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);

            // The known kind is registered; the unknown kind produced a diagnostic
            // and no registration of its own.
            var registrations = manager.GetRegistrations(PluginExtensionKind.McpDiscover);
            registrations.Should().ContainSingle().Which.PluginId.Should().Be(pluginName);
            manager.GetRegistrations("FutureKind").Should().BeEmpty();
            var discovery = manager.DiscoverManifestMcpAsync(
                new PluginRegistration(pluginName, PluginHostManager.ManifestExportName, PluginExtensionKind.McpDiscover));
            discovery.IsSuccess.Should().BeTrue();
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task DeclarativeSectionRemovalTakesEffectOnReload()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateDeclarativePluginsRoot("declarative-reload");
        var pluginId = Path.GetFileName(root);
        var manager = new PluginHostManager(
            root,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = CreateFakeDenoExecutable() },
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().ContainSingle();

            // Remove the section from the manifest and apply the reload: the synthetic
            // registration and the contribution must both disappear.
            var applied = CaptureReloadApplied(manager);
            File.WriteAllText(
                Path.Combine(root, "maieutics.json"),
                """
                {
                  "capabilities": ["tools.invoke"]
                }
                """);
            await applied.WaitAsync(deadline.Token);

            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().BeEmpty();
            var discovery = manager.DiscoverManifestMcpAsync(
                new PluginRegistration(pluginId, PluginHostManager.ManifestExportName, PluginExtensionKind.McpDiscover));
            discovery.IsSuccess.Should().BeFalse(discovery.Failure);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task DeclarativeSectionAdditionTakesEffectOnReload()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = Path.Combine(Path.GetTempPath(), $"declarative-add-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/declarative-add",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "capabilities": ["tools.invoke"]
            }
            """);
        var manager = new PluginHostManager(
            root,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = CreateFakeDenoExecutable() },
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            manager.GetRegistrations(PluginExtensionKind.McpDiscover).Should().BeEmpty();

            // Add the section and apply the reload (a workerless plugin never
            // triggers a host registry resend, so the manager republishes itself).
            var applied = CaptureReloadApplied(manager);
            File.WriteAllText(
                Path.Combine(root, "maieutics.json"),
                """
                {
                  "capabilities": ["tools.invoke"],
                  "extensions": {
                    "McpDiscover": [
                      { "module": "npm:@maieutics/probe-server", "transport": { "type": "stdio", "command": "deno" } }
                    ]
                  }
                }
                """);
            await applied.WaitAsync(deadline.Token);

            var registrations = manager.GetRegistrations(PluginExtensionKind.McpDiscover);
            var registration = registrations.Should().ContainSingle().Which;
            registration.PluginId.Should().Be(Path.GetFileName(root));
            var discovery = manager.DiscoverManifestMcpAsync(registration);
            discovery.IsSuccess.Should().BeTrue(discovery.Failure);
            discovery.Definitions.Should().ContainSingle().Which.Id.Should()
                .Be($"plugin:{Path.GetFileName(root)}::npm:@maieutics/probe-server");
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task McpDataFilePluginsPublishSyntheticRegistrations()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        // A pure mcp.json data file (ADR 0033): no manifest extensions section, no
        // entrypoints — the data file alone contributes through the same synthetic
        // registration and discovery path the manifest section uses.
        var root = CreateMcpDataFilePluginsRoot("declarative-data");
        var pluginId = Path.GetFileName(root);
        var manager = CreateManager(root);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);

            var registration = manager.GetRegistrations(PluginExtensionKind.McpDiscover)
                .Should().ContainSingle().Which;
            registration.PluginId.Should().Be(pluginId);
            registration.ExportName.Should().Be(PluginHostManager.ManifestExportName);

            var discovery = manager.DiscoverManifestMcpAsync(registration);
            discovery.IsSuccess.Should().BeTrue(discovery.Failure);
            var definition = discovery.Definitions.Should().ContainSingle().Which;
            definition.Id.Should().Be($"plugin:{pluginId}::probe");
            definition.Transport.Should().BeOfType<StdioMcpTransportDefinition>();
            definition.RootsEnabled.Should().BeTrue();
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task McpDataFileEditsReplaceTheContributionOnReload()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateMcpDataFilePluginsRoot("declarative-data-edit");
        var pluginId = Path.GetFileName(root);
        var manager = CreateManager(root);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            var registration = new PluginRegistration(
                pluginId,
                PluginHostManager.ManifestExportName,
                PluginExtensionKind.McpDiscover);
            var original = manager.DiscoverManifestMcpAsync(registration);
            original.IsSuccess.Should().BeTrue(original.Failure);

            // Rewriting the data file with a different transport is a declarative-only
            // change: the watcher reload re-parses it and the generation key follows.
            var applied = CaptureReloadApplied(manager);
            WriteMcpDataFile(
                root,
                """
                {
                  "mcpServers": {
                    "probe": { "type": "http", "url": "https://example.test/mcp" }
                  }
                }
                """);
            await applied.WaitAsync(deadline.Token);

            var reloaded = manager.DiscoverManifestMcpAsync(registration);
            reloaded.IsSuccess.Should().BeTrue(reloaded.Failure);
            var definition = reloaded.Definitions.Should().ContainSingle().Which;
            definition.Transport.Should().BeOfType<HttpMcpTransportDefinition>();
            definition.GenerationKey.Should().NotBe(original.Definitions[0].GenerationKey);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AnInvalidMcpDataFileEditKeepsThePreviousContribution()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        var root = CreateMcpDataFilePluginsRoot("declarative-data-broken");
        var pluginId = Path.GetFileName(root);
        var manager = CreateManager(root);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            var registration = new PluginRegistration(
                pluginId,
                PluginHostManager.ManifestExportName,
                PluginExtensionKind.McpDiscover);
            var original = manager.DiscoverManifestMcpAsync(registration);
            original.IsSuccess.Should().BeTrue(original.Failure);

            // The broken rewrite must degrade visibly and keep the plugin registered:
            // discovery fails (so the coordinator retains the previous contribution),
            // and repairing the file brings the contribution straight back.
            var applied = CaptureReloadApplied(manager);
            WriteMcpDataFile(root, "{");
            await applied.WaitAsync(deadline.Token);

            manager.GetRegistrations(PluginExtensionKind.McpDiscover)
                .Should().ContainSingle().Which.PluginId.Should().Be(pluginId);
            var broken = manager.DiscoverManifestMcpAsync(registration);
            broken.IsSuccess.Should().BeFalse(broken.Failure);
            broken.Failure.Should().Be("invalid_data_file");

            var repaired = CaptureReloadApplied(manager);
            WriteMcpDataFile(
                root,
                """
                {
                  "mcpServers": {
                    "probe": { "command": "deno", "args": ["info"] }
                  }
                }
                """);
            await repaired.WaitAsync(deadline.Token);
            var recovered = manager.DiscoverManifestMcpAsync(registration);
            recovered.IsSuccess.Should().BeTrue(recovered.Failure);
            recovered.Definitions[0].GenerationKey.Should().Be(original.Definitions[0].GenerationKey);
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>Captures the reload-applied signal so the awaited task observes exactly the
    /// automatic reload triggered by the watched change written right after the capture.</summary>
    private static Task CaptureReloadApplied(PluginHostManager manager)
    {
        return manager.ReloadApplied;
    }

    private static PluginHostManager CreateManager(string root)
    {
        return new PluginHostManager(
            root,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = CreateFakeDenoExecutable() },
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);
    }

    /// <summary>A workerless plugin project that declares MCP servers purely through an
    /// <c>mcp.json</c> data file (ADR 0033).</summary>
    private static string CreateMcpDataFilePluginsRoot(string pluginName)
    {
        var root = Path.Combine(Path.GetTempPath(), pluginName);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/mcp-data",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        // The data file is declared as a string-valued entrypoint (ADR 0033): the name
        // and location are the plugin's choice — here a subdirectory and a custom name.
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "capabilities": ["tools.invoke"],
              "entrypoints": { "mcp": "config/servers.json" }
            }
            """);
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(
            Path.Combine(root, "config", "servers.json"),
            """
            {
              "mcpServers": {
                "probe": { "command": "deno", "args": ["info"] }
              }
            }
            """);
        return root;
    }

    private static void WriteMcpDataFile(string root, string contents)
    {
        File.WriteAllText(Path.Combine(root, "config", "servers.json"), contents);
    }

    private static string CreateDeclarativePluginsRoot(
        string pluginName,
        bool includeUnknownKind = false)
    {
        // The kernel-side plugin id is the plugins-root-relative directory name
        // (the same id the host relay asserts), not the deno.json package name.
        var root = Path.Combine(Path.GetTempPath(), pluginName);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/declarative",
              "version": "0.1.0",
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        var unknownBlock = includeUnknownKind
            ? ",\n    \"FutureKind\": [{ \"opaque\": true }]"
            : string.Empty;
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            $$"""
            {
              "capabilities": ["tools.invoke"],
              "extensions": {
                "McpDiscover": [
                  { "module": "npm:@maieutics/probe-server", "transport": { "type": "stdio", "command": "deno" } }
                ]{{unknownBlock}}
              }
            }
            """);
        // Deliberately no mod.ts and no entrypoints: the plugin is data-only.
        return root;
    }

    private static string CreateFakeDenoExecutable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc-fake-deno-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
