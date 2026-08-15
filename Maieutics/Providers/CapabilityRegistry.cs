using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;

namespace Maieutics.Providers;

/// <summary>
///     A configurable capability knowledge base keyed by vendor, API format, endpoint, and model.
///     For each configured model source the potential compatibility is the union of what the source's
///     API format can express, what the owning vendor serves for the selected model, and any explicit
///     endpoint capability profile. The default compatibility is the full potential for known vendors
///     and nothing for unknown gateways; explicit endpoint profiles always add on top.
/// </summary>
internal sealed class CapabilityRegistry : IEquatable<CapabilityRegistry>
{
    /// <summary>
    ///     The provider-neutral capability names with known canonical spelling. Unknown names are not
    ///     rejected; they pass through with the configured spelling and remain comparable as opaque
    ///     capability identifiers.
    /// </summary>
    internal static readonly IReadOnlyList<string> CanonicalCapabilityNames =
    [
        "WebSearch",
        "FileSearch",
        "CodeInterpreter",
        "Shell",
        "ComputerUse",
        "ImageGeneration",
        "ApplyPatch",
        "Mcp"
    ];

    /// <summary>Known vendor endpoints and the capabilities their API serves, keyed by normalized host.</summary>
    private static readonly IReadOnlyDictionary<string, BuiltInVendor> BuiltInVendors =
        new Dictionary<string, BuiltInVendor>(StringComparer.OrdinalIgnoreCase)
        {
            ["api.openai.com"] = new(
                "openai",
                ["WebSearch", "FileSearch", "CodeInterpreter", "ComputerUse", "ImageGeneration", "ApplyPatch", "Mcp"]),
            ["api.anthropic.com"] = new("anthropic", [])
        };

