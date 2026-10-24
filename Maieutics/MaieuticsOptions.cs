using Maieutics.Permissions;

namespace Maieutics;

public sealed class MaieuticsOptions
{
    public const string SectionName = "Maieutics";

    public string? SystemPrompt { get; set; }

    public string DefaultProfile { get; set; } = string.Empty;

    // Legacy single-provider configuration. Removed after the compatibility window.
    public MaieuticsModelOptions Model { get; set; } = new();

    public MaieuticsProviderOptions Providers { get; set; } = new();

    public MaieuticsAgentOptions Agent { get; set; } = new();

    // App-wide permission defaults (ADR 0018 Phase 5): the second layer of the four-layer
    // overlay, between the built-in baseline and the workspace permissions.json profile.
    public MaieuticsPermissionsOptions Permissions { get; set; } = new();


    internal void ValidateCommon()
    {
        Agent.Validate();
        Permissions.Validate();
    }
}

/// <summary>App-wide permission defaults (<c>Maieutics:Permissions</c>, ADR 0018 Phase 5).
/// Per-kind allow/deny patterns mirror the workspace permissions.json shape so both layers of
/// the overlay speak the same grammar; denials win regardless of layer order.</summary>
public sealed class MaieuticsPermissionsOptions
{
    public MaieuticsPermissionKindOptions? Read { get; set; }

    public MaieuticsPermissionKindOptions? Write { get; set; }

    public MaieuticsPermissionKindOptions? Net { get; set; }

    public MaieuticsPermissionKindOptions? Env { get; set; }

    public MaieuticsPermissionKindOptions? Run { get; set; }

    public MaieuticsPermissionKindOptions? Ffi { get; set; }

    public MaieuticsPermissionKindOptions? Sys { get; set; }

    public MaieuticsPermissionKindOptions? Import { get; set; }

    /// <summary>The kernel-internal instruction-surface domain (ADR 0032): a physical-path kind;
    /// patterns must be absolute or variable-anchored like the other path kinds.</summary>
    public MaieuticsPermissionKindOptions? Instructions { get; set; }

    internal void Validate()
    {
        ValidateKind(Read);
        ValidateKind(Write);
        ValidateKind(Net);
        ValidateKind(Env);
        ValidateKind(Run);
        ValidateKind(Ffi);
        ValidateKind(Sys);
        ValidateKind(Import);
        ValidateKind(Instructions);
        ValidatePathPatterns(Read, PermissionKind.Read);
        ValidatePathPatterns(Write, PermissionKind.Write);
        ValidatePathPatterns(Ffi, PermissionKind.Ffi);
        ValidatePathPatterns(Instructions, PermissionKind.Instructions);
    }

    private static void ValidateKind(MaieuticsPermissionKindOptions? kind)
    {
        if (kind is null) return;
        foreach (var pattern in kind.Allow) ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        foreach (var pattern in kind.Deny) ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
    }

    /// <summary>Path-kind patterns in app-wide defaults must be absolute or variable-anchored: the
    /// app config is anchored to no directory, so a relative path pattern has no defined base
    /// here (the workspace profile resolves relatives against its own file). <c>run</c> is not a
    /// path kind for this rule — its values are bare command names or absolute paths.</summary>
    private static void ValidatePathPatterns(MaieuticsPermissionKindOptions? kind, PermissionKind model)
    {
        if (kind is null || !PermissionKinds.IsFilePathKind(model)) return;
        foreach (var pattern in kind.Allow.Concat(kind.Deny))
        {
            if (Path.IsPathRooted(pattern) || pattern.StartsWith("${", StringComparison.Ordinal)) continue;
            throw new ArgumentException(
                $"The {PermissionKinds.GetName(model)} permission pattern '{pattern}' is relative; " +
                "app-wide default path patterns must be absolute or use a ${env.*}/${var.*} variable token.");
        }
    }
}

public sealed class MaieuticsPermissionKindOptions
{
    public List<string> Allow { get; set; } = [];

    public List<string> Deny { get; set; } = [];

    public bool AllowAll { get; set; }

    public bool DenyAll { get; set; }
}

public sealed class MaieuticsModelOptions
{
    public string Provider { get; set; } = "OpenAI";

    public string Name { get; set; } = string.Empty;
}

public sealed class MaieuticsProviderOptions
{
    // Provider-specific legacy sections are read by their registered factories.
}

public sealed class MaieuticsAgentOptions
{
    public int MaxRetainedTurns { get; set; } = 50;

    public int MaxHistoryBytes { get; set; } = 400_000;

    // Legacy configuration input. Normalize before validation and remove after the compatibility window.
    public int? MaxHistoryCharacters { get; set; }

    public int MaxInputCharacters { get; set; } = 32_000;

    public int MaxResponseCharacters { get; set; } = 64_000;

    public int MaxModelIterationsPerTurn { get; set; } = 24;

    public TimeSpan MaxTurnDuration { get; set; } = TimeSpan.Zero;

    public int MaxToolCallsPerTurn { get; set; } = 48;

    public int MaxToolArgumentsBytes { get; set; } = 65_536;

    public int MaxToolResultBytes { get; set; } = 262_144;

    public int MaxToolProgressEventsPerCall { get; set; } = 256;

    public int EventBufferCapacity { get; set; } = 128;

    public MaieuticsAgentPersistenceOptions Persistence { get; set; } = new();

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetainedTurns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHistoryBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxInputCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxResponseCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxModelIterationsPerTurn, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTurnDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxToolCallsPerTurn, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxToolArgumentsBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxToolResultBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxToolProgressEventsPerCall, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(EventBufferCapacity, 1);
    }
}

// Persisted transcript storage is opt in while its recovery semantics stabilize; the flag is a
// startup-only setting and is not hot reloaded.
public sealed class MaieuticsAgentPersistenceOptions
{
    public bool Enabled { get; set; }
}
