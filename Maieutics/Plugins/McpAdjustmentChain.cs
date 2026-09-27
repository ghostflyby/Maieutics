using System.Collections.Immutable;
using System.Text.Json;
using Maieutics.Control;
using Maieutics.Mcp;
using Microsoft.Extensions.Logging;

namespace Maieutics.Plugins;

/// <summary>One worker extension-point registration that adjusts MCP declarations
/// (the <c>McpAdjust</c> extension point, ADR 0034).</summary>
internal sealed record McpAdjusterRegistration(string PluginId, string ExportName);

/// <summary>The adjustment topology: dependency-topological plugin order, each
/// plugin's declared dependencies (the adjustment authorization scope), and the
/// live adjuster registrations. Published by <c>PluginHostManager</c> whenever
/// the plugin set, the registry, or a manifest reload changes.</summary>
internal sealed record McpAdjustmentSnapshot(
    IReadOnlyList<string> OrderedPluginIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Dependencies,
    IReadOnlyList<McpAdjusterRegistration> Adjusters);

/// <summary>The MCP adjustment responsibility chain (ADR 0034). Adjusting workers
/// are invoked in dependency-topological order and see declarations only:
///
/// - <see cref="FoldCompositionAsync"/>: per registry revision, each adjuster
///   receives the in-scope server definitions (owners of its declared
///   dependencies) and may drop them; the drops remove the servers from the
///   composed view that downstream adjusters and the merge see.
/// - <see cref="AdjustToolsAsync"/>: per tool-listing materialization, the
///   server's adjuster chain threads the listing (remove by omission, rename /
///   alias / describe via <c>{aliasOf, name, description, inputSchema}</c>).
///
/// Every invocation is a one-shot request/response over the extension-point
/// channel; the chain never sees tool-call traffic — invocation always routes
/// to the owning server. Failures are sticky per adjuster (composition) and per
/// server (tools: the last adjusted listing; a server never successfully
/// adjusted exposes nothing), so a broken adjuster never resurrects removed
/// surface.
/// </summary>
internal sealed class McpAdjustmentChain : IMcpToolSurfaceAdjuster
{
    private readonly Func<string, string, string, JsonElement?, CancellationToken, Task<ExtensionCallOutcome>> invoke;
    private readonly ILogger logger;
    private readonly Lock gate = new();
    private readonly Dictionary<string, IReadOnlySet<string>> lastGoodDrops = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> lastGoodListings = new(StringComparer.Ordinal);
    private McpAdjustmentSnapshot snapshot = new([], ImmutableDictionary<string, IReadOnlyList<string>>.Empty, []);

    public McpAdjustmentChain(
        Func<string, string, string, JsonElement?, CancellationToken, Task<ExtensionCallOutcome>> invoke,
        ILogger logger)
    {
        this.invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Replaces the adjustment topology. Called on startup, on every host
    /// registry payload, and after declarative reloads.</summary>
    public void UpdateSnapshot(McpAdjustmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            this.snapshot = snapshot;
        }
    }

    /// <summary>The adjusters in scope for one server (owner's dependency scope,
    /// dependency-topological order). Exposed for change detection.</summary>
    public IReadOnlyList<McpAdjusterRegistration> ChainFor(string serverId)
    {
        var snapshot = Volatile.Read(ref this.snapshot);
        var owner = OwnerOf(serverId);
        var chain = new List<McpAdjusterRegistration>();
        foreach (var adjuster in snapshot.Adjusters)
        {
            if (OrderOf(snapshot, adjuster.PluginId) < 0) continue;
            if (InScope(snapshot, adjuster, owner)) chain.Add(adjuster);
        }

        return chain;
    }

    /// <summary>Stable fingerprint of the adjuster chain for one server: when it
    /// changes, the server's live connection re-projects its tool surface without
    /// reconnecting.</summary>
    public string FingerprintFor(string serverId)
    {
        return string.Join(
            "|",
            ChainFor(serverId).Select(static adjuster => $"{adjuster.PluginId}/{adjuster.ExportName}"));
    }

