using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Control;
using Maieutics.Execution;
using Microsoft.Extensions.AI;

namespace Maieutics.Jupyter.Tests;

public sealed class ReplControlHostTests
{
    private const string DenoControlClientScript = """
                                                   const socketPath = Deno.args[0];
                                                   const client = Deno.createHttpClient({ proxy: { transport: "unix", path: socketPath } });

                                                   async function waitForHealth() {
                                                     for (let i = 0; i < 200; i++) {
                                                       try {
                                                         const res = await fetch("http://localhost/health", { client });
                                                         if (res.ok && (await res.text()) === "ok") return;
                                                       } catch {}
                                                       await new Promise((resolve) => setTimeout(resolve, 100));
                                                     }
                                                     throw new Error("control channel health never became available");
                                                   }

                                                   await waitForHealth();

                                                   const ws = new WebSocket("ws://localhost/ws", { client });
                                                   const waiters = new Map();
                                                   ws.onmessage = (event) => {
                                                     const message = JSON.parse(String(event.data));
                                                     const waiter = waiters.get(message.type);
                                                     if (waiter !== undefined) {
                                                       waiters.delete(message.type);
                                                       waiter(message);
                                                     }
                                                   };
                                                   function waitFor(type) {
                                                     return new Promise((resolve, reject) => {
                                                       const timer = setTimeout(() => reject(new Error(`timed out waiting for ${type}`)), 5000);
                                                       waiters.set(type, (message) => { clearTimeout(timer); resolve(message); });
                                                     });
                                                   }
                                                   await new Promise((resolve, reject) => {
                                                     ws.onopen = () => resolve(undefined);
                                                     ws.onerror = () => reject(new Error("websocket failed to open"));
                                                   });
                                                   ws.send(JSON.stringify({
                                                     version: 1,
                                                     type: "control.hello",
                                                     payload: { sessionId: "test-session" },
                                                   }));
                                                   await waitFor("control.ready");
                                                   ws.send(JSON.stringify({ version: 1, type: "control.ping", correlationId: "p1" }));
                                                   await waitFor("control.pong");
                                                   ws.close();
                                                   client.close();
                                                   console.log("deno control channel ok");
                                                   """;

