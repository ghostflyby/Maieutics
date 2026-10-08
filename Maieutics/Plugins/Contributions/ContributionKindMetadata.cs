namespace Maieutics.Plugins.Contributions;

/// <summary>The framework-level configurability knobs of one contribution kind
/// (plugin-contribution framework §5) — the shared table both kinds read their bounds,
/// stickiness, retry policy, defaults, and interpolation behavior from, replacing the
/// per-kind constants and hardcoded interpreter values. The values are the kind's
/// declared behavior, not new enforcement: every default equals the former hardcoded
/// value byte-for-byte, so existing declarations behave identically.</summary>
/// <param name="MaxDeclaredEntriesPerDeclaration">The most roots/entries one declarative
/// entry may carry; null leaves the kind unbounded (its current shape). Skills: 32.</param>
/// <param name="MaxComputedEntries">The most entries one computed contribution (generator
/// output or publish payload) may carry; null leaves it unbounded. Skills: 256.</param>
/// <param name="StickyPerSourceKey">Whether the kind's generated parts are sticky per
/// source key ((pluginId, export/registration) triple) — true for both kinds.</param>
/// <param name="UsesKernelRetrySet">Whether the kernel keeps a per-plugin retry set for
/// the kind (never-succeeded-stays-new). Skills: true; MCP retries inside its revision
/// engine instead.</param>
/// <param name="DefaultTimeouts">The kind's default lifecycle timeouts in the
/// <see cref="Mcp.McpServerDefinition"/> constructor order (initialization, request,
/// shutdown, connection), or null when the kind carries no timeouts. MCP:
/// [30s, 2min, 5s, 30s] — exactly the former fixed values, so default-path generation
/// keys hash byte-identically.</param>
/// <param name="DeclarationInterpolation">Whether the kind's declaration grammar
/// interprets <c>${env.*}</c>/<c>${var.*}</c> tokens through the unified variable
/// table. Skills: true (roots); MCP: true (the mcp.json data path, and extensions
/// entries via their explicit opt-in member); ui: true (its data path rides the shared
/// interpolating collection pipeline).</param>
internal sealed record ContributionKindMetadata(
    int? MaxDeclaredEntriesPerDeclaration,
    int? MaxComputedEntries,
    bool StickyPerSourceKey,
    bool UsesKernelRetrySet,
    IReadOnlyList<TimeSpan>? DefaultTimeouts,
    bool DeclarationInterpolation);
