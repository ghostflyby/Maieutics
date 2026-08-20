using System.Text.Json;

namespace Maieutics.Permissions;

/// <summary>Loads a workspace <c>permissions.json</c> profile into a <see cref="PermissionLayer"/>.
/// The format is aligned with Deno's config-permissions object shape (ADR 0018 decision 3):
/// per-kind <c>{"allow":[...],"deny":[...]}</c> inside named sets, with the <c>default</c> set
/// applied unless another is selected. Relative paths are resolved against the profile file's
/// directory, matching Deno's config semantics. A missing file yields an empty layer; an invalid
/// file throws a typed <see cref="PermissionException"/> (the caller keeps last-known-good).</summary>
internal static class PermissionProfileLoader
{
    /// <summary>Loads the profile at <paramref name="path"/> and converts the selected set into a
    /// <see cref="PermissionLayer"/>. Returns an empty layer when the file does not exist.</summary>
    internal static PermissionLayer Load(string path, string? selectedSet = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return new PermissionLayer();

        PermissionProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize(
                File.ReadAllBytes(path),
                PermissionJsonContext.Default.PermissionProfile);
        }
        catch (JsonException exception)
        {
            throw new PermissionException(
                "permission_profile_invalid",
                $"The permissions profile '{path}' is not valid JSON: {exception.Message}");
        }

        if (profile is null)
            throw new PermissionException(
                "permission_profile_invalid",
                $"The permissions profile '{path}' is empty.");

        var setName = selectedSet ?? profile.DefaultSet;
        if (string.IsNullOrWhiteSpace(setName))
            throw new PermissionException(
                "permission_profile_invalid",
                $"The permissions profile '{path}' does not declare a default set.");

        if (profile.Sets is null || !profile.Sets.TryGetValue(setName, out var set))
            throw new PermissionException(
                "permission_profile_invalid",
                $"The permissions profile '{path}' does not declare the set '{setName}'.");

        return ToLayer(set, Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty);
    }

    private static PermissionLayer ToLayer(PermissionProfileSet set, string baseDirectory)
    {
        var kinds = new Dictionary<PermissionKind, PermissionKindRules>();
        AddKind(kinds, PermissionKind.Read, set.Read, baseDirectory);
        AddKind(kinds, PermissionKind.Write, set.Write, baseDirectory);
        AddKind(kinds, PermissionKind.Net, set.Net, baseDirectory);
        AddKind(kinds, PermissionKind.Env, set.Env, baseDirectory);
        AddKind(kinds, PermissionKind.Run, set.Run, baseDirectory);
        AddKind(kinds, PermissionKind.Ffi, set.Ffi, baseDirectory);
        AddKind(kinds, PermissionKind.Sys, set.Sys, baseDirectory);
        AddKind(kinds, PermissionKind.Import, set.Import, baseDirectory);
        return new PermissionLayer { Kinds = kinds };
    }

    private static void AddKind(
        Dictionary<PermissionKind, PermissionKindRules> kinds,
        PermissionKind kind,
        PermissionProfileKindRules? rules,
        string baseDirectory)
    {
        if (rules is null) return;

        // Path kinds (read, write, ffi) resolve relative values against the profile directory;
        // env, run, net, sys, and import values are names or host patterns, never paths (ADR 0018 §4).
        var resolvePaths = kind is PermissionKind.Read or PermissionKind.Write or PermissionKind.Ffi;
        kinds[kind] = new PermissionKindRules
        {
            AllowAll = rules.Allow is { Length: 1 } && IsAllowAll(rules.Allow[0]),
            DenyAll = rules.Deny is { Length: 1 } && IsAllowAll(rules.Deny[0]),
            Allow = resolvePaths ? ResolvePaths(rules.Allow, baseDirectory) : Literal(rules.Allow),
            Deny = resolvePaths ? ResolvePaths(rules.Deny, baseDirectory) : Literal(rules.Deny)
        };
    }

    private static IReadOnlyList<string> Literal(string[]? values)
    {
        return values is null || values.Length == 0 ? [] : values.Where(static value => value.Length > 0).ToArray();
    }

    private static bool IsAllowAll(string value)
    {
        return value.Length == 0 || value == "*";
    }

    private static IReadOnlyList<string> ResolvePaths(string[]? values, string baseDirectory)
    {
        if (values is null || values.Length == 0) return [];

        return values
            .Where(static value => value.Length > 0)
            .Select(value =>
                // Path.IsPathRooted is the cross-platform absolute-path test: it treats a leading
                // '/' (e.g. /etc/ssl) as rooted on every platform, whereas IsPathFullyQualified
                // would resolve such a path against the base directory on Windows.
                Path.IsPathRooted(value)
                    ? Path.GetFullPath(value)
                    : Path.GetFullPath(value, baseDirectory))
            .ToArray();
    }
}
