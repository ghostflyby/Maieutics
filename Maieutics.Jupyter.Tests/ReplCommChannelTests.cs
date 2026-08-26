using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Control;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

[Collection(JupyterSocketIntegrationCollection.Name)]
public sealed class ReplCommChannelTests
{
    [Fact(Timeout = 30_000)]
    public async Task CommEndpointHandshakesAndReceivesPush()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(registry, timeout.Token);
        await using (application)
        {
            using var socket = await ConnectCommSocketAsync(host.SocketPath, timeout.Token);
            await SendCommHelloAsync(socket, "test-session", timeout.Token);
            await ReceiveCommReadyAsync(socket, timeout.Token);

            var data = JsonSerializer.SerializeToElement(new { marker = "relay" });
            var message = new JupyterCommMessage(
                JupyterCommKind.Message,
                "comm-9",
                null,
                data,
                null,
                [new byte[] { 0xAB, 0xCD }],
                JupyterWireMessage.Create(
                    JupyterMessage.Create(
                        "comm_msg",
                        new JupyterCommMsgContent("comm-9", data),
                        JupyterJsonContext.Default.JupyterCommMsgContent,
                        JupyterSessionIdentity.Create("test"))));
            await host.PushCommMessageAsync("test-session", message, timeout.Token);

            var payload = await ReceiveCommBinaryAsync(socket, timeout.Token);
            var decoded = ReplControlHost.CommCodec.Decode(payload);
            decoded.Kind.Should().Be(JupyterCommKind.Message);
            decoded.CommId.Should().Be("comm-9");
            decoded.Data.Should().NotBeNull();
            decoded.Data!.Value.GetProperty("marker").GetString().Should().Be("relay");
            decoded.Buffers.Should().ContainSingle().Which.Should().Equal(new byte[] { 0xAB, 0xCD });
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task CommEndpointRejectsUnknownSession()
    {
        if (OperatingSystem.IsWindows()) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(registry, timeout.Token);
        await using (application)
        {
            using var socket = await ConnectCommSocketAsync(host.SocketPath, timeout.Token);
            await SendCommHelloAsync(socket, "unknown-session", timeout.Token);

            // The host closes the socket with a policy violation when the hello session is not owned.
            var (opcode, _) = await ReceiveCommFrameAsync(socket, timeout.Token);
            opcode.Should().Be(0x8);
        }
    }

    private static async Task<Socket> ConnectCommSocketAsync(string socketPath, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
        var handshake =
            "GET /comm HTTP/1.1\r\n" +
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

    private static async Task SendCommHelloAsync(Socket socket, string sessionId, CancellationToken ct)
    {
        var json = $$"""{"sessionId":"{{sessionId}}"}""";
        await SendAllAsync(socket, BuildClientFrame(Encoding.UTF8.GetBytes(json), 0x1), ct);
    }

    private static async Task<byte[]> ReceiveCommBinaryAsync(Socket socket, CancellationToken ct)
    {
        var (opcode, payload) = await ReceiveCommFrameAsync(socket, ct);
        opcode.Should().Be(0x2);
        return payload;
    }

    private static async Task ReceiveCommReadyAsync(Socket socket, CancellationToken ct)
    {
        var (opcode, payload) = await ReceiveCommFrameAsync(socket, ct);
        opcode.Should().Be(0x1);
        Encoding.UTF8.GetString(payload).Should().Contain("comm.ready");
    }

    private static async Task<(int Opcode, byte[] Payload)> ReceiveCommFrameAsync(Socket socket, CancellationToken ct)
    {
        var header = await ReceiveExactAsync(socket, 2, ct);
        var finOpcode = header[0];
        var opcode = finOpcode & 0x0f;
        var length = header[1] & 0x7f;
        if (length == 126)
        {
            var extended = await ReceiveExactAsync(socket, 2, ct);
            length = BinaryPrimitives.ReadUInt16BigEndian(extended);
        }
        else if (length == 127)
        {
            var extended = await ReceiveExactAsync(socket, 8, ct);
            length = checked((int)BinaryPrimitives.ReadUInt64BigEndian(extended));
        }

        var payload = await ReceiveExactAsync(socket, length, ct);
        return (opcode, payload);
    }

    private static async Task<byte[]> ReceiveExactAsync(Socket socket, int length, CancellationToken ct)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, ct);
            if (received == 0) throw new EndOfStreamException("The socket closed before the frame completed.");
            offset += received;
        }

        return buffer;
    }

    private static byte[] BuildClientFrame(byte[] payload, int opcode, bool final = true)
    {
        var firstByte = (final ? 0x80 : 0x00) | opcode;
        byte[] result;
        var mask = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        if (payload.Length < 126)
        {
            result = new byte[2 + 4 + payload.Length];
            result[0] = (byte)firstByte;
            result[1] = (byte)(0x80 | payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            result = new byte[4 + 4 + payload.Length];
            result[0] = (byte)firstByte;
            result[1] = 0x80 | 126;
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2, 2), (ushort)payload.Length);
        }
        else
        {
            result = new byte[10 + 4 + payload.Length];
            result[0] = (byte)firstByte;
            result[1] = 0x80 | 127;
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(2, 8), (ulong)payload.Length);
        }

        var offset = result.Length - payload.Length - 4;
        mask.CopyTo(result, offset);
        for (var index = 0; index < payload.Length; index++)
            result[offset + 4 + index] = (byte)(payload[index] ^ mask[index % 4]);

        return result;
    }

    private static async Task SendAllAsync(Socket socket, byte[] data, CancellationToken ct)
    {
        await socket.SendAsync(data, SocketFlags.None, ct);
    }

    private static async Task<string> ReadUntilHeadersAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var received = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
        return Encoding.ASCII.GetString(buffer, 0, received);
    }
}