    /// <summary>Folds the composed server view through every adjuster in
    /// dependency-topological order. Returns the ids dropped from the view; the
    /// callers remove them before the merge, and later adjusters never see them.</summary>
    public async Task<IReadOnlySet<string>> FoldCompositionAsync(
        IReadOnlyList<(string OwnerPluginId, string ServerId, McpTransportDefinition Transport)> view,
        CancellationToken cancellationToken)
    {
        var snapshot = Volatile.Read(ref this.snapshot);
        var dropped = ImmutableHashSet<string>.Empty;
        foreach (var adjuster in OrderedAdjusters(snapshot))
        {
            var inScope = view
                .Where(entry => !dropped.Contains(entry.ServerId) && InScope(snapshot, adjuster, entry.OwnerPluginId))
                .ToList();
            if (inScope.Count == 0) continue;

            var request = BuildCompositionRequest(inScope);
            var outcome = await invoke(
                adjuster.PluginId,
                adjuster.ExportName,
                ReplExtensionPointName.McpAdjust,
                request,
                cancellationToken).ConfigureAwait(false);
            var key = $"{adjuster.PluginId}/{adjuster.ExportName}";
            ImmutableHashSet<string>? drops = null;
            if (!outcome.IsError && TryParseDrops(outcome.Value, inScope, out var parsed))
                drops = parsed;
            if (drops is null)
            {
                if (lastGoodDrops.TryGetValue(key, out var sticky))
                {
                    logger.LogWarning(
                        "MCP adjuster '{Plugin}/{Export}' failed; its previous composition result stays active.",
                        adjuster.PluginId,
                        adjuster.ExportName);
                    dropped = dropped.Union(sticky);
                    continue;
                }

                logger.LogWarning(
                    "MCP adjuster '{Plugin}/{Export}' failed without a previous result; its scope passes through unadjusted.",
                    adjuster.PluginId,
                    adjuster.ExportName);
                continue;
            }

            lock (gate)
            {
                lastGoodDrops[key] = drops;
            }

            dropped = dropped.Union(drops);
        }

        return dropped;
    }

    /// <summary>Threads one server's tool listing through its adjuster chain.
    /// Returns the final adjusted listing, or <c>null</c> when the server must
    /// expose nothing (an invocation failed and no previous adjusted listing
    /// exists). An empty chain passes the listing through untouched.</summary>
    public async Task<JsonElement?> AdjustToolsAsync(
        string serverId,
        JsonElement listing,
        CancellationToken cancellationToken)
    {
        var chain = ChainFor(serverId);
        if (chain.Count == 0) return listing;

        var current = listing;
        foreach (var adjuster in chain)
        {
            var request = BuildToolsRequest(serverId, current);
            var outcome = await invoke(
                adjuster.PluginId,
                adjuster.ExportName,
                ReplExtensionPointName.McpAdjust,
                request,
                cancellationToken).ConfigureAwait(false);
            if (outcome.IsError || outcome.Value is not { ValueKind: JsonValueKind.Array } adjusted)
            {
                if (lastGoodListings.TryGetValue(serverId, out var sticky))
                {
                    logger.LogWarning(
                        "MCP adjuster chain failed for server '{ServerId}'; the last adjusted listing stays active.",
                        serverId);
                    return sticky;
                }

                logger.LogWarning(
                    "MCP adjuster chain failed for server '{ServerId}' without a previous result; its tools are not exposed.",
                    serverId);
                return null;
            }

            current = ValidateListing(adjusted, current, serverId);
        }

        lock (gate)
        {
            lastGoodListings[serverId] = current;
        }

        return current;
    }

    private IEnumerable<McpAdjusterRegistration> OrderedAdjusters(McpAdjustmentSnapshot snapshot)
    {
        var ordered = snapshot.Adjusters
            .Where(adjuster => OrderOf(snapshot, adjuster.PluginId) >= 0)
            .ToList();
        ordered.Sort((left, right) =>
            OrderOf(snapshot, left.PluginId).CompareTo(OrderOf(snapshot, right.PluginId)));
        return ordered;
    }

    private static int OrderOf(McpAdjustmentSnapshot snapshot, string pluginId)
    {
        var list = snapshot.OrderedPluginIds;
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], pluginId, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    private static bool InScope(
        McpAdjustmentSnapshot snapshot,
        McpAdjusterRegistration adjuster,
        string ownerPluginId)
    {
        return snapshot.Dependencies.TryGetValue(adjuster.PluginId, out var deps) &&
               deps.Contains(ownerPluginId);
    }

