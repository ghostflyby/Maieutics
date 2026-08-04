using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Maieutics.Agent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
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
    private readonly IReadOnlyList<AIFunction> scriptTools;

    public ReplControlHost(
        string socketPath,
        ReplControlSessionRegistry registry,
        ILogger<ReplControlHost> logger,
        IReadOnlyList<AIFunction>? scriptTools = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        this.socketPath = socketPath;
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.scriptTools = scriptTools ?? [];
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
        application.MapPost("/v1/tool.invoke", HandleToolInvokeAsync);
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

    private async Task HandleToolInvokeAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        ToolInvokeRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                ReplControlJsonContext.Default.ToolInvokeRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (request is null || request.Version != 1 || string.IsNullOrWhiteSpace(request.Tool))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var function = scriptTools.FirstOrDefault(candidate => candidate.Name == request.Tool);
        if (function is null)
        {
            await WriteEnvelopeAsync(
                context,
                ToolJson.CreateFailureEnvelope(
                    "tool_not_found",
                    $"Tool '{request.Tool}' is not available to scripts."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var arguments = new AIFunctionArguments(request.Arguments.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone()));
        JsonElement envelope;
        try
        {
            var result = await function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            envelope = result switch
            {
                null => ToolJson.CreateSuccessEnvelope(null),
                JsonElement element => ToolJson.CreateSuccessEnvelope(element),
                _ => ToolJson.CreateFailureEnvelope(
                    "tool_invalid_result",
                    "Script tools must return a structured JSON value.")
            };
        }
        catch (AgentToolException exception)
        {
            envelope = ToolJson.CreateFailureEnvelope(exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Script tool '{Tool}' failed.", request.Tool);
            envelope = ToolJson.CreateFailureEnvelope("tool_failed", exception.Message);
        }

        await WriteEnvelopeAsync(context, envelope, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteEnvelopeAsync(
        HttpContext context,
        JsonElement envelope,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(envelope.GetRawText(), cancellationToken).ConfigureAwait(false);
    }
}
