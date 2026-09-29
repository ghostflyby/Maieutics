namespace Maieutics.DenoRepl;

/// <summary>
///     Materializes the Deno REPL host and script client into one per-process module root.
/// </summary>
internal sealed class DenoReplModule
{
    /// <summary>The embedded-resource root (shared with PluginHostModule): resource
    /// names are package-relative paths batch-embedded by glob, so the materializer
    /// enumerates the manifest instead of a hand-maintained table and can never drift
    /// from what was embedded.</summary>
    private const string ResourcePrefix = "Deno/";

    private static readonly string[] MaterializedPackages =
    [
        "maieutics-deno-repl",
        "maieutics-plugin-sdk",
        "maieutics-runtime",
        "shared",
        "maieutics-repl-client"
    ];

    private readonly Lazy<MaterializedModules> modules =
        new(Materialize, LazyThreadSafetyMode.ExecutionAndPublication);

    internal string ClientUrl => modules.Value.ClientUrl;

    internal string MainUrl => modules.Value.MainUrl;

    /// <summary>File URL of the REPL <em>process</em> entry (<c>process_main.ts</c>), the child
    /// module the plugin host derives via worker-actor <c>spawnProcess</c> (ADR 0020). It is
    /// materialized beside <c>deno.json</c>, so the host-derived child resolves its
    /// <c>@ghostflyby/worker-actor</c> import through the config discovered upward from the entry
    /// module.</summary>
    internal string ProcessMainUrl => modules.Value.ProcessMainUrl;

    internal string ConfigFile => modules.Value.ConfigFile;

    internal string LockFile => modules.Value.LockFile;

    internal string ModuleDirectory => modules.Value.ModuleDirectory;

    private static MaterializedModules Materialize()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-repl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        foreach (var (resource, relativePath) in MaterializedResources())
            WriteEmbedded(resource, Path.Combine(root, relativePath));

        return new MaterializedModules(
            new Uri(Path.Combine(root, "maieutics-repl-client/mod.ts")).AbsoluteUri,
            new Uri(Path.Combine(root, "maieutics-deno-repl/main.ts")).AbsoluteUri,
            new Uri(Path.Combine(root, "maieutics-deno-repl/process_main.ts")).AbsoluteUri,
            Path.Combine(root, "maieutics-deno-repl/deno.json"),
            Path.Combine(root, "maieutics-deno-repl/deno.lock"),
            root);
    }

    private static IEnumerable<(string Resource, string RelativePath)> MaterializedResources()
    {
        var assembly = typeof(DenoReplModule).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            var relative = name[ResourcePrefix.Length..];
            var package = relative.Contains('/')
                ? relative[..relative.IndexOf('/', StringComparison.Ordinal)]
                : relative;
            if (!MaterializedPackages.Contains(package)) continue;
            yield return (name, relative);
        }
    }

    internal static void WriteEmbedded(string resourceName, string path)
    {
        using var stream = typeof(DenoReplModule).Assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException(
                               $"Missing embedded Deno module '{resourceName}'.");
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();
        Directory.CreateDirectory(
            Path.GetDirectoryName(path) ??
            throw new InvalidOperationException($"Cannot resolve the directory for '{path}'."));
        File.WriteAllText(path, source);
    }

    private sealed record MaterializedModules(
        string ClientUrl,
        string MainUrl,
        string ProcessMainUrl,
        string ConfigFile,
        string LockFile,
        string ModuleDirectory);
}
