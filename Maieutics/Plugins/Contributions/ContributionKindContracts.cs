using System.Text.Json;
using Maieutics.Mcp;

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

    public static readonly McpDiscoverContributionKind Instance = new();

    public override string KindName => ExtensionKind;

    public override string? DataEntryName => DataEntry;

    public override string? ExtensionPointName => ExtensionKind;

    public override bool HasComputeForm => true;

    public override bool HasPublishForm => false;

    public override string? PublishCapability => null;

    /// <summary>MCP discovery retries live inside the coordinator's revision engine
    /// (never-succeeded registrations stay new there); the kernel keeps no retry set.</summary>
    public override bool UsesKernelRetrySet => false;

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

    /// <summary>A failed generator keeps its sticky part and the kernel retries the
    /// plugin on the next registry frame (never-succeeded-stays-new).</summary>
    public override bool UsesKernelRetrySet => true;

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

    public override bool UsesKernelRetrySet => false;

    public override bool HasDeclarativeRegistrations => false;

    /// <summary>Data-entry-only kinds claim no extensions entry.</summary>
    public override bool OwnsExtensionKind(string canonicalKind) => false;
}
