using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Maieutics.Control;

internal static class ReplControlLimits
{
    internal const int MaximumInboundMessageBytes = 1024 * 1024;
    internal const int MaximumJsonDepth = 64;
    internal const int QueueCapacity = 64;

    /// <summary>Per-message ceiling for the dedicated comm WebSocket. Comm traffic carries
    /// widget state and native media buffers (ipywidgets Image/Audio/Video values travel as
    /// raw bytes in the message buffers, ipywidgets/widget.py _remove_buffers), so a single
    /// comm message can legitimately reach a few MB; 16 MiB covers typical media buffers while
    /// the control bus keeps its own 1 MiB ceiling (control messages are small). Exceeding the
    /// ceiling closes the comm connection only — the REPL process and its eval/output channels
    /// keep running.</summary>
    internal const int MaximumCommMessageBytes = 16 * 1024 * 1024;

    /// <summary>
    ///     How long a control request waits for a peer process to register before the kernel treats
    ///     it as unowned. A Deno child may connect its control socket before its parent finishes
    ///     registering the process id, but an unknown process must not hold a request open forever.
    /// </summary>
    internal static readonly TimeSpan PeerRegistrationWait = TimeSpan.FromSeconds(5);
}

internal static class ReplControlMessageReader
{
    private const int ReceiveBufferBytes = 64 * 1024;

    internal static async Task<string?> ReadAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        using var rented = MemoryPool<byte>.Shared.Rent(ReceiveBufferBytes);
        var writer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var result = await socket.ReceiveAsync(rented.Memory, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseOutputAsync(
                    socket,
                    WebSocketCloseStatus.NormalClosure,
                    "closed",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                await CloseOutputAsync(
                    socket,
                    WebSocketCloseStatus.InvalidMessageType,
                    "control messages must be text",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (result.Count > ReplControlLimits.MaximumInboundMessageBytes - writer.WrittenCount)
            {
                await CloseOutputAsync(
                    socket,
                    WebSocketCloseStatus.MessageTooBig,
                    $"control message exceeds {ReplControlLimits.MaximumInboundMessageBytes} bytes",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            writer.Write(rented.Memory.Span[..result.Count]);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(writer.WrittenSpan);
        }
    }

    private static Task CloseOutputAsync(
        WebSocket socket,
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken)
    {
        return socket.State is WebSocketState.Open or WebSocketState.CloseReceived
            ? socket.CloseOutputAsync(status, description, cancellationToken)
            : Task.CompletedTask;
    }
}
