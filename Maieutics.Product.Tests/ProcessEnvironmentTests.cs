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

        Assert.Equal(ProcessEnvironment.TermName, Assert.Contains("TERM", environment));
        Assert.False(environment.ContainsKey("MAIEUTICS_TEST_ONLY"));
        Assert.False(environment.ContainsKey("MAIEUTICS_TEST_ALSO_ABSENT"));
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

        Assert.Equal("kept", Assert.Contains("MAIEUTICS_TEST_KEEP", environment));
        Assert.False(environment.ContainsKey("MAIEUTICS_TEST_DROP"));
        Assert.True(environment.ContainsKey("TERM"));
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

        Assert.Equal("/usr/bin", Assert.Contains("PATH", environment));
        Assert.False(environment.ContainsKey("MAIEUTICS_TEST_DROP"));
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

        Assert.Equal("kept", Assert.Contains("MAIEUTICS_TEST_KEEP", environment));
        Assert.False(environment.ContainsKey("MAIEUTICS_TEST_DROP"));
    }

    [Fact]
    public void EnvDenyRemovesExactlyTheDeniedName()
    {
        using var scope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["MAIEUTICS_TEST_KEEP"] = "kept",
            ["MAIEUTICS_TEST_KEEP_TOO"] = "kept-too"
        });
        // env names match exactly (the kind's match mode): one deny removes the named variable
        // only, never a longer name that merely shares its prefix.
        var policy = Build(
            (PermissionKind.Env, new PermissionKindRules
            {
                Allow = ["MAIEUTICS_TEST_KEEP", "MAIEUTICS_TEST_KEEP_TOO"],
                Deny = ["MAIEUTICS_TEST_KEEP"]
            }));

        var environment = ProcessEnvironment.Capture(policy);

        Assert.False(environment.ContainsKey("MAIEUTICS_TEST_KEEP"));
        Assert.Equal("kept-too", Assert.Contains("MAIEUTICS_TEST_KEEP_TOO", environment));
        Assert.True(environment.ContainsKey("TERM"));
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

        Assert.False(environment.ContainsKey("PATH"));
        Assert.False(environment.ContainsKey("HOME"));
        // TERM stays pinned so the VT renderer keeps working regardless of the policy.
        Assert.Equal(ProcessEnvironment.TermName, Assert.Contains("TERM", environment));
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
        private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

        internal EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (name, value) in values)
            {
                _original.Add(name, Environment.GetEnvironmentVariable(name));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _original) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