    /// <summary>The vendor host to assume for each provider when no endpoint is configured.</summary>
    private static readonly IReadOnlyDictionary<string, string> DefaultVendorHosts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenAI"] = "api.openai.com",
            ["Anthropic"] = "api.anthropic.com"
        };

    private readonly IReadOnlyDictionary<string, EndpointCapabilityProfile> endpointProfiles;
    private readonly IReadOnlyDictionary<string, VendorCapabilityProfile> vendorProfiles;

    private CapabilityRegistry(
        IReadOnlyDictionary<string, EndpointCapabilityProfile> endpointProfiles,
        IReadOnlyDictionary<string, VendorCapabilityProfile> vendorProfiles)
    {
        this.endpointProfiles = endpointProfiles;
        this.vendorProfiles = vendorProfiles;
    }

    internal static CapabilityRegistry Empty { get; } =
        new(
            new Dictionary<string, EndpointCapabilityProfile>(StringComparer.Ordinal),
            new Dictionary<string, VendorCapabilityProfile>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    ///     Resolves the compatibility surface for one model source and model. Returns the vendor
    ///     identity, whether the vendor has known capability knowledge, whether the endpoint
    ///     matched an explicit profile, and the potential and effective capability name sets.
    ///     The potential is the capability ceiling the source's API format and the vendor's served
    ///     capabilities allow; the effective set is the default (the full potential for a known
    ///     vendor, nothing for an unknown gateway) plus any explicit endpoint profile.
    /// </summary>
    internal CapabilityResolution Resolve(IConfiguredChatClientSource source, string model)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var vendorId = ResolveVendorId(source);
        var knownVendor = IsKnownVendor(vendorId);
        var endpointCapabilities = GetEndpointCapabilities(source.EndpointUri);
        var aggregate = ResolveAggregateCapabilities(source, model, vendorId);
        var potential = IntersectCapabilities(source.FormatCapabilities, aggregate);
        var effective = knownVendor
            ? UnionCapabilities(potential, endpointCapabilities)
            : endpointCapabilities;

        return new CapabilityResolution(
            vendorId,
            knownVendor,
            IsKnown(source.EndpointUri),
            potential,
            effective);
    }

    internal static CapabilityRegistry Create(IConfiguration root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var endpoints = CreateEndpointProfiles(root.GetSection("Endpoints"));
        var vendors = CreateVendorProfiles(root.GetSection("Vendors"));
        return new CapabilityRegistry(endpoints, vendors);
    }

    internal static string ParseCapabilityName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Hosted capability names must not be empty.", nameof(name));

        var trimmed = name.Trim();
        if (!IsValidCapabilityName(trimmed))
            throw new ArgumentException(
                $"Hosted capability name '{name}' must be a universal name or a 'Format.Name' prefixed name.",
                nameof(name));

        var capability = trimmed.Split('.')[^1];
        return FindCanonicalName(capability) ?? trimmed;
    }

    private string? ResolveVendorId(IConfiguredChatClientSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.Vendor)) return source.Vendor;

        if (TryResolveHost(source) is not { } host) return null;

        foreach (var pair in vendorProfiles)
            if (pair.Value.EndpointHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                return pair.Key;

        return BuiltInVendors.TryGetValue(host, out var builtIn) ? builtIn.VendorId : null;
    }

    private string? TryResolveHost(IConfiguredChatClientSource source)
    {
        if (source.EndpointUri is not null)
        {
            return TryNormalizeEndpoint(source.EndpointUri, out var normalized)
                ? new Uri(normalized).Host
                : null;
        }

        return DefaultVendorHosts.TryGetValue(source.ProviderName, out var host) ? host : null;
    }

    private IReadOnlyList<string> ResolveAggregateCapabilities(
        IConfiguredChatClientSource source,
        string model,
        string? vendorId)
    {
        // Without vendor knowledge the API format is the only ceiling, so the format
        // capabilities themselves are the aggregate.
        if (vendorId is null) return source.FormatCapabilities;

        if (vendorProfiles.TryGetValue(vendorId, out var vendorProfile))
        {
            if (vendorProfile.ModelCapabilities.TryGetValue(model, out var modelCapabilities))
                return modelCapabilities;

            return vendorProfile.Capabilities;
        }

        if (BuiltInVendors.Values.FirstOrDefault(vendor =>
                string.Equals(vendor.VendorId, vendorId, StringComparison.OrdinalIgnoreCase)) is { } builtIn)
            return builtIn.Capabilities;

        // An explicit vendor without catalog knowledge narrows nothing; the format is the ceiling.
        return source.FormatCapabilities;
    }

    private bool IsKnownVendor(string? vendorId)
    {
        if (vendorId is null) return false;

        if (vendorProfiles.ContainsKey(vendorId)) return true;

        foreach (var builtIn in BuiltInVendors.Values)
            if (string.Equals(builtIn.VendorId, vendorId, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private IReadOnlyList<string> GetEndpointCapabilities(Uri? endpoint)
    {
        if (endpoint is null || !TryNormalizeEndpoint(endpoint, out var normalized) ||
            !endpointProfiles.TryGetValue(normalized, out var profile))
            return [];

        return profile.Capabilities;
    }

    private bool IsKnown(Uri? endpoint)
    {
        return endpoint is not null && TryNormalizeEndpoint(endpoint, out var normalized) &&
               endpointProfiles.ContainsKey(normalized);
    }

    /// <summary>Normalizes a configured capability URL, rejecting non-HTTP URLs and URL components that would make matching ambiguous.</summary>
    internal static string NormalizeEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException(
                "API endpoint capability URLs must be absolute HTTP or HTTPS URIs.",
                nameof(endpoint));
        if (!string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException(
                "API endpoint capability URLs must not contain user information.",
                nameof(endpoint));
        if (!string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException(
                "API endpoint capability URLs must not contain a query or fragment.",
                nameof(endpoint));

        return NormalizeEndpointCore(endpoint);
    }

    /// <summary>
    ///     Normalizes a provider source endpoint for table lookup. Unmatchable endpoints return
    ///     <see langword="false" /> rather than throwing so a previously valid source endpoint never
    ///     fails configuration because it carries components the capability table does not support.
    /// </summary>
    internal static bool TryNormalizeEndpoint(
        Uri endpoint,
        [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            return false;

        normalized = NormalizeEndpointCore(endpoint);
        return true;
    }

    public bool Equals(CapabilityRegistry? other)
    {
        if (other is null ||
            endpointProfiles.Count != other.endpointProfiles.Count ||
            vendorProfiles.Count != other.vendorProfiles.Count)
            return false;

        foreach (var pair in endpointProfiles)
            if (!other.endpointProfiles.TryGetValue(pair.Key, out var otherProfile) ||
                !EndpointProfilesEqual(pair.Value, otherProfile))
                return false;

        foreach (var pair in vendorProfiles)
            if (!other.vendorProfiles.TryGetValue(pair.Key, out var otherProfile) ||
                !VendorProfilesEqual(pair.Value, otherProfile))
                return false;

        return true;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as CapabilityRegistry);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var pair in endpointProfiles.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            hash.Add(pair.Key);
            hash.Add(pair.Value.Limits);
            foreach (var capability in pair.Value.Capabilities) hash.Add(capability);
        }

        foreach (var pair in vendorProfiles.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            hash.Add(pair.Key);
            foreach (var host in pair.Value.EndpointHosts) hash.Add(host);
            foreach (var capability in pair.Value.Capabilities) hash.Add(capability);
            foreach (var model in pair.Value.ModelCapabilities.OrderBy(static model => model.Key, StringComparer.Ordinal))
            {
                hash.Add(model.Key);
                foreach (var capability in model.Value) hash.Add(capability);
            }
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(CapabilityRegistry? left, CapabilityRegistry? right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(CapabilityRegistry? left, CapabilityRegistry? right)
    {
        return !Equals(left, right);
    }

    private static bool EndpointProfilesEqual(EndpointCapabilityProfile left, EndpointCapabilityProfile right)
    {
        return left.Limits == right.Limits && left.Capabilities.SequenceEqual(right.Capabilities);
    }

    private static bool VendorProfilesEqual(VendorCapabilityProfile left, VendorCapabilityProfile right)
    {
        if (!left.EndpointHosts.SequenceEqual(right.EndpointHosts, StringComparer.OrdinalIgnoreCase) ||
            !left.Capabilities.SequenceEqual(right.Capabilities) ||
            left.ModelCapabilities.Count != right.ModelCapabilities.Count)
            return false;

        foreach (var pair in left.ModelCapabilities)
            if (!right.ModelCapabilities.TryGetValue(pair.Key, out var other) ||
                !pair.Value.SequenceEqual(other))
                return false;

        return true;
    }

    private static IReadOnlyDictionary<string, EndpointCapabilityProfile> CreateEndpointProfiles(
        IConfigurationSection section)
    {
        var entries = section.Get<EndpointCapabilityOptions[]>(
            static binder => binder.ErrorOnUnknownConfiguration = true);
        if (entries is null || entries.Length == 0)
            return new Dictionary<string, EndpointCapabilityProfile>(StringComparer.Ordinal);

        var builder = new Dictionary<string, EndpointCapabilityProfile>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            entry.Validate();
            if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var url) ||
                url.Scheme is not ("http" or "https"))
                throw new ArgumentException(
                    "API endpoint capability URLs must be absolute HTTP or HTTPS URIs.",
                    nameof(entry.Url));

            var normalized = NormalizeEndpoint(url);
            if (builder.ContainsKey(normalized))
                throw new ArgumentException(
                    $"The API endpoint '{normalized}' is configured more than once.");

            builder.Add(normalized, entry.ToProfile(normalized));
        }

        return builder;
    }

    private static IReadOnlyDictionary<string, VendorCapabilityProfile> CreateVendorProfiles(
        IConfigurationSection section)
    {
        var entries = section.GetChildren().ToList();
        if (entries.Count == 0)
            return new Dictionary<string, VendorCapabilityProfile>(StringComparer.OrdinalIgnoreCase);

        var builder = new Dictionary<string, VendorCapabilityProfile>(StringComparer.OrdinalIgnoreCase);
        var claimedHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vendorSection in entries)
        {
            var vendorId = vendorSection.Key;
            if (!IsValidVendorId(vendorId))
                throw new ArgumentException(
                    $"Vendor identifier '{vendorId}' must be a non-empty identifier of letters, digits, '.', '_', or '-'.",
                    nameof(section));

            var options = new VendorCapabilityOptions();
            vendorSection.Bind(options, static binder => binder.ErrorOnUnknownConfiguration = true);
            var profile = options.ToProfile(vendorId);
            foreach (var host in profile.EndpointHosts)
                if (!claimedHosts.TryAdd(host, vendorId))
                    throw new ArgumentException(
                        $"The endpoint host '{host}' is claimed by both vendor '{claimedHosts[host]}' and vendor '{vendorId}'.");

            builder.Add(vendorId, profile);
        }

        return builder;
    }

    private static string NormalizeEndpointCore(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        var path = builder.Path.TrimEnd('/');
        builder.Path = path.Length == 0 ? "/" : path;
        builder.Query = string.Empty;
        builder.Fragment = string.Empty;
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static bool IsValidCapabilityName(string name)
    {
        var segments = name.Split('.');
        if (segments.Length > 2) return false;

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || !char.IsAsciiLetter(segment[0])) return false;

            foreach (var character in segment)
                if (!char.IsAsciiLetterOrDigit(character))
                    return false;
        }

        return true;
    }

    private static bool IsValidVendorId(string vendorId)
    {
        if (vendorId.Length == 0) return false;

        foreach (var character in vendorId)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
                return false;

        return true;
    }

    private static string? FindCanonicalName(string candidate)
    {
        foreach (var name in CanonicalCapabilityNames)
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                return name;

        return null;
    }

    private static IReadOnlyList<string> IntersectCapabilities(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return [];

        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in left)
            if (ContainsOrdinalIgnoreCase(right, name))
                result.Add(name);

        return [.. result];
    }

    private static IReadOnlyList<string> UnionCapabilities(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        // Copy even when one side is empty so the resolved sets never alias the registry's
        // stored capability arrays, which callers must not be able to mutate.
        if (left.Count == 0) return [.. right];
        if (right.Count == 0) return [.. left];

        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in left) result.Add(name);
        foreach (var name in right) result.Add(name);

        return [.. result];
    }

    private static bool ContainsOrdinalIgnoreCase(IReadOnlyList<string> values, string candidate)
    {
        foreach (var value in values)
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private sealed record BuiltInVendor(string VendorId, IReadOnlyList<string> Capabilities);
}

