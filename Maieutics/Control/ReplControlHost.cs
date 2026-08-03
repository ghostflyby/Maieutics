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
/// Owns the per-generation HTTP and WebSocket control channel for one Deno REPL process.
/// </summary>
internal sealed class ReplControlHost : IAsyncDisposable
{
    private const int WebSocketBufferSize = 16 * 1024;
    private readonly WebApplication application;
    private readonly ILogger<ReplControlHost> logger;
    private readonly string socketPath;
    private readonly Func<Socket, bool> peerVerifier;
    private int stopState;

    private ReplControlHost(
        WebApplication application,
        string socketPath,
        Func<Socket, bool> peerVerifier,
        ILogger<ReplControlHost> logger)
    {
        this.application = application;
        this.socketPath = socketPath;
        this.peerVerifier = peerVerifier;
        this.logger = logger;
    }

    /// <summary>Gets the unix domain socket path the channel listens on.</summary>
    public string SocketPath => socketPath;

    /// <summary>
    /// Starts the control channel. The expected process id is the spawned REPL child; connections
    /// whose peer identity does not match are rejected.
    /// </summary>
    public static async Task<ReplControlHost> StartAsync(
        string socketPath,
        int expectedProcessId,
        ILogger<ReplControlHost> logger,
        CancellationToken cancellationToken)
    {
        return await StartAsync(
            socketPath,
            CreateDefaultPeerVerifier(expectedProcessId),
            logger,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ReplControlHost> StartAsync(
        string socketPath,
        Func<Socket, bool> peerVerifier,
        ILogger<ReplControlHost> logger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        ArgumentNullException.ThrowIfNull(peerVerifier);
        ArgumentNullException.ThrowIfNull(logger);
        var maxSocketPathLength = OperatingSystem.IsMacOS() ? 104 : OperatingSystem.IsLinux() ? 108 : 260;
        if (socketPath.Length > maxSocketPathLength)
        {
            throw new ArgumentException(
                $"The control channel socket path is longer than the platform limit of {maxSocketPathLength} characters.",
                nameof(socketPath));
        }

        EnsureSocketDirectory(socketPath);

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
            options.ListenUnixSocket(socketPath, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });

        var application = builder.Build();
        application.Use(async (context, next) =>
        {
            var peerSocket = GetPeerSocket(context);
            if (peerSocket is null || !peerVerifier(peerSocket))
            {
                logger.LogWarning(
                    "Rejected control channel connection with an unexpected peer identity.");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        application.UseWebSockets();
        application.MapGet("/health", () => Results.Text("ok"));
        application.Map("/ws", HandleWebSocketAsync);

        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            TryDeleteSocketFile(socketPath);
            throw;
        }

        return new ReplControlHost(application, socketPath, peerVerifier, logger);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref stopState, 1) != 0)
        {
            return;
        }

        try
        {
            await application.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await application.DisposeAsync().ConfigureAwait(false);
            TryDeleteSocketFile(socketPath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static Func<Socket, bool> CreateDefaultPeerVerifier(int expectedProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedProcessId);
        var currentUserId = PeerProcessCredentials.GetCurrentUserId();
        return socket =>
        {
            if (!PeerProcessCredentials.TryGetPeerIdentity(socket, out var peerProcessId, out var peerUserId))
            {
                return false;
            }

            if (peerProcessId > 0)
            {
                return peerProcessId == expectedProcessId;
            }

            return peerUserId > 0 && peerUserId == currentUserId;
        };
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
