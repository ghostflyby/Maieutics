namespace Maieutics.Execution;

/// <summary>Classifies workspace-relative paths that carry agent instructions
/// (ADR 0032 decision 1): writes to these surfaces from the model's edit tools
/// require an explicit allow in the calling session's effective policy. The
/// classification is the mechanism; the shipped default policy and any
/// session overrides are configuration.</summary>
internal static class InstructionSurface
{
    /// <summary>Whether one workspace-relative path (forward slashes, no leading
    /// separator) is an instruction surface: any <c>AGENTS.md</c> at any depth, or
    /// any file under <c>.agents/</c>.</summary>
    public static bool IsInstructionSurface(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        if (normalized.EndsWith("/AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "AGENTS.md", StringComparison.OrdinalIgnoreCase))
            return true;

        return normalized.StartsWith(".agents/", StringComparison.Ordinal);
    }
}
