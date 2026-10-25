using Maieutics.Agent;
using Maieutics.Permissions;

namespace Maieutics.Execution;

/// <summary>
///     The model-tool surface's permission baseline and the shared judge for the kernel's
///     workspace read/write gates. The baseline grants read and write of the workspace root —
///     exactly what the kernel's own containment gave these tools before a policy existed — so an
///     unconfigured host keeps its behavior while configuration denials subtract uniformly from
///     local and bridged calls (AGENTS.md invariant 20): attaching an IDE frontend can never
///     change what a session may read or write. Instruction surfaces keep their own stricter
///     gate and are not loosened by this baseline.
/// </summary>
internal static class WorkspacePermissionSurface
{
    /// <summary>The calling-surface baseline for model-initiated workspace tools: read and write
    /// of the workspace root, the containment the tools enforced before permissions
    /// existed.</summary>
    internal static PermissionLayer ModelToolBaseline(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return new PermissionLayer
        {
            Kinds = new Dictionary<PermissionKind, PermissionKindRules>
            {
                [PermissionKind.Read] = new() { Allow = [workspaceRoot] },
                [PermissionKind.Write] = new() { Allow = [workspaceRoot] }
            }
        };
    }

    /// <summary>Acquires the surface policy for the calling session and enforces one physical
    /// path against it. Denials win over the baseline grant (invariant 20); a host without the
    /// acquirer keeps the legacy containment-only posture. The error codes match the bridged
    /// script surface so a denial reads the same whichever path served the call.</summary>
    internal static void Enforce(
        PermissionPolicyAcquirer? acquirer,
        Workspace workspace,
        PermissionKind kind,
        string physicalPath,
        string displayPath,
        AgentSessionId? sessionId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (acquirer is null) return;

        var policy = Acquire(acquirer, workspace, sessionId);
        if (!IsAllowed(policy, kind, physicalPath))
            throw Forbidden(kind, displayPath);
    }

    /// <summary>Acquires the surface policy once for a batch of judgements (a search scanning
    /// many files), so per-file checks share one composed snapshot.</summary>
    internal static EffectivePolicy Acquire(
        PermissionPolicyAcquirer acquirer,
        Workspace workspace,
        AgentSessionId? sessionId)
    {
        ArgumentNullException.ThrowIfNull(acquirer);
        return acquirer.Acquire(ModelToolBaseline(workspace.Capture().RootPath), sessionId);
    }

    /// <summary>Whether one physical path is permitted under an already-acquired policy:
    /// deny-wins over the grants, matching the broker resolver's precedence.</summary>
    internal static bool IsAllowed(EffectivePolicy policy, PermissionKind kind, string physicalPath)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var rules = policy.For(kind);
        if (rules.DenyAll || rules.Deny.Any(pattern => PermissionMatching.MatchesPath(pattern, physicalPath)))
            return false;
        return rules.AllowAll ||
               rules.Allow.Any(pattern => PermissionMatching.MatchesPath(pattern, physicalPath));
    }

    internal static AgentToolException Forbidden(PermissionKind kind, string displayPath)
    {
        return new AgentToolException(
            kind == PermissionKind.Write ? "workspace_write_forbidden" : "workspace_read_forbidden",
            $"The session's permission policy denies this {PermissionKinds.GetName(kind)} of '{displayPath}'.");
    }

    /// <summary>Judges one instruction-surface write against the kernel-internal
    /// <c>instructions</c> domain (ADR 0032 decision 1): the workspace-root write baseline never
    /// loosens this gate — only the session's explicit allows (physical workspace paths) admit,
    /// and a matching deny refuses even then. The gate stays dormant in hosts without the
    /// acquirer (the legacy containment-only posture) and denies fail-closed without a session.
    /// The relative path is judged at its physical address, so profile layers (whose path
    /// patterns the loader resolves to absolute) and session overrides (whose
    /// <c>${var.workspace}</c> tokens expand at acquisition) speak one address space.</summary>
    internal static void EnforceInstructions(
        PermissionPolicyAcquirer? acquirer,
        Workspace workspace,
        string relativePath,
        AgentSessionId? sessionId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (acquirer is null) return;
        // The workspace URI carries percent-encoded segments; every other gate judges the
        // decoded physical address, so the instruction gate decodes before classifying and
        // physicalizing — an allow written with the real path must match a URI that encoded it.
        var decoded = Uri.UnescapeDataString(relativePath);
        if (InstructionSurface.IsInstructionSurface(decoded) is false) return;
        if (sessionId is null)
            throw InstructionForbidden(decoded);

        var rules = acquirer.Acquire(PermissionLayer.Empty, sessionId).For(PermissionKind.Instructions);
        var physical = PhysicalInstructionPath(workspace, decoded);
        if (rules.DenyAll ||
            rules.Deny.Any(pattern => PermissionMatching.MatchesPath(pattern, physical)))
            throw InstructionForbidden(decoded);
        if (rules.AllowAll ||
            rules.Allow.Any(pattern => PermissionMatching.MatchesPath(pattern, physical)))
            return;

        throw InstructionForbidden(decoded);
    }

    /// <summary>The physical address of one instruction surface: the workspace root joined with
    /// the normalized relative path.</summary>
    internal static string PhysicalInstructionPath(Workspace workspace, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return Path.GetFullPath(Path.Combine(
            workspace.Capture().RootPath,
            relativePath.Replace('\\', Path.DirectorySeparatorChar)));
    }

    private static AgentToolException InstructionForbidden(string relativePath)
    {
        return new AgentToolException(
            "instructions_write_forbidden",
            $"'{relativePath}' is an instruction surface: model-initiated writes require an explicit allow " +
            "in the calling session's permission policy.");
    }
}
