using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Maieutics.Mcp;
using Maieutics.Permissions;

namespace Maieutics.Plugins.Contributions;

/// <summary>The plugin-declared MCP server discovery kind (ADR 0033/0034): manifest
/// <c>extensions.McpDiscover</c> entries plus the <c>mcp.json</c> data file. RegistryWide
/// delivery; its extensions entries pass through load untouched (per-entry validation
/// belongs to the discovery-time manifest branch, framework §3.2), and the data file's
/// interpretation — moved verbatim from the former TryLoad block — rides the
/// plugin-level sticky error channel so a broken file keeps the plugin loaded and the
/// previous contribution discoverable-but-failed.</summary>
internal sealed class McpDiscoverContributionKind : ContributionKindContract
{
    public const string ExtensionKind = "McpDiscover";
    public const string DataEntry = "mcp";

    /// <summary>The extensions entry's opt-in member: when true, the transport's string
    /// values expand through the variable table before interpretation. The member's
    /// presence alone changes the entry's canonical JSON (extensions fingerprint
    /// domain), so only new declarations can carry it — every existing entry (without
    /// the member) interprets byte-identically to before (ADR 0037).</summary>
    public const string InterpolateMember = "interpolate";

    /// <summary>The extensions entry's optional lifecycle-timeout overrides: an object
    /// whose members name the four timeouts with TimeSpan values; missing members take
    /// the metadata defaults (= the former fixed values, byte-identical generation
    /// keys). The member must be an object — any other shape fails the entry.</summary>
    public const string TimeoutsMember = "timeouts";

    public static readonly McpDiscoverContributionKind Instance = new();

    public override string KindName => ExtensionKind;

    public override string? DataEntryName => DataEntry;

    public override string? ExtensionPointName => ExtensionKind;

    public override bool HasComputeForm => true;

    public override bool HasPublishForm => false;

    public override string? PublishCapability => null;

    /// <summary>The former fixed discovery timeouts (initialization, request, shutdown,
    /// connection) as metadata defaults: default-path generation keys hash byte-for-byte
    /// identically to the hardcoded era.</summary>
    public override ContributionKindMetadata Metadata { get; } = new(
        MaxDeclaredEntriesPerDeclaration: null,
        MaxComputedEntries: null,
        StickyPerSourceKey: true,
        UsesKernelRetrySet: false,
        DefaultTimeouts:
        [
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
        ],
        DeclarationInterpolation: true);

    public override bool HasDeclarativeRegistrations => true;

    public override ContributionDeclarationResult ParseDeclared(
        ContributionDeclarationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Extensions entries pass through verbatim (they reach the descriptor's
        // Extensions record through the grammar reader's entry.Clone()); only the
        // mcp.json data file is interpreted at load — the former TryLoad block,
        // byte-for-byte.
        if (context.FindDataEntry(DataEntry) is not { } mcpEntry)
            return ContributionDeclarationResult.Empty;
        if (mcpEntry.Error is { } collectionError)
            return new(
                [],
                $"The '{DataEntry}' data entry could not be collected: {collectionError}");

        if (mcpEntry.Data is not { } collectedData)
            return ContributionDeclarationResult.Empty;
        try
        {
            var servers = McpServerFile.ReadJson(
                collectedData,
                context.RootDirectory,
                $"plugin:{context.PluginId}::");
            return new(
                [.. servers.Select(static server => (ContributionDescriptor)new McpServerContribution(server))],
                null);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or JsonException or InvalidDataException)
        {
            return new([], $"Invalid {DataEntry}.json data entry: {exception.Message}");
        }
    }

    public override ContributionDeclarationResult DeclarationsFromDescriptor(
        PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.McpServers.Count == 0 && descriptor.McpServersError is null)
            return ContributionDeclarationResult.Empty;
        return new(
            [.. descriptor.McpServers.Select(static server => (ContributionDescriptor)new McpServerContribution(server))],
            descriptor.McpServersError);
    }

