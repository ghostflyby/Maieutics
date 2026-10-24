using Maieutics.Agent;
using Maieutics.Control;
using Maieutics.DenoRepl;
using Maieutics.Execution;
using Maieutics.Mcp;
using Maieutics.Permissions;
using Maieutics.Plugins;
using Maieutics.Plugins.Contributions;
using Maieutics.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Maieutics.Product.Tests;

/// <summary>
///     Regression coverage for F4: an MCP stdio server child used to start with a hardcoded
///     empty policy — the run gate never engaged and the child environment came from the MCP
///     SDK's curated list instead of the permission module (AGENTS.md invariant 19, ADR 0018
///     §7). The real launch path — the production default transport factory — must render the
///     owning scope's policy before the child is spawned, and a plugin-declared child's owning
///     scope is the declaring plugin. The run-grant refusal and allowance cases through that
///     launch seam are covered beside <c>McpServerGeneration.CreateAsync</c>'s owning-scope
///     policy parameter, which the fix introduces.
/// </summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class McpProcessPolicyTests
{
    // Headroom over the watcher→debounce→reload chain's real-time tail, while still firing
    // inside the 60 s Fact timeout so the failure reports at the true wait.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(50);

    [Fact(Timeout = 60_000)]
    public async Task APluginScopedEnvironmentGrantBoundsTheStdioChildEnvironment()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The probe child is a POSIX shell command.");

        // The owning scope of a plugin-declared stdio server child is the declaring plugin:
        // the child's environment must be rendered from the plugin's approval-gated env grant
        // through the shared process-policy allowlist — not from the MCP SDK's curated list
        // (AGENTS.md invariant 19). The plugin loads because an env grant, unlike a run or ffi
        // grant, does not require process isolation.
        var directory = Path.Combine(Path.GetTempPath(), $"mcp-plugin-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, "child-env.txt");
        var root = CreateEnvironmentGrantPluginRoot("mcp-env-scope", probe);
        var pluginId = Path.GetFileName(root);
        var clock = new FakeTimeProvider();
        var manager = CreateManager(root, clock);

        const string grantedName = "MAIEUTICS_MCP_PLUGIN_ENV";
        var previousGrant = Environment.GetEnvironmentVariable(grantedName);
        var previousTerm = Environment.GetEnvironmentVariable("TERM");
        Environment.SetEnvironmentVariable(grantedName, "granted-value");
        Environment.SetEnvironmentVariable("TERM", "maieutics-parent-term");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            deadline.CancelAfter(Deadline);
            await manager.StartAsync(deadline.Token);
            await manager.WaitUntilReadyAsync(deadline.Token);
            Assert.Equal(
                pluginId,
                Assert.Single(manager.GetRegistrations(McpDiscoverContributionKind.ExtensionKind)).PluginId);

            // The coordinator starts the declared server straight from the manifest snapshot;
            // the probe file is the child's environment dump. The shell creates the file before
            // /usr/bin/env's output lands in it, so on a loaded runner an existence check can
            // win that race and read an empty dump (observed on ubuntu-24.04 CI) — wait for
            // the granted line's write event, the file's own completion notification.
            var lines = await WaitForProbeContentAsync(
                probe,
                line => line == $"{grantedName}=granted-value",
                TestContext.Current.CancellationToken);
            Assert.NotEmpty(lines);

            // The plugin's env grant reaches its child, and TERM is the pinned VT name — on
            // the SDK-curated environment neither holds: the grant is not in the SDK's list
            // and the parent's TERM is forwarded verbatim. The server's declared env still
            // wins on top of the policy.
            Assert.Contains($"{grantedName}=granted-value", lines);
            Assert.Contains($"TERM={ProcessEnvironment.TermName}", lines);
            Assert.Contains("MAIEUTICS_MCP_SERVER_DECLARED=server-side", lines);
        }
        finally
        {
            await manager.DisposeAsync();
            Environment.SetEnvironmentVariable(grantedName, previousGrant);
            Environment.SetEnvironmentVariable("TERM", previousTerm);
            if (Directory.Exists(root)) await TempArea.DeleteAsync(root, CancellationToken.None);
            CleanupDirectory(directory);
        }
    }

    [Theory]
    [InlineData("/usr/bin/safe-server", "/usr/bin/evil-server", true)]
    [InlineData("/usr/bin/safe-server", "/usr/bin/safe-server", false)]
    [InlineData("/opt/servers", "/opt/servers/nested/server", false)]
    [InlineData("/usr/bin/safe", "/usr/bin/safe-evil", true)]
    public async Task TerminalAndMcpJudgeARestrictedRunGrantAlike(
        string grant,
        string executable,
        bool expectedRefused)
    {
        var policy = BuildPolicy(
            (PermissionKind.Run, new PermissionKindRules { Allow = [grant] }));

        bool mcpRefused;
        try
        {
            McpServerGeneration.EnsureCommandAllowed(executable, policy);
            mcpRefused = false;
        }
        catch (ArgumentException)
        {
            mcpRefused = true;
        }

        await using var session = CreateTerminalSession(executable, policy);
        var terminalRefused = await Record.ExceptionAsync(() => session.StartAsync(TestContext.Current.CancellationToken))
            is AgentToolException { Code: "terminal_start_failed" };

        // One verdict for both launch surfaces: the same grant admits or refuses the same
        // command identically, including the directory-boundary cases.
        Assert.Equal(expectedRefused, mcpRefused);
        Assert.Equal(expectedRefused, terminalRefused);
    }

    [Fact(Timeout = 30_000)]
    public async Task AStdioServerOutsideTheOwningScopesRunGrantIsRefused()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The probe child is a POSIX shell command.");

        // The refusal must come from the real launch path — the default transport factory the
        // production caller uses — not from the static gate alone: the owning scope's policy
        // is rendered before any child is spawned (AGENTS.md invariant 19).
        var granted = Path.Combine(Path.GetTempPath(), $"mcp-run-granted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(granted);
        var forbidden = Path.Combine(Path.GetTempPath(), $"mcp-run-forbidden-{Guid.NewGuid():N}");
        var policy = BuildPolicy(
            (PermissionKind.Run, new PermissionKindRules { Allow = [granted] }));
        var definition = CreateStdioDefinition(
            Path.Combine(forbidden, "probe.sh"),
            [],
            new Dictionary<string, string?>());

        var failure = await Record.ExceptionAsync(() => McpServerGeneration.CreateAsync(
            definition,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            TestContext.Current.CancellationToken,
            policy: policy));

        Assert.True(
            failure is ArgumentException,
            $"expected the owning scope's run grant to refuse the command, got: {failure.GetType().FullName}");
    }

    [Fact(Timeout = 30_000)]
    public async Task AStdioServerInsideTheOwningScopesRunGrantIsAllowed()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The probe child is a POSIX shell command.");

        // The control case: a command inside the owning scope's grant passes the gate and the
        // launch proceeds — the grant restricts, it does not deny. The child runs (writing the
        // probe) and only then fails the MCP handshake, because it is not an MCP server.
        var granted = Path.Combine(Path.GetTempPath(), $"mcp-run-granted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(granted);
        var probe = Path.Combine(granted, "child-env.txt");
        var server = Path.Combine(granted, "probe.sh");
        File.WriteAllText(server, $"#!/bin/sh\n/usr/bin/env > '{probe}'\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                server,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var policy = BuildPolicy(
            (PermissionKind.Run, new PermissionKindRules { Allow = [granted] }));
        var definition = CreateStdioDefinition(server, [], new Dictionary<string, string?>());

        var failure = await Record.ExceptionAsync(() => McpServerGeneration.CreateAsync(
            definition,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            TestContext.Current.CancellationToken,
            policy: policy));

        Assert.NotNull(failure);
        Assert.False(
            failure is ArgumentException,
            $"the owning scope's run grant refused a command inside its directory: {failure.Message}");

        await WaitForProbeFileAsync(probe);
        Assert.True(File.Exists(probe), "the MCP stdio child never ran.");
        CleanupDirectory(granted);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheStdioChildEnvironmentIsRenderedByTheProcessPolicyAllowlist()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The probe child is a POSIX shell command.");

        var directory = Path.Combine(Path.GetTempPath(), $"mcp-process-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var parentLanguage = "maieutics-mcp-policy-probe";
        var parentTerm = "maieutics-mcp-parent-term";
        var previousLanguage = Environment.GetEnvironmentVariable("LANG");
        var previousTerm = Environment.GetEnvironmentVariable("TERM");
        Environment.SetEnvironmentVariable("LANG", parentLanguage);
        Environment.SetEnvironmentVariable("TERM", parentTerm);
        try
        {
            var probe = Path.Combine(directory, "child-env.txt");
            var definition = CreateStdioDefinition(
                "/bin/sh",
                ["-c", $"env > '{probe}'"],
                new Dictionary<string, string?> { ["MAIEUTICS_MCP_POLICY_PROBE"] = "declared" });

            // The child is not an MCP server, so the creation fails after the spawn; the
            // failure is expected and carries no assertion — the probe file is the evidence.
            var failure = await Record.ExceptionAsync(() => McpServerGeneration.CreateAsync(
                definition,
                NullLoggerFactory.Instance,
                TimeProvider.System,
                TestContext.Current.CancellationToken));
            Assert.NotNull(failure);

            await WaitForProbeFileAsync(probe);
            Assert.True(File.Exists(probe), "the MCP stdio child never ran.");
            var lines = await ReadProbeLinesAsync(probe);

            // A shared-allowlist name the MCP SDK's list does not forward is carried, TERM is
            // the pinned VT name (not the parent's), and the server's declared env still wins.
            Assert.Contains($"LANG={parentLanguage}", lines);
            Assert.Contains($"TERM={ProcessEnvironment.TermName}", lines);
            Assert.Contains("MAIEUTICS_MCP_POLICY_PROBE=declared", lines);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LANG", previousLanguage);
            Environment.SetEnvironmentVariable("TERM", previousTerm);
            CleanupDirectory(directory);
        }
    }

    /// <summary>Waits for the probe file to exist (its creation is the OS completion
    /// event for the child's spawn) — armed before a second existence check, closing
    /// the create-between-checks race.</summary>
    private static async Task WaitForProbeFileAsync(string probe)
    {
        if (File.Exists(probe)) return;

        using var watcher = new FileSystemWatcher(
            Path.GetDirectoryName(probe) ?? ".",
            Path.GetFileName(probe));
        watcher.EnableRaisingEvents = true;
        watcher.NotifyFilter = NotifyFilters.FileName;
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => created.TrySetResult();

        if (File.Exists(probe)) return;

        await created.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Waits for the probe dump to contain the expected line. The watch is
    /// armed BEFORE the existence check on every pass, so no write can slip between a
    /// read and the subscription; each write event completes the signal and the content
    /// is re-read then — never time-sliced. The shell creates the file before the env
    /// output lands, so creation alone proves nothing about the content.</summary>
    private static async Task<string[]> WaitForProbeContentAsync(
        string probe,
        Func<string, bool> expected,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var watcher = new FileSystemWatcher(
                Path.GetDirectoryName(probe) ?? ".",
                Path.GetFileName(probe));
            watcher.EnableRaisingEvents = true;
            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Created += (_, _) => written.TrySetResult();
            watcher.Changed += (_, _) => written.TrySetResult();

            if (File.Exists(probe))
            {
                var lines = await File.ReadAllLinesAsync(probe, cancellationToken);
                if (Array.Exists(lines, line => expected(line))) return lines;
            }

            await written.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Reads the probe dump once its expected marker line has landed: the
    /// marker's write is the content-arrival completion event, and every line the test
    /// asserts rides the same single redirection that wrote the marker.</summary>
    private static async Task<string[]> ReadProbeLinesAsync(string probe)
    {
        // The dump's own header (PATH=) is written by the same redirection as every
        // asserted line, so its presence proves the dump completed.
        return await WaitForProbeContentAsync(
            probe,
            line => line.StartsWith("PATH=", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
    }

    /// <summary>A data-only, loadable plugin whose <c>deno.json</c> declares an
    /// <c>env</c> grant and whose mcp.json declares one stdio server that dumps its
    /// environment to the probe path (ADR 0033).</summary>
    private static string CreateEnvironmentGrantPluginRoot(string pluginName, string probePath)
    {
        // Unique suffix: these classes run in parallel collections, and a fixed path would
        // let one run delete another run's live tree. The plugin id is the directory name.
        var root = Path.Combine(Path.GetTempPath(), $"{pluginName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            "{\n"
            + "  \"name\": \"@maieutics/mcp-env-scope\",\n"
            + "  \"version\": \"0.1.0\",\n"
            + "  \"permissions\": { \"default\": { \"read\": [\"./\"], \"env\": [\"MAIEUTICS_MCP_PLUGIN_ENV\"] } }\n"
            + "}");
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            "{\n  \"entrypoints\": { \"mcp\": \"config/servers.json\" }\n}");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(
            Path.Combine(root, "config", "servers.json"),
            "{\n"
            + "  \"mcpServers\": {\n"
            + "    \"probe\": {\n"
            + "      \"command\": \"/bin/sh\",\n"
            + $"      \"args\": [\"-c\", \"/usr/bin/env > '{probePath}'\"],\n"
            + "      \"env\": { \"MAIEUTICS_MCP_SERVER_DECLARED\": \"server-side\" }\n"
            + "    }\n"
            + "  }\n"
            + "}");
        return root;
    }

    private static PluginHostManager CreateManager(string root, FakeTimeProvider clock)
    {
        return new PluginHostManager(
            root,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = FakeDenoExecutable.Path },
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            clock,
            pluginApprovalsPath: PluginApprovalSeeds.SeedLocalPlugins(root));
    }

    private static McpServerDefinition CreateStdioDefinition(
        string command,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        var transport = new StdioMcpTransportDefinition(command, [.. arguments], null, environment);
        return new McpServerDefinition(
            "mcp-process-policy",
            transport,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero,
            RootsEnabled: false,
            ElicitationEnabled: false,
            McpServerDefinition.CreateGenerationKey(
                transport,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero,
                false,
                false));
    }

    private static TerminalSession CreateTerminalSession(string executable, EffectivePolicy policy)
    {
        return new TerminalSession(
            Guid.NewGuid().ToString("N"),
            true,
            Directory.GetCurrentDirectory(),
            TerminalSessionKind.Persistent,
            executable,
            [],
            policy,
            new TerminalOptions { Columns = 40, Rows = 10, SettleTimeout = TimeSpan.FromMilliseconds(30) },
            new FakeTerminalProcessFactory(new FakeTerminalProcess()),
            NullLogger<TerminalSession>.Instance);
    }

    private static EffectivePolicy BuildPolicy(params (PermissionKind Kind, PermissionKindRules Rules)[] kinds)
    {
        return PermissionLayerStore.Build(
            [new PermissionLayer { Kinds = kinds.ToDictionary(static entry => entry.Kind, static entry => entry.Rules) }],
            new VariableTable(new EmptyPolicyVariableSource()));
    }

    private sealed class EmptyPolicyVariableSource : IPermissionVariableSource
    {
        public string? GetVariable(string name) => null;
    }

    private static void CleanupDirectory(string directory)
    {
        try
        {
            TempArea.DeleteTree(directory);
        }
        catch (IOException)
        {
            // Best-effort teardown; a leftover temp directory is harmless.
        }
    }
}
