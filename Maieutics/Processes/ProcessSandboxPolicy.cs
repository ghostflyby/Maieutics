using Maieutics.Permissions;

namespace Maieutics.Processes;

/// <summary>Off uses the legacy launch paths. Prefer allows a user to authorize a one-shot
/// launch outside the sandbox. Require accepts only sandboxed launches.</summary>
internal enum ProcessSandboxMode
{
    Off,
    Prefer,
    Require
}

/// <summary>A refusal, a command that needs approval, or the argv and environment ready to
/// launch. The process owner must release any temporary directory attached to its plan.</summary>
internal abstract record ProcessLaunchPlan
{
    internal static ProcessLaunchPlan Denied(string reason)
    {
        return new DeniedPlan(reason);
    }

    internal static ProcessLaunchPlan Launch(
        IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string?> environment,
        bool sandboxed,
        string? temporaryDirectory)
    {
        return new LaunchPlan(argv, environment, sandboxed, temporaryDirectory);
    }

    internal sealed record DeniedPlan(string Reason) : ProcessLaunchPlan;

    internal sealed record ApprovalRequiredPlan(string Reason, LaunchPlan Candidate) : ProcessLaunchPlan;

    internal sealed record LaunchPlan(
        IReadOnlyList<string> Argv,
        IReadOnlyDictionary<string, string?> Environment,
        bool Sandboxed,
        string? TemporaryDirectory) : ProcessLaunchPlan
    {
        internal bool AllowsTerminalInput { get; init; } = true;

        internal void ReleaseTemporaryDirectory() => DeleteTemporaryDirectory(TemporaryDirectory);
    }

    internal static void DeleteTemporaryDirectory(string? path)
    {
        if (path is null) return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A retained file must not fail an otherwise settled launch.
        }
    }
}

/// <summary>Plans an Agent-controlled launch from the owning session's effective policy.</summary>
internal interface IProcessSandboxPolicy
{
    ProcessLaunchPlan Plan(
        EffectivePolicy policy,
        string executable,
        IReadOnlyList<string> arguments,
        bool outsideSandbox = false,
        string? workingDirectory = null,
        bool hasTerminal = false);
}
