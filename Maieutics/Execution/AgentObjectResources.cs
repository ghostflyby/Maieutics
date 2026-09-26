using Maieutics.Agent;

namespace Maieutics.Execution;

/// <summary>Serves the content-addressed object store as the read-only <c>objects://</c>
/// resource plane (ADR 0026): <c>objects://{sha256}</c> reads the full JSON object a
/// truncated tool result references. Read-only by construction — the plane has no catalog
/// entries (objects are an unbounded set, not a curated space) and no wait/cancel: they are
/// content addresses, not live tasks. The control-channel resource bridge and REPL fetch
/// reach it through the same registry as every other scheme.</summary>
internal sealed class AgentObjectResourceProvider(IAgentObjectStore store) : IResourceProvider
{
    private const long MaximumObjectBytes = 8 * 1024 * 1024;

    private const string MimeType = "application/json";

    public string Id => "objects";

    public ResourceProviderClass Class => ResourceProviderClass.BuiltIn;

    public IReadOnlyList<ResourceClaim> Claims => [new ResourceClaim("objects")];

    public ValueTask<ResourceReadResult> ReadAsync(
        string uri,
        ResourceReadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The URI parser case-folds hosts, so an uppercase spelling would silently read the
        // canonical object; require the verbatim canonical form `objects://{64 lowercase hex}`.
        if (!ResourceRegistry.TryParseUri(uri, out var parsed) ||
            parsed.Host.Length != 64 ||
            !IsLowerHex(parsed.Host) ||
            !string.Equals(uri, $"objects://{parsed.Host}", StringComparison.Ordinal))
        {
            throw new ResourceException(
                "resource_invalid_uri",
                "The value must be an objects:// URI whose host is a lowercase SHA-256 content address.");
        }

        Stream content;
        try
        {
            content = store.Open(parsed.Host);
        }
        catch (ArgumentException exception)
        {
            throw new ResourceException("resource_invalid_uri", exception.Message, exception);
        }
        catch (FileNotFoundException exception)
        {
            throw new ResourceException("resource_not_found", exception.Message, exception);
        }

        var bounded = new MemoryStream();
        using (content)
        {
            var buffer = new byte[64 * 1024];
            long copied = 0;
            int read;
            while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
            {
                copied += read;
                if (copied > Math.Min(request.MaxBytes, MaximumObjectBytes))
                    throw new ResourceException(
                        "resource_too_large",
                        $"The object '{parsed.Host}' exceeds the read limit.");
                bounded.Write(buffer, 0, read);
            }
        }

        bounded.Position = 0;
        return ValueTask.FromResult(new ResourceReadResult(bounded, MimeType));
    }

    private static bool IsLowerHex(string value)
    {
        foreach (var character in value)
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        return true;
    }
}
