using FluentAssertions;
using Maieutics.Execution;
using Maieutics.Permissions;
using Maieutics.Processes;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]

public sealed class ProcessEnvironmentTests
{
    [Fact]
    public void DefaultPolicyYieldsTheDefaultAllowlistWithTermPinned()
    {
        using var scope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["MAIEUTICS_TEST_ONLY"] = "should-not-appear"
        });
        Environment.SetEnvironmentVariable("MAIEUTICS_TEST_ALSO_ABSENT", null);
        var policy = EffectivePolicy.Default;

        var environment = ProcessEnvironment.Capture(policy);

        environment.Should().ContainKey("TERM").WhoseValue.Should().Be(ProcessEnvironment.TermName);
        environment.Should().NotContainKey("MAIEUTICS_TEST_ONLY");
        environment.Should().NotContainKey("MAIEUTICS_TEST_ALSO_ABSENT");
    }

    [Fact]
    public void PolicyEnvGrantsRestrictTheChildEnvironment()
    {
        using var scope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["MAIEUTICS_TEST_KEEP"] = "kept",
            ["MAIEUTICS_TEST_DROP"] = "dropped"
        });
        var policy = Build(
            (PermissionKind.Env, new PermissionKindRules { Allow = ["MAIEUTICS_TEST_KEEP"] }));

        var environment = ProcessEnvironment.Capture(policy);

        environment.Should().ContainKey("MAIEUTICS_TEST_KEEP").WhoseValue.Should().Be("kept");
        environment.Should().NotContainKey("MAIEUTICS_TEST_DROP");
        environment.Should().ContainKey("TERM");
    }

    [Fact]
    public void EmptyEnvGrantsFallBackToTheDefaultAllowlist()
    {
        using var scope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["MAIEUTICS_TEST_DROP"] = "dropped"
        });
        var policy = Build(
            (PermissionKind.Env, new PermissionKindRules { Allow = [] }));

        var environment = ProcessEnvironment.Capture(policy);

        environment.Should().ContainKey("PATH").WhoseValue.Should().Be("/usr/bin");
        environment.Should().NotContainKey("MAIEUTICS_TEST_DROP");
    }

    [Fact]
    public void ProfileDenyRemovesBaselineAllowedNames()
    {
        using var scope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["MAIEUTICS_TEST_KEEP"] = "kept",
            ["MAIEUTICS_TEST_DROP"] = "dropped"
        });
        // Baseline grants both names; the workspace profile denies one of them. Denials win
        // (AGENTS.md invariant 20), so the child must not see the denied variable.
        var policy = PermissionLayerStore.Build(
        [
            new PermissionLayer
            {
                Kinds = new Dictionary<PermissionKind, PermissionKindRules>
                {
                    [PermissionKind.Env] = new()
                    {
                        Allow = ["MAIEUTICS_TEST_KEEP", "MAIEUTICS_TEST_DROP"]
                    }
                }
            },
            new PermissionLayer
            {
                Kinds = new Dictionary<PermissionKind, PermissionKindRules>
                {
                    [PermissionKind.Env] = new() { Deny = ["MAIEUTICS_TEST_DROP"] }
                }
            }
        ], new VariableTable(new FakeVariableSource()));

        var environment = ProcessEnvironment.Capture(policy);

        environment.Should().ContainKey("MAIEUTICS_TEST_KEEP").WhoseValue.Should().Be("kept");
        environment.Should().NotContainKey("MAIEUTICS_TEST_DROP");
    }

    [Fact]
    public void PrefixDenyRemovesEveryMatchingName()
    {
        using var scope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["MAIEUTICS_TEST_KEEP"] = "kept",
            ["MAIEUTICS_TEST_KEEP_TOO"] = "kept-too"
        });
        // The deny pattern uses the same prefix semantics the permission resolver applies to
        // env names, so one prefix deny removes the whole family.
        var policy = Build(
            (PermissionKind.Env, new PermissionKindRules
            {
                Allow = ["MAIEUTICS_TEST_KEEP", "MAIEUTICS_TEST_KEEP_TOO"],
                Deny = ["MAIEUTICS_TEST"]
            }));

        var environment = ProcessEnvironment.Capture(policy);

        environment.Should().NotContainKey("MAIEUTICS_TEST_KEEP");
        environment.Should().NotContainKey("MAIEUTICS_TEST_KEEP_TOO");
        environment.Should().ContainKey("TERM");
    }

    [Fact]
    public void DenyAllEmptiesTheCapturedEnvironment()
    {
        using var scope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/test"
        });
        var policy = Build(
            (PermissionKind.Env, new PermissionKindRules { Allow = ["PATH"], DenyAll = true }));

        var environment = ProcessEnvironment.Capture(policy);

        environment.Should().NotContainKey("PATH");
        environment.Should().NotContainKey("HOME");
        // TERM stays pinned so the VT renderer keeps working regardless of the policy.
        environment.Should().ContainKey("TERM").WhoseValue.Should().Be(ProcessEnvironment.TermName);
    }

    private static EffectivePolicy Build(params (PermissionKind Kind, PermissionKindRules Rules)[] kinds)
    {
        return PermissionLayerStore.Build(
            [new PermissionLayer { Kinds = kinds.ToDictionary(static entry => entry.Kind, static entry => entry.Rules) }],
            new VariableTable(new FakeVariableSource()));
    }

    private sealed class FakeVariableSource : IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return null;
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> original = new(StringComparer.Ordinal);

        internal EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (name, value) in values)
            {
                original.Add(name, Environment.GetEnvironmentVariable(name));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in original) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
