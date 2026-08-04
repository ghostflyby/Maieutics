namespace Maieutics.Control;

/// <summary>
/// Materializes the embedded Deno REPL client module to a per-process temp file and exposes its
/// file URL for injection through the REPL child environment.
/// </summary>
internal sealed class ReplClientModule
{
    private const string ResourceName = "Maieutics.Deno.ReplClient.ts";
    private readonly Lazy<string> clientUrl =
        new(Materialize, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Gets the file URL of the materialized client module.</summary>
    public string ClientUrl => clientUrl.Value;

    private static string Materialize()
    {
        using var stream = typeof(ReplClientModule).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Missing embedded Deno REPL client module '{ResourceName}'.");
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();
        var path = Path.Combine(Path.GetTempPath(), $"mc-client-{Guid.NewGuid():N}.ts");
        File.WriteAllText(path, source);
        return new Uri(path).AbsoluteUri;
    }
}