/// <summary>The compatibility surface resolved for one model source and model.</summary>
internal sealed record CapabilityResolution(
    string? VendorId,
    bool KnownVendor,
    bool Matched,
    IReadOnlyList<string> Potential,
    IReadOnlyList<string> Effective);

internal sealed record EndpointCapabilityProfile(
    string NormalizedUrl,
    IReadOnlyList<string> Capabilities,
    EndpointCapabilityLimits? Limits);

internal sealed record EndpointCapabilityLimits(int MaxBuiltinToolCalls);

internal sealed record VendorCapabilityProfile(
    string VendorId,
    IReadOnlyList<string> EndpointHosts,
    IReadOnlyList<string> Capabilities,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ModelCapabilities);

internal sealed class EndpointCapabilityOptions
{
    public string? Url { get; set; }

    public string[]? Capabilities { get; set; }

    public EndpointCapabilityLimitsOptions? Limits { get; set; }

    internal EndpointCapabilityProfile ToProfile(string normalizedUrl)
    {
        var capabilities = new SortedSet<string>(StringComparer.Ordinal);
        if (Capabilities is not null)
            foreach (var name in Capabilities)
                capabilities.Add(CapabilityRegistry.ParseCapabilityName(name));

        return new EndpointCapabilityProfile(
            normalizedUrl,
            [.. capabilities],
            Limits?.MaxBuiltinToolCalls is { } max ? new EndpointCapabilityLimits(max) : null);
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Url))
            throw new ArgumentException("Every API endpoint capability entry requires a Url.", nameof(Url));
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException(
                "API endpoint capability URLs must be absolute HTTP or HTTPS URIs.",
                nameof(Url));
        if (Capabilities is not null && Capabilities.Length == 0)
            throw new ArgumentException(
                "Endpoint capability lists must not be empty when present.",
                nameof(Capabilities));
        if (Capabilities is not null)
            foreach (var name in Capabilities)
                _ = CapabilityRegistry.ParseCapabilityName(name);

        Limits?.Validate();
    }
}

