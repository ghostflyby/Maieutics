using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Maieutics.DenoRepl;

namespace Maieutics.Product.Tests;

/// <summary>
///     A raw-socket stand-in for the REPL child on the <c>/comm</c> channel: performs the
///     handshake and exchanges the shared binary comm frames without a real Deno process.
///     Unix only — on Windows the control host rides TCP loopback with its own credential
///     bootstrap (see ReplCommChannelTests).
/// </summary>
internal static class CommChildSocketStub
{
    internal static async Task<Socket> ConnectAsync(string socketPath, string sessionId, CancellationToken ct)
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
        var json = $$"""{"sessionId":"{{sessionId}}"}""";
        await SendFrameAsync(socket, Encoding.UTF8.GetBytes(json), opcode: 0x1, ct);
        return socket;
    }

    internal static async Task ExpectReadyAsync(Socket socket, CancellationToken ct)
    {
        var (opcode, payload) = await ReceiveFrameAsync(socket, ct);
        opcode.Should().Be(0x1);
        Encoding.UTF8.GetString(payload).Should().Contain("comm.ready");
    }

    internal static async Task SendAsync(Socket socket, ReplCommMessage message, CancellationToken ct)
    {
        await SendFrameAsync(socket, ReplCommCodec.Encode(message), opcode: 0x2, ct);
    }

    internal static async Task<(int Opcode, byte[] Payload)> ReceiveFrameAsync(Socket socket, CancellationToken ct)
    {
        var header = await ReceiveExactAsync(socket, 2, ct);
        var opcode = header[0] & 0x0f;
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

    private static async Task SendFrameAsync(Socket socket, byte[] payload, int opcode, CancellationToken ct)
    {
        var firstByte = 0x80 | opcode;
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

        await socket.SendAsync(result, SocketFlags.None, ct);
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

    private static async Task<string> ReadUntilHeadersAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var received = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
        return Encoding.ASCII.GetString(buffer, 0, received);
    }
}
