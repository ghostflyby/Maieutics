using System.Collections.Immutable;
using System.Text;
using Maieutics.Execution;
using ModelContextProtocol.Protocol;

namespace Maieutics.Mcp;

/// <summary>The resource-plane adapter over every connected MCP server (ADR 0026
/// decision 3). One provider instance covers all servers: it claims the
/// <c>mcp://</c> escape-hatch scheme plus the schemes of the resources and
/// templates in the live catalogs, resolves a URI by exact resource before
/// template match in <c>mcp.json</c> server order, and reads through
/// <c>resources/read</c> under the connection lease and request timeout.</summary>
internal sealed class McpResourceProvider : IResourceProvider, IResourceCatalogProvider
{
    private const string EscapeHatchScheme = "mcp";
    private const string EscapeHatchPrefix = "mcp://";

    private readonly Func<IMcpResourceCatalogSource> sourceFactory;
    private IMcpResourceCatalogSource? source;

    internal McpResourceProvider(Func<IMcpResourceCatalogSource> sourceFactory)
    {
        this.sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
    }

    /// <summary>Resolved on first use: the composition root wires this provider into the
    /// registry while the runtime configuration itself is still being constructed.</summary>
    private IMcpResourceCatalogSource Source => source ??= sourceFactory();

    public string Id => "mcp";

    public ResourceProviderClass Class => ResourceProviderClass.Mcp;

    public IReadOnlyList<ResourceClaim> Claims
    {
        get
        {
            var claims = ImmutableArray.CreateBuilder<ResourceClaim>();
            claims.Capacity = 8;
            claims.Add(new ResourceClaim(EscapeHatchScheme));
            foreach (var server in Source.GetResourceServers())
            {
                foreach (var resource in server.Catalog.Resources)
                {
                    var claim = ClaimFromResourceUri(resource.Uri);
                    if (claim is not null) claims.Add(claim);
                }

                foreach (var template in server.Catalog.Templates)
                {
                    var claim = McpResourceTemplateMatcher.TryGetClaim(template.UriTemplate);
                    if (claim is not null) claims.Add(claim);
                }
            }

            return claims.DistinctBy(static claim => (claim.Scheme, claim.Authority)).ToArray();
        }
    }

