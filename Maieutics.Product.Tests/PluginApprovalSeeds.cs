using Maieutics.Plugins;

namespace Maieutics.Product.Tests;

/// <summary>
///     Test seeding for the plugin approval gate (ADR 0037): writes the real approvals
///     file — production fingerprints, production records, production load path — so tests
///     exercise the gate without any production bypass hook. An unseeded manager fails
///     closed exactly like a fresh install.
/// </summary>
internal static class PluginApprovalSeeds
{
    /// <summary>Seeds approvals for the plugins-root project itself plus every local
    /// file-import target that loads — the same local set the manager's scan discovers.
    /// Returns the approvals file path to pass to the manager.</summary>
    public static string SeedLocalPlugins(string pluginsRoot)
    {
        var store = NewStore(out var path);
        ApproveDirectory(store, pluginsRoot);
        foreach (var importTarget in PluginManifest.ReadLocalImportTargets(pluginsRoot))
        {
            var directory = Path.GetDirectoryName(importTarget);
            if (directory is not null && Directory.Exists(directory)) ApproveDirectory(store, directory);
        }

        return path;
    }

    /// <summary>Seeds approvals for explicit plugin directories (for example a nested
    /// plugin the root project imports, or a registry-installed package directory).</summary>
    public static string SeedPlugins(params string[] pluginDirectories)
    {
        var store = NewStore(out var path);
        foreach (var directory in pluginDirectories) ApproveDirectory(store, directory);
        return path;
    }

    private static PluginApprovalStore NewStore(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"mc-plugin-approvals-{Guid.NewGuid():N}.json");
        return PluginApprovalStore.Load(path, out _);
    }

    private static void ApproveDirectory(PluginApprovalStore store, string directory)
    {
        if (!PluginManifest.TryLoad(directory, out var descriptor, out _)) return;
        store.Set(
            descriptor.Id,
            new PluginApprovalRecord(
                PluginDeclarationFingerprint.Compute(descriptor),
                descriptor.Name,
                DateTimeOffset.UtcNow,
                descriptor.Permissions));
    }
}
