using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Permissions;

namespace Maieutics.Product.Tests;

/// <summary>Drives the full acquisition path: the consumer's baseline overlaid with the
/// configuration-owned layers in invariant-20 order, denials kept alongside grants for the
/// enforcer's deny-wins precedence.</summary>
public sealed class PermissionAcquisitionTests
{
    [Fact]
    public void BaselineAloneComposesWithoutConfigurationLayers()
    {
        var acquirer = new PermissionPolicyAcquirer(() => new NullLayerSource(), new NullVariableSource());
        var baseline = Layer("read", allow: ["/workspace"]);

        var policy = acquirer.Acquire(baseline);

        policy.For(PermissionKind.Read).Allow.Should().Equal("/workspace");
        policy.For(PermissionKind.Read).Deny.Should().BeEmpty();
    }

    [Fact]
    public void LayersComposeInInvariantOrderAndAppendBothDirections()
    {
        var baseline = Layer("read", allow: ["/builtin"]);
        var source = new StubLayerSource(
            appDefaults: Layer("read", allow: ["/app-allow"], deny: ["/app-deny"]),
            workspaceProfile: Layer("read", allow: ["/workspace-allow"]),
            sessionOverride: Layer("read", allow: ["/override-allow"]));
        var acquirer = new PermissionPolicyAcquirer(() => source, new NullVariableSource());
        var sessionId = AgentSessionId.Create();

        var policy = acquirer.Acquire(baseline, sessionId);

        // Lists append in layer order; the enforcer applies deny-wins over the merged lists.
        policy.For(PermissionKind.Read).Allow.Should().Equal(
            "/builtin", "/app-allow", "/workspace-allow", "/override-allow");
        policy.For(PermissionKind.Read).Deny.Should().Equal("/app-deny");
    }

    [Fact]
    public void NullLayersAreSkippedAndTheOverrideIsScopedToItsSession()
    {
        var baseline = Layer("net", allow: ["localhost:80"]);
        var registry = new PermissionOverrideRegistry();
        var sessionId = AgentSessionId.Create();
        registry.Set(sessionId, Layer("net", deny: ["evil.example.com:443"]));
        var source = new RegistryLayerSource(registry);
        var acquirer = new PermissionPolicyAcquirer(() => source, new NullVariableSource());

        acquirer.Acquire(baseline, sessionId).For(PermissionKind.Net).Deny
            .Should().Equal("evil.example.com:443");
        // Another session (or no session) acquires without the override.
        acquirer.Acquire(baseline).For(PermissionKind.Net).Deny.Should().BeEmpty();
        acquirer.Acquire(baseline, AgentSessionId.Create()).For(PermissionKind.Net).Deny.Should().BeEmpty();
    }

    [Fact]
    public void AllowAllAndDenyAllMergeAcrossLayers()
    {
        var baseline = Layer("env", allowAll: true);
        var source = new StubLayerSource(
            appDefaults: Layer("env", denyAll: true),
            workspaceProfile: null,
            sessionOverride: null);
        var acquirer = new PermissionPolicyAcquirer(() => source, new NullVariableSource());

        var policy = acquirer.Acquire(baseline);

        policy.For(PermissionKind.Env).AllowAll.Should().BeTrue();
        policy.For(PermissionKind.Env).DenyAll.Should().BeTrue();
    }

    [Fact]
    public void PatternsExpandAgainstTheVariableTableAtAcquisitionTime()
    {
        var source = new StubLayerSource(
            appDefaults: Layer("read", allow: ["${var.workspace}/notes"]),
            workspaceProfile: null,
            sessionOverride: null);
        var acquirer = new PermissionPolicyAcquirer(() => source, new WorkspaceVariableSource("/tmp/ws"));

        var policy = acquirer.Acquire(PermissionLayer.Empty);

        // Variable expansion is pure string substitution; the pattern's own
        // separator is kept verbatim, so the expectation is a literal, not a
        // platform Path.Combine result.
        policy.For(PermissionKind.Read).Allow.Should().Equal("/tmp/ws/notes");
    }

    [Fact]
    public void UnresolvableVariablesFailTheAcquisitionLoudly()
    {
        var source = new StubLayerSource(
            appDefaults: Layer("read", allow: ["${var.doesNotExist}/x"]),
            workspaceProfile: null,
            sessionOverride: null);
        var acquirer = new PermissionPolicyAcquirer(() => source, new WorkspaceVariableSource("/tmp/ws"));

        var acquire = () => acquirer.Acquire(PermissionLayer.Empty);

        acquire.Should().Throw<PermissionException>().WithMessage("*doesNotExist*");
    }

    [Fact]
    public void OverrideRegistryStoresClearsAndScopesBySession()
    {
        var registry = new PermissionOverrideRegistry();
        var sessionId = AgentSessionId.Create();
        var layer = Layer("read", allow: ["/override"]);

        registry.TryGet(sessionId).Should().BeNull();
        registry.Set(sessionId, layer);
        registry.TryGet(sessionId).Should().BeSameAs(layer);
        registry.Clear(sessionId);
        registry.TryGet(sessionId).Should().BeNull();
    }

    private static PermissionLayer Layer(
        string kind,
        IReadOnlyList<string>? allow = null,
        IReadOnlyList<string>? deny = null,
        bool allowAll = false,
        bool denyAll = false)
    {
        var kinds = kind switch
        {
            "read" => PermissionKind.Read,
            "net" => PermissionKind.Net,
            "env" => PermissionKind.Env,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return new PermissionLayer
        {
            Kinds = new Dictionary<PermissionKind, PermissionKindRules>
            {
                [kinds] = new PermissionKindRules
                {
                    AllowAll = allowAll,
                    DenyAll = denyAll,
                    Allow = allow ?? [],
                    Deny = deny ?? []
                }
            }
        };
    }

    private sealed class NullLayerSource : IPermissionLayerSource
    {
        public PermissionLayer? AppDefaults => null;
        public PermissionLayer? WorkspaceProfile => null;
        public PermissionLayer? GetSessionOverride(AgentSessionId sessionId) => null;
    }

    private sealed class StubLayerSource(
        PermissionLayer? appDefaults,
        PermissionLayer? workspaceProfile,
        PermissionLayer? sessionOverride) : IPermissionLayerSource
    {
        public PermissionLayer? AppDefaults => appDefaults;

        public PermissionLayer? WorkspaceProfile => workspaceProfile;

        public PermissionLayer? GetSessionOverride(AgentSessionId sessionId) => sessionOverride;
    }

    private sealed class RegistryLayerSource(PermissionOverrideRegistry registry) : IPermissionLayerSource
    {
        public PermissionLayer? AppDefaults => null;

        public PermissionLayer? WorkspaceProfile => null;

        public PermissionLayer? GetSessionOverride(AgentSessionId sessionId) => registry.TryGet(sessionId);
    }

    private sealed class NullVariableSource : IPermissionVariableSource
    {
        public string? GetVariable(string name) => null;
    }

    /// <summary>Mirrors the Workspace seam's variable semantics for the fixed
    /// <c>var.workspace</c> key the acquisition tests exercise.</summary>
    private sealed class WorkspaceVariableSource(string root) : IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return name == "workspace" ? root : null;
        }
    }
}
