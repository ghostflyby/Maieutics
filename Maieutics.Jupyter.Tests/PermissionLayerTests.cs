using FluentAssertions;
using Maieutics.Execution;
using Maieutics.Permissions;

namespace Maieutics.Jupyter.Tests;

public sealed class PermissionLayerTests
{
    [Fact]
    public void EmptyLayersProduceEmptyPolicy()
    {
        var policy = PermissionLayerStore.Build([], CreateVariables());

        policy.Kinds.Should().BeEmpty();
        policy.For(PermissionKind.Read).Should().Be(PermissionKindRules.Empty);
    }

    [Fact]
    public void LaterLayerAllowancesAppendToEarlierOnes()
    {
        var baseline = Layer(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["/kernel"] }));
        var session = Layer(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["${var.workspace}/cache"] }));

        var policy = PermissionLayerStore.Build([baseline, session], CreateVariables(workspace: "/ws"));

        policy.For(PermissionKind.Read).Allow.Should().Equal("/kernel", "/ws/cache");
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
        read.AllowAll.Should().BeTrue();
        read.Deny.Should().Equal("/kernel/secret", "/kernel/secret/gift");
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
        write.DenyAll.Should().BeTrue();
        write.Allow.Should().Equal("/data");
    }

    [Fact]
    public void MultipleKindsComposeIndependently()
    {
        var layer = Layer(
            (PermissionKind.Net, new PermissionKindRules { Allow = ["localhost:8080"] }),
            (PermissionKind.Sys, new PermissionKindRules { Allow = ["hostname"] }),
            (PermissionKind.Env, new PermissionKindRules { Allow = ["HOME"] }));

        var policy = PermissionLayerStore.Build([layer], CreateVariables());

        policy.For(PermissionKind.Net).Allow.Should().Equal("localhost:8080");
        policy.For(PermissionKind.Sys).Allow.Should().Equal("hostname");
        policy.For(PermissionKind.Env).Allow.Should().Equal("HOME");
        policy.For(PermissionKind.Run).Should().Be(PermissionKindRules.Empty);
    }

    [Fact]
    public void VariablePatternsAreExpandedAtBuildTime()
    {
        var layer = Layer(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["${var.workspace}"] }));

        var policy = PermissionLayerStore.Build([layer], CreateVariables(workspace: "/ws"));

        policy.For(PermissionKind.Read).Allow.Should().Equal("/ws");
    }

    [Fact]
    public void UnknownVariableFailsBuildWithTypedError()
    {
        var layer = Layer(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["${var.missing}"] }));

        var build = () => PermissionLayerStore.Build([layer], CreateVariables());

        build.Should().Throw<PermissionException>()
            .Which.Code.Should().Be("permission_variable_unknown");
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
