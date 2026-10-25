using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Permissions;
using Maieutics.Processes;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>The process-sandbox seam (ADR 0045): the run gate is deny-by-default (the broker's
/// posture — a cell-chosen executable is model-controlled code), the mode decides wrapping,
/// the platform renderers carry the original argv positionally and render every read/write
/// denial as an explicit deny rule, and the spawn tool owns the child — bounded capture that
/// never kills on the cap, a tree-killing timeout, strict UTF-8, per-spawn temporary
/// directory cleanup, and the policy environment. The realpath/steering behavior is exercised
/// on the real platform launchers this host provides (declared platform variance: macOS
/// sandbox-exec; the shell-based tool tests are unix-shaped).</summary>
public sealed class ProcessSandboxTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create();

    public void Dispose()
    {
        _workspace.Dispose();
    }

    // ---------- plan matrix ----------

    [Fact]
    public void WithoutRunRulesTheSpawnSurfaceDeniesByDefault()
    {
        // The broker's posture, not the launch gate's unrestricted default: a cell-chosen
        // executable is model-controlled code, and the sanctioned path must not be weaker
        // than the escape hatch (the native Deno.Command broker check).
        var sandbox = new PolicyProcessSandboxPolicy(ProcessSandboxMode.Prefer, locateSeatbelt: () => "/usr/bin/sandbox-exec");

        var plan = sandbox.Plan(Policy(run: null), "/bin/echo", ["hi"]);

        var denied = Assert.IsType<ProcessLaunchPlan.DeniedPlan>(plan);
        Assert.Contains("denies by default", denied.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunDenialRefusesTheSpawn()
    {
        var policy = Policy(run: new PermissionKindRules { AllowAll = true, Deny = ["/usr/bin/curl"] });
        var sandbox = new PolicyProcessSandboxPolicy(ProcessSandboxMode.Prefer, locateSeatbelt: () => "/usr/bin/sandbox-exec");

        var plan = sandbox.Plan(policy, "/usr/bin/curl", ["http://example.com"]);

        var denied = Assert.IsType<ProcessLaunchPlan.DeniedPlan>(plan);
        Assert.Contains("run policy", denied.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunAllowlistAdmitsTheSpawn()
    {
        var policy = Policy(run: new PermissionKindRules { Allow = ["/bin/echo"] });
        var sandbox = new PolicyProcessSandboxPolicy(
            ProcessSandboxMode.Prefer,
            locateSeatbelt: () => null,
            locateBubblewrap: () => null);

        var launch = Assert.IsType<ProcessLaunchPlan.LaunchPlan>(sandbox.Plan(policy, "/bin/echo", ["hi"]));
        Assert.False(launch.Sandboxed, "no launcher: Prefer launches unwrapped behind the gate");
    }

    [Fact]
    public void OffModeLaunchesTheRequestedArgvUnwrapped()
    {
        var sandbox = new PolicyProcessSandboxPolicy(ProcessSandboxMode.Off, locateSeatbelt: () => "/usr/bin/sandbox-exec");

        var plan = sandbox.Plan(Policy(run: new PermissionKindRules { AllowAll = true }), "git", ["status"]);

        var launch = Assert.IsType<ProcessLaunchPlan.LaunchPlan>(plan);
        Assert.False(launch.Sandboxed);
        Assert.Equal(["git", "status"], launch.Argv);
    }

    [Fact]
    public void PreferWithoutALauncherLaunchesUnwrappedAndRequireRefuses()
    {
        var policy = Policy(run: new PermissionKindRules { AllowAll = true });
        var prefer = new PolicyProcessSandboxPolicy(ProcessSandboxMode.Prefer, locateSeatbelt: () => null, locateBubblewrap: () => null);
        var require = new PolicyProcessSandboxPolicy(ProcessSandboxMode.Require, locateSeatbelt: () => null, locateBubblewrap: () => null);

        var unwrapped = Assert.IsType<ProcessLaunchPlan.LaunchPlan>(prefer.Plan(policy, "git", ["status"]));
        Assert.False(unwrapped.Sandboxed);

        var refusal = Assert.IsType<ProcessLaunchPlan.DeniedPlan>(require.Plan(policy, "git", ["status"]));
        Assert.Contains("sandbox launcher", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedRequirePlanDeletesItsTemporaryDirectory()
    {
        string? created = null;
        var require = new PolicyProcessSandboxPolicy(
            ProcessSandboxMode.Require,
            locateSeatbelt: () => null,
            locateBubblewrap: () => null,
            createTemporaryDirectory: () =>
            {
                created = Path.Combine(Path.GetTempPath(), $"mc-spawn-{Guid.NewGuid():N}");
                Directory.CreateDirectory(created);
                return created;
            });

        var refusal = Assert.IsType<ProcessLaunchPlan.DeniedPlan>(
            require.Plan(Policy(run: new PermissionKindRules { AllowAll = true }), "git", []));

        Assert.NotNull(refusal);
        Assert.NotNull(created);
        Assert.False(Directory.Exists(created), "the unused per-spawn temporary directory is deleted");
    }

    // ---------- profile shape ----------

    [Fact]
    public void ReadAndWriteDenialsRenderAsSandboxDenyRules()
    {
        var denied = Path.Combine(_workspace.Path, "secrets");
        var profile = new PolicyProcessSandboxPolicy.SandboxProfile(
            [_workspace.Path],
            [_workspace.Path],
            [denied],
            TrustedPaths: [],
            NetworkAllowed: false);

        var profileText = PolicyProcessSandboxPolicy.BuildSeatbeltProfile(profile);

        Assert.Contains("(deny file-read* (subpath \"", profileText, StringComparison.Ordinal);
        Assert.Contains("(deny file-write* (subpath \"", profileText, StringComparison.Ordinal);
        Assert.Contains(denied, profileText, StringComparison.Ordinal);
        // Narrow process operations only: no blanket process*, no mach-lookup at all.
        Assert.DoesNotContain("mach-lookup", profileText, StringComparison.Ordinal);
        Assert.Contains("(allow process-exec*)(allow process-fork)", profileText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNetSwitchFailsClosedUnderAnyDenial()
    {
        var withDeny = Classify(Policy(run: null, netAllow: "example.com:443", netDeny: "evil.com"));
        Assert.False(withDeny.NetworkAllowed, "a host denial keeps the network off entirely");

        var unqualified = Classify(Policy(run: null, netAllowAll: true));
        Assert.True(unqualified.NetworkAllowed);
    }

    [Fact]
    public void APlantedSymlinkCannotSteerAGrantToItsTarget()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("The symlink stand-in needs a unix platform.");

        // The granted spelling is a symlink pointing outside itself: the steering guard drops
        // the grant instead of resolving it to the attacker-chosen target.
        var target = Directory.CreateDirectory(Path.Combine(_workspace.Path, "target"));
        var link = Path.Combine(_workspace.Path, "steer");
        File.CreateSymbolicLink(link, target.FullName);
        var profile = new PolicyProcessSandboxPolicy.SandboxProfile(
            [link],
            [],
            [],
            TrustedPaths: [],
            NetworkAllowed: false);

        var profileText = PolicyProcessSandboxPolicy.BuildSeatbeltProfile(profile);

        Assert.DoesNotContain(target.FullName, profileText, StringComparison.Ordinal);
        Assert.DoesNotContain("(allow file-read* (subpath \"" + link, profileText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSeatbeltRendererCarriesTheOriginalArgvAfterTheSeparator()
    {
        var profile = new PolicyProcessSandboxPolicy.SandboxProfile(
            ["/ws"],
            ["/tmp"],
            [],
            TrustedPaths: [],
            NetworkAllowed: false);

        var argv = PolicyProcessSandboxPolicy.RenderSeatbelt("/usr/bin/sandbox-exec", profile, "git", ["status", "--short"]);

        Assert.Equal("/usr/bin/sandbox-exec", argv[0]);
        Assert.Equal("-p", argv[1]);
        var separator = Array.IndexOf(argv.ToArray(), "--");
        Assert.True(separator > 0, "the separator precedes the requested argv");
        Assert.Equal(["git", "status", "--short"], argv.Skip(separator + 1).ToArray());
        var profileText = argv[2];
        Assert.Contains("subpath \"/ws\"", profileText, StringComparison.Ordinal);
        Assert.Contains("(deny network*)", profileText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBubblewrapRendererBindsAndSeparates()
    {
        var profile = new PolicyProcessSandboxPolicy.SandboxProfile(
            ["/ws"],
            ["/out"],
            ["/ws/secret"],
            TrustedPaths: [],
            NetworkAllowed: false);

        var argv = PolicyProcessSandboxPolicy.RenderBubblewrap("/usr/bin/bwrap", profile, "git", ["status"]);

        Assert.Equal("/usr/bin/bwrap", argv[0]);
        Assert.Contains(argv, static argument => argument == "--ro-bind");
        Assert.Contains(argv, static argument => argument == "--bind");
        Assert.Contains(argv, static argument => argument == "--unshare-net");
        var separator = Array.IndexOf(argv.ToArray(), "--");
        Assert.Equal(["git", "status"], argv.Skip(separator + 1).ToArray());
    }

    // ---------- the spawn tool over real processes ----------

    [Fact(Timeout = 30_000)]
    public async Task TheToolRunsARealProcessAndCapturesItsOutput()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows()) Assert.Skip("The shell stand-in is unix sh.");

        var functions = CreateFunctions();
        var result = (JsonElement)(await InvokeAsync(functions, "process_spawn", $$"""
            {"executable":"/bin/sh","arguments":["-c","echo planned; echo oops 1>&2"],"timeoutMs":10000}
            """))!;

        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Equal("planned\n", result.GetProperty("stdout").GetString());
        Assert.Equal("oops\n", result.GetProperty("stderr").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task TheToolForwardsStdinAndSurfacesANonZeroExit()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows()) Assert.Skip("The shell stand-in is unix sh.");

        var functions = CreateFunctions();
        var result = (JsonElement)(await InvokeAsync(functions, "process_spawn", """
            {"executable":"/bin/sh","arguments":["-c","cat; exit 3"],"stdin":"piped\n","timeoutMs":10000}
            """))!;

        Assert.Equal(3, result.GetProperty("exitCode").GetInt32());
        Assert.Equal("piped\n", result.GetProperty("stdout").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task TheToolRefusesARunDeniedExecutable()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var functions = CreateFunctions(denied: "/usr/bin/curl", kind: PermissionKind.Run);
        var failure = await Record.ExceptionAsync(() => InvokeAsync(functions, "process_spawn", """
            {"executable":"/usr/bin/curl","arguments":["http://example.com"]}
            """));

        Assert.Equal("process_spawn_denied", Assert.IsType<AgentToolException>(failure).Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheToolDeniesByDefaultWithoutRunGrants()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var functions = CreateFunctions(runRules: PermissionKindRules.Empty);
        var failure = await Record.ExceptionAsync(() => InvokeAsync(functions, "process_spawn", """
            {"executable":"/bin/echo","arguments":["hi"]}
            """));

        var denial = Assert.IsType<AgentToolException>(failure);
        Assert.Equal("process_spawn_denied", denial.Code);
        Assert.Contains("denies by default", denial.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheTimeoutKillsTheProcessTreeAndReportsIt()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows()) Assert.Skip("The shell stand-in is unix sh.");

        var functions = CreateFunctions();
        var result = (JsonElement)(await InvokeAsync(functions, "process_spawn", """
            {"executable":"/bin/sh","arguments":["-c","echo partial; sleep 30"],"timeoutMs":500}
            """))!;

        Assert.True(result.GetProperty("timedOut").GetBoolean());
        Assert.Equal("partial\n", result.GetProperty("stdout").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task OutputPastTheCapIsTruncatedWithoutKillingTheChild()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows()) Assert.Skip("The shell stand-in is unix sh.");

        var functions = CreateFunctions();
        // ~272 KiB of output against the 256 KiB per-stream cap: the child must still exit
        // cleanly with code 0 — the cap bounds the capture, never the execution.
        var result = (JsonElement)(await InvokeAsync(functions, "process_spawn", """
            {"executable":"/bin/sh","arguments":["-c","i=0; while [ $i -lt 16384 ]; do printf '0123456789ABCDEF\n'; i=$((i+1)); done; echo done"],"timeoutMs":30000}
            """))!;

        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.True(result.GetProperty("outputTruncated").GetBoolean());
        var stdout = result.GetProperty("stdout").GetString() ?? string.Empty;
        Assert.True(stdout.Length <= 256 * 1_024 + 1_024, "the captured prefix respects the cap");
        Assert.DoesNotContain("done\n", stdout, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    public async Task NonUtf8OutputFailsTyped()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows()) Assert.Skip("The shell stand-in is unix sh.");

        var functions = CreateFunctions();
        var failure = await Record.ExceptionAsync(() => InvokeAsync(functions, "process_spawn", """
            {"executable":"/bin/sh","arguments":["-c","printf \\\\xff"],"timeoutMs":10000}
            """));

        Assert.Equal("process_spawn_output_not_utf8", Assert.IsType<AgentToolException>(failure).Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task EnvironmentNameSelectionFiltersTheChildEnvironment()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows()) Assert.Skip("The env-printing stand-in is unix env.");

        var functions = CreateFunctions();
        var result = (JsonElement)(await InvokeAsync(functions, "process_spawn", """
            {"executable":"/usr/bin/env","environmentNames":["TERM"],"timeoutMs":10000}
            """))!;

        var printed = result.GetProperty("stdout").GetString() ?? string.Empty;
        Assert.Contains("TERM=", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("PATH=", printed, StringComparison.Ordinal);
    }

    [Fact(Timeout = 60_000)]
    public async Task AMacOsSandboxedSpawnCannotReadOutsideItsReadableSet()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) Assert.Skip("The seatbelt launcher is macOS-only.");

        // A canary under the shared temporary directory, with the session's read rules denying
        // exactly that tree: the sandboxed read of the canary fails while the same unsandboxed
        // read (mode Off) succeeds. The read denial rides an explicit deny rule.
        var deniedTree = Path.GetTempPath();
        var canary = Path.Combine(deniedTree, $"mc-sandbox-canary-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(canary, "canary\n", TestContext.Current.CancellationToken);
        try
        {
            var sandboxed = (JsonElement)(await InvokeAsync(
                CreateFunctions(denied: deniedTree, kind: PermissionKind.Read, mode: ProcessSandboxMode.Require),
                "process_spawn",
                $$"""{"executable":"/bin/cat","arguments":["{{JsonEncoded(canary)}}"],"timeoutMs":20000}"""))!;
            Assert.NotEqual(0, sandboxed.GetProperty("exitCode").GetInt32());
            Assert.NotEqual("canary\n", sandboxed.GetProperty("stdout").GetString());

            var unsandboxed = (JsonElement)(await InvokeAsync(
                CreateFunctions(mode: ProcessSandboxMode.Off),
                "process_spawn",
                $$"""{"executable":"/bin/cat","arguments":["{{JsonEncoded(canary)}}"],"timeoutMs":20000}"""))!;
            Assert.Equal(0, unsandboxed.GetProperty("exitCode").GetInt32());
            Assert.Equal("canary\n", unsandboxed.GetProperty("stdout").GetString());
        }
        finally
        {
            File.Delete(canary);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AMacOsSandboxedSpawnWritesInsideItsWritableSet()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) Assert.Skip("The seatbelt launcher is macOS-only.");

        // The positive direction of the same sandbox: the workspace root is writable by the
        // spawn baseline, and the write lands through the real seatbelt wrapper.
        var target = Path.Combine(_workspace.Path, $"written-{Guid.NewGuid():N}.txt");
        var result = (JsonElement)(await InvokeAsync(
            CreateFunctions(mode: ProcessSandboxMode.Require),
            "process_spawn",
            $$"""{"executable":"/bin/sh","arguments":["-c","echo payload > {{JsonEncoded(target)}}"],"timeoutMs":20000}"""))!;

        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Equal("payload\n", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    // ---------- helpers ----------

    private static string JsonEncoded(string value)
    {
        return JsonEncodedText.Encode(value).ToString();
    }

    private ProcessSpawnFunctions CreateFunctions(
        string? denied = null,
        ProcessSandboxMode mode = ProcessSandboxMode.Prefer,
        PermissionKind kind = PermissionKind.Run,
        PermissionKindRules? runRules = null)
    {
        // Without an explicit run posture the test session grants every executable; the
        // deny-by-default posture has its own dedicated tests above.
        var kinds = new Dictionary<PermissionKind, PermissionKindRules>
        {
            [PermissionKind.Run] = runRules ?? new PermissionKindRules { AllowAll = true }
        };
        if (denied is not null)
        {
            kinds[kind] = kind == PermissionKind.Run
                ? new PermissionKindRules { AllowAll = true, Deny = [denied] }
                : new PermissionKindRules { Deny = [denied] };
        }

        var acquirer = new PermissionPolicyAcquirer(
            () => new FixedLayerSource(new PermissionLayer { Kinds = kinds }),
            new NullVariableSource());
        return new ProcessSpawnFunctions(
            new PolicyProcessSandboxPolicy(mode, trustedWorkspaceRoot: _workspace.Path),
            acquirer,
            Workspace.Create(_workspace.Path, _workspace.Path));
    }

    private static EffectivePolicy Policy(
        PermissionKindRules? run,
        string? writeAllow = null,
        string? netAllow = null,
        bool netAllowAll = false,
        string? netDeny = null)
    {
        var kinds = new Dictionary<PermissionKind, PermissionKindRules>();
        if (run is not null) kinds[PermissionKind.Run] = run;
        if (writeAllow is not null) kinds[PermissionKind.Write] = new() { Allow = [writeAllow] };
        if (netAllow is not null || netAllowAll || netDeny is not null)
            kinds[PermissionKind.Net] = new()
            {
                Allow = netAllowAll ? [] : [netAllow ?? "example.com:443"],
                AllowAll = netAllowAll,
                Deny = netDeny is null ? [] : [netDeny]
            };
        return PermissionLayerStore.Build(
            [new PermissionLayer { Kinds = kinds }],
            new VariableTable(new NullVariableSource()));
    }

    private static PolicyProcessSandboxPolicy.SandboxProfile Classify(EffectivePolicy policy)
    {
        return PolicyProcessSandboxPolicy.Classify(policy, "git", []);
    }

    private static async Task<object?> InvokeAsync(
        ProcessSpawnFunctions functions,
        string name,
        string argumentsJson)
    {
        var function = functions.Functions.Single(candidate => candidate.Name == name);
        using var document = JsonDocument.Parse(argumentsJson);
        return await function.InvokeAsync(
            new AIFunctionArguments(document.RootElement.EnumerateObject().ToDictionary(
                    static property => property.Name,
                    static property => (object?)property.Value.Clone()))
            {
                Context = new Dictionary<object, object?>
                {
                    [typeof(ScriptInvocationContext)] = new ScriptInvocationContext(
                        AgentSessionId.Create().Value.ToString("N"))
                }
            },
            TestContext.Current.CancellationToken);
    }

    private sealed class FixedLayerSource(PermissionLayer appDefaults) : IPermissionLayerSource
    {
        public PermissionLayer? AppDefaults => appDefaults;

        public PermissionLayer? WorkspaceProfile => null;

        public PermissionLayer? GetSessionOverride(AgentSessionId sessionId) => null;
    }

    private sealed class NullVariableSource : IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return null;
        }
    }
}
