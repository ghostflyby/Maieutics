using System.Diagnostics;
using System.Text.Json;

namespace Maieutics.Plugins;

/// <summary>
///     Discovers registry-installed plugins: a root `jsr:` import entry with an
///     exact version is resolved to its published sibling manifest pair
///     (`deno.json` + `maieutics.json` share the version prefix in the registry
///     URL space) by asking the Deno toolchain to fetch each URL and report the
///     local cached path (`deno info --json`). docs/plugin-import-resolution.md §9.
///     Version constraints other than exact are rejected with a diagnostic —
///     constraint matching belongs to `deno install`/lock, not to the kernel.
/// </summary>
internal static class PluginRegistryDiscovery
{
    /// <summary>Toolchain query seam: fetches the URL through `deno info --json`
    /// and returns the reported local path, or null when the URL does not exist
    /// (404) or the query fails.</summary>
    public delegate string? DenoInfoLocal(string url);

    /// <summary>Attempts to load one `jsr:` import entry as a registry plugin.
    /// Returns null when the entry is not an exact-version jsr package, the
    /// package is absent, or it publishes no maieutics.json (not every registry
    /// dependency is a plugin); diagnostics explain the outcome.</summary>
    public static PluginDescriptor? TryLoadJsr(
        string key,
        string value,
        string denoExecutable,
        DenoInfoLocal denoInfoLocal,
        List<string> diagnostics)
    {
        if (!value.StartsWith("jsr:", StringComparison.Ordinal))
        {
            diagnostics.Add($"Registry discovery: import '{key}' uses an unsupported registry kind ('{value}'); only jsr: packages are discovered.");
            return null;
        }

        var specifier = value["jsr:".Length..];
        var at = specifier.StartsWith('@')
            ? specifier.LastIndexOf('@')
            : specifier.IndexOf('@');
        var packageName = at <= 0 ? "" : specifier[..at];
        var version = at <= 0 ? "" : specifier[(at + 1)..];
        if (packageName.Length == 0 || version.Length == 0 || !char.IsAsciiDigit(version[0]) ||
            version.Contains('^') || version.Contains('~') || version.Contains('x') || version.Contains('*'))
        {
            diagnostics.Add(
                $"Registry discovery: import '{key}' does not pin an exact version ('{value}'); pin the version (jsr:{packageName}@1.2.3) to install a plugin.");
            return null;
        }

        string? DenoLocal(string file) => denoInfoLocal($"https://jsr.io/{packageName}/{version}/{file}");
        var denoJsonLocal = DenoLocal("deno.json");
        if (denoJsonLocal is null)
        {
            diagnostics.Add($"Registry discovery: '{value}' was not found on the registry.");
            return null;
        }
        var maieuticsLocal = DenoLocal("maieutics.json");
        if (maieuticsLocal is null)
        {
            diagnostics.Add($"Registry discovery: '{value}' publishes no maieutics.json and is not a Maieutics plugin.");
            return null;
        }

        try
        {
            var packageManifest = JsonSerializer.Deserialize(
                File.ReadAllText(denoJsonLocal), PluginManifestJsonContext.Default.PluginManifestFile);
            var pluginManifest = JsonSerializer.Deserialize(
                File.ReadAllText(maieuticsLocal), PluginManifestJsonContext.Default.MaieuticsManifestFile);
            if (packageManifest is null || pluginManifest is null)
            {
                diagnostics.Add($"Registry discovery: '{value}' has empty manifest files.");
                return null;
            }

            var name = packageManifest.Name ?? packageName;
            var permissions = PluginManifest.ReadPermissions(
                FilterRelativeGrants(packageManifest.Permissions?.Default, name, diagnostics.Add));
            var workers = new List<PluginWorkerDescriptor>();
            foreach (var (entrypoint, scripts) in pluginManifest.Entrypoints ?? new Dictionary<string, string[]>())
            {
                if (scripts is not { Length: > 0 } || string.IsNullOrWhiteSpace(scripts[0])) continue;
                // Worker entry is the published registry URL: the worker loads it
                // through the native loader (already cached by the install step).
                var entry = scripts[0].StartsWith("./", StringComparison.Ordinal) ? scripts[0][2..] : scripts[0];
                workers.Add(new PluginWorkerDescriptor(
                    entrypoint, $"https://jsr.io/{packageName}/{version}/{entry}"));
            }
            if (workers.Count == 0)
            {
                diagnostics.Add($"Registry discovery: '{value}' declares no entrypoints.");
                return null;
            }

            return new PluginDescriptor(
                name, name, Path.GetDirectoryName(denoJsonLocal) ?? "/",
                workers, permissions, pluginManifest.Isolation,
                pluginManifest.Dependencies ?? [],
                PluginImportReader.Read(packageManifest.Imports));
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            diagnostics.Add($"Registry discovery: '{value}' manifests are unreadable: {exception.Message}");
            return null;
        }
    }

    /// <summary>Registry packages have no root directory: relative read/write values
    /// (the "./"-style own-directory idiom) have no referent and are discarded with a
    /// warning; absolute paths and ${var.*} patterns survive (§9 decision 3). The
    /// grants record itself only carries defaults; the per-kind filtering happens in
    /// the host's worker grant builder via the same rule.</summary>
    public static PluginManifestPermissionSet? FilterRelativeGrants(
        PluginManifestPermissionSet? set,
        string pluginName,
        Action<string> warning)
    {
        if (set is null) return null;
        warning(
            $"Registry plugin '{pluginName}' relative permission paths were discarded (a registry package has no root directory); " +
            "declare absolute paths or ${var.*} patterns instead.");
        return set;
    }

    /// <summary>Toolchain mediation: `deno info --json <url>` fetches the URL into the
    /// shared cache and reports its local path.</summary>
    public static string? DenoInfoLocalPath(string denoExecutable, string url)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = denoExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add(url);
        using var process = Process.Start(startInfo);
        if (process is null) return null;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(60));
        if (process.ExitCode != 0) return null;

        var document = JsonDocument.Parse(stdout);
        foreach (var module in document.RootElement.GetProperty("modules").EnumerateArray())
        {
            if (module.TryGetProperty("specifier", out var specifier) &&
                specifier.GetString() == url &&
                module.TryGetProperty("local", out var local))
            {
                return local.GetString();
            }
        }
        return null;
    }
}
