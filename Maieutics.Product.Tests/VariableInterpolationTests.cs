using Maieutics.Execution;
using Maieutics.Permissions;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]

public sealed class VariableInterpolationTests
{
    [Fact]
    public void PatternsWithoutTokensPassThroughUnchanged()
    {
        var table = CreateTable();

        Assert.Equal("/etc/ssl/certs", table.Expand("/etc/ssl/certs"));
        Assert.Equal("localhost:8080", table.Expand("localhost:8080"));
        Assert.Equal("", table.Expand(""));
    }

    [Fact]
    public void EnvVariablesResolveFromTheKernelEnvironment()
    {
        using var scope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["MAIEUTICS_TEST_CACHE"] = "/tmp/maieutics-test-cache"
        });
        var table = CreateTable();

        Assert.Equal("/tmp/maieutics-test-cache/esbuild", table.Expand("${env.MAIEUTICS_TEST_CACHE}/esbuild"));
    }

    [Fact]
    public void FixedVarVariablesResolveFromTheTable()
    {
        var table = CreateTable(fixedVariables: new Dictionary<string, string>
        {
            ["dataDir"] = "/Library/Application Support/Maieutics"
        });

        Assert.Equal("/Library/Application Support/Maieutics/maieutics.json", table.Expand("${var.dataDir}/maieutics.json"));
    }

    [Fact]
    public void WorkspaceVariableResolvesThroughTheSourceSeam()
    {
        var table = CreateTable(workspace: "/ws");

        Assert.Equal("/ws/cache", table.Expand("${var.workspace}/cache"));
    }

    [Fact]
    public void MultipleTokensInOnePatternExpandLeftToRight()
    {
        using var scope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["MAIEUTICS_TEST_TMP"] = "/tmp"
        });
        var table = CreateTable(workspace: "/ws");

        Assert.Equal("/ws/tmp/x", table.Expand("${var.workspace}${env.MAIEUTICS_TEST_TMP}/x"));
    }

    [Fact]
    public void UnknownEnvVariableFailsWithTypedError()
    {
        var table = CreateTable();

        var expand = () => table.Expand("${env.MAIEUTICS_VARIABLE_THAT_DOES_NOT_EXIST}");

        Assert.Equal("permission_variable_unknown", Assert.ThrowsAny<PermissionException>(() => expand.Invoke()).Code);
    }

    [Fact]
    public void UnknownVarVariableFailsWithTypedError()
    {
        var table = CreateTable();

        var expand = () => table.Expand("${var.missing}");

        Assert.Equal("permission_variable_unknown", Assert.ThrowsAny<PermissionException>(() => expand.Invoke()).Code);
    }

    [Fact]
    public void UnclosedTokenFailsWithTypedError()
    {
        var table = CreateTable();

        var expand = () => table.Expand("${env.HOME");

        Assert.Equal("permission_variable_malformed", Assert.ThrowsAny<PermissionException>(() => expand.Invoke()).Code);
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

        Assert.Equal("permission_variable_malformed", Assert.ThrowsAny<PermissionException>(() => expand.Invoke()).Code);
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
