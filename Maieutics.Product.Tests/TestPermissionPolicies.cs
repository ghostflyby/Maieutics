using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Permissions;
using Maieutics.Processes;

namespace Maieutics.Product.Tests;

/// <summary>Test acquirers for code paths that must run through the Phase 5 acquisition
/// pipeline without contributing configuration layers (the built-in baseline composes alone).</summary>
internal static class TestPermissionPolicies
{
    public static PermissionPolicyAcquirer Unconfigured() => new(() => new UnconfiguredLayerSource(), new NullVariableSource());

    internal static ProcessExecution ProcessSandboxOff() => new(new PolicyProcessSandboxPolicy(ProcessSandboxMode.Off));

    private sealed class UnconfiguredLayerSource : IPermissionLayerSource
    {
        public PermissionLayer? AppDefaults => null;

        public PermissionLayer? WorkspaceProfile => null;

        public PermissionLayer? GetSessionOverride(AgentSessionId sessionId) => null;
    }

    private sealed class NullVariableSource : IPermissionVariableSource
    {
        public string? GetVariable(string name) => null;
    }
}
