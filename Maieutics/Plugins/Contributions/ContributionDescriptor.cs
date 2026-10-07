using Maieutics.Mcp;

namespace Maieutics.Plugins.Contributions;

/// <summary>One contribution entry as the framework sees it. The abstract base carries
/// only the differential minimum — the identity key the diff compares and the inert
/// marker — while closed concrete subclasses wrap the existing domain records, so the
/// consumer engines keep owning their value shapes. The framework never serializes
/// this type (source-generated JSON contexts stay on the closed domain records), so
/// the abstraction is NativeAOT safe.</summary>
internal abstract class ContributionDescriptor
{
    protected ContributionDescriptor(string identityKey, string? diagnostic)
    {
        IdentityKey = identityKey;
        Diagnostic = diagnostic;
    }

    /// <summary>The identity key the diff and equality use: MCP = server id (Ordinal);
    /// Skills = skill name (Ordinal).</summary>
    public string IdentityKey { get; }

    /// <summary>Non-null marks an inert entry: it stays visible in its list but never
    /// reaches the consumption face (the skills inert-diagnostic precedent).</summary>
    public string? Diagnostic { get; }
}

/// <summary>Closed concrete view of one discovered MCP server definition.</summary>
internal sealed class McpServerContribution : ContributionDescriptor
{
    public McpServerContribution(McpServerDefinition definition)
        : base(definition.Id, diagnostic: null) => Definition = definition;

    public McpServerDefinition Definition { get; }
}

/// <summary>Closed concrete view of one skill descriptor; a diagnosed skill rides as an
/// inert entry exactly like it does in the catalog today.</summary>
internal sealed class SkillEntryContribution : ContributionDescriptor
{
    public SkillEntryContribution(Skills.SkillDescriptor skill)
        : base(skill.Name, skill.Diagnostic) => Skill = skill;

    public Skills.SkillDescriptor Skill { get; }
}
