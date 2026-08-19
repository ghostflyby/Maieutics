using FluentAssertions;
using Maieutics.Execution;
using Maieutics.Permissions;
using Maieutics.Processes;

namespace Maieutics.Jupyter.Tests;

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
