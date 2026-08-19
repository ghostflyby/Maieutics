using FluentAssertions;
using Maieutics.Execution;
using Maieutics.Permissions;

namespace Maieutics.Jupyter.Tests;

public sealed class DenoPermissionRendererTests
{
    [Fact]
    public void EmptyPolicyRendersNoFlags()
    {
        var policy = Build([]);

        DenoPermissionRenderer.Render(policy).Should().BeEmpty();
    }

    [Fact]
    public void AllowlistsRenderAsCommaJoinedFlagsInKindOrder()
    {
        var policy = Build(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["/kernel", "/ws"] }),
            (PermissionKind.Net, new PermissionKindRules { Allow = ["localhost:8080", "api.example.com"] }),
            (PermissionKind.Env, new PermissionKindRules { Allow = ["HOME", "PATH"] }));

        DenoPermissionRenderer.Render(policy).Should().Equal(
            "--allow-read=/kernel,/ws",
            "--allow-net=localhost:8080,api.example.com",
            "--allow-env=HOME,PATH");
    }

    [Fact]
    public void AllowAllRendersTheUnsuffixedFlag()
    {
        var policy = Build(
            (PermissionKind.Run, new PermissionKindRules { AllowAll = true }));

        DenoPermissionRenderer.Render(policy).Should().Equal("--allow-run");
    }

    [Fact]
    public void DenyListsRenderAsDenyFlags()
    {
        var policy = Build(
            (PermissionKind.Read, new PermissionKindRules { Deny = ["/ws/secret"] }),
            (PermissionKind.Write, new PermissionKindRules { DenyAll = true }));

        DenoPermissionRenderer.Render(policy).Should().Equal(
            "--deny-read=/ws/secret",
            "--deny-write");
    }

    [Fact]
    public void AllowAndDenyForTheSameKindRenderBothFlags()
    {
        var policy = Build(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["/ws"], Deny = ["/ws/secret"] }));

        DenoPermissionRenderer.Render(policy).Should().Equal(
            "--allow-read=/ws",
            "--deny-read=/ws/secret");
    }

    [Fact]
    public void FlagsAreAlreadyExpanded()
    {
        var policy = Build(
            [(PermissionKind.Ffi, new PermissionKindRules { Allow = ["${var.workspace}/lib.dylib"] })],
            new VariableTable(
                new FakeVariableSource("/ws"),
                fixedVariables: null,
                getEnvironmentVariable: null));

        DenoPermissionRenderer.Render(policy).Should().Equal("--allow-ffi=/ws/lib.dylib");
    }

    [Fact]
    public void NetUnixSocketsAndSysNamesRenderVerbatim()
    {
        var policy = Build(
            (PermissionKind.Net, new PermissionKindRules { Allow = ["unix:/tmp/maieutics.sock"] }),
            (PermissionKind.Sys, new PermissionKindRules { Allow = ["hostname", "loadavg"] }),
            (PermissionKind.Import, new PermissionKindRules { Allow = ["npm:", "https://deno.land/"] }));

        DenoPermissionRenderer.Render(policy).Should().Equal(
            "--allow-net=unix:/tmp/maieutics.sock",
            "--allow-sys=hostname,loadavg",
            "--allow-import=npm:,https://deno.land/");
    }

    private static EffectivePolicy Build(
        params (PermissionKind Kind, PermissionKindRules Rules)[] kinds)
    {
        return Build(kinds, new VariableTable(new FakeVariableSource(null)));
    }

    private static EffectivePolicy Build(
        (PermissionKind Kind, PermissionKindRules Rules)[] kinds,
        VariableTable variables)
    {
        return PermissionLayerStore.Build(
            [new PermissionLayer { Kinds = kinds.ToDictionary(static entry => entry.Kind, static entry => entry.Rules) }],
            variables);
    }

    private sealed class FakeVariableSource(string? workspace) : IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return name == "workspace" ? workspace : null;
        }
    }
}
