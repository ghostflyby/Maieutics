using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Maieutics.Execution;

/// <summary>Precedence classes for resource providers. Lower values win a claim
/// contest: built-ins (the workspace plane) cannot be shadowed, user-configured
/// bridges outrank MCP servers, and MCP servers resolve in configuration order
/// (ADR 0026 decision 2).</summary>
internal enum ResourceProviderClass
{
    BuiltIn = 0,
    Custom = 1,
    Mcp = 2
}

/// <summary>One provider claim on a portion of the resource URI space. A null
/// authority claims the whole scheme; an authority claims only that host.</summary>
internal sealed record ResourceClaim(string Scheme, string? Authority = null)
{
    internal bool Matches(Uri uri)
    {
        return string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) &&
               (Authority is null ||
                string.Equals(uri.Host, Authority, StringComparison.OrdinalIgnoreCase));
    }

    internal bool EqualsClaim(ResourceClaim other)
    {
        return string.Equals(Scheme, other.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Authority, other.Authority, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A read request against one resource URI. <see cref="MaxBytes"/> bounds
/// the returned body; providers that know the length up front must refuse larger
/// bodies with <see cref="ResourceException"/>.</summary>
/// <param name="MaxBytes">Maximum number of body bytes the caller will accept.</param>
internal readonly record struct ResourceReadRequest(long MaxBytes);

/// <summary>A successfully opened resource body. The caller owns and disposes
/// <see cref="Content"/>; binary stays binary (invariant 26).</summary>
/// <param name="Content">The resource body positioned at its start.</param>
/// <param name="MimeType">The provider-advertised media type, when known.</param>
internal sealed record ResourceReadResult(Stream Content, string? MimeType);

/// <summary>Typed failure raised by providers and by registry resolution. Codes are
/// stable wire values shared by the read tool and the control-channel endpoint.</summary>
internal sealed class ResourceException : Exception
{
    internal ResourceException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    internal string Code { get; }
}

/// <summary>One provider of virtual resource bodies over a claim on the URI space
/// (ADR 0026). Implementations must be safe for concurrent reads.</summary>
internal interface IResourceProvider
{
    /// <summary>Stable provider identifier surfaced in catalogs and conflicts.</summary>
    string Id { get; }

    /// <summary>The provider's precedence class.</summary>
    ResourceProviderClass Class { get; }

    /// <summary>The claims currently in effect; may change over the provider's
    /// lifetime (the MCP provider's claims follow configuration reloads).</summary>
    IReadOnlyList<ResourceClaim> Claims { get; }

    /// <summary>Opens one resource body. Throws <see cref="ResourceException"/> for
    /// typed failures; providers never return a null result.</summary>
    ValueTask<ResourceReadResult> ReadAsync(
        string uri,
        ResourceReadRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Optional catalog surface for providers that can enumerate their URI
/// space (the workspace plane is enumerated by <c>list_directory</c> instead).</summary>
internal interface IResourceCatalogProvider : IResourceProvider
{
    /// <summary>Enumerates the provider's resources and templates. Conflicts are
    /// reported by the registry, not mixed into the catalog.</summary>
    ValueTask<ImmutableArray<ResourceCatalogEntry>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>One entry of the <c>list_resources</c> catalog.</summary>
internal sealed record ResourceCatalogEntry(
    string ProviderId,
    string Uri,
    string? Name,
    string? Description,
    string? MimeType,
    string Kind,
    string? Template);

/// <summary>A registration conflict that disabled one provider claim. Conflicts
/// are always surfaced (catalog and log), never silently resolved (ADR 0026
/// decision 2).</summary>
internal sealed record ResourceConflict(
    string ProviderId,
    string Scheme,
    string? Authority,
    string Reason,
    string? ShadowedBy);

/// <summary>Resolves resource URIs to the provider that reads them. The registry is
/// stateless over live provider claims: resolution recomputes on every read, so
/// dynamic claim changes (MCP reloads) take effect immediately.</summary>
internal sealed class ResourceRegistry
{
    // Only the owning class may claim a reserved scheme; a null owner means the scheme
    // is unallocatable (file:// stays closed so OS paths cannot bypass workspace
    // containment). The workspace plane cannot be shadowed and the MCP escape-hatch
    // scheme is owned by the MCP provider (ADR 0026 decision 2).
    private static readonly IReadOnlyDictionary<string, ResourceProviderClass?> ReservedSchemeOwners =
        new Dictionary<string, ResourceProviderClass?>(StringComparer.OrdinalIgnoreCase)
        {
            ["workspace"] = ResourceProviderClass.BuiltIn,
            ["mcp"] = ResourceProviderClass.Mcp,
            ["file"] = null
        };

    private readonly IReadOnlyList<IResourceProvider> providers;

    public ResourceRegistry(IEnumerable<IResourceProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        this.providers = providers.ToArray();
    }

    internal IReadOnlyList<IResourceProvider> Providers => providers;

    /// <summary>Parses an absolute resource URI. Query and fragment are rejected so
    /// the full URI stays one opaque resource identifier for the owning provider.</summary>
    internal static bool TryParseUri(string uri, out Uri parsed)
    {
        parsed = new Uri("about:blank");
        if (string.IsNullOrWhiteSpace(uri) || uri.IndexOf('#') >= 0)
            return false;

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var candidate) ||
            candidate.OriginalString.Length == 0)
            return false;

        parsed = candidate;
        return true;
    }

    /// <summary>Returns the provider that reads <paramref name="uri"/>, or null when
    /// no active claim covers it. Winner order: claim specificity (authority beats
    /// scheme wildcard), provider class, registration order (ADR 0026 decision 2).</summary>
    internal IResourceProvider? Resolve(Uri uri)
    {
        var state = ComputeState();
        IResourceProvider? winner = null;
        var winnerSpecificity = -1;
        foreach (var (provider, index) in providers.Select((value, index) => (value, index)))
        {
            foreach (var claim in provider.Claims)
            {
                if (state.DisabledClaims.Contains((index, claim)) || !claim.Matches(uri)) continue;

                var specificity = claim.Authority is null ? 1 : 2;
                if (winner is null ||
                    specificity > winnerSpecificity ||
                    (specificity == winnerSpecificity && provider.Class < winner.Class))
                {
                    winner = provider;
                    winnerSpecificity = specificity;
                }

                break;
            }
        }

        return winner;
    }

    /// <summary>Returns every conflict that disabled a claim: reserved-scheme
    /// violations and same-class duplicate claim tuples (the later registration is
    /// shadowed). Computed live so dynamic claims are reflected.</summary>
    internal ImmutableArray<ResourceConflict> GetConflicts()
    {
        return ComputeState().Conflicts;
    }

    private (HashSet<(int Index, ResourceClaim Claim)> DisabledClaims, ImmutableArray<ResourceConflict> Conflicts)
        ComputeState()
    {
        var disabled = new HashSet<(int, ResourceClaim)>();
        var conflicts = ImmutableArray.CreateBuilder<ResourceConflict>();
        for (var index = 0; index < providers.Count; index++)
        {
            var provider = providers[index];
            foreach (var claim in provider.Claims)
            {
                if (ReservedSchemeOwners.TryGetValue(claim.Scheme, out var ownerClass) &&
                    (ownerClass is null || provider.Class != ownerClass))
                {
                    disabled.Add((index, claim));
                    conflicts.Add(new ResourceConflict(
                        provider.Id,
                        claim.Scheme,
                        claim.Authority,
                        "reserved_scheme",
                        null));
                }
            }
        }

        for (var index = 0; index < providers.Count; index++)
        {
            var provider = providers[index];
            for (var claimIndex = 0; claimIndex < provider.Claims.Count; claimIndex++)
            {
                var claim = provider.Claims[claimIndex];
                if (disabled.Contains((index, claim))) continue;

                for (var previous = 0; previous < index && !disabled.Contains((index, claim)); previous++)
                {
                    if (providers[previous].Class != provider.Class) continue;

                    var shadow = providers[previous].Claims.FirstOrDefault(claim.EqualsClaim);
                    if (shadow is null || disabled.Contains((previous, shadow))) continue;

                    disabled.Add((index, claim));
                    conflicts.Add(new ResourceConflict(
                        provider.Id,
                        claim.Scheme,
                        claim.Authority,
                        "duplicate_claim",
                        providers[previous].Id));
                }
            }
        }

        return (disabled, conflicts.ToImmutable());
    }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ResourceCatalogResult))]
internal sealed partial class ResourceJsonSerializerContext : JsonSerializerContext;

/// <summary>The <c>list_resources</c> tool result: the merged catalog of every
/// catalog-capable provider plus all registration conflicts.</summary>
internal sealed record ResourceCatalogResult(
    ImmutableArray<ResourceCatalogEntry> Resources,
    ImmutableArray<ResourceConflict> Conflicts);
