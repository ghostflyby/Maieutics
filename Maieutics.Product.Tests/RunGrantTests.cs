using Maieutics.Execution;
using Maieutics.Permissions;
using Maieutics.Processes;

namespace Maieutics.Product.Tests;

/// <summary>The run launch gate's full decision matrix: denials always win (AGENTS.md invariant
/// 20) and subtract from the grants instead of switching the gate into a mode that blocks
/// everything, while a policy without run rules keeps the unrestricted default (ADR 0018 §7
/// decision 2).</summary>
public sealed class RunGrantTests
{
    [Fact]
    public void DenyAllBeatsAllowAll()
    {
        var policy = Build(new PermissionKindRules { AllowAll = true, DenyAll = true });

        Assert.False(RunGrant.Allows(policy, "/usr/bin/anything"));
    }

    [Fact]
    public void DenyAllBeatsAnExplicitAllowlist()
    {
        var policy = Build(new PermissionKindRules { Allow = ["/usr/bin/ls"], DenyAll = true });

        Assert.False(RunGrant.Allows(policy, "/usr/bin/ls"));
    }

    [Fact]
    public void ADenyEntryBeatsAnAllowingDirectoryPrefix()
    {
        var policy = Build(new PermissionKindRules { Allow = ["/usr/bin"], Deny = ["/usr/bin/evil"] });

        Assert.False(RunGrant.Allows(policy, "/usr/bin/evil"));
        Assert.True(RunGrant.Allows(policy, "/usr/bin/ls"));
    }

    [Fact]
    public void ADenyWithoutAnAllowlistSubtractsInsteadOfBlockingEverything()
    {
        // A deny-only policy must refuse exactly the denied command, not brick every launch:
        // the unrestricted default stays minus the denials already applied.
        var policy = Build(new PermissionKindRules { Deny = ["/usr/bin/curl"] });

        Assert.False(RunGrant.Allows(policy, "/usr/bin/curl"));
        Assert.True(RunGrant.Allows(policy, "/bin/ls"));
    }

    [Fact]
    public void BareCommandNamesMatchExactly()
    {
        // run values are bare names or absolute paths; the boundary rule keeps a granted name
        // from admitting a longer name that merely shares its prefix.
        var policy = Build(new PermissionKindRules { Allow = ["deno"] });

        Assert.True(RunGrant.Allows(policy, "deno"));
        Assert.False(RunGrant.Allows(policy, "deno-evil"));
    }

    [Fact]
    public void EmptyRulesKeepTheUnrestrictedDefault()
    {
        Assert.True(RunGrant.Allows(EffectivePolicy.Default, "/usr/bin/anything"));
        Assert.True(RunGrant.Allows(Build(new PermissionKindRules { Allow = [], Deny = [] }), "/usr/bin/anything"));
    }

    private static EffectivePolicy Build(PermissionKindRules run)
    {
        return PermissionLayerStore.Build(
            [new PermissionLayer { Kinds = new Dictionary<PermissionKind, PermissionKindRules> { [PermissionKind.Run] = run } }],
            new VariableTable(new EmptyVariableSource()));
    }

    private sealed class EmptyVariableSource : IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return null;
        }
    }
}
