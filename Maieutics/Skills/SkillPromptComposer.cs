using System.Text;

namespace Maieutics.Skills;

/// <summary>Composes the skill catalog section appended to a run's system prompt (ADR 0039).
/// Only names, descriptions, and <c>skill://</c> pointers ride the prompt — bodies are read
/// on demand — and entries are grouped by source so plugin-contributed entries (stages 2-3)
/// are presented as plugin context, not human policy. An empty catalog composes to null and
/// the base prompt passes through untouched.</summary>
internal static class SkillPromptComposer
{
    internal static string? Compose(string? basePrompt, SkillCatalogSnapshot snapshot)
    {
        if (snapshot.Skills.Length == 0) return null;

        var output = new StringBuilder();
        if (basePrompt is { Length: > 0 } trimmed)
        {
            output.Append(trimmed.TrimEnd());
            output.Append("\n\n");
        }

        output.AppendLine("## Skills");
        output.AppendLine();
        output.AppendLine(
            "The following skills are available. Each is a named, reusable procedure; read its body with `read_text` on the `skill://` URI when a task matches.");
        foreach (var group in snapshot.Skills.GroupBy(static skill => skill.Source))
        {
            output.AppendLine();
            output.AppendLine($"### {GroupTitle(group.Key)}");
            foreach (var skill in group)
                output.AppendLine($"- {skill.Name}: {skill.Description} (skill://{skill.Name})");
        }

        return output.ToString();
    }

    private static string GroupTitle(SkillSource source)
    {
        return source switch
        {
            SkillSource.Workspace => "Workspace skills",
            SkillSource.User => "User skills",
            SkillSource.PluginDeclared => "Plugin skills",
            SkillSource.PluginGenerated => "Plugin-generated skills",
            SkillSource.PluginPublished => "Plugin-published skills",
            _ => "Skills"
        };
    }
}