    /// <summary>Interprets one <c>McpDiscover</c> entry into a server definition — the
    /// former <c>TryToMcpDefinition</c>, moved into the owning contract so the entry
    /// grammar's configurability members are interpreted beside it. The discovery-time
    /// branches keep their all-or-nothing failure granularity; this only interprets.
    /// Two optional members ride the kind's own grammar (framework §5):
    /// <c>interpolate</c> expands the transport's string values through the unified
    /// variable table before deserialization, and <c>timeouts</c> overrides the
    /// metadata defaults per name. Without the members — every existing entry — the
    /// interpretation and the generation key are byte-identical to the fixed-value era
    /// (ADR 0037: the opt-in member itself changes the canonical JSON, so only new
    /// declarations carry it). The <c>module</c> member is never interpolated: it is
    /// the server id slug, not a path.</summary>
    public static bool TryToDefinition(
        string pluginId,
        JsonElement discovery,
        Permissions.VariableTable? variables,
        [NotNullWhen(true)] out McpServerDefinition? definition)
    {
        definition = null;
        if (discovery.ValueKind != JsonValueKind.Object ||
            !discovery.TryGetProperty("module", out var module) ||
            module.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(module.GetString()) ||
            !discovery.TryGetProperty("transport", out var transport) ||
            transport.ValueKind != JsonValueKind.Object)
            return false;

        if (discovery.TryGetProperty(InterpolateMember, out var interpolate))
        {
            // Strict grammar: the member present but not the JSON boolean true fails
            // the entry instead of silently interpreting it without expansion —
            // the same rule the timeouts member applies to its own shape.
            if (interpolate.ValueKind != JsonValueKind.True) return false;
            if (!TryExpandTransport(transport, variables, out transport)) return false;
        }

        var metadata = Instance.Metadata;
        if (metadata.DefaultTimeouts is not { Count: 4 } defaultTimeouts)
            throw new InvalidOperationException(
                "The MCP contribution kind metadata must carry the four default timeouts.");
        var initialization = defaultTimeouts[0];
        var request = defaultTimeouts[1];
        var shutdown = defaultTimeouts[2];
        var connection = defaultTimeouts[3];

        // The timeouts member's strict grammar covers its shape too: a member present
        // but not an object fails the entry exactly like an unknown name inside it —
        // silently interpreting the entry on the default timeouts would hide the
        // declaration's real intent.
        if (discovery.TryGetProperty(TimeoutsMember, out var timeoutsElement) &&
            (timeoutsElement.ValueKind != JsonValueKind.Object ||
             !TryReadTimeoutOverrides(
                 timeoutsElement,
                 initialization, request, shutdown, connection,
                 out initialization, out request, out shutdown, out connection)))
            return false;

        McpTransportDefinition payload;
        try
        {
            payload = transport.Deserialize(McpJsonContext.Default.McpTransportDefinition)
                      ?? throw new JsonException("The transport payload is null.");
        }
        catch (JsonException)
        {
            return false;
        }

        var id = $"plugin:{pluginId}::{module.GetString()}";
        switch (payload)
        {
            case StdioMcpTransportDefinition stdio when !string.IsNullOrWhiteSpace(stdio.Command):
                definition = new McpServerDefinition(
                    id,
                    stdio,
                    initialization,
                    request,
                    shutdown,
                    connection,
                    true,
                    true,
                    McpServerDefinition.CreateGenerationKey(
                        stdio,
                        initialization,
                        request,
                        shutdown,
                        connection,
                        true,
                        true));
                return true;

            case HttpMcpTransportDefinition { Endpoint.IsAbsoluteUri: true } http:
                definition = new McpServerDefinition(
                    id,
                    http,
                    initialization,
                    request,
                    shutdown,
                    connection,
                    false,
                    false,
                    McpServerDefinition.CreateGenerationKey(
                        http,
                        initialization,
                        request,
                        shutdown,
                        connection,
                        false,
                        false));
                return true;

            default:
                return false;
        }
    }

    /// <summary>Expands every string value under the transport object (recursively)
    /// through the variable table. Without a table nothing expands — the skills-roots
    /// rule. An unresolvable variable fails the entry: the discovery branch's
    /// all-or-nothing granularity turns that into the sticky last-good path.</summary>
    private static bool TryExpandTransport(
        JsonElement transport,
        Permissions.VariableTable? variables,
        out JsonElement expanded)
    {
        expanded = transport;
        if (variables is null) return true;
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteExpanded(writer, transport, variables);
            }

