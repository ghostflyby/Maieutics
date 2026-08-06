namespace Maieutics.Control;

/// <summary>
///     Materializes the embedded Deno REPL client module to a per-process temp file and exposes its
///     file URL for injection through the REPL child environment.
/// </summary>
internal sealed class ReplClientModule
{
    private static readonly (string Resource, string RelativePath)[] Entries =
    [
        ("Maieutics.Deno.ReplClient.ts", "maieutics-repl-client/mod.ts"),
        ("Maieutics.Deno.ReplClientWindowsBootstrap.ts", "maieutics-repl-client/windows_bootstrap.ts"),
        ("Maieutics.Deno.Shared.Protocol.ts", "shared/protocol.ts"),
        ("Maieutics.Deno.Shared.Bus.ts", "shared/bus.ts")
    ];

    private readonly Lazy<string> clientUrl =
        new(Materialize, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Gets the file URL of the materialized client module.</summary>
    public string ClientUrl => clientUrl.Value;

    private static string Materialize()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-repl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        foreach (var (resource, relativePath) in Entries) WriteEmbedded(resource, Path.Combine(root, relativePath));

        return new Uri(Path.Combine(root, "maieutics-repl-client/mod.ts")).AbsoluteUri;
    }

    internal static void WriteEmbedded(string resourceName, string path)
    {
        using var stream = typeof(ReplClientModule).Assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException(
                               $"Missing embedded Deno module '{resourceName}'.");
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();
        Directory.CreateDirectory(
            Path.GetDirectoryName(path) ??
            throw new InvalidOperationException($"Cannot resolve the directory for '{path}'."));
        File.WriteAllText(path, source);
    }
}