using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Maieutics.Control;
using Maieutics.DenoRepl;
using Maieutics.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

/// <summary>
///     Extension point calls through the plugin host connection (ADR 0020 §7.2): the retired
///     <c>extension.invoke</c> protocol is replaced by the general <c>host.invoke</c>
///     request-response family. The kernel sends <c>host.invoke</c> with a correlation id, the
///     host invokes the plugin worker's <c>Remote&lt;T&gt;</c> surface in-process and answers
///     <c>host.invokeResult</c> / <c>host.invokeError</c> echoing that id, and the pending call
///     completes with the result or the typed failure. These tests cover the .NET send side
///     (envelope shape), the correlation through the host message switch, the failure path, the
///     invoke timeout, caller cancellation, and the host-disconnect failure — using the same
///     simulated-host seam (<see cref="PluginHostManager.AttachHostAsync"/> +
///     <see cref="PluginHostManager.HandleHostMessage"/>) the host derive tests use.
/// </summary>
public sealed class PluginHostInvokeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact]
    public void InvokeEnvelopeSerializesCamelCaseWithCorrelationId()
    {
        var payload = new HostInvokePayload(
            "plugin-1",
            "./main",
            ReplExtensionPointName.McpDiscover,
            JsonSerializer.SerializeToElement(
                new DiscoverContextPayload("registry_update"),
                ReplControlJsonContext.Default.DiscoverContextPayload));

        var json = JsonSerializer.Serialize(
            new ReplEnvelope(
                1,
                ReplMessageType.HostInvoke,
                "c-1",
                JsonSerializer.SerializeToElement(
                    payload,
                    ReplControlJsonContext.Default.HostInvokePayload)),
            ReplControlJsonContext.Default.ReplEnvelope);

        json.Should().Contain("\"host.invoke\"");
        json.Should().Contain("\"correlationId\":\"c-1\"");
        json.Should().Contain("\"pluginId\":\"plugin-1\"");
        json.Should().Contain("\"exportName\":\"./main\"");
        json.Should().Contain("\"extensionPoint\":\"McpDiscover\"");
        json.Should().Contain("\"request\":{\"reason\":\"registry_update\"}");
    }

    [Fact(Timeout = 60_000)]
    public async Task InvokeSendsHostInvokeAndCompletesWithTheResult()
    {
        if (OperatingSystem.IsWindows())
            return; // The simulated host attaches over a Unix-socket Kestrel harness.

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var harness = await CreateHarnessAsync(deadline.Token);

        var invoke = harness.Manager.InvokeExtensionPointAsync(
            "plugin-1",
            "./main",
            ReplExtensionPointName.McpDiscover,
            request: null,
            deadline.Token);
        invoke.IsCompleted.Should().BeFalse("a pending invoke waits for the host response");

        var sent = await harness.Host!.ReadSentAsync(deadline.Token);
        sent.Should().Contain("\"host.invoke\"");
        var envelope = JsonSerializer.Deserialize(sent, ReplControlJsonContext.Default.ReplEnvelope)!;
        envelope.CorrelationId.Should().NotBeNullOrWhiteSpace();
        var payload = JsonSerializer.Deserialize(
            envelope.Payload?.GetRawText() ?? string.Empty,
            ReplControlJsonContext.Default.HostInvokePayload)!;
        payload.PluginId.Should().Be("plugin-1");
        payload.ExportName.Should().Be("./main");
        payload.ExtensionPoint.Should().Be(ReplExtensionPointName.McpDiscover);

        harness.Manager.HandleHostMessage(
            "{\"version\":1,\"type\":\"host.invokeResult\",\"correlationId\":\"" + envelope.CorrelationId +
            "\",\"payload\":{\"value\":[{\"module\":\"npm:@maieutics/probe-server\"}]}}");

        var outcome = await invoke;
        outcome.IsError.Should().BeFalse(outcome.Message);
        outcome.Value.Should().NotBeNull();
        outcome.Value!.Value.GetArrayLength().Should().Be(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task InvokeErrorResponseCompletesWithTheTypedFailure()
    {
        if (OperatingSystem.IsWindows())
            return; // The simulated host attaches over a Unix-socket Kestrel harness.

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var harness = await CreateHarnessAsync(deadline.Token);

        var invoke = harness.Manager.InvokeExtensionPointAsync(
            "plugin-1",
            "./main",
            ReplExtensionPointName.ToolPreInvoke,
            request: null,
            deadline.Token);

        var envelope = EnvelopeOf(await harness.Host!.ReadSentAsync(deadline.Token));
        harness.Manager.HandleHostMessage(
            "{\"version\":1,\"type\":\"host.invokeError\",\"correlationId\":\"" + envelope.CorrelationId +
            "\",\"payload\":{\"code\":\"extension_failed\",\"message\":\"boom\"}}");

        var outcome = await invoke;
        outcome.IsError.Should().BeTrue();
        outcome.Code.Should().Be("extension_failed");
        outcome.Message.Should().Be("boom");
        outcome.Value.Should().BeNull();
    }

    [Fact(Timeout = 60_000)]
    public async Task InvokeTimesOutWhenTheHostNeverAnswers()
    {
        if (OperatingSystem.IsWindows())
            return; // The simulated host attaches over a Unix-socket Kestrel harness.

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using var harness = await CreateHarnessAsync(deadline.Token);

        // The simulated host never answers; the 15s invoke budget cancels the call. The timeout
        // token is linked from the caller's token, so a caller cancellation cancels promptly too.
        var invoke = harness.Manager.InvokeExtensionPointAsync(
            "plugin-1",
            "./main",
            ReplExtensionPointName.ToolPreInvoke,
            request: null,
            deadline.Token);
        _ = await harness.Host!.ReadSentAsync(deadline.Token);

        await invoke
            .Invoking(static task => task)
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact(Timeout = 60_000)]
    public async Task InvokeCancelsPromptlyWhenTheCallerCancels()
    {
        if (OperatingSystem.IsWindows())
            return; // The simulated host attaches over a Unix-socket Kestrel harness.

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var harness = await CreateHarnessAsync(deadline.Token);

        using var callCancellation = new CancellationTokenSource();
        var invoke = harness.Manager.InvokeExtensionPointAsync(
            "plugin-1",
            "./main",
            ReplExtensionPointName.ToolPreInvoke,
            request: null,
            callCancellation.Token);
        _ = await harness.Host!.ReadSentAsync(deadline.Token);

        callCancellation.Cancel();
        await invoke
            .Invoking(static task => task)
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact(Timeout = 60_000)]
    public async Task HostDisconnectFailsPendingInvokes()
    {
        if (OperatingSystem.IsWindows())
            return; // The simulated host attaches over a Unix-socket Kestrel harness.

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var harness = await CreateHarnessAsync(deadline.Token);

        var invoke = harness.Manager.InvokeExtensionPointAsync(
            "plugin-1",
            "./main",
            ReplExtensionPointName.ToolPreInvoke,
            request: null,
            deadline.Token);
        _ = await harness.Host!.ReadSentAsync(deadline.Token);

        // Closing the host connection runs the manager's receive-loop finally, which fails every
        // pending invoke with a typed host_disconnected outcome (never a hang).
        harness.Host!.Dispose();

        var outcome = await invoke;
        outcome.IsError.Should().BeTrue();
        outcome.Code.Should().Be("host_disconnected");
        outcome.Message.Should().Contain("closed");
    }

    [Fact]
    public void ResponsesForUnknownCorrelationIdsAreIgnored()
    {
        var registry = new ReplControlSessionRegistry();
        var manager = new PluginHostManager(
            Path.Combine(Path.GetTempPath(), $"mc-host-invoke-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions(),
            new PluginHostModule(),
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);

        // Unknown message types and stale responses must never crash the host receive loop.
        manager.HandleHostMessage(
            """{"version":1,"type":"host.invokeResult","correlationId":"unknown","payload":{"value":{}}}""");
        manager.HandleHostMessage(
            """{"version":1,"type":"host.invokeError","correlationId":"unknown","payload":{"code":"x","message":"y"}}""");
        manager.HandleHostMessage("not-json");
    }

    private static ReplEnvelope EnvelopeOf(string json)
    {
        return JsonSerializer.Deserialize(json, ReplControlJsonContext.Default.ReplEnvelope)!;
    }

    /// <summary>Attaches a simulated host directly to the manager (the same receive loop a real
    /// control-host connection feeds). The fake socket captures the kernel's outgoing envelopes;
    /// the simulated host answers by driving <see cref="PluginHostManager.HandleHostMessage"/>
    /// directly. A fake deno executable keeps the manager's real host process from racing the
    /// simulated socket.</summary>
    private static async Task<HostHarness> CreateHarnessAsync(CancellationToken cancellationToken)
    {
        var fakeDenoPath = CreateFakeDenoExecutable();
        var manager = new PluginHostManager(
            Path.Combine(Path.GetTempPath(), $"mc-host-invoke-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = fakeDenoPath },
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);
        await manager.StartAsync(cancellationToken);

        var fakeSocket = new FakeHostWebSocket();
        var attach = manager.AttachHostAsync(fakeSocket, cancellationToken);
        for (var attempt = 0; attempt < 100 && !manager.GetStatus().ControlConnected; attempt++)
            await Task.Delay(50, cancellationToken);
        manager.GetStatus().ControlConnected.Should().BeTrue();

        var harness = new HostHarness(fakeSocket, manager, fakeDenoPath);
        // Keep the attach loop referenced so the fake socket is read until the harness disposes it.
        _ = attach;
        return harness;
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

    private sealed class HostHarness : IAsyncDisposable
    {
        internal HostHarness(FakeHostWebSocket host, PluginHostManager manager, string fakeDenoPath)
        {
            Host = host;
            Manager = manager;
            this.fakeDenoPath = fakeDenoPath;
        }

        internal FakeHostWebSocket? Host { get; }

        internal PluginHostManager Manager { get; }

        private readonly string fakeDenoPath;

        public async ValueTask DisposeAsync()
        {
            Host?.Dispose();
            await Manager.DisposeAsync().ConfigureAwait(false);
            try
            {
                File.Delete(fakeDenoPath);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    /// <summary>In-memory host WebSocket: captures the kernel's outgoing envelopes (SendAsync) and
    /// blocks on receive until disposed (the manager's receive loop then ends). Modeled on the
    /// <c>DenoReplHostDeriveTests</c> fake socket.</summary>
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
}
