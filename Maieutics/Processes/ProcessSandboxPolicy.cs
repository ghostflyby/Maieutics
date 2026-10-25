using Maieutics.Permissions;

namespace Maieutics.Processes;

/// <summary>How the kernel treats sandbox enforcement for processes spawned on behalf of a
/// session's Deno child (ADR 0045): <see cref="Off"/> leaves those spawns on the child's own
/// Deno permission path (the broker's run check — the pre-ADR-0045 behavior);
/// <see cref="Prefer"/> routes them through the kernel launch seam and wraps them in a
/// platform sandbox when a launcher exists, launching unwrapped (with the run gate, the
/// environment allowlist, and a warning) when it does not; <see cref="Require"/> refuses the
/// spawn when no launcher exists. Terminal and MCP children always launch through the kernel
/// seam regardless of this mode.</summary>
internal enum ProcessSandboxMode
{
    Off,
    Prefer,
    Require
}

/// <summary>The decision one spawn request resolves to: a typed refusal, or the argument
/// vector to actually launch (the requested executable verbatim, or a platform sandbox wrapper
/// carrying it) together with the environment the child receives — always captured from the
/// session's effective policy by <see cref="ProcessEnvironment"/>, never the kernel's own
/// environment.</summary>
internal abstract record ProcessLaunchPlan
{
    internal static ProcessLaunchPlan Denied(string reason)
    {
        return new DeniedPlan(reason);
    }

    internal static ProcessLaunchPlan Launch(
        IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string?> environment,
        bool sandboxed)
    {
        return new LaunchPlan(argv, environment, sandboxed);
    }

    internal sealed record DeniedPlan(string Reason) : ProcessLaunchPlan;

    internal sealed record LaunchPlan(
        IReadOnlyList<string> Argv,
        IReadOnlyDictionary<string, string?> Environment,
        bool Sandboxed) : ProcessLaunchPlan;
}

/// <summary>The process-sandbox enforcement seam for spawns requested from inside a Deno child
/// (ADR 0045; the seat AGENTS.md invariant 22 anticipated): the kernel-side launch path calls
/// the one registered implementation with the calling session's <see cref="EffectivePolicy"/>
/// snapshot and the requested executable and arguments, and launches exactly what comes back.
/// The default implementation derives the sandbox from that same snapshot — the run rules gate
/// the executable, the read/write rules shape the filesystem bindings, the net rules decide
/// the network, and <see cref="ProcessEnvironment"/> builds the environment — so one policy
/// answers for every surface (ADR 0018 §12). Internal Deno children themselves stay exempt
/// (invariant 22); this seam judges only the processes they ask the kernel to start.</summary>
internal interface IProcessSandboxPolicy
{
    /// <summary>Plans one spawn: refuses executables the policy's <c>run</c> rules deny, and
    /// otherwise returns the launch vector — wrapped in a platform sandbox derived from the
    /// policy when the mode and an available launcher allow it.</summary>
    ProcessLaunchPlan Plan(EffectivePolicy policy, string executable, IReadOnlyList<string> arguments);
}
