namespace Maieutics.Skills;

/// <summary>Configuration of the skill discovery sources (ADR 0039). Bound from
/// <c>Maieutics:Skills</c>; roots are startup-fixed (the same lifetime semantics as the
/// workspace home) and never participate in configuration-reload identity — catalog
/// freshness comes from the per-root watchers, not from reloads.</summary>
internal sealed class SkillsOptions
{
    internal const string SectionName = "Maieutics:Skills";

    /// <summary>Whether skill discovery runs at all. Disabled leaves the catalog empty and
    /// the <c>skill://</c> plane serving nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Overrides the workspace skill root. The default is
    ///     <c>&lt;workspace root&gt;/.agents/skills</c>; a null or empty value keeps it.
    /// </summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>
    ///     Overrides the user-level skill root. The default is
    ///     <c>~/.agents/skills</c>; a null or empty value keeps it, and an unresolvable user
    ///     profile disables the user source.
    /// </summary>
    public string? UserRoot { get; set; }

    /// <summary>Validates that explicitly configured roots are absolute; defaults are
    /// resolved by the composition root.</summary>
    public void Validate()
    {
        if (WorkspaceRoot is { Length: > 0 } && !Path.IsPathRooted(WorkspaceRoot))
            throw new InvalidOperationException(
                $"The configured value '{WorkspaceRoot}' for {SectionName}:WorkspaceRoot must be an absolute path.");
        if (UserRoot is { Length: > 0 } && !Path.IsPathRooted(UserRoot))
            throw new InvalidOperationException(
                $"The configured value '{UserRoot}' for {SectionName}:UserRoot must be an absolute path.");
    }
}
