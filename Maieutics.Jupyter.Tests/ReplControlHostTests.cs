using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Maieutics.Control;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

public sealed class ReplControlHostTests
{
    [Fact(Timeout = 30_000)]
    public async Task HealthEndpointRespondsOverUnixSocket()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var socketPath = CreateSocketPath();
        await using var host = await ReplControlHost.StartAsync(
            socketPath,
            Environment.ProcessId,
            NullLogger<ReplControlHost>.Instance,
            timeout.Token);

        var response = await SendHttpRequestAsync(socketPath, "GET /health", timeout.Token);
        response.Should().StartWith("HTTP/1.1 200");
        response.Should().Contain("ok");
    }

    [Fact(Timeout = 30_000)]
    public async Task WebSocketEchoesTextAndBinaryOverUnixSocket()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var socketPath = CreateSocketPath();
        await using var host = await ReplControlHost.StartAsync(
            socketPath,
            Environment.ProcessId,
            NullLogger<ReplControlHost>.Instance,
            timeout.Token);

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);
        var handshake =
            "GET /ws HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n";
        await socket.SendAsync(Encoding.ASCII.GetBytes(handshake), SocketFlags.None, timeout.Token);
        var response = await ReadUntilHeadersAsync(socket, timeout.Token);
        response.Should().Contain("101");

        await socket.SendAsync(BuildClientFrame(Encoding.UTF8.GetBytes("ping"), 0x1), SocketFlags.None, timeout.Token);
        var (textOpcode, textPayload) = await ReceiveFrameAsync(socket, timeout.Token);
        textOpcode.Should().Be(0x1);
        Encoding.UTF8.GetString(textPayload).Should().Be("ping");

        byte[] binary = [0x00, 0x01, 0xFE, 0xFF];
        await socket.SendAsync(BuildClientFrame(binary, 0x2), SocketFlags.None, timeout.Token);
        var (binaryOpcode, binaryPayload) = await ReceiveFrameAsync(socket, timeout.Token);
        binaryOpcode.Should().Be(0x2);
        binaryPayload.Should().Equal(binary);
    }

    [Fact(Timeout = 30_000)]
    public async Task RejectsConnectionsWithUnexpectedPeerIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var socketPath = CreateSocketPath();
        await using var host = await ReplControlHost.StartAsync(
            socketPath,
            static _ => false,
            NullLogger<ReplControlHost>.Instance,
            timeout.Token);

        var response = await SendHttpRequestAsync(socketPath, "GET /health", timeout.Token);
        response.Should().Contain("403");
    }

    [Fact(Timeout = 30_000)]
    public async Task LinuxRejectsRealPeerWithWrongExpectedProcessId()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var socketPath = CreateSocketPath();
        await using var host = await ReplControlHost.StartAsync(
            socketPath,
            Environment.ProcessId + 1_000_000,
            NullLogger<ReplControlHost>.Instance,
            timeout.Token);

        var response = await SendHttpRequestAsync(socketPath, "GET /health", timeout.Token);
        response.Should().Contain("403");
    }

    [Fact(Timeout = 30_000)]
    public async Task StopRemovesSocketFileAndIsIdempotent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var socketPath = CreateSocketPath();
        var host = await ReplControlHost.StartAsync(
            socketPath,
            Environment.ProcessId,
            NullLogger<ReplControlHost>.Instance,
            timeout.Token);
        File.Exists(socketPath).Should().BeTrue();

        await host.StopAsync(timeout.Token);
        File.Exists(socketPath).Should().BeFalse();
        await host.StopAsync(timeout.Token);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task UpdateExpectedProcessIdRejectsOldPeerOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var socketPath = CreateSocketPath();
        var host = await ReplControlHost.StartAsync(
            socketPath,
            Environment.ProcessId,
            NullLogger<ReplControlHost>.Instance,
            timeout.Token);
        await using (host)
        {
            var accepted = await SendHttpRequestAsync(socketPath, "GET /health", timeout.Token);
            accepted.Should().Contain("200");

            host.UpdateExpectedProcessId(Environment.ProcessId + 1_000_000);
            var rejected = await SendHttpRequestAsync(socketPath, "GET /health", timeout.Token);
            rejected.Should().Contain("403");
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task RealDenoClientTalksToControlChannelOverUnixSocket()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var socketPath = CreateSocketPath();
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
            startInfo.ArgumentList.Add(socketPath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the deno control channel client.");

            await using (var host = await ReplControlHost.StartAsync(
                socketPath,
                process.Id,
                NullLogger<ReplControlHost>.Instance,
                timeout.Token))
            {
                await process.WaitForExitAsync(timeout.Token);
                process.ExitCode.Should().Be(0, await GetProcessOutputAsync(process));
            }
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

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
        await new Promise((resolve, reject) => {
          ws.onopen = () => resolve(undefined);
          ws.onerror = () => reject(new Error("websocket failed to open"));
        });
        const echo = new Promise((resolve, reject) => {
          ws.onmessage = (event) => resolve(event.data);
          ws.onerror = () => reject(new Error("websocket error"));
        });
        ws.send("ping");
        const received = await echo;
        if (received !== "ping") throw new Error(`unexpected echo: ${received}`);
        ws.close();
        client.close();
        console.log("deno control channel ok");
        """;

    private static string CreateSocketPath()
        => ReplControlHost.CreateSocketPath();

    private static async Task<string> SendHttpRequestAsync(string socketPath, string requestLine, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
        var request = $"{requestLine} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
        await socket.SendAsync(Encoding.ASCII.GetBytes(request), SocketFlags.None, ct);
        return await ReadUntilEndAsync(socket, ct);
    }

    private static async Task<string> ReadUntilEndAsync(Socket socket, CancellationToken ct)
    {
        var builder = new StringBuilder();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, ct);
            if (read == 0)
            {
                return builder.ToString();
            }

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
            if (read == 0)
            {
                return builder.ToString();
            }

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return builder.ToString();
    }

    private static byte[] BuildClientFrame(byte[] payload, int opcode)
    {
        if (payload.Length >= 126)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Test frames must be shorter than 126 bytes.");
        }

        var mask = new byte[4];
        Random.Shared.NextBytes(mask);
        using var stream = new MemoryStream();
        stream.WriteByte((byte)(0x80 | opcode));
        stream.WriteByte((byte)(0x80 | payload.Length));
        stream.Write(mask);
        for (var i = 0; i < payload.Length; i++)
        {
            stream.WriteByte((byte)(payload[i] ^ mask[i % 4]));
        }

        return stream.ToArray();
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
            if (read == 0)
            {
                throw new EndOfStreamException("The control channel closed the connection.");
            }

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
