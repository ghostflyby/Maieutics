using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Permissions;

/// <summary>Wire shape of one kind entry in a <c>permissions.json</c> profile layer, aligned with
/// Deno's config-permissions object form: <c>{"allow":[...],"deny":[...]}</c> per kind (decision 3,
/// ADR 0018; verified against Deno 2.9.5). Relative paths are resolved against the profile file's
/// directory by the Phase 5 loader, so these DTOs carry the declared patterns verbatim.</summary>
internal sealed record PermissionProfileKindRules
{
    [JsonPropertyName("allow")]
    public string[]? Allow { get; init; }

    [JsonPropertyName("deny")]
    public string[]? Deny { get; init; }
}

/// <summary>A named permission set inside a <c>permissions.json</c> profile. The <c>default</c> set
/// is what a profile applies unless the profile selects another set; matching Deno's <c>-P</c>
/// semantics, the store always renders to CLI flags and/or the broker (ADR 0018 §12).</summary>
internal sealed record PermissionProfileSet
{
    [JsonPropertyName("read")]
    public PermissionProfileKindRules? Read { get; init; }

    [JsonPropertyName("write")]
    public PermissionProfileKindRules? Write { get; init; }

    [JsonPropertyName("net")]
    public PermissionProfileKindRules? Net { get; init; }

    [JsonPropertyName("env")]
    public PermissionProfileKindRules? Env { get; init; }

    [JsonPropertyName("run")]
    public PermissionProfileKindRules? Run { get; init; }

    [JsonPropertyName("ffi")]
    public PermissionProfileKindRules? Ffi { get; init; }

    [JsonPropertyName("sys")]
    public PermissionProfileKindRules? Sys { get; init; }

    [JsonPropertyName("import")]
    public PermissionProfileKindRules? Import { get; init; }
}

/// <summary>The <c>permissions.json</c> workspace profile document: named sets plus the set to apply
/// when none is selected. Unknown fields are tolerated by the source-generated reader, matching the
/// persisted-format rule (tolerate unknown fields).</summary>
internal sealed record PermissionProfile
{
    [JsonPropertyName("sets")]
    public Dictionary<string, PermissionProfileSet>? Sets { get; init; }

    [JsonPropertyName("default")]
    public string? DefaultSet { get; init; }
}

/// <summary>Source-generated JSON contract for the Phase 5 workspace <c>permissions.json</c> schema
/// (NativeAOT-safe; AGENTS.md: use source-generated serialization on protocol and persisted paths).</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(PermissionProfile))]
[JsonSerializable(typeof(PermissionProfileSet))]
[JsonSerializable(typeof(PermissionProfileKindRules))]
internal sealed partial class PermissionJsonContext : JsonSerializerContext
{
}
