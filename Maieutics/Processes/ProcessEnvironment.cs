using Maieutics.Permissions;

namespace Maieutics.Processes;

/// <summary>Builds the allowlisted environment for a general (terminal or MCP) child from the
/// effective policy's <c>env</c> grants. The default policy yields exactly the previous
/// <see cref="Capture"/> allowlist, so existing terminal behavior is unchanged; when a layer
/// restricts <c>env</c>, only the granted names are carried. Provider credentials never cross into
/// a shell child (AGENTS.md invariant 23: kinds a process sandbox cannot express are enforced by
/// their owning layer — env is enforced here at spawn time).</summary>
internal static class ProcessEnvironment
{
    internal const string TermName = "xterm-256color";

    private static readonly string[] DefaultAllowedEnvironmentNames =
    [
        "PATH",
        "HOME",
        "USERPROFILE",
        "HOMEDRIVE",
        "HOMEPATH",
        "LOCALAPPDATA",
        "APPDATA",
        "TMPDIR",
        "TMP",
        "TEMP",
        "LANG",
        "LC_ALL",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        "SYSTEMROOT",
        "WINDIR",
        "COMSPEC",
        "PATHEXT",
        "TERM"
    ];

    /// <summary>Captures the child environment from <paramref name="policy"/>: every name granted by
    /// the <c>env</c> kind (when the kind has explicit grants), otherwise the default allowlist.
    /// <c>TERM</c> is always pinned to the VT terminal name so PTY children render correctly
    /// regardless of the policy.</summary>
    internal static IReadOnlyDictionary<string, string?> Capture(EffectivePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var allowed = AllowedNames(policy);
        var result = new Dictionary<string, string?>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in allowed)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) result[name] = value;
        }

        result["TERM"] = TermName;
        return result;
    }

    private static IReadOnlyList<string> AllowedNames(EffectivePolicy policy)
    {
        var rules = policy.For(PermissionKind.Env);
        if (rules.AllowAll) return DefaultAllowedEnvironmentNames;
        if (rules.Allow.Count > 0) return rules.Allow;

        return DefaultAllowedEnvironmentNames;
    }
}
