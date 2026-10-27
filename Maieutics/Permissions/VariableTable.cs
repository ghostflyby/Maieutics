using System.Text;
using Maieutics.Execution;

namespace Maieutics.Permissions;

/// <summary>Single-source variable table for permission path patterns. <c>${env.X}</c> resolves
/// from the kernel process environment at expansion time; <c>${var.X}</c> resolves from the fixed
/// internal set (dataDir, pluginsDir) or the live workspace seam (var.workspace). Unknown
/// variables fail expansion with a typed error so a typo cannot silently narrow or widen a grant
/// (ADR 0018 §4). The <c>var.*</c> namespace is reserved for Maieutics-internal paths and is
/// intentionally separate from <c>env.*</c>: a path grant can never depend on a user-set
/// environment variable expanding differently on another machine.</summary>
internal sealed class VariableTable
{
    /// <summary>A table whose patterns are already expanded: <see cref="Expand"/> returns each
    /// pattern verbatim, for composing layers whose values came out of an earlier build (a
    /// derived deny overlay on a composed policy) — re-expanding literal paths would misread any
    /// <c>${</c> in them as a token.</summary>
    internal static VariableTable Expanded { get; } = new(new NullVariableSource(), passthrough: true);

    private readonly IPermissionVariableSource _source;
    private readonly IReadOnlyDictionary<string, string> _fixedVariables;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly bool _passthrough;

    internal VariableTable(
        IPermissionVariableSource source,
        IReadOnlyDictionary<string, string>? fixedVariables = null,
        Func<string, string?>? getEnvironmentVariable = null,
        bool passthrough = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _fixedVariables = fixedVariables ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _passthrough = passthrough;
    }

    /// <summary>Expands every <c>${...}</c> token in <paramref name="pattern"/>. Patterns without
    /// tokens are returned unchanged. A malformed token or an unresolvable variable throws
    /// <see cref="PermissionException"/> with a <c>permission_variable_*</c> code.</summary>
    internal string Expand(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (_passthrough || pattern.IndexOf("${", StringComparison.Ordinal) < 0) return pattern;

        var builder = new StringBuilder(pattern.Length);
        var position = 0;
        while (position < pattern.Length)
        {
            var start = pattern.IndexOf("${", position, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(pattern, position, pattern.Length - position);
                break;
            }

            builder.Append(pattern, position, start - position);
            var end = pattern.IndexOf('}', start + 2);
            if (end < 0) throw Malformed(pattern);

            var name = pattern[(start + 2)..end];
            builder.Append(Resolve(name));
            position = end + 1;
        }

        return builder.ToString();
    }

    private string Resolve(string name)
    {
        if (name.StartsWith("env.", StringComparison.Ordinal))
        {
            var environmentName = name["env.".Length..];
            if (environmentName.Length == 0) throw Malformed(name);

            return _getEnvironmentVariable(environmentName)
                   ?? throw Unknown($"Environment variable {environmentName} is not set.");
        }

        if (name.StartsWith("var.", StringComparison.Ordinal))
        {
            var variableName = name["var.".Length..];
            if (variableName.Length == 0) throw Malformed(name);

            if (_fixedVariables.TryGetValue(variableName, out var fixedValue)) return fixedValue;

            var liveValue = _source.GetVariable(variableName);
            if (liveValue is not null) return liveValue;

            throw Unknown($"Internal variable {variableName} is not defined.");
        }

        throw Malformed($"The variable '{name}' is not in the env.* or var.* namespace.");
    }

    private sealed class NullVariableSource : IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return null;
        }
    }

    private static PermissionException Unknown(string message)
    {
        return new PermissionException("permission_variable_unknown", message);
    }

    private static PermissionException Malformed(string pattern)
    {
        return new PermissionException(
            "permission_variable_malformed",
            $"The permission pattern '{pattern}' contains a malformed variable token.");
    }
}
