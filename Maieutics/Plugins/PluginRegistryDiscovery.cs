using System.Diagnostics;
using System.Text.Json;
using Maieutics.DenoExecution;

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
    public delegate Task<string?> DenoInfoLocalAsync(string url, CancellationToken cancellationToken);

    /// <summary>Attempts to load one `jsr:` import entry as a registry plugin.
    /// Returns null when the entry is not an exact-version jsr package, the
    /// package is absent, or it publishes no maieutics.json (not every registry
    /// dependency is a plugin); diagnostics explain the outcome.</summary>
    public static async Task<PluginDescriptor?> TryLoadJsrAsync(
        string key,
        string value,
        string denoExecutable,
        DenoInfoLocalAsync denoInfoLocal,
        List<string> diagnostics,
        CancellationToken cancellationToken)
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

        Task<string?> DenoLocal(string file) => denoInfoLocal($"https://jsr.io/{packageName}/{version}/{file}", cancellationToken);
        var denoJsonLocal = await DenoLocal("deno.json").ConfigureAwait(false);
        if (denoJsonLocal is null)
        {
            diagnostics.Add($"Registry discovery: '{value}' was not found on the registry.");
            return null;
        }
        var maieuticsLocal = await DenoLocal("maieutics.json").ConfigureAwait(false);
        if (maieuticsLocal is null)
        {
            diagnostics.Add($"Registry discovery: '{value}' publishes no maieutics.json and is not a Maieutics plugin.");
            return null;
        }

        try
        {
            var packageManifest = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(denoJsonLocal, cancellationToken), PluginManifestJsonContext.Default.PluginManifestFile);
            var pluginManifest = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(maieuticsLocal, cancellationToken), PluginManifestJsonContext.Default.MaieuticsManifestFile);
            if (packageManifest is null || pluginManifest is null)
            {
                diagnostics.Add($"Registry discovery: '{value}' has empty manifest files.");
                return null;
            }

            var name = packageManifest.Name ?? packageName;
            var permissions = PluginManifest.ReadPermissions(
                FilterRelativeGrants(packageManifest.Permissions?.Default, name, diagnostics.Add));
            var workers = new List<PluginWorkerDescriptor>();
            foreach (var (entrypoint, entryScript) in EnumerateRegistryEntrypoints(pluginManifest.Entrypoints))
            {
                // Worker entry is the published registry URL: the worker loads it
                // through the native loader (already cached by the install step).
                var entry = entryScript.StartsWith("./", StringComparison.Ordinal) ? entryScript[2..] : entryScript;
                workers.Add(new PluginWorkerDescriptor(
                    entrypoint, $"https://jsr.io/{packageName}/{version}/{entry}"));
            }
            if (workers.Count == 0)
            {
                diagnostics.Add($"Registry discovery: '{value}' declares no entrypoints.");
                return null;
            }

            // Registry-discovered plugins carry no maieutics.json, so they start with
            // no capability grants and no declarative extensions or MCP data file
            // (deny-by-default; ADR 0033).
            return new PluginDescriptor(
                name, name, Path.GetDirectoryName(denoJsonLocal) ?? "/",
                workers, permissions, pluginManifest.Isolation,
                pluginManifest.Dependencies ?? [],
                PluginImportReader.Read(packageManifest.Imports),
                [],
                [],
                [],
                [],
                [],
                [],
                InspectionsContentReadAll: false);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            diagnostics.Add($"Registry discovery: '{value}' manifests are unreadable: {exception.Message}");
            return null;
        }
    }

    /// <summary>Attempts to load one `npm:` import entry as a registry plugin.
    /// npm packages are extracted into browsable directories under the shared npm
    /// cache, so the sibling manifests are read directly from the package root;
    /// the worker entry is a file URL into that directory, and the package root
    /// doubles as the plugin root directory (relative read grants are meaningful
    /// here, unlike the jsr case).</summary>
    public static async Task<PluginDescriptor?> TryLoadNpmAsync(
        string key,
        string value,
        string denoExecutable,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!value.StartsWith("npm:", StringComparison.Ordinal))
        {
            diagnostics.Add($"Registry discovery: import '{key}' uses an unsupported registry kind ('{value}'); only jsr: and npm: packages are discovered.");
            return null;
        }

        var package = await NpmPackageAsync(denoExecutable, value, cancellationToken).ConfigureAwait(false);
        if (package is null)
        {
            diagnostics.Add($"Registry discovery: '{value}' could not be resolved (offline or not found).");
            return null;
        }
        return TryLoadFromPackageDir(key, value, package.LocalPath, diagnostics);
    }

    /// <summary>Shared tail of registry discovery: the package directory (extracted
    /// npm cache directory, or the jsr hash bucket holding deno.json) carries the
    /// sibling manifest pair; entrypoints become file URLs into that directory.</summary>
    public static PluginDescriptor? TryLoadFromPackageDir(
        string key,
        string value,
        string packageDir,
        List<string> diagnostics)
    {
        try
        {
            // npm packages may carry neither deno.json nor maieutics.json: only the
            // plugin declaration is required — identity falls back to the specifier.
            var denoJsonPath = Path.Combine(packageDir, "deno.json");
            var packageManifest = File.Exists(denoJsonPath)
                ? JsonSerializer.Deserialize(
                    File.ReadAllText(denoJsonPath), PluginManifestJsonContext.Default.PluginManifestFile)
                : null;
            var maieuticsPath = Path.Combine(packageDir, "maieutics.json");
            var pluginManifest = File.Exists(maieuticsPath)
                ? JsonSerializer.Deserialize(
                    File.ReadAllText(maieuticsPath), PluginManifestJsonContext.Default.MaieuticsManifestFile)
                : null;
            if (pluginManifest is null)
            {
                diagnostics.Add($"Registry discovery: '{value}' publishes no maieutics.json and is not a Maieutics plugin.");
                return null;
            }

            // Identity fallback for npm packages without deno.json: package.json
            // (the sibling identity file) carries the canonical package name.
            var name = packageManifest?.Name
                       ?? ReadPackageJsonName(packageDir)
                       ?? key;
            var permissions = PluginManifest.ReadPermissions(packageManifest?.Permissions?.Default);
            var workers = new List<PluginWorkerDescriptor>();
            foreach (var (entrypoint, entryScript) in EnumerateRegistryEntrypoints(pluginManifest.Entrypoints))
            {
                var entry = entryScript.StartsWith("./", StringComparison.Ordinal) ? entryScript[2..] : entryScript;
                workers.Add(new PluginWorkerDescriptor(
                    entrypoint, new Uri(Path.Combine(packageDir, entry)).AbsoluteUri));
            }
            if (workers.Count == 0)
            {
                diagnostics.Add($"Registry discovery: '{value}' declares no entrypoints.");
                return null;
            }

            return new PluginDescriptor(
                name, name, packageDir,
                workers, permissions, pluginManifest.Isolation,
                pluginManifest.Dependencies ?? [],
                PluginImportReader.Read(packageManifest?.Imports),
                [],
                [],
                [],
                [],
                [],
                [],
                InspectionsContentReadAll: false);
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


    /// <summary>Reads the npm identity file's package name, when present.</summary>
    private static string? ReadPackageJsonName(string packageDir)
    {
        var packageJsonPath = Path.Combine(packageDir, "package.json");
        if (!File.Exists(packageJsonPath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            return document.RootElement.TryGetProperty("name", out var name) &&
                   name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Toolchain mediation: `deno info --json {url}` fetches the URL into the
    /// shared cache and reports its local path. Name and version mirror the toolchain's
    /// answer; consumers resolve the local directory.</summary>
    // ReSharper disable NotAccessedPositionalProperty.Global
    public sealed record NpmPackageInfo(string Name, string Version, string LocalPath);
    // ReSharper restore NotAccessedPositionalProperty.Global

    /// <summary>`deno info --json npm:spec` resolves ranges/tags and reports the
    /// extracted package directory (browsable) under the shared npm cache.</summary>
    public static async Task<NpmPackageInfo?> NpmPackageAsync(
        string denoExecutable,
        string specifier,
        CancellationToken cancellationToken)
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
        startInfo.ArgumentList.Add(specifier);
        // Discovery runs before the plugin host takes its own slot, so this probe carries
        // its own lease; the suite's process budget would otherwise miss every `deno info`.
        using var slot = await InternalDenoProcessGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var process = Process.Start(startInfo);
        if (process is null) return null;
        var stdout = await ReadOutputAsync(process, TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
        if (stdout is null) return null;

        var document = JsonDocument.Parse(stdout);
        var packages = document.RootElement.GetProperty("npmPackages");
        foreach (var package in packages.EnumerateObject())
        {
            var name = package.Name;
            var version = package.Value.TryGetProperty("version", out var v) ? v.GetString() : null;
            var localPath = package.Value.TryGetProperty("localPath", out var lp) ? lp.GetString() : null;
            if (version is null || localPath is null) continue;
            var at = name.LastIndexOf('@');
            var packageName = at > 0 && name.StartsWith('@') ? name[..at] : name.Split('/').Last().Split('@')[0];
            return new NpmPackageInfo(packageName, version, localPath);
        }
        return null;
    }

    public static async Task<string?> DenoInfoLocalPathAsync(
        string denoExecutable,
        string url,
        CancellationToken cancellationToken)
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
        using var slot = await InternalDenoProcessGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var process = Process.Start(startInfo);
        if (process is null) return null;
        var stdout = await ReadOutputAsync(process, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        if (stdout is null) return null;

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

    /// <summary>Reads the child's stdout within one total budget: the wait is bounded
    /// and asynchronous — a stalled child is killed instead of blocking startup (and
    /// never a pool thread) forever. Both pipes are drained concurrently (a child
    /// blocked on a full pipe never exits), and a child that outlives the budget is
    /// killed instead of being orphaned by the <c>using</c> disposal. Returns null
    /// when the child failed or the budget elapsed; the caller's cancellation
    /// propagates.</summary>
    private static async Task<string?> ReadOutputAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            var stdout = await standardOutput.ConfigureAwait(false);
            if (process.ExitCode != 0) return null;

            return stdout;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The caller canceled, or the budget elapsed: kill a stalled child tree so
            // nothing is orphaned by the using disposal.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception killException) when (
                killException is InvalidOperationException or ObjectDisposedException)
            {
                // The process exited between the check and the kill.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return null;
        }
        finally
        {
            // Both drains are observed whichever way this ends: completed drains make
            // the fault-only continuation a no-op, pending ones get their eventual
            // faults absorbed so nothing surfaces as an unobserved task exception.
            ObserveDrain(standardOutput);
            ObserveDrain(standardError);
        }
    }

    private static void ObserveDrain(Task<string> drain)
    {
        // The drain completes once the child dies (killed or exited); only faults are
        // observed here so they never surface as unobserved task exceptions.
        drain.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }


    /// <summary>Enumerates a registry package manifest's `entrypoints` as
    /// (name, first script) pairs. Registry manifests are only read, never
    /// interpreted as kernel declarations, so string values (data entries, ADR 0033)
    /// are simply not worker sources and are skipped here.</summary>
    private static IEnumerable<(string Name, string EntryScript)> EnumerateRegistryEntrypoints(
        JsonElement? entrypoints)
    {
        if (entrypoints is not { ValueKind: JsonValueKind.Object } section) yield break;
        foreach (var entry in section.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Array) continue;
            var first = entry.Value.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            if (first is not null) yield return (entry.Name, first);
        }
    }
}
