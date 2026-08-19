using FluentAssertions;
using Maieutics.Execution;
using Maieutics.Permissions;

namespace Maieutics.Jupyter.Tests;

public sealed class VariableInterpolationTests
{
    [Fact]
    public void PatternsWithoutTokensPassThroughUnchanged()
    {
        var table = CreateTable();

        table.Expand("/etc/ssl/certs").Should().Be("/etc/ssl/certs");
        table.Expand("localhost:8080").Should().Be("localhost:8080");
        table.Expand("").Should().Be("");
    }

    [Fact]
    public void EnvVariablesResolveFromTheKernelEnvironment()
    {
        using var scope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["MAIEUTICS_TEST_CACHE"] = "/tmp/maieutics-test-cache"
        });
        var table = CreateTable();

        table.Expand("${env.MAIEUTICS_TEST_CACHE}/esbuild").Should().Be("/tmp/maieutics-test-cache/esbuild");
    }

    [Fact]
    public void FixedVarVariablesResolveFromTheTable()
    {
        var table = CreateTable(fixedVariables: new Dictionary<string, string>
        {
            ["dataDir"] = "/Library/Application Support/Maieutics"
        });

        table.Expand("${var.dataDir}/maieutics.json").Should().Be("/Library/Application Support/Maieutics/maieutics.json");
    }

    [Fact]
    public void WorkspaceVariableResolvesThroughTheSourceSeam()
    {
        var table = CreateTable(workspace: "/ws");

        table.Expand("${var.workspace}/cache").Should().Be("/ws/cache");
    }

    [Fact]
    public void MultipleTokensInOnePatternExpandLeftToRight()
    {
        using var scope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["MAIEUTICS_TEST_TMP"] = "/tmp"
        });
        var table = CreateTable(workspace: "/ws");

        table.Expand("${var.workspace}${env.MAIEUTICS_TEST_TMP}/x").Should().Be("/ws/tmp/x");
    }

    [Fact]
    public void UnknownEnvVariableFailsWithTypedError()
    {
        var table = CreateTable();

        var expand = () => table.Expand("${env.MAIEUTICS_VARIABLE_THAT_DOES_NOT_EXIST}");

        expand.Should().Throw<PermissionException>()
            .Which.Code.Should().Be("permission_variable_unknown");
    }

    [Fact]
    public void UnknownVarVariableFailsWithTypedError()
    {
        var table = CreateTable();

        var expand = () => table.Expand("${var.missing}");

        expand.Should().Throw<PermissionException>()
            .Which.Code.Should().Be("permission_variable_unknown");
    }

    [Fact]
    public void UnclosedTokenFailsWithTypedError()
    {
        var table = CreateTable();

        var expand = () => table.Expand("${env.HOME");

        expand.Should().Throw<PermissionException>()
            .Which.Code.Should().Be("permission_variable_malformed");
    }

    [Theory]
    [InlineData("${other.x}")]
    [InlineData("${x}")]
    [InlineData("${}")]
    [InlineData("${env.}")]
    public void NonNamespaceTokensFailWithTypedError(string pattern)
    {
        var table = CreateTable();

        var expand = () => table.Expand(pattern);

        expand.Should().Throw<PermissionException>()
            .Which.Code.Should().Be("permission_variable_malformed");
    }

    private static VariableTable CreateTable(
        string? workspace = null,
        IReadOnlyDictionary<string, string>? fixedVariables = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        return new VariableTable(
            new FakeVariableSource(workspace),
            fixedVariables,
            getEnvironmentVariable);
    }

    private sealed class FakeVariableSource(string? workspace) : IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return name == "workspace" ? workspace : null;
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