    public async ValueTask<ResourceReadResult> ReadAsync(
        string uri,
        ResourceReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        cancellationToken.ThrowIfCancellationRequested();

        var (server, resourceUri) = uri.StartsWith(EscapeHatchPrefix, StringComparison.OrdinalIgnoreCase)
            ? ResolveEscapeHatch(uri)
            : (ResolveByCatalog(uri) ?? throw new ResourceException(
                    "resource_not_found",
                    $"No registered MCP server serves the resource '{uri}'."),
                uri);
        return await ReadFromServerAsync(server, resourceUri, request, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ImmutableArray<ResourceCatalogEntry>> ListAsync(
        CancellationToken cancellationToken)
    {
        var entries = ImmutableArray.CreateBuilder<ResourceCatalogEntry>();
        foreach (var server in Source.GetResourceServers())
        {
            foreach (var resource in server.Catalog.Resources)
                entries.Add(new ResourceCatalogEntry(
                    Id,
                    resource.Uri,
                    resource.Name,
                    resource.Description,
                    resource.MimeType,
                    "resource",
                    null));

            foreach (var template in server.Catalog.Templates)
                entries.Add(new ResourceCatalogEntry(
                    Id,
                    template.UriTemplate,
                    template.Name,
                    template.Description,
                    template.MimeType,
                    "template",
                    template.UriTemplate));
        }

        return entries.ToImmutable();
    }

    /// <summary>Reads <c>mcp://&lt;serverId&gt;/&lt;encoded resource uri&gt;</c> from one
    /// named server, bypassing cross-server resolution entirely.</summary>
    private (McpResourceServerAccess Server, string ResourceUri) ResolveEscapeHatch(string uri)
    {
        var rest = uri[EscapeHatchPrefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0)
            throw new ResourceException(
                "resource_invalid_uri",
                $"The '{EscapeHatchScheme}' scheme requires the form {EscapeHatchPrefix}<serverId>/<encoded resource uri>.");

        var serverId = rest[..slash];
        string resourceUri;
        try
        {
            resourceUri = Uri.UnescapeDataString(rest[(slash + 1)..]);
        }
        catch (UriFormatException exception)
        {
            throw new ResourceException(
                "resource_invalid_uri",
                "The escaped resource URI inside the mcp URI is not valid.",
                exception);
        }

        var server = Source.GetResourceServers().FirstOrDefault(candidate =>
            string.Equals(candidate.Id, serverId, StringComparison.OrdinalIgnoreCase));
        return server is null
            ? throw new ResourceException(
                "resource_not_found",
                $"No MCP server named '{serverId}' is registered.")
            : (server, resourceUri);
    }

    /// <summary>Picks the owning server for a plain resource URI: exact resource URIs
    /// beat template matches, and earlier servers (configuration order) win ties.</summary>
    private McpResourceServerAccess? ResolveByCatalog(string uri)
    {
        var servers = Source.GetResourceServers();
        return servers.FirstOrDefault(server =>
                   server.Catalog.Resources.Any(resource =>
                       string.Equals(resource.Uri, uri, StringComparison.Ordinal))) ??
               servers.FirstOrDefault(server => server.Catalog.Contains(uri));
    }    private async ValueTask<ResourceReadResult> ReadFromServerAsync(
        McpResourceServerAccess server,
        string resourceUri,
        ResourceReadRequest request,
        CancellationToken cancellationToken)
    {
        var lease = server.Generation.TryAcquire();
        if (lease is null)
            throw new ResourceException(
                "resource_provider_unavailable",
                $"MCP server '{server.Id}' is reconnecting; its resources are temporarily unavailable.");

        try
        {
            var contents = await server.Generation.ReadResourceAsync(resourceUri, cancellationToken)
                .ConfigureAwait(false);
            if (contents.Length == 0)
                throw new ResourceException(
                    "resource_not_found",
                    $"MCP server '{server.Id}' returned no content for '{resourceUri}'.");

            var stream = Materialize(server.Id, resourceUri, contents, out var mimeType);
            try
            {
                if (stream.Length > request.MaxBytes)
                    throw new ResourceException(
                        "resource_too_large",
                        $"The resource exceeds the {request.MaxBytes} byte read limit.");

                return new ResourceReadResult(stream, mimeType);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static MemoryStream Materialize(
        string serverId,
        string resourceUri,
        IReadOnlyList<ResourceContents> contents,
        out string? mimeType)
    {
        var stream = new MemoryStream();
        mimeType = null;
        foreach (var content in contents)
        {
            mimeType ??= content.MimeType;
            switch (content)
            {
                case TextResourceContents text:
                    if (stream.Length > 0) stream.WriteByte((byte)'\n');

                    stream.Write(Encoding.UTF8.GetBytes(text.Text));
                    break;
                case BlobResourceContents blob:
                    byte[] bytes;
                    try
                    {
                        bytes = blob.DecodedData.ToArray();
                    }
                    catch (FormatException exception)
                    {
                        stream.Dispose();
                        throw new ResourceException(
                            "resource_provider_failed",
                            $"MCP server '{serverId}' returned a malformed blob for '{resourceUri}'.",
                            exception);
                    }

                    if (stream.Length > 0) stream.WriteByte((byte)'\n');

                    stream.Write(bytes);
                    break;
                default:
                    stream.Dispose();
                    throw new ResourceException(
                        "resource_provider_failed",
                        $"MCP server '{serverId}' returned an unsupported content type for '{resourceUri}'.");
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static ResourceClaim? ClaimFromResourceUri(string uri)
    {
        // Exact resources are literal URIs; a generic parse covers the common
        // hierarchical forms, and scheme-only extraction covers exotic ones.
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return string.IsNullOrEmpty(parsed.Host)
                ? new ResourceClaim(parsed.Scheme)
                : new ResourceClaim(parsed.Scheme, parsed.Host);

        var separator = uri.IndexOf(':');
        return separator > 0 && uri.Take(separator).All(McpResourceTemplateMatcher.IsSchemeCharacter)
            ? new ResourceClaim(uri[..separator])
            : null;
    }
}
