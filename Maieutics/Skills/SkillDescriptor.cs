namespace Maieutics.Skills;

/// <summary>Where one skill entered the catalog. Lower values win name conflicts when
/// the catalog merges sources (ADR 0039): workspace-scoped human authorship outranks the
/// user-level root, and both outrank every plugin contribution (stages 2-3).</summary>
internal enum SkillSource : byte
{
    Workspace = 0,
    User = 1,
    PluginDeclared = 2,
    PluginGenerated = 3,
    PluginPublished = 4
}

/// <summary>One discovered skill: the catalog entry (name + description) the system prompt
/// carries and the body location the <c>skill://</c> plane serves on demand. A descriptor
/// with a non-null <see cref="Diagnostic"/> is inert — it stays visible in diagnostics but
/// never reaches the prompt or the resource plane.</summary>
/// <param name="Name">The catalog name; matches the <c>skill://{name}</c> host.</param>
/// <param name="Description">The one-line catalog description; required, bounded.</param>
/// <param name="Source">The contributing source's precedence class.</param>
/// <param name="RootDirectory">
///     The absolute declared discovery root this skill was found under. The resource plane
///     re-checks body-path containment against it on every read.
/// </param>
/// <param name="BodyPath">
///     Absolute path of the skill's <c>SKILL.md</c> for filesystem sources. Read fresh on
///     every <c>skill://</c> read, so a body edit is visible without a catalog rebuild.
/// </param>
/// <param name="Diagnostic">
///     Why this skill is inert (missing description, invalid name, unreadable frontmatter).
///     Null for a usable skill.
/// </param>
internal sealed record SkillDescriptor(
    string Name,
    string Description,
    SkillSource Source,
    string RootDirectory,
    string? BodyPath = null,
    string? Diagnostic = null)
{
    /// <summary>The longest catalog name accepted (<c>skill://</c> host stays short).</summary>
    internal const int MaximumNameLength = 64;

    /// <summary>The longest description accepted into the catalog section of the prompt.</summary>
    internal const int MaximumDescriptionLength = 1024;

    /// <summary>Names are the <c>skill://</c> host: lowercase letters, digits, and hyphens,
    /// starting alphanumeric, so every catalog name is a valid opaque URI host.</summary>
    internal static bool IsValidName(string name)
    {
        if (name is not { Length: > 0 } || name.Length > MaximumNameLength) return false;
        if (name[0] is not ((>= 'a' and <= 'z') or (>= '0' and <= '9'))) return false;
        foreach (var character in name)
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                return false;
        return true;
    }
}