            expanded = JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
            return true;
        }
        catch (Permissions.PermissionException)
        {
            return false;
        }
    }

    private static void WriteExpanded(
        Utf8JsonWriter writer,
        JsonElement element,
        Permissions.VariableTable variables)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteExpanded(writer, property.Value, variables);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteExpanded(writer, item, variables);

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                var value = element.GetString();
                writer.WriteStringValue(value is null ? string.Empty : variables.Expand(value));
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>Folds the optional <c>timeouts</c> overrides over the defaults. Names
    /// are the four lifecycle timeouts; values are positive invariant TimeSpans; an
    /// unknown name, a non-string value, or a non-positive span fails the entry
    /// (strict grammar, matching the mcp.json key discipline). The member's own shape
    /// (object vs anything else) is checked by the caller with the same strictness.</summary>
    private static bool TryReadTimeoutOverrides(
        JsonElement timeouts,
        TimeSpan initialization,
        TimeSpan request,
        TimeSpan shutdown,
        TimeSpan connection,
        out TimeSpan newInitialization,
        out TimeSpan newRequest,
        out TimeSpan newShutdown,
        out TimeSpan newConnection)
    {
        newInitialization = initialization;
        newRequest = request;
        newShutdown = shutdown;
        newConnection = connection;
        foreach (var member in timeouts.EnumerateObject())
        {
            if (member.Value.ValueKind != JsonValueKind.String) return false;
            var raw = member.Value.GetString();
            // A colon-bearing duration form is required: invariant "30" would parse
            // as thirty days, so a bare number is rejected as a declaration mistake.
            if (raw is null ||
                !raw.Contains(':', StringComparison.Ordinal) ||
                !TimeSpan.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var value) ||
                value <= TimeSpan.Zero)
                return false;

            switch (member.Name)
            {
                case "initialization": newInitialization = value; break;
                case "request": newRequest = value; break;
                case "shutdown": newShutdown = value; break;
                case "connection": newConnection = value; break;
                default: return false;
            }
        }

        return true;
    }
}

/// <summary>The skill contribution kind (ADR 0039 stages 2-3): manifest
/// <c>extensions.Skills</c> entries pass through load untouched — the roots are
/// re-enumerated fresh by the per-plugin reconcile pass (the PerPlugin delivery's
/// declared part), never interpreted at load; worker generators ride the
/// <c>Skills</c> registration; runtime publication flows through
/// <c>skills.publish</c>.</summary>
internal sealed class SkillsContributionKind : ContributionKindContract
{
    public const string ExtensionKind = "Skills";
    public const string PublishCapabilityName = "skills.publish";

    public static readonly SkillsContributionKind Instance = new();

    public override string KindName => ExtensionKind;

    public override string? DataEntryName => null;

    public override string? ExtensionPointName => ExtensionKind;

    public override bool HasComputeForm => true;

    public override bool HasPublishForm => true;

    public override string? PublishCapability => PublishCapabilityName;

    /// <summary>The former skills constants (32 roots per entry, 256 computed entries)
    /// as metadata: the delivery reads them instead of private constants, so the kind's
    /// bounds live in one table.</summary>
    public override ContributionKindMetadata Metadata { get; } = new(
        MaxDeclaredEntriesPerDeclaration: 32,
        MaxComputedEntries: 256,
        StickyPerSourceKey: true,
        UsesKernelRetrySet: true,
        DefaultTimeouts: null,
        DeclarationInterpolation: true);

    /// <summary>Declarative skills ride per-plugin passes, not synthetic
    /// registrations; worker generators join through their own registrations.</summary>
    public override bool HasDeclarativeRegistrations => false;
}

/// <summary>The declarative UI form kind (ADR 0038 stage 3), registered B 期 as a
/// text-only catalog entry: it claims the <c>ui</c> data-entry name for grammar routing
/// and the catalog-generated unknown-name diagnostics. Its TryLoad interpretation
/// block, <c>PluginUiFormDefinition</c> product, and <c>descriptor.UiForm</c> field
/// stay exactly where they were — this is a name registration, not a migration
/// (framework §6-B).</summary>
internal sealed class UiContributionKind : ContributionKindContract
{
    public const string DataEntry = "ui";

    public static readonly UiContributionKind Instance = new();

    public override string KindName => DataEntry;

    public override string? DataEntryName => DataEntry;

    public override string? ExtensionPointName => null;

    public override bool HasComputeForm => false;

    public override bool HasPublishForm => false;

    public override string? PublishCapability => null;

    public override ContributionKindMetadata Metadata { get; } = new(
        MaxDeclaredEntriesPerDeclaration: null,
        MaxComputedEntries: null,
        StickyPerSourceKey: true,
        UsesKernelRetrySet: false,
        DefaultTimeouts: null,
        DeclarationInterpolation: true);

    public override bool HasDeclarativeRegistrations => false;

    /// <summary>Data-entry-only kinds claim no extensions entry.</summary>
    public override bool OwnsExtensionKind(string canonicalKind) => false;
}
