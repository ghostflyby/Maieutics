using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Permissions;
using Maieutics.Processes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

/// <summary>The same real-process contract runs under seatbelt on macOS and bubblewrap
/// on Linux. A broken installed launcher is a failure. Linux CI installs and probes bubblewrap.</summary>
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
    public async Task TerminalRunUsesTheSandboxWithoutARunGrantOrApproval()
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var outside = Path.Combine(PhysicalParent, "outside.txt");
        var inside = Path.Combine(PhysicalWorkspace, "inside.txt");
        await File.WriteAllTextAsync(outside, "outside", budget.Token);
        var sandbox = new PolicyProcessSandboxPolicy(ProcessSandboxMode.Require);
        await using var registry = new TerminalRegistry(
            Workspace.Create(PhysicalWorkspace, PhysicalWorkspace),
            new TerminalOptions(), new LocalTerminalProcessFactory(), NullLogger<TerminalSession>.Instance,
            TestPermissionPolicies.Unconfigured(), new ProcessExecution(sandbox));

        var result = await registry.RunOnceAsync(AgentSessionId.Create(), "/bin/sh",
            ["-c", "if /bin/cat \"$1\" >/dev/null 2>&1; then exit 17; fi; printf confined >\"$2\"", "test", outside, inside],
            TimeSpan.FromSeconds(15), new TerminalSnapshotRequest(), budget.Token);

        Assert.True(result.Settled);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("confined", await File.ReadAllTextAsync(inside, budget.Token));
    }

    [Fact(Timeout = 30_000)]
    public async Task LinuxTerminalCanUseHomeAsWorkspaceWhenOptionalCredentialsAreAbsent()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("This exercises absent Linux mount targets in a writable home.");
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var credential = Path.Combine(PhysicalWorkspace, ".npmrc");
        var absent = Path.Combine(PhysicalWorkspace, ".aws");
        var output = Path.Combine(PhysicalWorkspace, "output.txt");
        await File.WriteAllTextAsync(credential, "private", budget.Token);
        var protections = ProcessReadProtection.CaptureOptional([], PhysicalWorkspace, null, null);
        var sandbox = new PolicyProcessSandboxPolicy(ProcessSandboxMode.Require,
            readMode: MaieuticsProcessSandboxReadMode.HostRead, optionalProtectedPaths: protections);
        var workspace = Workspace.Create(PhysicalWorkspace, PhysicalWorkspace);
        await using var registry = new TerminalRegistry(
            workspace, new TerminalOptions(), new LocalTerminalProcessFactory(), NullLogger<TerminalSession>.Instance,
            TestPermissionPolicies.Unconfigured(), new ProcessExecution(sandbox));

        var terminal = await registry.RunOnceAsync(AgentSessionId.Create(), "/bin/sh",
            ["-c", "if ( : <\"$1\" ) 2>/dev/null; then exit 6; fi; printf writable >\"$2\"",
                "sandbox-test", credential, output],
            TimeSpan.FromSeconds(15), new TerminalSnapshotRequest(), budget.Token);
        Assert.True(terminal.Settled);
        Assert.Equal(0, terminal.ExitCode);
        Assert.Equal("writable", await File.ReadAllTextAsync(output, budget.Token));
        Assert.False(Directory.Exists(absent));

        var explicitDeny = new PermissionPolicyAcquirer(
            () => new LayerSource(new PermissionLayer
            {
                Kinds = new Dictionary<PermissionKind, PermissionKindRules>
                {
                    [PermissionKind.Read] = new() { Deny = [absent] }
                }
            }), new VariableSource());
        var policy = explicitDeny.Acquire(WorkspacePermissionSurface.ModelToolBaseline(PhysicalWorkspace));
        Assert.IsType<ProcessLaunchPlan.DeniedPlan>(
            sandbox.Plan(policy, "/bin/sh", [], workingDirectory: PhysicalWorkspace));
    }

    [Theory(Timeout = 30_000)]
    [InlineData("/bin/zsh", "zsh-ok", "-f", "-c", "printf zsh-ok")]
    [InlineData("/usr/bin/uname", "Darwin", "-a")]
    [InlineData("/usr/bin/perl", "perl-ok", "-e", "print 'perl-ok'")]
    [InlineData("/usr/bin/git", "git version", "--version")]
    [InlineData("/usr/bin/python3", "Python 3", "--version")]
    [InlineData("/usr/bin/ruby", "ruby-ok", "-e", "puts 'ruby-ok'")]
    [InlineData("/usr/bin/ssh", "OpenSSH_", "-V")]
    public async Task MacOsSystemToolsRunWithHostReadSandbox(
        string executable, string expected, params string[] arguments)
    {
        if (!OperatingSystem.IsMacOS()) Assert.Skip("These are macOS system tools.");
        using var budget = Budget(TestContext.Current.CancellationToken);
        var control = await InvokeAsync(CreateFunctions(mode: ProcessSandboxMode.Off), executable, arguments, null, budget.Token);
        if (control.GetProperty("exitCode").GetInt32() != 0)
            Assert.Skip($"The host installation of {executable} is unavailable.");
        var result = await InvokeAsync(CreateFunctions(readMode: MaieuticsProcessSandboxReadMode.HostRead),
            executable, arguments, null, budget.Token);

        Assert.True(result.GetProperty("sandboxed").GetBoolean());
        Assert.True(result.GetProperty("exitCode").GetInt32() == 0, result.GetRawText());
        Assert.Contains(expected, result.GetProperty("stdout").GetString() + result.GetProperty("stderr").GetString());
    }

    [Theory(Timeout = 30_000)]
    [InlineData("/bin/sh", false)]
    [InlineData("/bin/zsh", false)]
    [InlineData("/bin/sh", true)]
    [InlineData("/bin/zsh", true)]
    public async Task MacOsSandboxedInteractiveShellInterruptsItsForegroundJob(string shell, bool hostRead)
    {
        if (!OperatingSystem.IsMacOS()) Assert.Skip("This verifies job control through Seatbelt.");
        using var budget = Budget(TestContext.Current.CancellationToken);
        var options = new TerminalOptions { GracefulExitTimeout = TimeSpan.FromSeconds(2) };
        var policy = TestPermissionPolicies.Unconfigured().Acquire(
            WorkspacePermissionSurface.ModelToolBaseline(PhysicalWorkspace));
        await using var session = new TerminalSession("sandbox", false, PhysicalWorkspace,
            TerminalSessionKind.Persistent, shell, shell == "/bin/zsh" ? ["-f", "-i"] : ["-i"],
            policy, options, new LocalTerminalProcessFactory(), NullLogger<TerminalSession>.Instance,
            new ProcessExecution(new PolicyProcessSandboxPolicy(ProcessSandboxMode.Require, readMode: hostRead
                ? MaieuticsProcessSandboxReadMode.HostRead
                : MaieuticsProcessSandboxReadMode.Allowlist)));
        var snapshot = new TerminalSnapshotRequest(Full: true);

        await session.ExecuteInputAsync(TerminalInputBatchParser.Parse("t PS1='MAIEUTICS-'PROMPT\nk <CR>"),
            snapshot, budget.Token);
        await WaitForLineAsync(session, "MAIEUTICS-PROMPT", budget.Token);

        const string job = """t /usr/bin/perl -MPOSIX -e '$|=1; $SIG{INT}=sub{exit 130}; die "not in foreground" unless POSIX::tcgetpgrp(0)==getpgrp(); print "MAIEUTICS-", "FOREGROUND\n"; select(undef,undef,undef,30)'; printf 'MAIEUTICS-%s:%s\n' INTERRUPTED "$?" """;
        await session.ExecuteInputAsync(TerminalInputBatchParser.Parse(job + "\nk <CR>"), snapshot, budget.Token);
        await WaitForLineAsync(session, "MAIEUTICS-FOREGROUND", budget.Token);
        await session.InterruptAsync(snapshot, budget.Token);
        await WaitForLineAsync(session, "MAIEUTICS-INTERRUPTED:130", budget.Token);
        Assert.Equal("idle", session.GetSnapshot().State);
    }

    private static async Task WaitForLineAsync(TerminalSession session, string expected, CancellationToken token)
    {
        while (true)
        {
            var version = session.ScreenVersion;
            var snapshot = session.Snapshot(new TerminalSnapshotRequest(Full: true));
            if (snapshot.Frame.Lines.Any(line => line.Text.Contains(expected, StringComparison.Ordinal))) return;

            try
            {
                await session.WaitForScreenVersionAsync(version + 1, token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"Terminal did not print {expected}. Last screen: " +
                    string.Join(Environment.NewLine, snapshot.Frame.Lines.Select(line => line.Text)));
            }
        }
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

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BroadReadPreservesProtectedPathsAndWorkspaceWrites(bool useReadAllowAll)
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var ordinary = Path.Combine(PhysicalParent, "developer.conf");
        var secret = Path.Combine(PhysicalParent, "credentials");
        await File.WriteAllTextAsync(ordinary, "normal configuration", budget.Token);
        await File.WriteAllTextAsync(secret, "protected credential", budget.Token);
        var alias = Path.Combine(PhysicalWorkspace, "credential-alias");
        File.CreateSymbolicLink(alias, secret);
        var output = Path.Combine(PhysicalWorkspace, "output.txt");
        var functions = CreateFunctions(
            new() { [PermissionKind.Read] = new() { AllowAll = useReadAllowAll } },
            readMode: useReadAllowAll ? MaieuticsProcessSandboxReadMode.Allowlist : MaieuticsProcessSandboxReadMode.HostRead,
            protectedPaths: [secret]);
        var result = await ShellAsync(functions, """
            cat "$1" || exit 5
            if cat "$2" 2>/dev/null || cat "$3" 2>/dev/null; then exit 6; fi
            if printf overwrite >"$1"; then exit 7; fi
            printf produced >"$4"
            """, [ordinary, secret, alias, output], null, budget.Token);
        Assert.True(result.GetProperty("exitCode").GetInt32() == 0, result.GetRawText());
        Assert.Equal("normal configuration", result.GetProperty("stdout").GetString());
        Assert.Equal("produced", await File.ReadAllTextAsync(output, budget.Token));
    }

    [Fact(Timeout = 30_000)]
    public async Task BroadReadKeepsAProtectedPathHiddenWhenTheHostCreatesItLater()
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var home = Directory.CreateDirectory(Path.Combine(PhysicalParent, "home")).FullName;
        var protectedFile = Path.Combine(home, "credentials");
        var normal = Path.Combine(home, "normal-config");
        await File.WriteAllTextAsync(normal, "normal", budget.Token);
        const string body = """
            test "$(cat "$1")" = normal || exit 4
            printf 'started\n'
            read -r release || exit 5
            if cat "$2" 2>/dev/null; then exit 6; fi
            printf 'hidden\n'
            """;
        var policy = TestPermissionPolicies.Unconfigured().Acquire(
            WorkspacePermissionSurface.ModelToolBaseline(PhysicalWorkspace));
        var sandbox = new PolicyProcessSandboxPolicy(ProcessSandboxMode.Require,
            readMode: MaieuticsProcessSandboxReadMode.HostRead, protectedPaths: [protectedFile]);
        var plan = Assert.IsType<ProcessLaunchPlan.LaunchPlan>(
            sandbox.Plan(policy, "/bin/sh", ["-c", body, "test", normal, protectedFile],
                workingDirectory: PhysicalWorkspace));
        try
        {
            var startInfo = new ProcessStartInfo(plan.Argv[0])
            {
                UseShellExecute = false,
                WorkingDirectory = PhysicalWorkspace,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in plan.Argv.Skip(1)) startInfo.ArgumentList.Add(argument);
            startInfo.Environment.Clear();
            foreach (var (name, value) in plan.Environment) startInfo.Environment[name] = value;

            await using var child = await TestChildProcess.StartAsync(startInfo, budget.Token);
            var ready = await child.Raw.StandardOutput.ReadLineAsync(budget.Token);
            if (ready != "started")
                Assert.Fail(await child.ReadToEndAsync(budget.Token));

            await File.WriteAllTextAsync(protectedFile, "late credential", budget.Token);
            await child.Raw.StandardInput.WriteLineAsync("continue".AsMemory(), budget.Token);
            child.Raw.StandardInput.Close();
            var output = await child.ReadToEndAsync(budget.Token);
            Assert.Equal(0, child.Raw.ExitCode);
            Assert.Equal("hidden\n", output);
        }
        finally
        {
            plan.ReleaseTemporaryDirectory();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task HostReadCannotConnectToHostUnixSocketEvenWithIpNetworkAllowed()
    {
        RequireUnix();
        using var budget = Budget(TestContext.Current.CancellationToken);
        var socketPath = Path.Combine("/tmp", $"mc-{Guid.NewGuid():N}.sock");
        try
        {
            using var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            server.Bind(new UnixDomainSocketEndPoint(socketPath));
            server.Listen(1);
            var functions = CreateFunctions(new() { [PermissionKind.Net] = new() { AllowAll = true } },
                readMode: MaieuticsProcessSandboxReadMode.HostRead);
            var result = await InvokeAsync(functions, "/usr/bin/curl",
                ["--disable", "--noproxy", "*", "--unix-socket", socketPath, "--connect-timeout", "1", "--max-time", "2", "http://localhost/"],
                null, budget.Token);
            Assert.True(result.GetProperty("exitCode").GetInt32() == 7, result.GetRawText());
            Assert.False(server.Poll(0, SelectMode.SelectRead));
        }
        finally
        {
            File.Delete(socketPath);
        }
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
            if (readDenial)
                Assert.NotEqual(0, read.GetProperty("exitCode").GetInt32());
            else
            {
                AssertSuccess(read);
                Assert.Equal("secret-canary\n", read.GetProperty("stdout").GetString());
            }
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
        if (readDenial)
        {
            Assert.NotEqual(0, read.GetProperty("exitCode").GetInt32());
            Assert.DoesNotContain("ancestor-canary", read.GetProperty("stdout").GetString() ?? string.Empty);
        }
        else
        {
            AssertSuccess(read);
            Assert.Equal("ancestor-canary", read.GetProperty("stdout").GetString());
        }
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
    [InlineData(false, MaieuticsProcessSandboxReadMode.Allowlist)]
    [InlineData(true, MaieuticsProcessSandboxReadMode.Allowlist)]
    [InlineData(true, MaieuticsProcessSandboxReadMode.HostRead)]
    public async Task OnlyAnUnqualifiedNetworkGrantAllowsHostConnections(
        bool denyOverridesAllow, MaieuticsProcessSandboxReadMode readMode)
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
            var denied = await InvokeAsync(CreateFunctions(deniedRules, readMode: readMode), "/usr/bin/curl", curl, null, budget.Token);
            Assert.True(denied.GetProperty("sandboxed").GetBoolean());
            Assert.NotEqual(0, denied.GetProperty("exitCode").GetInt32());
            var allowed = await InvokeAsync(CreateFunctions(new() { [PermissionKind.Net] = new() { AllowAll = true } }, readMode: readMode),
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
        if (OperatingSystem.IsLinux() && PolicyProcessSandboxPolicy.LocateBubblewrap(null) is null)
            Assert.Skip("The real Linux sandbox tests require bubblewrap.");
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
        string? workspaceRoot = null,
        MaieuticsProcessSandboxReadMode readMode = MaieuticsProcessSandboxReadMode.Allowlist,
        IReadOnlyList<string>? protectedPaths = null)
    {
        var kinds = rules is null ? new Dictionary<PermissionKind, PermissionKindRules>() : new(rules);
        kinds[PermissionKind.Run] = new() { AllowAll = true };
        var acquirer = new PermissionPolicyAcquirer(
            () => new LayerSource(new PermissionLayer { Kinds = kinds }), new VariableSource());
        var root = workspaceRoot ?? _workspace.Path;
        return new ProcessSpawnFunctions(
            new ProcessExecution(new PolicyProcessSandboxPolicy(
                mode, createTemporaryDirectory: createTemporaryDirectory, readMode: readMode,
                protectedPaths: protectedPaths)),
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
                [typeof(AgentSessionId)] = AgentSessionId.Create()
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
