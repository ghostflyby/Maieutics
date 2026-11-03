using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Permissions;
using Maieutics.Processes;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>The same real-process contract runs under seatbelt on macOS and bubblewrap
/// on Linux. Require mode makes an unavailable or broken launcher a failure on either OS.</summary>
[Collection(ProductIntegrationCollection.Name)]
[Trait("Category", "Sandbox")]
public sealed class ProcessSandboxConformanceTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create();

    private string PhysicalParent => PolicyProcessSandboxPolicy.ResolveRealPath(_workspace.ParentPath) ?? _workspace.ParentPath;
    private string PhysicalWorkspace => Path.Combine(PhysicalParent, "workspace");

    public void Dispose() => _workspace.Dispose();

    [Fact(Timeout = 30_000)]
    public async Task ArgumentsStdinExitStatusAndWorkingDirectoryMatchTheUnwrappedProcess()
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        const string script = "printf '<%s>\\n' \"$1\" \"$2\"; test \"$(pwd -P)\" = \"$3\" || exit 8; cat; printf 'err\\n' >&2; exit 7";
        var arguments = new[] { "a b;$(echo nope)", "中文\"quote", PhysicalWorkspace };
        var wrapped = await ShellAsync(CreateFunctions(), script, arguments, "in\n", budget.Token);
        var plain = await ShellAsync(CreateFunctions(mode: ProcessSandboxMode.Off), script, arguments, "in\n", budget.Token);
        Assert.True(wrapped.GetProperty("sandboxed").GetBoolean());
        Assert.Equal(7, wrapped.GetProperty("exitCode").GetInt32());
        Assert.Equal("<a b;$(echo nope)>\n<中文\"quote>\nin\n", wrapped.GetProperty("stdout").GetString());
        Assert.Equal(plain.GetProperty("stdout").GetString(), wrapped.GetProperty("stdout").GetString());
        Assert.Equal(plain.GetProperty("stderr").GetString(), wrapped.GetProperty("stderr").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task WorkspaceWritesPersistAndPrivateTemporaryFilesAreRemoved()
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var target = Path.Combine(PhysicalWorkspace, "written file.txt");
        var result = await ShellAsync(CreateFunctions(),
            "printf payload >\"$1\"; test \"$TMPDIR\" = \"$TMP\" && test \"$TMP\" = \"$TEMP\" || exit 9; printf temporary >\"$TMPDIR/probe\" || exit 10; printf '%s' \"$TMPDIR\"",
            [target], null, budget.Token);
        AssertSuccess(result);
        Assert.Equal("payload", await File.ReadAllTextAsync(target, budget.Token));
        var temporary = Assert.IsType<string>(result.GetProperty("stdout").GetString());
        Assert.False(Directory.Exists(temporary), "The launch-owned temporary tree must be removed after process exit.");
    }

    [Fact(Timeout = 30_000)]
    public async Task WorkspaceWithSymlinkedAncestorsKeepsItsPhysicalWorkingDirectory()
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var firstParent = Directory.CreateDirectory(Path.Combine(PhysicalParent, "actual-parent")).FullName;
        var secondParent = Directory.CreateDirectory(Path.Combine(PhysicalParent, "other-parent")).FullName;
        var physical = Directory.CreateDirectory(Path.Combine(secondParent, "selected-workspace")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(PhysicalParent, "parent-alias"), firstParent);
        Directory.CreateSymbolicLink(Path.Combine(firstParent, "inner-alias"), secondParent);
        // The final directory is not a symlink (Workspace rejects that); its ancestors are.
        var spelling = Path.Combine(PhysicalParent, "parent-alias", "inner-alias", "selected-workspace");
        var result = await ShellAsync(CreateFunctions(workspaceRoot: spelling),
            "printf persisted >written; pwd -P", [], null, budget.Token);
        AssertSuccess(result);
        Assert.Equal(physical + "\n", result.GetProperty("stdout").GetString());
        Assert.Equal("persisted", await File.ReadAllTextAsync(Path.Combine(physical, "written"), budget.Token));
    }

    [Fact(Timeout = 30_000)]
    public async Task SyntheticAncestorsAndSharedTempAreNotExtraWriteGrants()
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var name = $"maieutics-ungranted-{Guid.NewGuid():N}";
        var paths = new[] { "/" + name, "/tmp/" + name, "/dev/" + name };
        try
        {
            foreach (var path in paths)
            {
                var result = await ShellAsync(CreateFunctions(),
                    "if printf escaped >\"$1\"; then exit 42; fi", [path], null, budget.Token);
                AssertSuccess(result);
                Assert.False(File.Exists(path));
            }
        }
        finally
        {
            foreach (var path in paths)
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task DenyingAllReadsRefusesTheSandboxedLaunchAndReleasesItsTemporaryTree()
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var temporary = Path.Combine(PhysicalParent, "refused-launch");
        var functions = CreateFunctions(new() { [PermissionKind.Read] = new() { DenyAll = true } },
            createTemporaryDirectory: () => Directory.CreateDirectory(temporary).FullName);
        var failure = await Assert.ThrowsAnyAsync<AgentToolException>(() => InvokeAsync(
            functions, "/bin/echo", ["must-not-run"], null, budget.Token));
        Assert.Equal("process_spawn_denied", failure.Code);
        Assert.Contains("denies all filesystem reads", failure.Message);
        Assert.False(Directory.Exists(temporary));
    }

    [Fact(Timeout = 30_000)]
    public async Task AnUnlistedHostFileCannotBeRead()
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var canary = Path.Combine(PhysicalParent, "outside.txt");
        await File.WriteAllTextAsync(canary, "outside-canary\n", budget.Token);
        var wrapped = await InvokeAsync(CreateFunctions(), "/bin/cat", [canary], null, budget.Token);
        Assert.True(wrapped.GetProperty("sandboxed").GetBoolean());
        Assert.NotEqual(0, wrapped.GetProperty("exitCode").GetInt32());
        Assert.DoesNotContain("outside-canary", wrapped.GetProperty("stdout").GetString() ?? string.Empty);
        var plain = await InvokeAsync(CreateFunctions(mode: ProcessSandboxMode.Off), "/bin/cat", [canary], null, budget.Token);
        Assert.Equal(0, plain.GetProperty("exitCode").GetInt32());
        Assert.Equal("outside-canary\n", plain.GetProperty("stdout").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task AnExplicitReadGrantDoesNotBecomeAWriteGrant()
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var readable = Directory.CreateDirectory(Path.Combine(PhysicalParent, "readonly")).FullName;
        var canary = Path.Combine(readable, "canary.txt");
        await File.WriteAllTextAsync(canary, "readable\n", budget.Token);
        var functions = CreateFunctions(new() { [PermissionKind.Read] = new() { Allow = [readable] } });
        var result = await ShellAsync(functions,
            "cat \"$1\" || exit 2; if printf overwritten >\"$1\"; then exit 42; fi", [canary], null, budget.Token);
        AssertSuccess(result);
        Assert.Equal("readable\n", result.GetProperty("stdout").GetString());
        Assert.Equal("readable\n", await File.ReadAllTextAsync(canary, budget.Token));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task DenialsWinOverWritableParentsAndSymlinkAliases(bool readDenial, bool directory)
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var secretDirectory = Directory.CreateDirectory(Path.Combine(PhysicalWorkspace, "secrets")).FullName;
        var canary = Path.Combine(secretDirectory, "secret.txt");
        await File.WriteAllTextAsync(canary, "secret-canary\n", budget.Token);
        var alias = Path.Combine(PhysicalWorkspace, "alias.txt");
        File.CreateSymbolicLink(alias, canary);
        var kind = readDenial ? PermissionKind.Read : PermissionKind.Write;
        var functions = CreateFunctions(new() { [kind] = new() { Deny = [directory ? secretDirectory : canary] } });
        foreach (var path in new[] { canary, alias })
        {
            var read = await InvokeAsync(functions, "/bin/cat", [path], null, budget.Token);
            Assert.True(read.GetProperty("sandboxed").GetBoolean());
            Assert.NotEqual(0, read.GetProperty("exitCode").GetInt32());
            var write = await ShellAsync(functions,
                "if printf overwritten >\"$1\"; then exit 42; fi", [path], null, budget.Token);
            AssertSuccess(write);
        }
        Assert.Equal("secret-canary\n", await File.ReadAllTextAsync(canary, budget.Token));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AncestorDenialsOverrideWorkspaceGrants(bool readDenial)
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var canary = Path.Combine(PhysicalWorkspace, "ancestor-secret");
        await File.WriteAllTextAsync(canary, "ancestor-canary", budget.Token);
        var control = await InvokeAsync(CreateFunctions(), "/bin/cat", [canary], null, budget.Token);
        AssertSuccess(control);
        Assert.Equal("ancestor-canary", control.GetProperty("stdout").GetString());

        var kind = readDenial ? PermissionKind.Read : PermissionKind.Write;
        var functions = CreateFunctions(new() { [kind] = new() { Deny = [PhysicalParent] } });
        var read = await InvokeAsync(functions, "/bin/cat", [canary], null, budget.Token);
        Assert.True(read.GetProperty("sandboxed").GetBoolean());
        Assert.NotEqual(0, read.GetProperty("exitCode").GetInt32());
        Assert.DoesNotContain("ancestor-canary", read.GetProperty("stdout").GetString() ?? string.Empty);
        var write = await ShellAsync(functions, "printf overwritten >\"$1\"", [canary], null, budget.Token);
        Assert.True(write.GetProperty("sandboxed").GetBoolean());
        Assert.NotEqual(0, write.GetProperty("exitCode").GetInt32());
        Assert.Equal("ancestor-canary", await File.ReadAllTextAsync(canary, budget.Token));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AncestorDenialsOverridePrivateTemporaryGrants(bool sharedTemp)
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var ancestor = sharedTemp ? Path.GetTempPath()
            : Directory.CreateDirectory(Path.Combine(PhysicalParent, "temporary-parent")).FullName;
        var temporary = Path.Combine(ancestor, $"denied-launch-{Guid.NewGuid():N}");
        string CreateTemporary() => Directory.CreateDirectory(temporary).FullName;
        const string script = "printf temporary >\"$TMPDIR/probe\"";
        var control = await ShellAsync(CreateFunctions(createTemporaryDirectory: CreateTemporary),
            script, [], null, budget.Token);
        AssertSuccess(control);

        var functions = CreateFunctions(new() { [PermissionKind.Write] = new() { Deny = [ancestor] } },
            createTemporaryDirectory: CreateTemporary);
        var denied = await ShellAsync(functions, script, [], null, budget.Token);
        Assert.True(denied.GetProperty("sandboxed").GetBoolean());
        Assert.NotEqual(0, denied.GetProperty("exitCode").GetInt32());
        Assert.False(Directory.Exists(temporary), "A denied temporary ancestor must not bypass cleanup.");
    }

    [Fact(Timeout = 30_000)]
    public async Task LinuxRootDenialsRefuseTheLaunchAndReleaseTheTemporaryTree()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("This checks the Linux root-denial planning refusal.");
        using var budget = Budget(TestContext.Current.CancellationToken);
        var temporary = Path.Combine(PhysicalParent, "root-denied-launch");
        var functions = CreateFunctions(new() { [PermissionKind.Read] = new() { Deny = ["/"] } },
            createTemporaryDirectory: () => Directory.CreateDirectory(temporary).FullName);
        var failure = await Assert.ThrowsAnyAsync<AgentToolException>(() =>
            InvokeAsync(functions, "/bin/echo", ["must-not-run"], null, budget.Token));
        Assert.Equal("process_spawn_denied", failure.Code);
        Assert.Contains("filesystem root", failure.Message);
        Assert.False(Directory.Exists(temporary));
    }

    [Fact(Timeout = 30_000)]
    public async Task APlantedGrantSymlinkCannotExposeItsTarget()
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var outside = Directory.CreateDirectory(Path.Combine(PhysicalParent, "outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "secret"), "steered-canary", budget.Token);
        var link = Path.Combine(PhysicalWorkspace, "steer");
        Directory.CreateSymbolicLink(link, outside);
        var functions = CreateFunctions(new()
        {
            [PermissionKind.Read] = new() { Allow = [link] },
            [PermissionKind.Write] = new() { Allow = [link] }
        });
        var result = await InvokeAsync(functions, "/bin/cat", [Path.Combine(link, "secret")], null, budget.Token);
        Assert.True(result.GetProperty("sandboxed").GetBoolean());
        Assert.NotEqual(0, result.GetProperty("exitCode").GetInt32());
        Assert.DoesNotContain("steered-canary", result.GetProperty("stdout").GetString() ?? string.Empty);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyAnUnqualifiedNetworkGrantAllowsHostConnections(bool denyOverridesAllow)
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var serving = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        var server = ServeHttpAsync(listener, serving.Token);
        try
        {
            var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            var curl = new[] { "--disable", "--noproxy", "*", "--connect-timeout", "2", "--max-time", "4", "--silent", "--show-error", url };
            var deniedRules = new Dictionary<PermissionKind, PermissionKindRules>
            {
                [PermissionKind.Net] = denyOverridesAllow
                    ? new() { AllowAll = true, Deny = ["example.invalid"] }
                    : new() { Allow = ["127.0.0.1"] }
            };
            var denied = await InvokeAsync(CreateFunctions(deniedRules), "/usr/bin/curl", curl, null, budget.Token);
            Assert.True(denied.GetProperty("sandboxed").GetBoolean());
            Assert.NotEqual(0, denied.GetProperty("exitCode").GetInt32());
            var allowed = await InvokeAsync(CreateFunctions(new() { [PermissionKind.Net] = new() { AllowAll = true } }),
                "/usr/bin/curl", curl, null, budget.Token);
            AssertSuccess(allowed);
            Assert.Equal("reachable\n", allowed.GetProperty("stdout").GetString());
        }
        finally
        {
            await serving.CancelAsync();
            listener.Stop();
            try { await server; }
            catch (Exception exception) when (serving.IsCancellationRequested &&
                                               exception is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellationAfterChildReadinessRemovesTheLaunchOwnedTree()
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(PhysicalWorkspace, "ready");
        watcher.Created += (_, _) => ready.TrySetResult();
        watcher.Error += (_, args) => ready.TrySetException(args.GetException());
        watcher.EnableRaisingEvents = true;
        var temporary = Path.Combine(PhysicalParent, "launch-temp");
        var functions = CreateFunctions(createTemporaryDirectory: () => Directory.CreateDirectory(temporary).FullName);
        var running = ShellAsync(functions, "printf ready >ready; sleep 30", [], null, cancellation.Token);
        try
        {
            await ready.Task.WaitAsync(budget.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.False(Directory.Exists(temporary), "Cancellation must finish process teardown and temporary-tree cleanup before returning.");
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await running; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task LinuxFileDenyMasksCannotBeUnlockedThroughThePrivateTempDirectory()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("This checks the Linux mount-mask representation.");
        using var budget = Budget(TestContext.Current.CancellationToken);
        var canary = Path.Combine(PhysicalWorkspace, "secret");
        await File.WriteAllTextAsync(canary, "secret-canary", budget.Token);
        var functions = CreateFunctions(new() { [PermissionKind.Read] = new() { Deny = [canary] } });
        var result = await ShellAsync(functions,
            "chmod 644 \"$TMPDIR/.deny-mask/file\" 2>/dev/null || :; cat \"$1\"", [canary], null, budget.Token);
        Assert.True(result.GetProperty("sandboxed").GetBoolean());
        Assert.NotEqual(0, result.GetProperty("exitCode").GetInt32());
        Assert.Equal("secret-canary", await File.ReadAllTextAsync(canary, budget.Token));
    }

    [Fact(Timeout = 30_000)]
    public async Task LinuxMissingDenialsInsideAnExposedTreeFailClosedWithoutCreatingHostPaths()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Seatbelt can express future paths; Linux mount masks require existing targets.");
        using var budget = Budget(TestContext.Current.CancellationToken);
        var missing = Path.Combine(PhysicalWorkspace, "not-created");
        var functions = CreateFunctions(new() { [PermissionKind.Read] = new() { Deny = [missing] } });
        var failure = await Assert.ThrowsAnyAsync<AgentToolException>(() => InvokeAsync(functions, "/bin/echo", ["must-not-run"], null, budget.Token));
        Assert.Equal("process_spawn_denied", failure.Code);
        Assert.Contains("missing denied path", failure.Message);
        Assert.False(Path.Exists(missing), "Planning must not create denied mount targets in the host workspace.");
    }

    [Fact(Timeout = 30_000)]
    public async Task LinuxActiveHostProcPidDenialsFailDuringLauncherSetupAndReleaseTheTemporaryTree()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("This checks host versus sandbox PID namespace mount targets.");
        using var budget = Budget(TestContext.Current.CancellationToken);
        var activeHostPath = $"/proc/{Environment.ProcessId}";
        Assert.True(Directory.Exists(activeHostPath), "The denied host process must remain alive through planning and launcher setup.");
        var control = await InvokeAsync(CreateFunctions(), "/bin/echo", ["launched"], null, budget.Token);
        AssertSuccess(control);
        Assert.Equal("launched\n", control.GetProperty("stdout").GetString());

        var temporary = Path.Combine(PhysicalParent, "proc-denied-launch");
        var functions = CreateFunctions(new() { [PermissionKind.Read] = new() { Deny = [activeHostPath] } },
            createTemporaryDirectory: () => Directory.CreateDirectory(temporary).FullName);
        // Host existence passes planning; the replacement procfs belongs to the new PID
        // namespace. This remains a launcher failure, not a typed planning refusal.
        var result = await InvokeAsync(functions, "/bin/echo", ["must-not-run"], null, budget.Token);
        Assert.True(result.GetProperty("sandboxed").GetBoolean());
        Assert.NotEqual(0, result.GetProperty("exitCode").GetInt32());
        Assert.Equal(string.Empty, result.GetProperty("stdout").GetString());
        Assert.Contains("bwrap:", result.GetProperty("stderr").GetString() ?? string.Empty);
        Assert.False(Directory.Exists(temporary), "A procfs setup failure must release the launch-owned temporary tree.");
    }

    private static void RequireUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Skip("These conformance cases require seatbelt or bubblewrap.");
    }

    private static CancellationTokenSource Budget(CancellationToken cancellationToken)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        return budget;
    }

    private static void AssertSuccess(JsonElement result)
    {
        Assert.True(result.GetProperty("sandboxed").GetBoolean());
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
    }

    private ProcessSpawnFunctions CreateFunctions(
        Dictionary<PermissionKind, PermissionKindRules>? rules = null,
        ProcessSandboxMode mode = ProcessSandboxMode.Require,
        Func<string>? createTemporaryDirectory = null,
        string? workspaceRoot = null)
    {
        var kinds = rules is null ? new Dictionary<PermissionKind, PermissionKindRules>() : new(rules);
        kinds[PermissionKind.Run] = new() { AllowAll = true };
        var acquirer = new PermissionPolicyAcquirer(
            () => new LayerSource(new PermissionLayer { Kinds = kinds }), new VariableSource());
        var root = workspaceRoot ?? _workspace.Path;
        return new ProcessSpawnFunctions(
            new PolicyProcessSandboxPolicy(mode, createTemporaryDirectory: createTemporaryDirectory, trustedWorkspaceRoot: root),
            acquirer, Workspace.Create(root, root));
    }

    private static Task<JsonElement> ShellAsync(
        ProcessSpawnFunctions functions, string script, string[] arguments, string? stdin, CancellationToken token) =>
        InvokeAsync(functions, "/bin/sh", ["-c", script, "sandbox-test", .. arguments], stdin, token);

    private static async Task<JsonElement> InvokeAsync(
        ProcessSpawnFunctions functions, string executable, string[] arguments, string? stdin, CancellationToken token)
    {
        var result = await Assert.Single(functions.Functions).InvokeAsync(new AIFunctionArguments
        {
            ["executable"] = executable, ["arguments"] = arguments, ["stdin"] = stdin, ["timeoutMs"] = 15000,
            Context = new Dictionary<object, object?>
            {
                [typeof(ScriptInvocationContext)] = new ScriptInvocationContext(AgentSessionId.Create().Value.ToString("N"))
            }
        }, token);
        return Assert.IsType<JsonElement>(result);
    }

    private static async Task ServeHttpAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 10\r\nConnection: close\r\n\r\nreachable\n"u8.ToArray(), token);
        }
    }

    private sealed class LayerSource(PermissionLayer layer) : IPermissionLayerSource
    {
        public PermissionLayer AppDefaults => layer;
        public PermissionLayer? WorkspaceProfile => null;
        public PermissionLayer? GetSessionOverride(AgentSessionId sessionId) => null;
    }

    private sealed class VariableSource : IPermissionVariableSource
    {
        public string? GetVariable(string name) => null;
    }
}
