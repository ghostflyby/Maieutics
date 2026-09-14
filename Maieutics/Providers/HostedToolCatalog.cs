using Microsoft.Extensions.AI;

namespace Maieutics.Providers;

/// <summary>Maps provider-neutral hosted capability names onto the provider-neutral hosted tool
/// types Microsoft.Extensions.AI defines. A capability name that has no such type (or that only
/// makes sense as a local function, like <c>Shell</c>) produces no tool here: those names stay
/// declaration-only until a provider-neutral representation exists for them.</summary>
internal static class HostedToolCatalog
{
    private const string WebSearchCapability = "WebSearch";

    /// <summary>Builds the hosted tools for the resolved capability names, in capability order.
    /// Names without a provider-neutral tool type are skipped, and a tool type is emitted at
    /// most once however many spellings of its capability appear.</summary>
    internal static IReadOnlyList<AITool> Create(IEnumerable<string> capabilityNames)
    {
        ArgumentNullException.ThrowIfNull(capabilityNames);
        var tools = new List<AITool>();
        var webSearchAdded = false;
        foreach (var name in capabilityNames)
        {
            if (!string.Equals(name, WebSearchCapability, StringComparison.OrdinalIgnoreCase)) continue;
            if (webSearchAdded) continue;

            tools.Add(new HostedWebSearchTool());
            webSearchAdded = true;
        }

        return tools;
    }
}