    [Fact(Timeout = 30_000)]
    public async Task HealthEndpointRespondsOverUnixSocket()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(registry, timeout.Token);
        await using (application)
        {
            var response = await SendHttpRequestAsync(host.SocketPath, "GET /health", timeout.Token);
            response.Should().StartWith("HTTP/1.1 200");
            response.Should().Contain("ok");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task BusHandshakeBindsSessionAndPings()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(registry, timeout.Token);
        await using (application)
        {
            using var socket = await ConnectWebSocketAsync(host.SocketPath, timeout.Token);
            await SendBusAsync(
                socket,
                """{"version":1,"type":"control.hello","payload":{"sessionId":"test-session"}}""",
                timeout.Token);
            var ready = await ReceiveBusAsync(socket, timeout.Token);
            ready.Should().Contain("\"type\":\"control.ready\"");

            await SendBusAsync(
                socket,
                """{"version":1,"type":"control.ping","correlationId":"p1"}""",
                timeout.Token);
            var pong = await ReceiveBusAsync(socket, timeout.Token);
            pong.Should().Contain("\"type\":\"control.pong\"");
            pong.Should().Contain("\"correlationId\":\"p1\"");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task BusValidatesCommOrderingAndRejectsUnknownTypes()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(registry, timeout.Token);
        await using (application)
        {
            using var socket = await ConnectWebSocketAsync(host.SocketPath, timeout.Token);
            await SendBusAsync(
                socket,
                """{"version":1,"type":"control.hello","payload":{"sessionId":"test-session"}}""",
                timeout.Token);
            await ReceiveBusAsync(socket, timeout.Token);

            await SendBusAsync(
                socket,
                """{"version":1,"type":"comm.msg","payload":{"commId":"c1"}}""",
                timeout.Token);
            (await ReceiveBusAsync(socket, timeout.Token)).Should().Contain("comm_not_open");

            await SendBusAsync(
                socket,
                """{"version":1,"type":"comm.open","payload":{"commId":"c1","targetName":"test"}}""",
                timeout.Token);
            (await ReceiveBusAsync(socket, timeout.Token)).Should().Contain("\"type\":\"comm.ack\"");

            await SendBusAsync(
                socket,
                """{"version":1,"type":"comm.msg","payload":{"commId":"c1"}}""",
                timeout.Token);
            (await ReceiveBusAsync(socket, timeout.Token)).Should().Contain("\"type\":\"comm.ack\"");

            await SendBusAsync(
                socket,
                """{"version":1,"type":"nonsense.unknown"}""",
                timeout.Token);
            (await ReceiveBusAsync(socket, timeout.Token)).Should().Contain("unknown_message");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task FragmentedWebSocketMessageAtLimitIsAccepted()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(registry, timeout.Token);
        await using (application)
        {
            using var socket = await ConnectWebSocketAsync(host.SocketPath, timeout.Token);
            await SendBusAsync(
                socket,
                """{"version":1,"type":"control.hello","payload":{"sessionId":"test-session"}}""",
                timeout.Token);
            await ReceiveBusAsync(socket, timeout.Token);

            var message = CreateBusMessageOfSize(ReplControlLimits.MaximumInboundMessageBytes);
            await SendFragmentedBusAsync(socket, message, timeout.Token);

            var pong = await ReceiveBusAsync(socket, timeout.Token);
            pong.Should().Contain("\"type\":\"control.pong\"");
            pong.Should().Contain("\"correlationId\":\"limit\"");
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(1)]
    [InlineData(1024 * 1024)]
    public async Task OversizedFragmentedWebSocketMessageClosesWithoutDispatch(int bytesOverLimit)
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(registry, timeout.Token);
        await using (application)
        {
            using var socket = await ConnectWebSocketAsync(host.SocketPath, timeout.Token);
            await SendBusAsync(
                socket,
                """{"version":1,"type":"control.hello","payload":{"sessionId":"test-session"}}""",
                timeout.Token);
            await ReceiveBusAsync(socket, timeout.Token);

            var message = CreateBusMessageOfSize(
                ReplControlLimits.MaximumInboundMessageBytes + bytesOverLimit);
            await SendFragmentedBusAsync(socket, message, timeout.Token, true);

            var (opcode, payload) = await ReceiveFrameAsync(socket, timeout.Token);
            opcode.Should().Be(0x8, "an oversized message must close before it can be dispatched");
            BinaryPrimitives.ReadUInt16BigEndian(payload).Should()
                .Be((ushort)WebSocketCloseStatus.MessageTooBig);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ControlCancelCancelsInFlightToolCall()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (application, host) = await ReplControlTestHost.StartAsync(
            registry,
            timeout.Token,
            [CreateBlockingFunction(started)]);
        await using (application)
        {
            using var socket = await ConnectWebSocketAsync(host.SocketPath, timeout.Token);
            await SendBusAsync(
                socket,
                """{"version":1,"type":"control.hello","payload":{"sessionId":"test-session"}}""",
                timeout.Token);
            await ReceiveBusAsync(socket, timeout.Token);

            var token = timeout.Token;

            var invoke = Task.Run(
                () => PostToolInvokeAsync(
                    host.SocketPath,
                    "blocking_test",
                    "{}",
                    "cancel-me",
                    token),
                timeout.Token);
            await started.Task.WaitAsync(timeout.Token);
            await SendBusAsync(
                socket,
                """{"version":1,"type":"control.cancel","payload":{"correlationId":"cancel-me"}}""",
                timeout.Token);
            (await ReceiveBusAsync(socket, timeout.Token)).Should().Contain("control.cancelled");

            var response = await invoke;
            response.Should().Contain("\"status\":\"cancelled\"");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ToolProgressIsPushedOverTheBus()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(
            registry,
            timeout.Token,
            [CreateProgressFunction()]);
        await using (application)
        {
            using var socket = await ConnectWebSocketAsync(host.SocketPath, timeout.Token);
            await SendBusAsync(
                socket,
                """{"version":1,"type":"control.hello","payload":{"sessionId":"test-session"}}""",
                timeout.Token);
            await ReceiveBusAsync(socket, timeout.Token);

            var token = timeout.Token;

            var invoke = Task.Run(
                () => PostToolInvokeAsync(
                    host.SocketPath,
                    "progress_test",
                    "{}",
                    "progress-1",
                    token),
                timeout.Token);
            var progress = await ReceiveBusAsync(socket, timeout.Token);
            progress.Should().Contain("\"type\":\"tool.progress\"");
            progress.Should().Contain("\"correlationId\":\"progress-1\"");
            progress.Should().Contain("\"stage\":\"building\"");

            var response = await invoke;
            response.Should().Contain("\"status\":\"ok\"");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task LinuxRejectsPeerNotInRegistry()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId + 1_000_000, "other-session");
        var (application, host) = await ReplControlTestHost.StartAsync(registry, timeout.Token);
        await using (application)
        {
            var response = await SendHttpRequestAsync(host.SocketPath, "GET /health", timeout.Token);
            response.Should().Contain("403");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task LinuxRegistryRebindChangesAcceptedPeer()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(registry, timeout.Token);
        await using (application)
        {
            var accepted = await SendHttpRequestAsync(host.SocketPath, "GET /health", timeout.Token);
            accepted.Should().Contain("200");

            registry.Unregister(Environment.ProcessId);
            registry.Register(Environment.ProcessId + 1_000_000, "other-session");
            var rejected = await SendHttpRequestAsync(host.SocketPath, "GET /health", timeout.Token);
            rejected.Should().Contain("403");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task DisposeRemovesSocketFile()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (application, host) = await ReplControlTestHost.StartAsync(
            new ReplControlSessionRegistry(),
            timeout.Token);
        var socketPath = host.SocketPath;
        File.Exists(socketPath).Should().BeTrue();

        await application.StopAsync(timeout.Token);
        await application.DisposeAsync();
        File.Exists(socketPath).Should().BeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task ToolInvokeEndpointRunsWorkspaceFunctions()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var root = Path.Combine(Path.GetTempPath(), $"mc-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var functions = new WorkspaceFunctions(Workspace.Create(root, root)).Functions;
            var registry = new ReplControlSessionRegistry();
            registry.Register(Environment.ProcessId, "test-session");
            var (application, host) = await ReplControlTestHost.StartAsync(
                registry,
                timeout.Token,
                functions);
            await using (application)
            {
                var success = await PostToolInvokeAsync(
                    host.SocketPath,
                    "list_directory",
                    "{}",
                    null,
                    timeout.Token);
                success.Should().Contain("\"status\":\"ok\"");
                success.Should().Contain("\"uri\"");

                var missing = await PostToolInvokeAsync(
                    host.SocketPath,
                    "repl_execute",
                    "{}",
                    null,
                    timeout.Token);
                missing.Should().Contain("tool_not_found");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ToolInvokeAcceptsBodyAtLimitAndRejectsOneByteOver()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var invocations = 0;
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(
            registry,
            timeout.Token,
            [CreatePayloadFunction(() => invocations++)]);
        await using (application)
        {
            var accepted = await PostRawToolInvokeAsync(
                host.SocketPath,
                CreateToolInvokeBodyOfSize(ReplControlLimits.MaximumInboundMessageBytes),
                false,
                timeout.Token);
            accepted.Should().StartWith("HTTP/1.1 200");
            accepted.Should().Contain("\"status\":\"ok\"");
            invocations.Should().Be(1);

            var rejected = await PostRawToolInvokeAsync(
                host.SocketPath,
                CreateToolInvokeBodyOfSize(ReplControlLimits.MaximumInboundMessageBytes + 1),
                false,
                timeout.Token);
            rejected.Should().StartWith("HTTP/1.1 413");
            invocations.Should().Be(1, "an oversized body must not be deserialized or invoked");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ChunkedToolInvokeOverLimitIsRejectedBeforeInvocation()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var invocations = 0;
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(
            registry,
            timeout.Token,
            [CreatePayloadFunction(() => invocations++)]);
        await using (application)
        {
            var rejected = await PostRawToolInvokeAsync(
                host.SocketPath,
                CreateToolInvokeBodyOfSize(ReplControlLimits.MaximumInboundMessageBytes + 1),
                true,
                timeout.Token);

            rejected.Should().StartWith("HTTP/1.1 413");
            invocations.Should().Be(0);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ToolInvokeRejectsJsonBeyondTheDepthLimit()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var invocations = 0;
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(
            registry,
            timeout.Token,
            [CreatePayloadFunction(() => invocations++)]);
        await using (application)
        {
            var nested = new string('[', ReplControlLimits.MaximumJsonDepth) +
                         "0" +
                         new string(']', ReplControlLimits.MaximumJsonDepth);
            var body =
                "{\"version\":1,\"tool\":\"payload_test\",\"arguments\":{\"value\":" + nested +
                "},\"sessionId\":\"test-session\"}";
            var rejected = await PostRawToolInvokeAsync(host.SocketPath, body, false, timeout.Token);

            rejected.Should().StartWith("HTTP/1.1 400");
            invocations.Should().Be(0);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task RealDenoClientTalksToControlChannelOverUnixSocket()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var registry = new ReplControlSessionRegistry();
        var (application, host) = await ReplControlTestHost.StartAsync(registry, timeout.Token);
        await using (application)
        {
            var scriptPath = Path.Combine(
                Path.GetTempPath(),
                $"maieutics-control-deno-{Guid.NewGuid():N}.ts");
            await File.WriteAllTextAsync(scriptPath, DenoControlClientScript, timeout.Token);
            try
            {
                var startInfo = new ProcessStartInfo("deno")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                startInfo.ArgumentList.Add("run");
                startInfo.ArgumentList.Add("--no-prompt");
                startInfo.ArgumentList.Add("--allow-net");
                startInfo.ArgumentList.Add("--allow-read");
                startInfo.ArgumentList.Add("--allow-write");
                startInfo.ArgumentList.Add(scriptPath);
                startInfo.ArgumentList.Add(host.SocketPath);
                using var process = Process.Start(startInfo)
                                    ?? throw new InvalidOperationException(
                                        "Could not start the deno control channel client.");
                registry.Register(process.Id, "test-session");

                await process.WaitForExitAsync(timeout.Token);
                process.ExitCode.Should().Be(0, await GetProcessOutputAsync(process));
            }
            finally
            {
                File.Delete(scriptPath);
            }
        }
    }

    private static async Task<string> SendHttpRequestAsync(string socketPath, string requestLine, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
        var request = $"{requestLine} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
        await socket.SendAsync(Encoding.ASCII.GetBytes(request), SocketFlags.None, ct);
        return await ReadUntilEndAsync(socket, ct);
    }

    private static async Task<string> PostToolInvokeAsync(
        string socketPath,
        string tool,
        string argumentsJson,
        string? correlationId,
        CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
        var correlation = string.IsNullOrWhiteSpace(correlationId)
            ? string.Empty
            : ",\"correlationId\":\"" + correlationId + "\"";
        var body =
            "{\"version\":1,\"tool\":\"" + tool + "\",\"arguments\":" + argumentsJson +
            correlation + ",\"sessionId\":\"test-session\"}";
        var request =
            "POST /v1/tool.invoke HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
            "Connection: close\r\n\r\n" +
            body;
        await socket.SendAsync(Encoding.UTF8.GetBytes(request), SocketFlags.None, ct);
        return await ReadUntilEndAsync(socket, ct);
    }

    private static async Task<string> PostRawToolInvokeAsync(
        string socketPath,
        string body,
        bool chunked,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = chunked
            ? "POST /v1/tool.invoke HTTP/1.1\r\n" +
              "Host: localhost\r\n" +
              "Content-Type: application/json\r\n" +
              "Transfer-Encoding: chunked\r\n" +
              "Connection: close\r\n\r\n" +
              $"{bodyBytes.Length:X}\r\n"
            : "POST /v1/tool.invoke HTTP/1.1\r\n" +
              "Host: localhost\r\n" +
              "Content-Type: application/json\r\n" +
              $"Content-Length: {bodyBytes.Length}\r\n" +
              "Connection: close\r\n\r\n";
        // Read concurrently with the send so an early rejection (413) is captured by the pending
        // read before the server aborts the connection; the reset discards unread buffer data.
        var response = ReadUntilEndAsync(socket, cancellationToken);
        try
        {
            await SendAllAsync(socket, Encoding.ASCII.GetBytes(headers), cancellationToken);
            await SendAllAsync(socket, bodyBytes, cancellationToken);
            if (chunked) await SendAllAsync(socket, "\r\n0\r\n\r\n"u8.ToArray(), cancellationToken);
        }
        catch (SocketException)
        {
            // The server may finish an early 413 response before the client has flushed the rejected body.
        }

        return await response;
    }

    private static async Task<Socket> ConnectWebSocketAsync(string socketPath, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
        var handshake =
            "GET /ws HTTP/1.1\r\n" +
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

    private static async Task SendFragmentedBusAsync(
        Socket socket,
        string json,
        CancellationToken cancellationToken,
        bool allowEarlyClose = false)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var firstLength = payload.Length / 2;
        await SendAllAsync(
            socket,
            BuildClientFrame(payload.AsSpan(0, firstLength), 0x1, false),
            cancellationToken);
        try
        {
            await SendAllAsync(
                socket,
                BuildClientFrame(payload.AsSpan(firstLength), 0x0),
                cancellationToken);
        }
        catch (SocketException) when (allowEarlyClose)
        {
            // A well-over-limit message can be rejected before its final frame has fully flushed.
        }
    }

    private static async Task<string> ReceiveBusAsync(Socket socket, CancellationToken ct)
    {
        var (opcode, payload) = await ReceiveFrameAsync(socket, ct);
        opcode.Should().Be(0x1);
        return Encoding.UTF8.GetString(payload);
    }

    private static AIFunction CreateBlockingFunction(TaskCompletionSource? started = null)
    {
        return AIFunctionFactory.Create(
            (CancellationToken ct) => WaitForCancellationAsync(ct, started),
            new AIFunctionFactoryOptions
            {
                Name = "blocking_test",
                Description = "Blocks until cancelled."
            });
    }

    private static AIFunction CreateProgressFunction()
    {
        return AIFunctionFactory.Create(
            (AIFunctionArguments arguments, CancellationToken ct) =>
                ReportProgressAsync(arguments, ct),
            new AIFunctionFactoryOptions
            {
                Name = "progress_test",
                Description = "Reports progress then returns."
            });
    }

    private static AIFunction CreatePayloadFunction(Action invoked)
    {
        return AIFunctionFactory.Create(
            (string value) =>
            {
                invoked();
                return JsonSerializer.SerializeToElement(new { length = value.Length });
            },
            new AIFunctionFactoryOptions
            {
                Name = "payload_test",
                Description = "Returns the input length."
            });
    }

    private static async Task<object?> ReportProgressAsync(
        AIFunctionArguments arguments,
        CancellationToken ct)
    {
        if (arguments.Context is { } map &&
            map.TryGetValue(typeof(ReplToolProgress), out var raw) &&
            raw is ReplToolProgress progress)
            await progress.ReportAsync(
                new ToolProgressPayload(50, 100, "building"),
                ct);

        return JsonSerializer.SerializeToElement(new { done = true });
    }

    private static async Task<object?> WaitForCancellationAsync(CancellationToken ct, TaskCompletionSource? started = null)
    {
        started?.TrySetResult();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = ct.Register(() => completion.TrySetResult());
        await completion.Task.WaitAsync(ct);
        return JsonSerializer.SerializeToElement(new { cancelled = true });
    }

    private static async Task<string> ReadUntilEndAsync(Socket socket, CancellationToken ct)
    {
        var builder = new StringBuilder();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, ct);
            if (read == 0) return builder.ToString();

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
    }

    private static async Task<string> ReadUntilHeadersAsync(Socket socket, CancellationToken ct)
    {
        var builder = new StringBuilder();
        var buffer = new byte[4096];
        while (!builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, ct);
            if (read == 0) return builder.ToString();

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return builder.ToString();
    }

    private static string CreateBusMessageOfSize(int size)
    {
        const string prefix = "{\"version\":1,\"type\":\"control.ping\",\"correlationId\":\"limit\",\"padding\":\"";
        const string suffix = "\"}";
        var paddingLength = size - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix);
        if (paddingLength < 0) throw new ArgumentOutOfRangeException(nameof(size));

        return prefix + new string('x', paddingLength) + suffix;
    }

    private static string CreateToolInvokeBodyOfSize(int size)
    {
        const string prefix = "{\"version\":1,\"tool\":\"payload_test\",\"arguments\":{\"value\":\"";
        const string suffix = "\"},\"sessionId\":\"test-session\"}";
        var valueLength = size - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix);
        if (valueLength < 0) throw new ArgumentOutOfRangeException(nameof(size));

        return prefix + new string('x', valueLength) + suffix;
    }

    private static byte[] BuildClientFrame(ReadOnlySpan<byte> payload, int opcode, bool endOfMessage = true)
    {
        var mask = new byte[4];
        Random.Shared.NextBytes(mask);
        using var stream = new MemoryStream();
        stream.WriteByte((byte)((endOfMessage ? 0x80 : 0) | opcode));
        if (payload.Length < 126)
        {
            stream.WriteByte((byte)(0x80 | payload.Length));
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            stream.WriteByte(0x80 | 126);
            Span<byte> length = stackalloc byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)payload.Length);
            stream.Write(length);
        }
        else
        {
            stream.WriteByte(0x80 | 127);
            Span<byte> length = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)payload.Length);
            stream.Write(length);
        }

        stream.Write(mask);
        for (var i = 0; i < payload.Length; i++) stream.WriteByte((byte)(payload[i] ^ mask[i % 4]));

        return stream.ToArray();
    }

    private static async Task SendAllAsync(
        Socket socket,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        while (!bytes.IsEmpty)
        {
            var sent = await socket.SendAsync(bytes, SocketFlags.None, cancellationToken);
            if (sent == 0) throw new EndOfStreamException("The control channel closed while receiving a test request.");

            bytes = bytes[sent..];
        }
    }

    private static async Task<(int Opcode, byte[] Payload)> ReceiveFrameAsync(Socket socket, CancellationToken ct)
    {
        var header = await ReadExactAsync(socket, 2, ct);
        var opcode = header[0] & 0x0F;
        var length = header[1] & 0x7F;
        if (length == 126)
        {
            var extended = await ReadExactAsync(socket, 2, ct);
            length = (extended[0] << 8) | extended[1];
        }
        else if (length == 127)
        {
            throw new NotSupportedException("The echo server must not send extended 64-bit frame lengths.");
        }

        return (opcode, await ReadExactAsync(socket, length, ct));
    }

    private static async Task<byte[]> ReadExactAsync(Socket socket, int count, CancellationToken ct)
    {
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await socket.ReceiveAsync(result.AsMemory(offset, count - offset), SocketFlags.None, ct);
            if (read == 0) throw new EndOfStreamException("The control channel closed the connection.");

            offset += read;
        }

        return result;
    }

    private static async Task<string> GetProcessOutputAsync(Process process)
    {
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        return $"stdout: {stdout}\nstderr: {stderr}";
    }
}