internal sealed class EndpointCapabilityLimitsOptions
{
    public int? MaxBuiltinToolCalls { get; set; }

    internal void Validate()
    {
        if (MaxBuiltinToolCalls is { } max && max < 1)
            throw new ArgumentOutOfRangeException(
                nameof(MaxBuiltinToolCalls),
                max,
                "MaxBuiltinToolCalls must be at least 1.");
    }
}

internal sealed class VendorCapabilityOptions
{
    public string[]? Endpoints { get; set; }

    public string[]? Capabilities { get; set; }

    public Dictionary<string, VendorModelCapabilityOptions>? Models { get; set; }

    internal VendorCapabilityProfile ToProfile(string vendorId)
    {
        var capabilities = new SortedSet<string>(StringComparer.Ordinal);
        if (Capabilities is not null)
            foreach (var name in Capabilities)
                capabilities.Add(CapabilityRegistry.ParseCapabilityName(name));

        var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Endpoints is not null)
            foreach (var endpoint in Endpoints)
            {
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https"))
                    throw new ArgumentException(
                        "Vendor endpoint URLs must be absolute HTTP or HTTPS URIs.",
                        nameof(Endpoints));

                hosts.Add(new Uri(CapabilityRegistry.NormalizeEndpoint(uri)).Host);
            }

