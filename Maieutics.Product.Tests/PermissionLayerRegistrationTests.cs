using System.Text.Json.Nodes;
using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.Execution;
using Maieutics.Permissions;
using Maieutics.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maieutics.Product.Tests;

/// <summary>Host-level regression coverage for the permission acquisition path through the real
/// composition root (ADR 0018 Phase 5, AGENTS.md invariant 20). Every consumer acquires its
/// effective policy from the container-resolved acquirer, so the configuration-owned layers must
/// actually reach it: the app defaults bound from <c>Maieutics:Permissions</c>, the workspace
/// <c>permissions.json</c> profile beside the active maieutics.json, and the layer source the
/// acquirer resolves. The existing permission tests inject a hand-built layer source or construct
/// their consumers directly, which is why a missing registration and a collapsed overlay stayed
/// invisible.</summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class PermissionLayerRegistrationTests
{
    /// <summary>The shell the terminal tests start through the container-resolved registry. The
    /// substituted process factory never executes it, so the value only has to be a stable name a
    /// permission layer can list as a grant or a denial.</summary>
    private static string ShellExecutable => OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";

    [Fact(Timeout = 30_000)]
    public async Task ContainerResolvesTheLayerSourceAndComposesTheConfiguredAppDefaults()
    {
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = CreateTemporaryDirectory();
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration(
                root,
                new JsonObject
                {
                    ["Run"] = new JsonObject { ["Deny"] = new JsonArray(ShellExecutable) }
                }),
            TestContext.Current.CancellationToken);

        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            await using var host = builder.Build();
            await InitializeRuntimeAsync(host.Services, TestContext.Current.CancellationToken);

            var acquirer = host.Services.GetRequiredService<PermissionPolicyAcquirer>();
            var policy = acquirer.Acquire(PermissionLayer.Empty, AgentSessionId.Create());

            // The configured denial belongs to a configuration-owned layer, so it must appear in
            // the composed policy the enforcers consume instead of being dropped on the way there.
            Assert.Contains(ShellExecutable, policy.For(PermissionKind.Run).Deny);
        }
        finally
        {
            await DeleteTemporaryRootAsync(root);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task WorkspacePermissionsProfileBesideTheConfigurationFileContributesItsDeny()
    {
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = CreateTemporaryDirectory();
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration(root),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(root, "permissions.json"),
            """
            {
              "sets": {
                "default": { "read": { "deny": ["./secret"] } }
              },
              "default": "default"
            }
            """,
            TestContext.Current.CancellationToken);

        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            await using var host = builder.Build();
            await InitializeRuntimeAsync(host.Services, TestContext.Current.CancellationToken);

            var acquirer = host.Services.GetRequiredService<PermissionPolicyAcquirer>();
            var policy = acquirer.Acquire(PermissionLayer.Empty, AgentSessionId.Create());

            // Relative profile paths resolve against the profile file's directory (Deno's config
            // semantics), so the composed denial carries the resolved absolute path.
            Assert.Contains(Path.Combine(root, "secret"), policy.For(PermissionKind.Read).Deny);
        }
        finally
        {
            await DeleteTemporaryRootAsync(root);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task TerminalStartThroughTheContainerSucceedsUnderAMatchingRunAllowlist()
    {
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = CreateTemporaryDirectory();
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration(
                root,
                new JsonObject
                {
                    ["Run"] = new JsonObject { ["Allow"] = new JsonArray(ShellExecutable) }
                }),
            TestContext.Current.CancellationToken);

        try
        {
            var process = new FakeTerminalProcess();
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            builder.Services.RemoveAll<ITerminalProcessFactory>();
            builder.Services.AddSingleton<ITerminalProcessFactory>(new FakeTerminalProcessFactory(process));
            await using var host = builder.Build();
            await InitializeRuntimeAsync(host.Services, TestContext.Current.CancellationToken);

            var registry = host.Services.GetRequiredService<TerminalRegistry>();
            var run = registry.RunOnceAsync(
                AgentSessionId.Create(),
                ShellExecutable,
                [],
                TimeSpan.FromSeconds(5),
                new TerminalSnapshotRequest(),
                TestContext.Current.CancellationToken);
            // The substituted child exits as soon as the one-shot run waits on it; a run that never
            // started cannot settle this way.
            process.EndOfOutput();
            process.RaiseExited(0);

            var result = await run;

            Assert.True(result.Settled);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            await DeleteTemporaryRootAsync(root);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task TerminalStartThroughTheContainerRejectsAnExecutableOutsideTheRunAllowlist()
    {
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = CreateTemporaryDirectory();
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration(
                root,
                new JsonObject
                {
                    ["Run"] = new JsonObject
                    {
                        ["Allow"] = new JsonArray(Path.Combine(root, "some-other-shell"))
                    }
                }),
            TestContext.Current.CancellationToken);

        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            builder.Services.RemoveAll<ITerminalProcessFactory>();
            builder.Services.AddSingleton<ITerminalProcessFactory>(
                new FakeTerminalProcessFactory(new FakeTerminalProcess()));
            await using var host = builder.Build();
            await InitializeRuntimeAsync(host.Services, TestContext.Current.CancellationToken);

            var registry = host.Services.GetRequiredService<TerminalRegistry>();
            var start = () => registry.RunOnceAsync(
                AgentSessionId.Create(),
                ShellExecutable,
                [],
                TimeSpan.FromSeconds(5),
                new TerminalSnapshotRequest(),
                TestContext.Current.CancellationToken);

            // The configured layer restricts `run`, so the launch path must reject with its typed
            // tool failure. This is what proves the layer reached the enforcer: without it the
            // composed rules stay empty, the terminal check short-circuits, and the run settles.
            var failure = await Assert.ThrowsAnyAsync<AgentToolException>(async () => { await start.Invoke(); });
            Assert.Equal("terminal_start_failed", failure.Code);
        }
        finally
        {
            await DeleteTemporaryRootAsync(root);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-permission-layer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Deletes the test's temporary directory, retrying with an asynchronous backoff:
    /// the lock holder has no completion signal, so the retry waits by delay — asynchronously,
    /// never blocking a pool thread. Teardown gets its own budget, not the test token: after a
    /// timeout that token is canceled and would abort cleanup and leak the tree.</summary>
    private static Task DeleteTemporaryRootAsync(string root) =>
        TempDirectoryCleanup.DeleteWithRetryAsync(root);

    /// <summary>Builds the configuration the composition root binds. The workspace home points at
    /// the test's own directory so the host under test reads and writes only inside it; the
    /// optional permission section is the app-wide defaults layer.</summary>
    private static string CreateConfiguration(string root, JsonObject? permissions = null)
    {
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var maieutics = new JsonObject
        {
            ["Workspace"] = new JsonObject { ["Root"] = workspace }
        };
        if (permissions is not null) maieutics["Permissions"] = permissions;

        return new JsonObject { ["Maieutics"] = maieutics }.ToJsonString();
    }

    /// <summary>Completes the runtime's initial snapshot, which is what the layer source reads:
    /// until then the configuration object has no active snapshot to contribute layers from. The
    /// plugin host is handed over unstarted — an empty model catalog touches no plugin state, so
    /// these tests never need a real deno child.</summary>
    private static async Task InitializeRuntimeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var runtime = services.GetRequiredService<MaieuticsRuntimeConfiguration>();
        var pluginHosts = services.GetRequiredService<PluginHostManager>();
        await runtime.InitializeAsync(pluginHosts, cancellationToken);
    }

    private static Dictionary<string, string?> ClearedProviderEnvironment()
    {
        return new Dictionary<string, string?>
        {
            ["MAIEUTICS_CONFIG"] = null,
            ["MAIEUTICS_PROFILE"] = null,
            ["MAIEUTICS_PROVIDER"] = null,
            ["MAIEUTICS_MODEL"] = null,
            ["MAIEUTICS_OPENAI_API"] = null,
            ["MAIEUTICS_WORKSPACE"] = null,
            ["OPENAI_API_KEY"] = null,
            ["OPENAI_BASE_URL"] = null,
            ["ANTHROPIC_API_KEY"] = null,
            ["ANTHROPIC_BASE_URL"] = null,
            ["Maieutics__DefaultProfile"] = null,
            ["Maieutics__Model__Provider"] = null,
            ["Maieutics__Model__Name"] = null,
            ["Maieutics__Workspace__Root"] = null,
            ["Maieutics__Permissions__Run__Deny"] = null
        };
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> original = new(StringComparer.Ordinal);

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
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
