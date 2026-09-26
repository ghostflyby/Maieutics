using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Control;
using Maieutics.Frontend;
using Maieutics.Execution;
using Maieutics.Permissions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maieutics.Product.Tests;

/// <summary>The control channel's model-orchestration endpoints (ADR 0031): spawn, bounded
/// wait, and cancel, scoped to the calling Deno process's owning Agent session.</summary>
public sealed class ModelOrchestrationEndpointTests
{
    [Fact(Timeout = 30_000)]
    public async Task SpawnWaitAndCancelRoundTripThroughTheControlChannel()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The control-host test harness rides a Unix domain socket.");

        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var harness = await CreateHarnessAsync(deadline.Token);
        await using (harness.Application)
        {
            var spawn = await harness.SendJsonAsync(
                "POST",
                "/v1/model/subagents?session=test-session",
                """{"version":1,"input":"orchestrated task","sessionId":"test-session"}""",
                deadline.Token);
            spawn.StatusCode.Should().Be(200);
            var handle = JsonDocument.Parse(spawn.Body).RootElement;
            handle.GetProperty("childSessionId").GetString().Should().NotBeNullOrWhiteSpace();
            handle.GetProperty("runId").GetString().Should().NotBeNullOrWhiteSpace();
            handle.GetProperty("taskUri").GetString().Should().StartWith("task://agent/");
            var runId = handle.GetProperty("runId").GetString()!;

            var wait = await harness.SendJsonAsync(
                "GET",
                $"/v1/model/subagents/{runId}?session=test-session&timeoutMs=10000",
                null,
                deadline.Token);
            wait.StatusCode.Should().Be(200);
            var result = JsonDocument.Parse(wait.Body).RootElement;
            result.GetProperty("status").GetString().Should().Be("complete");
            result.GetProperty("report").GetString().Should().Be("done");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task GenericTaskPlaneWaitsAndCancelsByUri()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The control-host test harness rides a Unix domain socket.");

        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var harness = await CreateHarnessAsync(deadline.Token);
        await using (harness.Application)
        {
            var spawn = await harness.SendJsonAsync(
                "POST",
                "/v1/model/subagents?session=test-session",
                """{"version":1,"input":"orchestrated task","sessionId":"test-session"}""",
                deadline.Token);
            var runId = JsonDocument.Parse(spawn.Body).RootElement.GetProperty("runId").GetString()!;
            var owner = harness.Manager.Id.Value.ToString("N");
            var taskUri = $"task://agent/{owner}/{runId}";

            var wait = await harness.SendJsonAsync(
                "GET",
                $"/v1/tasks?uri={Uri.EscapeDataString(taskUri)}&timeoutMs=10000",
                null,
                deadline.Token);
            wait.StatusCode.Should().Be(200);
            var snapshot = JsonDocument.Parse(wait.Body).RootElement;
            snapshot.GetProperty("kind").GetString().Should().Be("agent");
            snapshot.GetProperty("status").GetString().Should().Be("complete");
            snapshot.GetProperty("agent").GetProperty("report").GetString().Should().Be("done");

            var cancel = await harness.SendJsonAsync(
                "POST",
                "/v1/tasks/cancel?session=test-session",
                $$"""{"uri":"{{taskUri}}","sessionId":"test-session"}""",
                deadline.Token);
            cancel.StatusCode.Should().Be(200);
            JsonDocument.Parse(cancel.Body).RootElement.GetProperty("status").GetString()
                .Should().Be("complete");

            var unknown = await harness.SendJsonAsync(
                "GET",
                $"/v1/tasks?uri={Uri.EscapeDataString($"task://agent/{owner}/{Guid.NewGuid():N}")}&timeoutMs=200",
                null,
                deadline.Token);
            unknown.StatusCode.Should().Be(404);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task GenericTaskPlaneAddressesTerminalOneShotsAndValidatesRequests()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The control-host test harness rides a Unix domain socket.");

        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var harness = await CreateHarnessAsync(deadline.Token);
        await using (harness.Application)
        {
            var oneShot = await harness.Terminals.RunOnceAsync(
                harness.Manager.Id,
                "sh",
                ["-c", "sleep 60"],
                TimeSpan.FromMilliseconds(200),
                new TerminalSnapshotRequest(),
                deadline.Token);
            oneShot.Settled.Should().BeFalse();
            oneShot.TaskUri.Should().StartWith("task://terminal/");

            // Waiting on a task that has not reached a terminal state times out at the
            // requested bound instead of returning a mid-flight snapshot.
            var notSettled = await harness.SendJsonAsync(
                "GET",
                $"/v1/tasks?uri={Uri.EscapeDataString(oneShot.TaskUri!)}&timeoutMs=1",
                null,
                deadline.Token);
            notSettled.StatusCode.Should().Be(408);
            notSettled.Body.Should().Contain("task_wait_timeout");

            var invalidWait = await harness.SendJsonAsync(
                "GET",
                $"/v1/tasks?uri={Uri.EscapeDataString("https://example.com/heap")}&timeoutMs=100",
                null,
                deadline.Token);
            invalidWait.StatusCode.Should().Be(400);

            var invalidCancel = await harness.SendJsonAsync(
                "POST",
                "/v1/tasks/cancel?session=test-session",
                """{"uri":"file:///etc/hosts","sessionId":"test-session"}""",
                deadline.Token);
            invalidCancel.StatusCode.Should().Be(400);

            harness.Process.EndOfOutput();
            harness.Process.RaiseExited(0);
            var settled = await harness.SendJsonAsync(
                "GET",
                $"/v1/tasks?uri={Uri.EscapeDataString(oneShot.TaskUri!)}&timeoutMs=10000",
                null,
                deadline.Token);
            settled.StatusCode.Should().Be(200);
            var final = JsonDocument.Parse(settled.Body).RootElement;
            final.GetProperty("kind").GetString().Should().Be("terminal");
            final.GetProperty("status").GetString().Should().Be("complete");
            final.GetProperty("terminal").GetProperty("exitCode").GetInt32().Should().Be(0);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task WaitAndCancelRejectUnknownRunsAndUnownedSessions()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The control-host test harness rides a Unix domain socket.");

        using var deadline = CreateDeadline(TestContext.Current.CancellationToken);
        var harness = await CreateHarnessAsync(deadline.Token);
        await using (harness.Application)
        {
            var unknown = Guid.NewGuid().ToString("N");
            var wait = await harness.SendJsonAsync(
                "GET",
                $"/v1/model/subagents/{unknown}?session=test-session&timeoutMs=200",
                null,
                deadline.Token);
            wait.StatusCode.Should().Be(404);
            wait.Body.Should().Contain("resource_not_found");

            var unowned = await harness.SendJsonAsync(
                "POST",
                $"/v1/model/subagents/{unknown}/cancel?session=not-registered",
                null,
                deadline.Token);
            unowned.StatusCode.Should().Be(401);
        }
    }

    private static async Task<Harness> CreateHarnessAsync(CancellationToken cancellationToken)
    {
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var buffer = new SubagentEventBuffer();
        var overrides = new PermissionOverrideRegistry();
        var scripted = new ScriptedChatClient((_, _) => StreamAsync("done"));
        var subagents = new AgentSubagentOptions
        {
            MaxDepth = 1,
            MaxDetachedChildren = 4,
            EventSink = buffer
        };
        var manager = new MaieuticsAgentSessionManager(
            new FixedProfileProvider(new AgentRunProfile(scripted, new AgentSessionOptions())),
            familiesRoot: null,
            storeFactory: null,
            NullLogger<MaieuticsAgentSessionManager>.Instance,
            subagents: subagents);
        var terminalProcess = new FakeTerminalProcess();
        var terminals = new TerminalRegistry(
            Workspace.Create(Directory.GetCurrentDirectory(), Directory.GetCurrentDirectory()),
            new TerminalOptions { SettleTimeout = TimeSpan.FromMilliseconds(30) },
            new FakeTerminalProcessFactory(terminalProcess),
            NullLogger<TerminalSession>.Instance,
            TestPermissionPolicies.Unconfigured());
        var taskResources = new TaskResourceProvider(
        [
            new AgentTaskResourceSource(
                sessionId => manager.IsLive(sessionId) && manager.Resolve(sessionId) is AgentSession live
                    ? live
                    : null,
                () => [manager.Id]),
            new TerminalTaskResourceSource(terminals)
        ]);
        var surface = new ModelOrchestrationSurface(
            () => manager,
            _ => manager.Id,
            () => new AgentSessionOptions { Subagents = subagents },
            () => [],
            () => overrides)
        {
            TaskResources = () => taskResources
        };
        var socketPath = ReplControlHost.CreateSocketPath();
        var host = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            orchestration: surface);
        var application = await ReplControlTestHost.StartAsync(socketPath, host, cancellationToken);
        return new Harness(application, host, manager, overrides, buffer, terminals, terminalProcess);
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return deadline;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(string text)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    private sealed class Harness(
        WebApplication application,
        ReplControlHost host,
        MaieuticsAgentSessionManager manager,
        PermissionOverrideRegistry overrides,
        SubagentEventBuffer buffer,
        TerminalRegistry terminals,
        FakeTerminalProcess terminalProcess) : IAsyncDisposable
    {
        internal WebApplication Application { get; } = application;

        internal ReplControlHost Host { get; } = host;

        internal MaieuticsAgentSessionManager Manager { get; } = manager;

        internal PermissionOverrideRegistry Overrides { get; } = overrides;

        internal SubagentEventBuffer Buffer { get; } = buffer;

        internal TerminalRegistry Terminals { get; } = terminals;

        internal FakeTerminalProcess Process { get; } = terminalProcess;

        internal async Task<(int StatusCode, string Body)> SendJsonAsync(
            string method,
            string path,
            string? body,
            CancellationToken cancellationToken)
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(
                new UnixDomainSocketEndPoint(Host.SocketPath),
                cancellationToken).ConfigureAwait(false);
            var payload = body ?? string.Empty;
            var headers = method == "POST"
                ? $"{method} {path} HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(payload)}\r\nConnection: close\r\n\r\n{payload}"
                : $"{method} {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
            await socket.SendAsync(
                Encoding.ASCII.GetBytes(headers),
                SocketFlags.None,
                cancellationToken).ConfigureAwait(false);
            var raw = await ReadUntilEndAsync(socket, cancellationToken).ConfigureAwait(false);
            var separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var status = raw[..separator].Split('\r')[0];
            var statusCode = int.Parse(status.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            // Kestrel answers without a content length, so the body arrives chunked; the
            // JSON payload sits between the size lines.
            var bodyText = raw[(separator + 4)..];
            var payloadStart = bodyText.IndexOf('{');
            var payloadEnd = bodyText.LastIndexOf('}');
            if (payloadStart >= 0 && payloadEnd > payloadStart)
                bodyText = bodyText.Substring(payloadStart, payloadEnd - payloadStart + 1);
            return (statusCode, bodyText);
        }

        private static async Task<string> ReadUntilEndAsync(Socket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            var builder = new StringBuilder();
            while (true)
            {
                var received = await socket.ReceiveAsync(
                    buffer.AsMemory(),
                    SocketFlags.None,
                    cancellationToken).ConfigureAwait(false);
                if (received == 0) break;
                builder.Append(Encoding.UTF8.GetString(buffer, 0, received));
            }

            return builder.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            Manager.Dispose();
            await Terminals.DisposeAsync().ConfigureAwait(false);
            await Application.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ScriptedChatClient(
        Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> response)
        : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<ChatResponse>(new NotSupportedException());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return response(messages.ToArray(), cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FixedProfileProvider(AgentRunProfile profile) : IAgentRunProfileProvider
    {
        public Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IAgentRunProfileLease>(new Lease(profile));
        }

        private sealed class Lease(AgentRunProfile profile) : IAgentRunProfileLease
        {
            public AgentRunProfile Profile { get; } = profile;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