        var modelCapabilities = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (Models is not null)
            foreach (var pair in Models)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    throw new ArgumentException("Vendor model identifiers must not be empty.", nameof(Models));

                pair.Value.Validate();
                var names = new SortedSet<string>(StringComparer.Ordinal);
                if (pair.Value.Capabilities is not null)
                    foreach (var name in pair.Value.Capabilities)
                        names.Add(CapabilityRegistry.ParseCapabilityName(name));

                modelCapabilities.Add(pair.Key, [.. names]);
            }

        return new VendorCapabilityProfile(vendorId, [.. hosts], [.. capabilities], modelCapabilities);
    }

    internal void Validate()
    {
        if (Endpoints is not null)
            foreach (var endpoint in Endpoints)
            {
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https"))
                    throw new ArgumentException(
                        "Vendor endpoint URLs must be absolute HTTP or HTTPS URIs.",
                        nameof(Endpoints));

                _ = CapabilityRegistry.NormalizeEndpoint(uri);
            }

        if (Capabilities is not null)
            foreach (var name in Capabilities)
                _ = CapabilityRegistry.ParseCapabilityName(name);

        if (Models is not null)
            foreach (var pair in Models)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    throw new ArgumentException("Vendor model identifiers must not be empty.", nameof(Models));

                pair.Value.Validate();
            }
    }
}

internal sealed class VendorModelCapabilityOptions
{
    public string[]? Capabilities { get; set; }

    internal void Validate()
    {
        if (Capabilities is not null)
            foreach (var name in Capabilities)
                _ = CapabilityRegistry.ParseCapabilityName(name);
    }
}
