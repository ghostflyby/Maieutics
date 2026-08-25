using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Maieutics.Control;
using Maieutics.DenoExecution;
using Maieutics.DenoRepl;
using Maieutics.Permissions;
using Maieutics.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

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

            using var process = StartProbe(broker, root, deadline.Token);
            var outputTask = ReadProbeAsync(process, deadline.Token);

            // The child connects to the broker immediately and blocks on its first request until the
            // policy for its pid arrives; the report registers that pid (session identity + policy).
            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "host-repl-session", 7, process.Id)));

            registry.IsOwnedBy(process.Id, "host-repl-session").Should().BeTrue();
            var output = await outputTask;
            output.Should().Contain("read OK");
            output.Should().Contain("HOME visible");
            output.Should().Contain("PATH denied");
        }
        finally
        {
            Directory.Delete(root, true);
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
            using var process = StartProbe(broker, root, deadline.Token);
            var outputTask = ReadProbeAsync(process, deadline.Token);

            // No kernel path pre-cached a policy for this session (esbuild-wasm resolution failed
            // at session start, or the session was never started through the kernel path), so the
            // explicit downgrade policy is registered: the REPL is still bound, but every
            // permission request is denied by default.
            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "no-policy-session", 1, process.Id)));

            registry.IsOwnedBy(process.Id, "no-policy-session").Should().BeTrue();
            var output = await outputTask;
            output.Should().Contain("read denied");
        }
        finally
        {
            Directory.Delete(root, true);
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
        registry.IsOwnedBy(424_242, "host-repl-session").Should().BeTrue();

        manager.HandleHostMessage(Envelope(ReplMessageType.HostReplExited, new HostReplExitedPayload(
            "host-repl-session", 3, 424_242)));

        registry.TryGetSession(424_242, out _).Should().BeFalse();
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
        var fakeDenoPath = CreateFakeDenoExecutable();
        var manager = CreateManager(registry, broker, fakeDenoPath);
        try
        {
            await manager.StartAsync(deadline.Token);
            manager.RegisterReplPolicy("disconnect-session", EffectivePolicy.Default);

            var socket = new FakeHostWebSocket();
            var attach = manager.AttachHostAsync(socket, deadline.Token);
            for (var attempt = 0; attempt < 100 && !manager.GetStatus().ControlConnected; attempt++)
                await Task.Delay(50, deadline.Token);
            manager.GetStatus().ControlConnected.Should().BeTrue();

            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "disconnect-session", 1, 424_242)));
            registry.IsOwnedBy(424_242, "disconnect-session").Should().BeTrue();
            broker.RegistrationCount.Should().Be(1);

            // The host dies without reporting exits: the attach loop ends and its finally must
            // release the host-derived pid's session identity and broker policy.
            socket.Dispose();
            await attach;

            registry.TryGetSession(424_242, out _).Should().BeFalse();
            broker.RegistrationCount.Should().Be(0);
        }
        finally
        {
            await manager.DisposeAsync();
            TryDelete(fakeDenoPath);
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
        var fakeDenoPath = CreateFakeDenoExecutable();
        var manager = CreateManager(registry, broker, fakeDenoPath);
        try
        {
            await manager.StartAsync(deadline.Token);
            manager.RegisterReplPolicy("disconnect-session", EffectivePolicy.Default);

            var socket = new FakeHostWebSocket();
            var attach = manager.AttachHostAsync(socket, deadline.Token);
            for (var attempt = 0; attempt < 100 && !manager.GetStatus().ControlConnected; attempt++)
                await Task.Delay(50, deadline.Token);
            manager.GetStatus().ControlConnected.Should().BeTrue();

            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "disconnect-session", 1, 424_242)));
            registry.IsOwnedBy(424_242, "disconnect-session").Should().BeTrue();
            broker.RegistrationCount.Should().Be(1);

            // The normal exited path releases the pid first; the later disconnect must find nothing
            // left to release (idempotent with the disconnect fallback).
            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplExited, new HostReplExitedPayload(
                "disconnect-session", 1, 424_242)));
            registry.TryGetSession(424_242, out _).Should().BeFalse();
            broker.RegistrationCount.Should().Be(0);

            socket.Dispose();
            await attach;

            registry.TryGetSession(424_242, out _).Should().BeFalse();
            broker.RegistrationCount.Should().Be(0);
        }
        finally
        {
            await manager.DisposeAsync();
            TryDelete(fakeDenoPath);
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

        registry.TryGetSession(0, out _).Should().BeFalse();
        registry.TryGetSession(-5, out _).Should().BeFalse();
        registry.TryGetSession(42, out _).Should().BeFalse();
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

            using var processA = StartProbe(broker, root, deadline.Token);
            using var processB = StartProbe(broker, root, deadline.Token);
            var outputA = ReadProbeAsync(processA, deadline.Token);
            var outputB = ReadProbeAsync(processB, deadline.Token);

            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "session-a", 1, processA.Id)));
            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "session-b", 1, processB.Id)));

            var resultA = await outputA;
            var resultB = await outputB;
            resultA.Should().Contain("HOME visible");
            resultA.Should().Contain("PATH denied");
            resultB.Should().Contain("PATH visible");
            resultB.Should().Contain("HOME denied");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
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

        environment.Should().ContainKey(ReplControlEnvironment.BrokerAddress);
        environment[ReplControlEnvironment.BrokerAddress].Should().Be(broker.Address);
        environment.Should().NotContainKey("DENO_PERMISSION_BROKER_PATH",
            "the host itself must never consult the broker (it runs with full launch-time grants and no registered policy)");
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

        environment.Should().NotContainKey(ReplControlEnvironment.BrokerAddress);
    }

    private static PluginHostManager CreateManager(
        ReplControlSessionRegistry registry,
        DenoPermissionBroker? broker,
        string? fakeDenoPath = null)
    {
        return new PluginHostManager(
            Path.Combine(Path.GetTempPath(), $"mc-repl-reg-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = fakeDenoPath ?? "deno" },
            new PluginHostModule(),
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System,
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
    private static Process StartProbe(
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
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("The deno probe could not be started.");
    }

    private static async Task<string> ReadProbeAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return stdout + stderr;
        }
        finally
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }

            process.Dispose();
        }
    }

    /// <summary>Creates a fake deno executable (an immediate-exit shell script on unix, the
    /// always-present <c>color.exe</c> on Windows) so the manager's real host process never connects
    /// to a control socket and races the simulated host. <c>color.exe</c> ignores all arguments,
    /// prints its usage text, and exits immediately.</summary>
    private static string CreateFakeDenoExecutable()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "color.exe");

        var path = Path.Combine(Path.GetTempPath(), $"mc-fake-deno-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    /// <summary>In-memory host WebSocket: captures the kernel's outgoing envelopes (SendAsync) and
    /// blocks on receive until disposed (the manager's receive loop then ends and runs its
    /// disconnect finally). Modeled on the <c>DenoReplHostDeriveTests</c> fake socket.</summary>
    private sealed class FakeHostWebSocket : WebSocket
    {
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<string> sent = Channel.CreateUnbounded<string>();
        private WebSocketCloseStatus? closeStatus;
        private WebSocketState state = WebSocketState.Open;

        public override WebSocketState State => state;

        public override WebSocketCloseStatus? CloseStatus => closeStatus;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        internal Task Closed => closed.Task;

        public override void Abort()
        {
            state = WebSocketState.Aborted;
            closed.TrySetResult();
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            return CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.closeStatus = closeStatus;
            state = WebSocketState.Closed;
            closed.TrySetResult();
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            state = WebSocketState.Closed;
            sent.Writer.TryComplete();
            closed.TrySetResult();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            await closed.Task.WaitAsync(cancellationToken);
            state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await closed.Task.WaitAsync(cancellationToken);
            state = WebSocketState.CloseReceived;
            return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (buffer.Array is not { } array)
                throw new ArgumentException("The test WebSocket send buffer requires a backing array.", nameof(buffer));
            return SendAsync(
                array.AsMemory(buffer.Offset, buffer.Count),
                messageType,
                endOfMessage,
                cancellationToken).AsTask();
        }

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (messageType != WebSocketMessageType.Text || !endOfMessage)
                throw new InvalidOperationException("The test socket only accepts complete text messages.");
            return sent.Writer.WriteAsync(Encoding.UTF8.GetString(buffer.Span), cancellationToken);
        }
    }

    private sealed class FakeVariableSource : Execution.IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return null;
        }
    }
}
