using Maieutics.Execution;
using Maieutics.Permissions;

namespace Maieutics.Product.Tests;

public sealed class PermissionLayerTests
{
    [Fact]
    public void EmptyLayersProduceEmptyPolicy()
    {
        var policy = PermissionLayerStore.Build([], CreateVariables());

        Assert.Empty(policy.Kinds);
        Assert.Equal(PermissionKindRules.Empty, policy.For(PermissionKind.Read));
    }

    [Fact]
    public void LaterLayerAllowancesAppendToEarlierOnes()
    {
        var baseline = Layer(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["/kernel"] }));
        var session = Layer(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["${var.workspace}/cache"] }));

        var policy = PermissionLayerStore.Build([baseline, session], CreateVariables(workspace: "/ws"));

        Assert.Equal(["/kernel", "/ws/cache"], policy.For(PermissionKind.Read).Allow);
    }

    [Fact]
    public void DenialsAlwaysWinOverGrantsRegardlessOfLayerOrder()
    {
        var baseline = Layer(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["/kernel"], Deny = ["/kernel/secret"] }));
        var session = Layer(
            (PermissionKind.Read, new PermissionKindRules { AllowAll = true, Deny = ["/kernel/secret/gift"] }));

        var policy = PermissionLayerStore.Build([baseline, session], CreateVariables());

        var read = policy.For(PermissionKind.Read);
        Assert.True(read.AllowAll);
        Assert.Equal(["/kernel/secret", "/kernel/secret/gift"], read.Deny);
    }

    [Fact]
    public void DenyAllInOneLayerStaysInEffectThroughLaterGrants()
    {
        var appDefaults = Layer(
            (PermissionKind.Write, new PermissionKindRules { DenyAll = true }));
        var session = Layer(
            (PermissionKind.Write, new PermissionKindRules { Allow = ["/data"] }));

        var policy = PermissionLayerStore.Build([appDefaults, session], CreateVariables());

        var write = policy.For(PermissionKind.Write);
        Assert.True(write.DenyAll);
        Assert.Equal(["/data"], write.Allow);
    }

    [Fact]
    public void MultipleKindsComposeIndependently()
    {
        var layer = Layer(
            (PermissionKind.Net, new PermissionKindRules { Allow = ["localhost:8080"] }),
            (PermissionKind.Sys, new PermissionKindRules { Allow = ["hostname"] }),
            (PermissionKind.Env, new PermissionKindRules { Allow = ["HOME"] }));

        var policy = PermissionLayerStore.Build([layer], CreateVariables());

        Assert.Equal(["localhost:8080"], policy.For(PermissionKind.Net).Allow);
        Assert.Equal(["hostname"], policy.For(PermissionKind.Sys).Allow);
        Assert.Equal(["HOME"], policy.For(PermissionKind.Env).Allow);
        Assert.Equal(PermissionKindRules.Empty, policy.For(PermissionKind.Run));
    }

    [Fact]
    public void VariablePatternsAreExpandedAtBuildTime()
    {
        var layer = Layer(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["${var.workspace}"] }));

        var policy = PermissionLayerStore.Build([layer], CreateVariables(workspace: "/ws"));

        Assert.Equal(["/ws"], policy.For(PermissionKind.Read).Allow);
    }

    [Fact]
    public void UnknownVariableFailsBuildWithTypedError()
    {
        var layer = Layer(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["${var.missing}"] }));

        var build = () => PermissionLayerStore.Build([layer], CreateVariables());

        Assert.Equal("permission_variable_unknown", Assert.ThrowsAny<PermissionException>(build.Invoke).Code);
    }

    [Fact]
    public void EmptyPatternFailsBuildWithTypedError()
    {
        // An empty path pattern would prefix-match everything, so it must fail the build
        // instead of silently widening the grant.
        var layer = Layer(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["/tmp"], Deny = [""] }));

        var build = () => PermissionLayerStore.Build([layer], CreateVariables());

        Assert.Equal("permission_pattern_empty", Assert.ThrowsAny<PermissionException>(build.Invoke).Code);
    }

    [Fact]
    public void PatternExpandingToEmptyFailsBuildWithTypedError()
    {
        var layer = Layer(
            (PermissionKind.Write, new PermissionKindRules { Allow = ["${var.workspace}"] }));

        // The variable resolves, but to an empty value: an empty pattern would match everything.
        var build = () => PermissionLayerStore.Build([layer], CreateVariables(workspace: string.Empty));

        Assert.Equal("permission_pattern_empty", Assert.ThrowsAny<PermissionException>(build.Invoke).Code);
    }

    [Fact]
    public void StarPatternFailsBuildWithTypedError()
    {
        // '*' is the allow-all/deny-all flag's spelling, never a list entry: in a path kind it
        // would silently match nothing, in a prefix kind it would match everything.
        var layer = Layer(
            (PermissionKind.Net, new PermissionKindRules { Allow = ["*"] }));

        var build = () => PermissionLayerStore.Build([layer], CreateVariables());

        Assert.Equal("permission_pattern_invalid", Assert.ThrowsAny<PermissionException>(build.Invoke).Code);
    }

    [Fact]
    public void CommaPatternFailsBuildWithTypedError()
    {
        // Deno renders a kind's list as one comma-separated flag, so a pattern containing a
        // comma cannot be expressed; it must fail the build instead of silently narrowing the
        // grant to the pieces Deno parses.
        var layer = Layer(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["/tmp,a"] }));

        var build = () => PermissionLayerStore.Build([layer], CreateVariables());

        Assert.Equal("permission_pattern_invalid", Assert.ThrowsAny<PermissionException>(build.Invoke).Code);
    }

    private static PermissionLayer Layer(params (PermissionKind Kind, PermissionKindRules Rules)[] kinds)
    {
        return new PermissionLayer
        {
            Kinds = kinds.ToDictionary(static entry => entry.Kind, static entry => entry.Rules)
        };
    }

    private static VariableTable CreateVariables(string? workspace = null)
    {
        var source = new FakeVariableSource(workspace);
        return new VariableTable(source);
    }

    private sealed class FakeVariableSource(string? workspace) : IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return name == "workspace" ? workspace : null;
        }
    }
}
