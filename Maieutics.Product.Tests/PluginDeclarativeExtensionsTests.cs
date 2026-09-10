using System.Text.Json;
using FluentAssertions;
using Maieutics.Control;
using Maieutics.DenoRepl;
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
            return; // The fake deno executable is a shell script.

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
            return; // The fake deno executable is a shell script.

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
