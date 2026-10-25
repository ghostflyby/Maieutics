using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Permissions;
using Maieutics.Processes;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>The process-sandbox seam (ADR 0045): the run gate refuses denied executables, the
/// mode decides wrapping, the platform renderers carry the original argv positionally, and the
/// spawn tool owns the child — bounded capture, tree-killing timeout, strict UTF-8, and the
/// policy environment. The policy-derived scope is exercised on the real platform launchers
/// this host provides (declared platform variance: macOS sandbox-exec, Linux bwrap).</summary>
public sealed class ProcessSandboxTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create();

    public void Dispose()
    {
        _workspace.Dispose();
    }

    // ---------- plan matrix ----------

    [Fact]
    public void ARunDenialRefusesTheSpawn()
    {
        var policy = Policy(new PermissionKindRules { Deny = ["/usr/bin/curl"] });
        var sandbox = new PolicyProcessSandboxPolicy(ProcessSandboxMode.Prefer, locateSeatbelt: () => "/usr/bin/sandbox-exec");

        var plan = sandbox.Plan(policy, "/usr/bin/curl", ["http://example.com"]);

        var denied = Assert.IsType<ProcessLaunchPlan.DeniedPlan>(plan);
        Assert.Contains("run policy", denied.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OffModeLaunchesTheRequestedArgvUnwrapped()
    {
        var sandbox = new PolicyProcessSandboxPolicy(ProcessSandboxMode.Off, locateSeatbelt: () => "/usr/bin/sandbox-exec");

        var plan = sandbox.Plan(Policy(null), "git", ["status"]);

        var launch = Assert.IsType<ProcessLaunchPlan.LaunchPlan>(plan);
        Assert.False(launch.Sandboxed);
        Assert.Equal(["git", "status"], launch.Argv);
    }

    [Fact]
    public void PreferWithoutALauncherLaunchesUnwrappedAndRequireRefuses()
    {
        var policy = Policy(null);
        var prefer = new PolicyProcessSandboxPolicy(ProcessSandboxMode.Prefer, locateSeatbelt: () => null, locateBubblewrap: () => null);
        var require = new PolicyProcessSandboxPolicy(ProcessSandboxMode.Require, locateSeatbelt: () => null, locateBubblewrap: () => null);

        var unwrapped = Assert.IsType<ProcessLaunchPlan.LaunchPlan>(prefer.Plan(policy, "git", ["status"]));
        Assert.False(unwrapped.Sandboxed);

        var refusal = Assert.IsType<ProcessLaunchPlan.DeniedPlan>(require.Plan(policy, "git", ["status"]));
        Assert.Contains("sandbox launcher", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSeatbeltRendererCarriesTheOriginalArgvAfterTheSeparator()
    {
        var profile = new PolicyProcessSandboxPolicy.SandboxProfile(
            ["/ws"],
            ["/tmp"],
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
    public void ThePolicyShapesTheProfile()
    {
        // Write grants widen the writable set; net grants turn the network on.
        var withWriteAndNet = Classify(Policy(
            run: null,
            writeAllow: Path.Combine(_workspace.Path, "out"),
            netAllowed: true));
        Assert.True(withWriteAndNet.NetworkAllowed);
        Assert.Contains(withWriteAndNet.WritablePaths, path => path.Contains("out", StringComparison.Ordinal));

        var bare = Classify(Policy(run: null));
        Assert.False(bare.NetworkAllowed);
        Assert.DoesNotContain(bare.WritablePaths, path => path.Contains(_workspace.Path, StringComparison.Ordinal));
    }

    // ---------- the spawn tool over real processes ----------

    [Fact(Timeout = 30_000)]
    public async Task TheToolRunsARealProcessAndCapturesItsOutput()
    {
        var functions = CreateFunctions();
        var result = (JsonElement)(await InvokeAsync(functions, "process_spawn", $$"""
            {"executable":"{{Sh}}","arguments":["-c","echo planned; echo oops 1>&2"],"timeoutMs":10000}
            """))!;

        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Equal("planned\n", result.GetProperty("stdout").GetString());
        Assert.Equal("oops\n", result.GetProperty("stderr").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task TheToolForwardsStdinAndSurfacesANonZeroExit()
    {
        var functions = CreateFunctions();
        var result = (JsonElement)(await InvokeAsync(functions, "process_spawn", $$"""
            {"executable":"{{Sh}}","arguments":["-c","cat; exit 3"],"stdin":"piped\n","timeoutMs":10000}
            """))!;

        Assert.Equal(3, result.GetProperty("exitCode").GetInt32());
        Assert.Equal("piped\n", result.GetProperty("stdout").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task TheToolRefusesARunDeniedExecutable()
    {
        var functions = CreateFunctions(denied: "/usr/bin/curl");
        var failure = await Record.ExceptionAsync(() => InvokeAsync(functions, "process_spawn", """
            {"executable":"/usr/bin/curl","arguments":["http://example.com"]}
            """));

        Assert.Equal("process_spawn_denied", Assert.IsType<AgentToolException>(failure).Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheTimeoutKillsTheProcessTreeAndReportsIt()
    {
        var functions = CreateFunctions();
        var result = (JsonElement)(await InvokeAsync(functions, "process_spawn", $$"""
            {"executable":"{{Sh}}","arguments":["-c","sleep 30"],"timeoutMs":500}
            """))!;

        Assert.True(result.GetProperty("timedOut").GetBoolean());
    }

    [Fact(Timeout = 30_000)]
    public async Task NonUtf8OutputFailsTyped()
    {
        var functions = CreateFunctions();
        var failure = await Record.ExceptionAsync(() => InvokeAsync(functions, "process_spawn", $$"""
            {"executable":"{{Sh}}","arguments":["-c","printf \\\\xff"],"timeoutMs":10000}
            """));

        Assert.Equal("process_spawn_output_not_utf8", Assert.IsType<AgentToolException>(failure).Code);
    }

    [Fact(Timeout = 60_000)]
    public async Task AMacOsSandboxedSpawnCannotReadOutsideItsReadableSet()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Skip("The seatbelt launcher is macOS-only.");

        // A canary under the shared temporary directory, with the session's write rules
        // denying exactly that tree: the writable set is empty, so the sandboxed read of the
        // canary fails while the same unsandboxed read (mode Off) succeeds.
        var deniedTree = Path.GetTempPath();
        var canary = Path.Combine(deniedTree, $"mc-sandbox-canary-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(canary, "canary\n", TestContext.Current.CancellationToken);
        try
        {
            var sandboxed = (JsonElement)(await InvokeAsync(
                CreateFunctions(denied: deniedTree, kind: PermissionKind.Write, mode: ProcessSandboxMode.Require),
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

    // ---------- helpers ----------

    private static string Sh => OperatingSystem.IsWindows() ? "cmd" : "/bin/sh";

    private static string JsonEncoded(string value)
    {
        return JsonEncodedText.Encode(value).ToString();
    }

    private ProcessSpawnFunctions CreateFunctions(
        string? denied = null,
        ProcessSandboxMode mode = ProcessSandboxMode.Prefer,
        PermissionKind kind = PermissionKind.Run)
    {
        var acquirer = denied is null
            ? TestPermissionPolicies.Unconfigured()
            : new PermissionPolicyAcquirer(
                () => new DenyingLayerSource(denied, kind),
                new NullVariableSource());
        return new ProcessSpawnFunctions(
            new PolicyProcessSandboxPolicy(mode),
            acquirer,
            Workspace.Create(_workspace.Path, _workspace.Path));
    }

    private static EffectivePolicy Policy(
        PermissionKindRules? run,
        string? writeAllow = null,
        bool netAllowed = false)
    {
        var kinds = new Dictionary<PermissionKind, PermissionKindRules>();
        if (run is not null) kinds[PermissionKind.Run] = run;
        if (writeAllow is not null) kinds[PermissionKind.Write] = new() { Allow = [writeAllow] };
        if (netAllowed) kinds[PermissionKind.Net] = new() { Allow = ["example.com:443"] };
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

    private sealed class DenyingLayerSource(string denied, PermissionKind kind) : IPermissionLayerSource
    {
        public PermissionLayer? AppDefaults { get; } = new()
        {
            Kinds = new Dictionary<PermissionKind, PermissionKindRules>
            {
                [kind] = new() { Deny = [denied] }
            }
        };

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
