using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maieutics.Control;

/// <summary>
/// Owns the process-wide HTTP and WebSocket control channel shared by all Deno REPL children.
/// One stateless server listens on one unix socket; each request is attributed to a REPL
/// session through the peer process identity resolved at accept time.
/// </summary>
internal sealed class ReplControlHost : IHostedService, IAsyncDisposable
{
    private const int WebSocketBufferSize = 16 * 1024;
    private readonly ReplControlSessionRegistry registry;
    private readonly ILogger<ReplControlHost> logger;
    private WebApplication? application;
    private string? socketPath;
    private int stopState;

    public ReplControlHost(ReplControlSessionRegistry registry, ILogger<ReplControlHost> logger)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the unix domain socket path the channel listens on.</summary>
    public string SocketPath => socketPath
        ?? throw new InvalidOperationException("The REPL control channel is not started.");

    /// <summary>Creates a short socket path within the platform unix socket length limit.</summary>
    internal static string CreateSocketPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mc-{Guid.NewGuid():N}"[..15]);
        return Path.Combine(directory, "sock");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var path = CreateSocketPath();
        EnsureSocketDirectory(path);

        // The default JSON configuration sources use FSEvents-backed file watching, which can block
        // in constrained sandboxes. Polling is deterministic and matches the executable's config provider.
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "maieutics-control"
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.ListenUnixSocket(path, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });

        var built = builder.Build();
        built.Use(async (context, next) =>
        {
            if (!Authorize(context))
            {
                logger.LogWarning("Rejected control channel connection with an unexpected peer identity.");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        built.UseWebSockets();
        built.MapGet("/health", () => Results.Text("ok"));
        built.Map("/ws", HandleWebSocketAsync);

        try
        {
            await built.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await built.DisposeAsync().ConfigureAwait(false);
            TryDeleteSocketFile(path);
            throw;
        }

        application = built;
        socketPath = path;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref stopState, 1) != 0)
        {
            return;
        }

        var current = application;
        var path = socketPath;
        application = null;
        socketPath = null;
        if (current is not null)
        {
            try
            {
                await current.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await current.DisposeAsync().ConfigureAwait(false);
                if (path is not null)
                {
                    TryDeleteSocketFile(path);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
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

    private static void EnsureSocketDirectory(string socketPath)
    {
        var directory = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        if (Directory.Exists(socketPath))
        {
            throw new IOException($"The control channel socket path is a directory: '{socketPath}'.");
        }

        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }
    }

    private static void TryDeleteSocketFile(string socketPath)
    {
        try
        {
            if (File.Exists(socketPath))
            {
                File.Delete(socketPath);
            }
        }
        catch (Exception)
        {
            // Socket cleanup must not mask the primary shutdown outcome.
        }
    }
}
