using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Maieutics.Control;

/// <summary>
/// Owns the process-wide HTTP and WebSocket control channel shared by all Deno REPL children.
/// The channel is mapped onto the single application host; each request is attributed to a REPL
/// session through the peer process identity resolved at accept time.
/// </summary>
internal sealed class ReplControlHost : IDisposable
{
    private const int WebSocketBufferSize = 16 * 1024;
    private readonly string socketPath;
    private readonly ReplControlSessionRegistry registry;
    private readonly ILogger<ReplControlHost> logger;

    public ReplControlHost(
        string socketPath,
        ReplControlSessionRegistry registry,
        ILogger<ReplControlHost> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        this.socketPath = socketPath;
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the unix domain socket path the channel listens on.</summary>
    public string SocketPath => socketPath;

    /// <summary>Creates a short socket path within the platform unix socket length limit.</summary>
    internal static string CreateSocketPath()
    {
        var name = $"mc-{Guid.NewGuid():N}"[..15] + ".sock";
        return Path.Combine(Path.GetTempPath(), name);
    }

    /// <summary>Maps the control channel middleware and endpoints onto the application.</summary>
    internal void MapEndpoints(WebApplication application)
    {
        application.Use(async (context, next) =>
        {
            if (!Authorize(context))
            {
                logger.LogWarning("Rejected control channel connection with an unexpected peer identity.");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        application.UseWebSockets();
        application.MapGet("/health", () => Results.Text("ok"));
        application.Map("/ws", HandleWebSocketAsync);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(socketPath);
        }
        catch
        {
            // Kestrel removes the socket file on close; best-effort cleanup must not mask shutdown.
        }
    }

    private bool Authorize(HttpContext context)
    {
        var peerSocket = GetPeerSocket(context);
        if (peerSocket is null ||
            !PeerProcessCredentials.TryGetPeerIdentity(peerSocket, out var peerProcessId, out var peerUserId))
        {
            return false;
        }

        if (peerProcessId > 0)
        {
            return registry.TryGetSession(peerProcessId, out _);
        }

        return peerUserId > 0 && peerUserId == PeerProcessCredentials.GetCurrentUserId();
    }

    private static Socket? GetPeerSocket(HttpContext context)
    {
        return context.Features.Get<IConnectionSocketFeature>()?.Socket;
    }

    private static async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var buffer = new byte[WebSocketBufferSize];
        while (socket.State == WebSocketState.Open)
        {
            var received = await socket.ReceiveAsync(buffer, context.RequestAborted).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                await socket
                    .CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            }

            await socket
                .SendAsync(
                    buffer.AsMemory(0, received.Count),
                    received.MessageType,
                    received.EndOfMessage,
                    context.RequestAborted)
                .ConfigureAwait(false);
        }
    }
}