using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Control;
using Maieutics.DenoExecution;
using Maieutics.DenoRepl;
using Maieutics.Permissions;
using Maieutics.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

/// <summary>
///     B5b (ADR 0020): the kernel sends <c>host.repl.derive</c> to the plugin host and the
///     session factory derives the REPL through it. These tests cover the .NET send side: the
///     derive instruction assembly (entry / env / static permission shell), the derive outcome
///     correlation through the host message switch, and the factory's dual-track behavior with a
///     simulated host that reports <c>host.repl.spawned</c> / <c>host.repl.deriveFailed</c> and
///     then serves the eval channel. The simulated host is attached through
///     <see cref="PluginHostManager.AttachHostAsync"/> (the same receive loop the real control-host
///     connection feeds) and answers by driving <see cref="PluginHostManager.HandleHostMessage"/>,
///     the same seam the B2/B4 registration tests use; the eval channel is a real Kestrel WebSocket.
///     The actor-skeleton eval bridge (process_main.ts serving the WebSocket eval channel) is a
///     later migration step, so the eval channel here is driven directly.
/// </summary>
public sealed class DenoReplHostDeriveTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact]
    public void DerivePayloadSerializesCamelCaseWithPermissionsArrayShape()
    {
        var payload = new HostReplDerivePayload(
            "session-1",
            2,
            "file:///tmp/mc-repl-x/maieutics-deno-repl/process_main.ts",
            new Dictionary<string, string> { ["MAIEUTICS_REPL_SESSION"] = "session-1" },
            new HostReplPermissions(Read: JsonSerializer.SerializeToElement(
                new[] { "/tmp/modules", "/tmp/ws" })),
            Report: true);

        var json = JsonSerializer.Serialize(
            new ReplEnvelope(
                1,
                ReplMessageType.HostReplDerive,
                "c1",
                JsonSerializer.SerializeToElement(payload, ReplControlJsonContext.Default.HostReplDerivePayload)),
            ReplControlJsonContext.Default.ReplEnvelope);

        json.Should().Contain("\"host.repl.derive\"");
        json.Should().Contain("\"entryUrl\":\"file:///tmp/mc-repl-x/maieutics-deno-repl/process_main.ts\"");
        json.Should().Contain("\"env\":{\"MAIEUTICS_REPL_SESSION\":\"session-1\"}");
        // A string-array kind renders as a JSON array (the host parses boolean | string[]).
        json.Should().Contain("\"read\":[\"/tmp/modules\",\"/tmp/ws\"]");
        json.Should().Contain("\"report\":true");
        // Null (denied) kinds are omitted entirely — the host's parser rejects a literal null.
        json.Should().NotContain("\"write\"");

        // The deriveFailed counterpart is registered in the same source-generated context.
        var failed = JsonSerializer.Serialize(
            new HostReplDeriveFailedPayload("session-1", 2, "boom"),
            ReplControlJsonContext.Default.HostReplDeriveFailedPayload);
        failed.Should().Contain("\"message\":\"boom\"");
    }

    [Fact]
    public void BuildReplEnvironmentMatchesTheKernelDerivedSet()
    {
        var environment = DenoReplEnvironment.Build(
            "unix:/tmp/mc.sock",
            "env-session",
            3,
            "file:///tmp/mod.ts",
            windowsPipeName: OperatingSystem.IsWindows() ? "mc-test-pipe" : null);

        environment.Should().ContainKey(DenoReplEnvironment.IpcAddress);
        environment.Should().ContainKey(DenoReplEnvironment.SessionId);
        environment.Should().ContainKey(DenoReplEnvironment.Generation);
        environment.Should().ContainKey(DenoReplEnvironment.ClientModule);
        environment.Should().NotContainKey(DenoReplEnvironment.BrokerAddress,
            "the host appends the broker path itself (B5a); the kernel env must not repeat it");
        environment[DenoReplEnvironment.Generation].Should().Be("3");
        environment[DenoReplEnvironment.SessionId].Should().Be("env-session");
    }

    [Fact]
    public void BuildReplEnvironmentWindowsBranchAddsPipeAndSystemRoot()
    {
        // The host-derived child on Windows bootstraps its process-verified credential through the
        // named pipe and needs SystemRoot for the kernel32 FFI target; the kernel env carries both
        // (the host appends only the broker path and never invents a pipe name). On unix the same
        // keys must be absent.
        var environment = DenoReplEnvironment.Build(
            "unix:/tmp/mc.sock",
            "env-session",
            3,
            "file:///tmp/mod.ts",
            windowsPipeName: OperatingSystem.IsWindows() ? "mc-test-pipe" : null);

        if (OperatingSystem.IsWindows())
        {
            environment.Should().ContainKey(DenoReplEnvironment.PipeName);
            environment[DenoReplEnvironment.PipeName].Should().Be("mc-test-pipe");
            environment.Should().ContainKey("SystemRoot");
        }
        else
        {
            environment.Should().NotContainKey(DenoReplEnvironment.PipeName);
            environment.Should().NotContainKey("SystemRoot");
        }

        // Neither derivation path puts the broker path in the kernel env: the host forwards its own
        // MAIEUTICS_PERMISSION_BROKER as DENO_PERMISSION_BROKER_PATH for the REPL child it derives.
        environment.Should().NotContainKey(DenoReplEnvironment.BrokerAddress);
    }

    [Fact]
    public void PolicyRendersToAHostReplPermissionsStaticShell()
    {
        var policy = PermissionLayerStore.Build(
            [
                new PermissionLayer
                {
                    Kinds = new Dictionary<PermissionKind, PermissionKindRules>
                    {
                        [PermissionKind.Read] = new() { Allow = ["/tmp/modules", "/tmp/ws"] },
                        [PermissionKind.Env] = new() { Allow = ["HOME"] },
                        [PermissionKind.Net] = new() { AllowAll = true },
                        [PermissionKind.Write] = new() { DenyAll = true }
                    }
                }
            ],
            new VariableTable(new FakeVariableSource()));

        var shell = DenoPermissionRenderer.BuildHostReplPermissions(policy);

        shell.Should().NotBeNull();
        shell!.Read.Should().NotBeNull();
        shell.Read!.Value.ValueKind.Should().Be(JsonValueKind.Array);
        shell.Read.Value.EnumerateArray().Select(static item => item.GetString()).Should()
            .Equal("/tmp/modules", "/tmp/ws");
        shell.Env.Should().NotBeNull();
        shell.Env!.Value.EnumerateArray().Single().GetString().Should().Be("HOME");
        // AllowAll renders as JSON true (worker-actor spawnProcess grants the kind fully).
        shell.Net.Should().NotBeNull();
        shell.Net!.Value.ValueKind.Should().Be(JsonValueKind.True);
        // DenyAll (and absent kinds) render as null (deny-by-default).
        shell.Write.Should().BeNull();
        shell.Run.Should().BeNull();
    }

    [Fact]
    public void FullyDeniedPolicyOmitsTheShellEntirely()
    {
        var shell = DenoPermissionRenderer.BuildHostReplPermissions(EffectivePolicy.Default);

        shell.Should().BeNull();
    }

    [Fact(Timeout = 60_000)]
    public async Task DeriveFailedReportCompletesThePendingDeriveWithTheFailure()
    {
        if (OperatingSystem.IsWindows())
            return; // The host harness attaches over a Unix socket (control + eval channels).

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var harness = await CreateHostHarnessAsync(deadline.Token);

        var derive = harness.Manager.RequestReplDeriveAsync(
            new HostReplDerivePayload("failed-session", 1, "file:///proc/main.ts", new()),
            deadline.Token);
        derive.IsCompleted.Should().BeFalse();

        harness.Manager.HandleHostMessage(
            """{"version":1,"type":"host.repl.deriveFailed","payload":{"sessionId":"failed-session","generation":1,"message":"the host rejected the derive"}}""");

        var outcome = await derive;
        outcome.Failed.Should().BeTrue();
        outcome.Message.Should().Be("the host rejected the derive");
    }

    [Fact(Timeout = 60_000)]
    public async Task SpawnedReportCompletesThePendingDeriveAsSpawned()
    {
        if (OperatingSystem.IsWindows())
            return; // The host harness attaches over a Unix socket (control + eval channels).

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var registry = new ReplControlSessionRegistry();
        await using var harness = await CreateHostHarnessAsync(deadline.Token, registry);

        var derive = harness.Manager.RequestReplDeriveAsync(
            new HostReplDerivePayload("spawned-session", 1, "file:///proc/main.ts", new()),
            deadline.Token);
        derive.IsCompleted.Should().BeFalse();

        harness.Manager.HandleHostMessage(
            """{"version":1,"type":"host.repl.spawned","payload":{"sessionId":"spawned-session","generation":1,"pid":1234}}""");

        var outcome = await derive;
        outcome.Failed.Should().BeFalse();
        registry.IsOwnedBy(1234, "spawned-session").Should().BeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task StaleReportsForAnotherSessionDoNotCompleteTheDerive()
    {
        if (OperatingSystem.IsWindows())
            return; // The host harness attaches over a Unix socket (control + eval channels).

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var harness = await CreateHostHarnessAsync(deadline.Token);

        var derive = harness.Manager.RequestReplDeriveAsync(
            new HostReplDerivePayload("session-a", 0, "file:///proc/main.ts", new()),
            deadline.Token);
        // A failure and a spawn for a different session/generation must not complete this derive.
        harness.Manager.HandleHostMessage(
            """{"version":1,"type":"host.repl.deriveFailed","payload":{"sessionId":"session-b","generation":0,"message":"x"}}""");
        harness.Manager.HandleHostMessage(
            """{"version":1,"type":"host.repl.spawned","payload":{"sessionId":"session-b","generation":0,"pid":42}}""");
        derive.IsCompleted.Should().BeFalse();

        harness.Manager.HandleHostMessage(
            """{"version":1,"type":"host.repl.spawned","payload":{"sessionId":"session-a","generation":0,"pid":43}}""");
        (await derive).Failed.Should().BeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task DuplicateDeriveForTheSameSessionIsRejected()
    {
        if (OperatingSystem.IsWindows())
            return; // The host harness attaches over a Unix socket (control + eval channels).

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var harness = await CreateHostHarnessAsync(deadline.Token);

        var first = harness.Manager.RequestReplDeriveAsync(
            new HostReplDerivePayload("dup-session", 0, "file:///proc/main.ts", new()),
            deadline.Token);
        // A duplicate derive for the same session/generation is rejected synchronously.
        await harness.Manager.Invoking(manager => manager.RequestReplDeriveAsync(
                new HostReplDerivePayload("dup-session", 0, "file:///proc/main.ts", new()),
                deadline.Token))
            .Should().ThrowAsync<InvalidOperationException>();
        harness.Manager.HandleHostMessage(
            """{"version":1,"type":"host.repl.spawned","payload":{"sessionId":"dup-session","generation":0,"pid":55}}""");
        (await first).Failed.Should().BeFalse();
    }

    [Fact(Timeout = 120_000)]
    public async Task FactoryDerivesThroughAHostSpawnedReportThenWaitsForTheEvalChannel()
    {
        if (OperatingSystem.IsWindows())
            return; // The host harness attaches over a Unix socket (control + eval channels).

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var credentials = new ReplControlCredentialRegistry();
        await using var harness = await CreateFactoryHarnessAsync(deadline.Token, registry, credentials);

        var options = new DenoReplOptions { HostDerivedRepl = true, StartupTimeout = TimeSpan.FromSeconds(30) };
        var factory = new LocalDenoReplSessionFactory(
            options,
            harness.ControlHost,
            new DenoReplModule(),
            harness.EvalHost,
            registry,
            credentials,
            NullLogger<DenoReplProcess>.Instance,
            DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance),
            pluginHosts: harness.Manager);

        var credential = credentials.Issue("half-integration-session");
        var start = factory.StartAsync(
            Directory.GetCurrentDirectory(),
            "half-integration-session",
            1,
            deadline.Token);

        // Simulated host: read the derive instruction from the attached socket, then report the
        // spawned pid (the kernel registers the session identity and broker policy for it).
        var deriveJson = await harness.Host!.ReadSentAsync(deadline.Token);
        deriveJson.Should().Contain("\"host.repl.derive\"");
        var envelope = JsonSerializer.Deserialize(deriveJson, ReplControlJsonContext.Default.ReplEnvelope)!;
        var payload = JsonSerializer.Deserialize(
            envelope.Payload?.GetRawText() ?? string.Empty,
            ReplControlJsonContext.Default.HostReplDerivePayload)!;
        payload.SessionId.Should().Be("half-integration-session");
        payload.EntryUrl.Should().Contain("process_main.ts");
        payload.Env.Should().ContainKey("MAIEUTICS_REPL_SESSION");
        payload.Env.Should().NotContainKey("DENO_PERMISSION_BROKER_PATH");
        harness.Manager.HandleHostMessage(
            """{"version":1,"type":"host.repl.spawned","payload":{"sessionId":"half-integration-session","generation":1,"pid":424242}}""");

        // The host-derived child connects the eval channel; the kernel verifies it by credential.
        using var evalSocket = await ConnectWebSocketAsync(
            harness.ControlHost.SocketPath,
            ReplEvalProtocol.WebSocketPath,
            deadline.Token);
        var serveEval = Task.Run(() => ServeEvalAsync(evalSocket, credential, deadline.Token), deadline.Token);

        var generation = await start;
        await using (generation)
        {
            registry.IsOwnedBy(424242, "half-integration-session").Should().BeTrue();
            generation.ExitCode.Should().BeNull();
            // A shutdown round-trips the dispose handshake through the eval channel.
            await generation.ShutdownAsync(deadline.Token);
        }

        await serveEval;
    }

    [Fact(Timeout = 120_000)]
    public async Task FactoryFallsBackToKernelDerivationWhenTheHostRejectsTheDerive()
    {
        if (OperatingSystem.IsWindows())
            return; // The host harness attaches over a Unix socket (control + eval channels).

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        var credentials = new ReplControlCredentialRegistry();
        await using var harness = await CreateFactoryHarnessAsync(deadline.Token, registry, credentials);

        var options = new DenoReplOptions
        {
            HostDerivedRepl = true,
            StartupTimeout = TimeSpan.FromSeconds(30),
            // The kernel fallback refuses to install the module graph on demand so the test fails
            // fast and deterministically when the graph is not already cached.
            AutoInstallModuleGraph = false
        };
        var factory = new LocalDenoReplSessionFactory(
            options,
            harness.ControlHost,
            new DenoReplModule(),
            harness.EvalHost,
            registry,
            credentials,
            NullLogger<DenoReplProcess>.Instance,
            DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance),
            pluginHosts: harness.Manager);

        var start = factory.StartAsync(
            Directory.GetCurrentDirectory(),
            "fallback-session",
            1,
            deadline.Token);

        // The host rejects the derive before any pid exists; the factory must fall back to the
        // kernel-derived path instead of surfacing the host's rejection.
        var deriveJson = await harness.Host!.ReadSentAsync(deadline.Token);
        deriveJson.Should().Contain("\"host.repl.derive\"");
        harness.Manager.HandleHostMessage(
            """{"version":1,"type":"host.repl.deriveFailed","payload":{"sessionId":"fallback-session","generation":1,"message":"no module graph"}}""");

        try
        {
            var generation = await start;
            // The kernel fallback produced a real kernel-derived REPL, never the host's failure.
            await using (generation)
            {
                await generation.ShutdownAsync(deadline.Token);
            }
        }
        catch (Exception exception)
        {
            // The kernel fallback itself may fail when the esbuild-wasm module graph is absent
            // (no network/cache): that is the kernel path's own error, never the host rejection.
            exception.Message.Should().NotContain("no module graph");
        }
    }

    /// <summary>
    ///     C1 end-to-end: a REAL plugin host process derives a REAL REPL child
    ///     (<c>process_main.ts</c>) and the host-derived session factory starts a session whose
    ///     eval channel is served by that child. The derive instruction flows over the real
    ///     Kestrel control bus; the host emits <c>host.repl.spawned</c>, the kernel registers the
    ///     pid, and the child boots its WebSocket REPL client — the factory's
    ///     <c>WaitForConnectionAsync</c> resolves when the child completes the eval handshake.
    ///     Then a real Aves execution of <c>1 + 1</c> returns 2 through the session, proving the
    ///     host-derived path is functionally equivalent to the kernel-derived REPL.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task FactoryStartsARealHostDerivedReplAndExecutesAvesCode()
    {
        if (OperatingSystem.IsWindows())
            return; // The real-host harness attaches over a Unix socket (peer identity).

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(150));
        var registry = new ReplControlSessionRegistry();
        var credentials = new ReplControlCredentialRegistry();
        await using var harness = await CreateRealHostHarnessAsync(deadline.Token, registry, credentials);

        var options = new DenoReplOptions
        {
            HostDerivedRepl = true,
            StartupTimeout = TimeSpan.FromSeconds(60),
            ExecutionTimeout = TimeSpan.FromSeconds(60),
            AutoInstallModuleGraph = true
        };
        var factory = new LocalDenoReplSessionFactory(
            options,
            harness.ControlHost,
            new DenoReplModule(),
            harness.EvalHost,
            registry,
            credentials,
            NullLogger<DenoReplProcess>.Instance,
            DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance),
            pluginHosts: harness.Manager);

        var session = new DenoReplSession(
            AgentSessionId.Create(),
            "host-derived-e2e",
            false,
            Directory.GetCurrentDirectory(),
            options,
            factory,
            new DenoReplSessionTests.ImmediatePresentationRouter(),
            NullLogger<DenoReplSession>.Instance);
        await using (session)
        {
            await session.StartAsync(deadline.Token);
            var result = await session.ExecuteAsync(
                "1 + 1",
                AgentToolCallId.Create(),
                deadline.Token);
            result.ExecutionStatus.Should().Be("ok");
            result.Outputs
                .Where(static item => item is { Kind: "result", Value: not null })
                .Select(static item => item.Value?.GetInt32())
                .Should().Contain(2);
        }
    }

    /// <summary>Attaches a REAL plugin host process over the Kestrel control bus (no simulated
    /// host socket): the manager starts the materialized <c>mod.ts</c> host, it connects the
    /// control bus, and <c>host.repl.derive</c> instructions reach the real <c>ReplManager</c>
    /// which derives the real REPL child.</summary>
    private static async Task<HostHarness> CreateRealHostHarnessAsync(
        CancellationToken cancellationToken,
        ReplControlSessionRegistry registry,
        ReplControlCredentialRegistry credentials)
    {
        var socketPath = ReplControlHost.CreateSocketPath();
        var manager = CreateManager(registry, broker: null, fakeDenoPath: null, socketPath);
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            pluginHosts: manager);
        var evalHost = new ReplEvalWebSocketHost(registry, credentials);
        var application = await StartHostAsync(socketPath, controlHost, evalHost, cancellationToken);

        // The manager's host process connects the control bus (its hello registers the host pid);
        // the manager then accepts host.repl.* reports from the real host.
        await manager.StartAsync(cancellationToken);
        for (var attempt = 0; attempt < 200 && !manager.GetStatus().ControlConnected; attempt++)
            await Task.Delay(100, cancellationToken);
        manager.GetStatus().ControlConnected.Should().BeTrue();

        var harness = new HostHarness
        {
            ControlHost = controlHost,
            EvalHost = evalHost,
            Manager = manager
        };
        harness.Initialize(application, fakeDenoPath: null);
        return harness;
    }

    private sealed class HostHarness : IAsyncDisposable
    {
        internal required ReplControlHost ControlHost { get; init; }

        internal required ReplEvalWebSocketHost EvalHost { get; init; }

        /// <summary>Simulated host WebSocket when the host process is a fake deno
        /// (see <see cref="CreateHarnessAsync"/>); null when a real host process is
        /// attached through Kestrel (see <see cref="CreateRealHostHarnessAsync"/>).</summary>
        internal FakeHostWebSocket? Host { get; init; }

        internal required PluginHostManager Manager { get; init; }

        private WebApplication? application;
        private string? fakeDenoPath;

        internal void Initialize(WebApplication app, string? fakeDenoPath)
        {
            application = app;
            this.fakeDenoPath = fakeDenoPath;
        }

        public async ValueTask DisposeAsync()
        {
            Host?.Dispose();
            await Manager.DisposeAsync().ConfigureAwait(false);
            if (application is not null) await application.DisposeAsync().ConfigureAwait(false);
            if (fakeDenoPath is not null) TryDelete(fakeDenoPath);
        }
    }

    /// <summary>Attaches a simulated host directly to the manager (the same receive loop a real
    /// control-host connection feeds). The fake socket captures the kernel's outgoing envelopes so
    /// the test can assert the derive instruction; the simulated host answers by driving
    /// <see cref="PluginHostManager.HandleHostMessage"/> directly (the B2/B4 test seam).</summary>
    private static async Task<HostHarness> CreateHostHarnessAsync(
        CancellationToken cancellationToken,
        ReplControlSessionRegistry? registry = null)
    {
        return await CreateHarnessAsync(
            cancellationToken,
            registry ?? new ReplControlSessionRegistry(),
            new ReplControlCredentialRegistry(),
            attachEvalHost: false).ConfigureAwait(false);
    }

    /// <summary>Attaches a simulated host AND starts a real Kestrel control + eval host so the
    /// factory's eval channel can be served by a real WebSocket.</summary>
    private static async Task<HostHarness> CreateFactoryHarnessAsync(
        CancellationToken cancellationToken,
        ReplControlSessionRegistry registry,
        ReplControlCredentialRegistry credentials)
    {
        return await CreateHarnessAsync(cancellationToken, registry, credentials, attachEvalHost: true)
            .ConfigureAwait(false);
    }

    private static async Task<HostHarness> CreateHarnessAsync(
        CancellationToken cancellationToken,
        ReplControlSessionRegistry registry,
        ReplControlCredentialRegistry credentials,
        bool attachEvalHost)
    {
        var fakeDenoPath = CreateFakeDenoExecutable();
        var manager = CreateManager(registry, broker: null, fakeDenoPath);
        await manager.StartAsync(cancellationToken);

        var socketPath = ReplControlHost.CreateSocketPath();
        var controlHost = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            pluginHosts: manager);
        var evalHost = new ReplEvalWebSocketHost(registry, credentials);
        var application = await StartHostAsync(socketPath, controlHost, evalHost, cancellationToken);

        // The simulated host owns the test process pid so the eval connection's peer identity is
        // resolvable while its credential proves the session.
        registry.RegisterPluginHost(Environment.ProcessId, "test-host");

        var fakeSocket = new FakeHostWebSocket();
        var attach = manager.AttachHostAsync(fakeSocket, cancellationToken);
        for (var attempt = 0; attempt < 100 && !manager.GetStatus().ControlConnected; attempt++)
            await Task.Delay(50, cancellationToken);
        manager.GetStatus().ControlConnected.Should().BeTrue();

        var harness = new HostHarness
        {
            ControlHost = controlHost,
            EvalHost = evalHost,
            Host = fakeSocket,
            Manager = manager
        };
        harness.Initialize(application, fakeDenoPath);
        // Keep the attach loop referenced so the fake socket is read until the harness disposes it.
        _ = attach;
        return harness;
    }

    private static async Task ServeEvalAsync(
        Socket socket,
        string credential,
        CancellationToken cancellationToken)
    {
        // Sends the eval hello and serves the connection until the kernel disposes it: respond to
        // repl.eval.dispose with a result and close, so ShutdownAsync completes deterministically.
        var hello = JsonSerializer.Serialize(
            new ReplEvalEnvelope(
                ReplEvalProtocol.Version,
                ReplEvalMessageType.Hello,
                "hello-1",
                ReplEvalProtocol.Payload(
                    new ReplEvalIdentity("half-integration-session", 1, credential),
                    ReplEvalJsonContext.Default.ReplEvalIdentity)),
            ReplEvalJsonContext.Default.ReplEvalEnvelope);
        await SendBusAsync(socket, hello, cancellationToken);

        // The kernel replies ready; then the child only speaks on dispose.
        _ = await ReceiveBusAsync(socket, cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            string message;
            try
            {
                message = await ReceiveBusAsync(socket, cancellationToken);
            }
            catch (SocketException)
            {
                return;
            }

            var disposeEnvelope = JsonSerializer.Deserialize(
                message,
                ReplEvalJsonContext.Default.ReplEvalEnvelope)!;
            if (disposeEnvelope.Type != ReplEvalMessageType.Dispose) continue;

            var result = JsonSerializer.Serialize(
                new ReplEvalEnvelope(
                    ReplEvalProtocol.Version,
                    ReplEvalMessageType.Result,
                    disposeEnvelope.CorrelationId,
                    ReplEvalProtocol.Payload(
                        new ReplEvalResultPayload(),
                        ReplEvalJsonContext.Default.ReplEvalResultPayload)),
                ReplEvalJsonContext.Default.ReplEvalEnvelope);
            await SendBusAsync(socket, result, cancellationToken);
            // Complete the WebSocket close handshake so the kernel's receive loop ends cleanly.
            var close = BuildClientFrame([0x03, 0xE8], 0x8);
            await SendAllAsync(socket, close, cancellationToken);
            try
            {
                _ = await ReceiveFrameAsync(socket, cancellationToken);
            }
            catch (SocketException)
            {
                // The kernel already closed the underlying socket.
            }

            socket.Close();
            return;
        }
    }

    /// <summary>Creates a fake deno executable (an immediate-exit shell script) so the manager's
    /// real host process never connects to the control socket and races the simulated host.</summary>
    private static string CreateFakeDenoExecutable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc-fake-deno-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
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

    private static PluginHostManager CreateManager(
        ReplControlSessionRegistry registry,
        DenoPermissionBroker? broker,
        string? fakeDenoPath = null,
        string? socketPath = null)
    {
        return new PluginHostManager(
            Path.Combine(Path.GetTempPath(), $"mc-repl-derive-{Guid.NewGuid():N}"),
            socketPath ?? ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = fakeDenoPath ?? "deno" },
            new PluginHostModule(),
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            broker);
    }

    private static async Task<WebApplication> StartHostAsync(
        string socketPath,
        ReplControlHost controlHost,
        ReplEvalWebSocketHost evalHost,
        CancellationToken cancellationToken)
    {
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "maieutics-repl-derive-test"
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MinRequestBodyDataRate = null;
            options.Limits.MinResponseDataRate = null;
            options.ListenUnixSocket(socketPath, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });
        var application = builder.Build();
        evalHost.MapEndpoint(application);
        // The REPL child now opens the dedicated binary output endpoint as part of its startup;
        // the test kernel must serve it or the child fails to start.
        new ReplOutputWebSocketHost(
                new ReplControlSessionRegistry(),
                new ReplControlCredentialRegistry())
            .MapEndpoint(application);
        controlHost.MapEndpoints(application);
        await application.StartAsync(cancellationToken);
        return application;
    }

    private static async Task<Socket> ConnectWebSocketAsync(string socketPath, string path, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
        var handshake =
            $"GET {path} HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n";
        await socket.SendAsync(Encoding.ASCII.GetBytes(handshake), SocketFlags.None, ct);
        var response = await ReadUntilHeadersAsync(socket, ct);
        response.Should().Contain("101");
        return socket;
    }

    private static async Task SendBusAsync(Socket socket, string json, CancellationToken ct)
    {
        await SendAllAsync(socket, BuildClientFrame(Encoding.UTF8.GetBytes(json), 0x1), ct);
    }

    private static async Task<string> ReceiveBusAsync(Socket socket, CancellationToken ct)
    {
        var (opcode, payload) = await ReceiveFrameAsync(socket, ct);
        opcode.Should().Be(0x1);
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<string> ReadUntilHeadersAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var received = new StringBuilder();
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var count = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
            received.Append(Encoding.ASCII.GetString(buffer, 0, count));
        }

        return received.ToString();
    }

    private static async Task SendAllAsync(Socket socket, byte[] bytes, CancellationToken ct)
    {
        var sent = 0;
        while (sent < bytes.Length)
            sent += await socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None, ct);
    }

    private static byte[] BuildClientFrame(byte[] payload, int opcode)
    {
        // Client frames must be masked (RFC 6455): the mask bit is 0x80 in the length byte, a
        // 4-byte masking key follows, and the payload is XORed with the key.
        var mask = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(mask);
        var header = new List<byte> { (byte)(0x80 | opcode) };
        var lengthValue = payload.Length;
        if (lengthValue < 126)
        {
            header.Add((byte)(0x80 | lengthValue));
        }
        else if (lengthValue <= ushort.MaxValue)
        {
            header.Add((byte)(0x80 | 126));
            header.Add((byte)(lengthValue >> 8));
            header.Add((byte)(lengthValue & 0xFF));
        }
        else
        {
            header.Add((byte)(0x80 | 127));
            var length = (ulong)lengthValue;
            for (var shift = 56; shift >= 0; shift -= 8) header.Add((byte)(length >> shift));
        }

        var masked = new byte[payload.Length];
        for (var index = 0; index < payload.Length; index++)
            masked[index] = (byte)(payload[index] ^ mask[index & 3]);

        return [.. header, .. mask, .. masked];
    }

    private static async Task<(int Opcode, byte[] Payload)> ReceiveFrameAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[2];
        await ReceiveExactlyAsync(socket, buffer, ct);
        var opcode = buffer[0] & 0x0F;
        var length = buffer[1] & 0x7F;
        if (length == 126)
        {
            var extended = new byte[2];
            await ReceiveExactlyAsync(socket, extended, ct);
            length = (extended[0] << 8) | extended[1];
        }
        else if (length == 127)
        {
            var extended = new byte[8];
            await ReceiveExactlyAsync(socket, extended, ct);
            length = (int)((ulong)(extended[0] & 0x7F) << 56 | (ulong)extended[1] << 48 |
                           (ulong)extended[2] << 40 | (ulong)extended[3] << 32 | (ulong)extended[4] << 24 |
                           (ulong)extended[5] << 16 | (ulong)extended[6] << 8 | extended[7]);
        }

        var payload = new byte[length];
        await ReceiveExactlyAsync(socket, payload, ct);
        return (opcode, payload);
    }

    private static async Task ReceiveExactlyAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        var received = 0;
        while (received < buffer.Length)
            received += await socket.ReceiveAsync(buffer.AsMemory(received), SocketFlags.None, ct);
    }

    /// <summary>In-memory host WebSocket: captures the kernel's outgoing envelopes (SendAsync) and
    /// blocks on receive until disposed (the manager's receive loop then ends). Modeled on the
    /// <c>ReplEvalWebSocketTests</c> test socket, with a close-frame completion instead of a
    /// channel exception so <see cref="PluginHostManager.AttachHostAsync"/> exits cleanly.</summary>
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

        internal ValueTask<string> ReadSentAsync(CancellationToken cancellationToken)
        {
            return sent.Reader.ReadAsync(cancellationToken);
        }

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
