using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Maieutics.Control;
using Maieutics.DenoExecution;
using Maieutics.DenoRepl;
using Maieutics.Permissions;
using Maieutics.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

using FakeHostWebSocket = Maieutics.Testing.TestWebSocket;
namespace Maieutics.Product.Tests;

/// <summary>
///     B2 (ADR 0020): the kernel binds a REPL process the plugin host derived. The
///     <c>host.repl.spawned</c> / <c>host.repl.exited</c> host messages must give the host-derived
///     REPL the same session identity and broker policy a kernel-derived REPL has: the report is
///     dispatched through <see cref="PluginHostManager.HandleHostMessage"/> (the same switch the
///     host WebSocket loop feeds), the session registry and the permission broker are the
///     observable effect. A real Deno child connects to the broker for the pid the report
///     registered, proving the registration is authoritative at the permission boundary.
/// </summary>
public sealed class PluginHostReplRegistrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact(Timeout = 60_000)]
    public async Task SpawnedReportRegistersSessionIdentityAndBrokerPolicy()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var registry = new ReplControlSessionRegistry();
        await using var broker = DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);
        var manager = CreateManager(registry, broker);
        var root = CreateProbeRoot();
        try
        {
            var policy = CreatePolicy(root, "HOME");
            manager.RegisterReplPolicy("host-repl-session", policy);

            await using var process = await StartProbeAsync(broker, root, deadline.Token);
            var outputTask = ReadProbeAsync(process, deadline.Token);

            // The child connects to the broker immediately and blocks on its first request until the
            // policy for its pid arrives; the report registers that pid (session identity + policy).
            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "host-repl-session", 7, process.Id)));

            Assert.True(registry.IsOwnedBy(process.Id, "host-repl-session"));
            var output = await outputTask;
            Assert.Contains("read OK", output);
            Assert.Contains("HOME visible", output);
            Assert.Contains("PATH denied", output);
        }
        finally
        {
            await TempArea.DeleteAsync(root, CancellationToken.None);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task SpawnedReportWithoutCachedPolicyRegistersDefaultPolicy()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var registry = new ReplControlSessionRegistry();
        await using var broker = DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);
        var manager = CreateManager(registry, broker);
        var root = CreateProbeRoot();
        try
        {
            await using var process = await StartProbeAsync(broker, root, deadline.Token);
            var outputTask = ReadProbeAsync(process, deadline.Token);

            // No kernel path pre-cached a policy for this session (esbuild-wasm resolution failed
            // at session start, or the session was never started through the kernel path), so the
            // explicit downgrade policy is registered: the REPL is still bound, but every
            // permission request is denied by default.
            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "no-policy-session", 1, process.Id)));

            Assert.True(registry.IsOwnedBy(process.Id, "no-policy-session"));
            var output = await outputTask;
            Assert.Contains("read denied", output);
        }
        finally
        {
            await TempArea.DeleteAsync(root, CancellationToken.None);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ExitedReportReleasesSessionIdentityAndBrokerPolicy()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var registry = new ReplControlSessionRegistry();
        await using var broker = DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);
        var manager = CreateManager(registry, broker);
        manager.RegisterReplPolicy("host-repl-session", EffectivePolicy.Default);

        manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
            "host-repl-session", 3, 424_242)));
        Assert.True(registry.IsOwnedBy(424_242, "host-repl-session"));

        manager.HandleHostMessage(Envelope(ReplMessageType.HostReplExited, new HostReplExitedPayload(
            "host-repl-session", 3, 424_242)));

        Assert.False(registry.TryGetSession(424_242, out _));
        // UnregisterProcess is idempotent; the released slot cannot be observed directly, but the
        // session identity release above is the observable half of the pair.
        broker.UnregisterProcess(424_242);
    }

    [Fact(Timeout = 60_000)]
    public async Task HostDisconnectReleasesRegisteredHostReplRegistrations()
    {
        // A crashed or killed host never reports host.repl.exited for the REPLs it derived; when
        // the host connection ends, the manager must release every still-tracked host-derived pid's
        // session identity and broker policy (the same registrations the spawned report created).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var registry = new ReplControlSessionRegistry();
        await using var broker = DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);
        var manager = CreateManager(registry, broker, FakeDenoExecutable.Path);
        try
        {
            await manager.StartAsync(deadline.Token);
            manager.RegisterReplPolicy("disconnect-session", EffectivePolicy.Default);

            var socket = new FakeHostWebSocket();
            var attached = manager.HostConnectionAttached;
            var attach = manager.AttachHostAsync(socket, deadline.Token);
            // The manager completes the attach signal in the same locked transition that
            // installed the socket, so the awaited task proves acceptance instead of inferring
            // it from the fake's first receive or polling GetStatus().ControlConnected.
            await attached.WaitAsync(TimeSpan.FromSeconds(30), deadline.Token);
            Assert.True(manager.GetStatus().ControlConnected);

            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "disconnect-session", 1, 424_242)));
            Assert.True(registry.IsOwnedBy(424_242, "disconnect-session"));
            Assert.Equal(1, broker.RegistrationCount);

            // The host dies without reporting exits: the attach loop ends and its finally must
            // release the host-derived pid's session identity and broker policy.
            socket.Dispose();
            await attach;

            Assert.False(registry.TryGetSession(424_242, out _));
            Assert.Equal(0, broker.RegistrationCount);
        }
        finally
        {
            await manager.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task HostDisconnectAfterExitedReportsIsIdempotent()
    {
        // The host that reported host.repl.exited for a REPL must not be cleaned up twice: the
        // disconnect fallback only releases pids that are still tracked, so a normal exited report
        // followed by a disconnect releases nothing and never throws.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var registry = new ReplControlSessionRegistry();
        await using var broker = DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);
        var manager = CreateManager(registry, broker, FakeDenoExecutable.Path);
        try
        {
            await manager.StartAsync(deadline.Token);
            manager.RegisterReplPolicy("disconnect-session", EffectivePolicy.Default);

            var socket = new FakeHostWebSocket();
            var attached = manager.HostConnectionAttached;
            var attach = manager.AttachHostAsync(socket, deadline.Token);
            // The manager completes the attach signal in the same locked transition that
            // installed the socket, so the awaited task proves acceptance instead of inferring
            // it from the fake's first receive or polling GetStatus().ControlConnected.
            await attached.WaitAsync(TimeSpan.FromSeconds(30), deadline.Token);
            Assert.True(manager.GetStatus().ControlConnected);

            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "disconnect-session", 1, 424_242)));
            Assert.True(registry.IsOwnedBy(424_242, "disconnect-session"));
            Assert.Equal(1, broker.RegistrationCount);

            // The normal exited path releases the pid first; the later disconnect must find nothing
            // left to release (idempotent with the disconnect fallback).
            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplExited, new HostReplExitedPayload(
                "disconnect-session", 1, 424_242)));
            Assert.False(registry.TryGetSession(424_242, out _));
            Assert.Equal(0, broker.RegistrationCount);

            socket.Dispose();
            await attach;

            Assert.False(registry.TryGetSession(424_242, out _));
            Assert.Equal(0, broker.RegistrationCount);
        }
        finally
        {
            await manager.DisposeAsync();
        }
    }

    [Fact]
    public void MalformedReportsAreIgnoredWithoutThrowing()
    {
        var registry = new ReplControlSessionRegistry();
        var manager = CreateManager(registry, broker: null);

        // Positive-pid / non-empty-session validation rejects the reports; unknown message types
        // stay tolerated like every other host bus message. None of these may crash the loop.
        manager.HandleHostMessage(
            """{"version":1,"type":"host.repl.spawned","payload":{"sessionId":"","generation":1,"pid":0}}""");
        manager.HandleHostMessage(
            """{"version":1,"type":"host.repl.exited","payload":{"sessionId":"bad","generation":1,"pid":-5}}""");
        manager.HandleHostMessage("""{"version":1,"type":"host.repl.unknown","payload":{}}""");
        manager.HandleHostMessage("not-json");

        Assert.False(registry.TryGetSession(0, out _));
        Assert.False(registry.TryGetSession(-5, out _));
        Assert.False(registry.TryGetSession(42, out _));
    }

    [Fact(Timeout = 60_000)]
    public async Task ReplPoliciesAreKeyedBySessionId()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var registry = new ReplControlSessionRegistry();
        await using var broker = DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);
        var manager = CreateManager(registry, broker);
        var root = CreateProbeRoot();
        try
        {
            manager.RegisterReplPolicy("session-a", CreatePolicy(root, "HOME"));
            manager.RegisterReplPolicy("session-b", CreatePolicy(root, "PATH"));

            await using var processA = await StartProbeAsync(broker, root, deadline.Token);
            await using var processB = await StartProbeAsync(broker, root, deadline.Token);
            var outputA = ReadProbeAsync(processA, deadline.Token);
            var outputB = ReadProbeAsync(processB, deadline.Token);

            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "session-a", 1, processA.Id)));
            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "session-b", 1, processB.Id)));

            var resultA = await outputA;
            var resultB = await outputB;
            Assert.Contains("HOME visible", resultA);
            Assert.Contains("PATH denied", resultA);
            Assert.Contains("PATH visible", resultB);
            Assert.Contains("HOME denied", resultB);
        }
        finally
        {
            await TempArea.DeleteAsync(root, CancellationToken.None);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task BrokerAddressIsPassedToTheHostWhenConfigured()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var broker = DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);
        var options = new PluginHostProcessOptions(
            "deno",
            "/host.ts",
            "/control.sock",
            "/plugins.json",
            "host-1",
            "/sdk.ts",
            "/worker.ts",
            "/deno.json",
            broker);

        var environment = ReplControlEnvironment.FromHostOptions(options);

        Assert.True(environment.ContainsKey(ReplControlEnvironment.BrokerAddress));
        Assert.Equal(broker.Address, environment[ReplControlEnvironment.BrokerAddress]);
        Assert.False(environment.ContainsKey("DENO_PERMISSION_BROKER_PATH"));
    }

    [Fact]
    public void BrokerAddressIsOmittedWhenNoBrokerIsConfigured()
    {
        var options = new PluginHostProcessOptions(
            "deno",
            "/host.ts",
            "/control.sock",
            "/plugins.json",
            "host-1",
            "/sdk.ts",
            "/worker.ts",
            "/deno.json");

        var environment = ReplControlEnvironment.FromHostOptions(options);

        Assert.False(environment.ContainsKey(ReplControlEnvironment.BrokerAddress));
    }

    private static PluginHostManager CreateManager(
        ReplControlSessionRegistry registry,
        DenoPermissionBroker? broker,
        string? denoExecutable = null)
    {
        return new PluginHostManager(
            Path.Combine(Path.GetTempPath(), $"mc-repl-reg-{Guid.NewGuid():N}"),
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = denoExecutable ?? "deno" },
            new PluginHostModule(),
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            null,
            null,
            broker);
    }

    private static EffectivePolicy CreatePolicy(string readableRoot, params string[] allowedEnv)
    {
        return PermissionLayerStore.Build(
            [
                new PermissionLayer
                {
                    Kinds = new Dictionary<PermissionKind, PermissionKindRules>
                    {
                        [PermissionKind.Read] = new() { Allow = [readableRoot] },
                        [PermissionKind.Env] = new() { Allow = allowedEnv }
                    }
                }
            ],
            new VariableTable(new FakeVariableSource()));
    }

    private static string CreateProbeRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-host-repl-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Envelope<T>(string type, T payload)
    {
        return JsonSerializer.Serialize(
            new { version = 1, type, payload },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    /// <summary>Starts a real Deno child that connects to the broker and probes read + env
    /// permissions. The child blocks on its first broker request until the policy for its pid is
    /// registered, so a report can register it after spawn (the broker's slot waits, ADR 0018).</summary>
    private static async Task<TestChildProcess> StartProbeAsync(
        DenoPermissionBroker broker,
        string root,
        CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(root, $"probe-{Guid.NewGuid():N}.ts");
        var targetPath = Path.Combine(root, "target.txt");
        File.WriteAllText(targetPath, "payload");
        var escapedTarget = targetPath.Replace("\\", "\\\\");
        File.WriteAllText(
            scriptPath,
            "try { await Deno.readTextFile(\"" + escapedTarget + "\"); console.log(\"read OK\"); }\n" +
            "catch (e) { console.log(\"read denied:\", String(e).split(\"\\n\")[0]); }\n" +
            "try { console.log(\"HOME visible:\", Deno.env.get(\"HOME\")); }\n" +
            "catch (e) { console.log(\"HOME denied:\", String(e).split(\"\\n\")[0]); }\n" +
            "try { console.log(\"PATH visible:\", Deno.env.get(\"PATH\")); }\n" +
            "catch (e) { console.log(\"PATH denied:\", String(e).split(\"\\n\")[0]); }\n");
        var startInfo = new ProcessStartInfo
        {
            FileName = "deno",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--no-prompt");
        startInfo.Environment["DENO_PERMISSION_BROKER_PATH"] = broker.Address;
        startInfo.ArgumentList.Add(scriptPath);
        return await TestChildProcess.StartAsync(startInfo, cancellationToken).ConfigureAwait(false);
    }

    private static Task<string> ReadProbeAsync(TestChildProcess child, CancellationToken cancellationToken)
    {
        return child.ReadToEndAsync(cancellationToken);
    }

    /// <summary>In-memory host WebSocket: captures the kernel's outgoing envelopes (SendAsync) and

    private sealed class FakeVariableSource : Execution.IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return null;
        }
    }
}