    private static string OwnerOf(string serverId)
    {
        // Server ids are "plugin:<ownerId>::<key>" (kernel-assigned).
        var withoutScheme = serverId.StartsWith("plugin:", StringComparison.Ordinal)
            ? serverId["plugin:".Length..]
            : serverId;
        var separator = withoutScheme.IndexOf("::", StringComparison.Ordinal);
        return separator < 0 ? withoutScheme : withoutScheme[..separator];
    }

    private JsonElement BuildCompositionRequest(
        IReadOnlyList<(string OwnerPluginId, string ServerId, McpTransportDefinition Transport)> inScope)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("reason", "composition");
            writer.WriteStartArray("servers");
            foreach (var entry in inScope)
            {
                writer.WriteStartObject();
                writer.WriteString("id", entry.ServerId);
                writer.WritePropertyName("transport");
                JsonSerializer.Serialize(writer, entry.Transport, McpJsonContext.Default.McpTransportDefinition);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Parse(stream);
    }

    private static JsonElement BuildToolsRequest(string serverId, JsonElement listing)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("reason", "tools");
            writer.WriteString("server", serverId);
            writer.WritePropertyName("tools");
            listing.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Parse(stream);
    }

    private static JsonElement Parse(MemoryStream stream)
    {
        stream.Position = 0;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static bool TryParseDrops(
        JsonElement? response,
        IReadOnlyList<(string OwnerPluginId, string ServerId, McpTransportDefinition Transport)> inScope,
        out ImmutableHashSet<string> drops)
    {
        drops = ImmutableHashSet<string>.Empty;
        if (response is not { ValueKind: JsonValueKind.Object } body ||
            !body.TryGetProperty("servers", out var servers) ||
            servers.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var inScopeIds = inScope.Select(static entry => entry.ServerId).ToHashSet(StringComparer.Ordinal);
        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var server in servers.EnumerateArray())
        {
            if (server.ValueKind != JsonValueKind.Object ||
                !server.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var serverId = id.GetString() ?? string.Empty;
            if (!inScopeIds.Contains(serverId)) continue;
            if (server.TryGetProperty("drop", out var drop) && drop.ValueKind == JsonValueKind.True)
            {
                builder.Add(serverId);
            }
        }

        drops = builder.ToImmutable();
        return true;
    }

    /// <summary>Validates one adjuster's output against the listing it received:
    /// every entry must map an identity the previous stage exposed (unknown
    /// <c>aliasOf</c> entries are dropped with a warning — there is no way to
    /// route a tool that no server declared).</summary>
    private JsonElement ValidateListing(JsonElement adjusted, JsonElement previous, string serverId)
    {
        // The identities a downstream adjuster may reference are each entry's EXPOSED
        // name: the explicit rename when present, otherwise the entry's aliasOf.
        var known = new HashSet<string>(StringComparer.Ordinal);
        if (previous.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in previous.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object) continue;
                if (tool.TryGetProperty("name", out var renamed) && renamed.ValueKind == JsonValueKind.String)
                    known.Add(renamed.GetString() ?? string.Empty);
                else if (tool.TryGetProperty("aliasOf", out var alias) && alias.ValueKind == JsonValueKind.String)
                    known.Add(alias.GetString() ?? string.Empty);
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            var warned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in adjusted.EnumerateArray())
            {
                var aliasOf = tool.ValueKind == JsonValueKind.Object &&
                    tool.TryGetProperty("aliasOf", out var value) &&
                    value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : null;
                if (aliasOf is null || !known.Contains(aliasOf))
                {
                    if (aliasOf is not null && warned.Add(aliasOf))
                        logger.LogWarning(
                            "MCP adjuster for server '{ServerId}' returned tool '{Tool}' with no input identity; it is dropped.",
                            serverId,
                            aliasOf);
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("aliasOf", aliasOf);
                if (tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    writer.WritePropertyName("name");
                    name.WriteTo(writer);
                }

                if (tool.TryGetProperty("description", out var description) &&
                    description.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                {
                    writer.WritePropertyName("description");
                    description.WriteTo(writer);
                }

                if (tool.TryGetProperty("inputSchema", out var schema) && schema.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("inputSchema");
                    schema.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        stream.Position = 0;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}
