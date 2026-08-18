using System.Collections.Concurrent;
using System.Net.WebSockets;
using Maieutics.Control;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace Maieutics.DenoRepl;

internal sealed class ReplEvalWebSocketHost(
    ReplControlSessionRegistry sessionRegistry,
    ReplControlCredentialRegistry credentialRegistry) : IAsyncDisposable
{
    private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ReplControlCredentialRegistry credentialRegistry =
        credentialRegistry ?? throw new ArgumentNullException(nameof(credentialRegistry));
    private readonly ReplControlSessionRegistry sessionRegistry =
        sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
    private readonly ConcurrentDictionary<ReplEvalConnectionKey, ConnectionSlot> slots = new();
    private int disposeState;
    private int shutdownState;

    /// <summary>
    ///     Terminates every eval connection so Kestrel shutdown does not wait for the upgraded
    ///     WebSocket requests. Call from <c>IHostApplicationLifetime.ApplicationStopping</c>.
    /// </summary>
    internal void BeginShutdown()
    {
        if (Interlocked.Exchange(ref shutdownState, 1) != 0) return;
        _ = lifetime.CancelAsync();
    }

    internal void MapEndpoint(WebApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.Map(ReplEvalProtocol.WebSocketPath, branch =>
        {
            branch.UseWebSockets();
            branch.Run(HandleWebSocketAsync);
        });
    }

    internal async Task<ReplEvalWebSocketConnection> WaitForConnectionAsync(
        string sessionId,
        int generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        var slot = slots.GetOrAdd(new ReplEvalConnectionKey(sessionId, generation), static _ => new ConnectionSlot());
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        return await slot.Connection.Task.WaitAsync(wait.Token).ConfigureAwait(false);
    }

    internal async Task AttachAsync(
        WebSocket socket,
        int peerProcessId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);

        (ReplEvalIdentity Identity, string CorrelationId) hello;
        try
        {
            hello = await ReplEvalWebSocketConnection
                .ReadHelloAsync(socket, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ReplEvalProtocolException)
        {
            await CloseRejectedAsync(socket, "invalid REPL eval hello").ConfigureAwait(false);
            return;
        }

        if (peerProcessId > 0)
        {
            try
            {
                await sessionRegistry.WaitForIdentityAsync(peerProcessId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        if (!IsVerified(hello.Identity, peerProcessId))
        {
            await CloseRejectedAsync(socket, "REPL eval identity is not verified").ConfigureAwait(false);
            return;
        }

        var key = new ReplEvalConnectionKey(hello.Identity.SessionId, hello.Identity.Generation);
        var slot = slots.GetOrAdd(key, static _ => new ConnectionSlot());
        if (!slot.TryReserve())
        {
            await CloseRejectedAsync(socket, "REPL eval connection already attached").ConfigureAwait(false);
            return;
        }

        ReplEvalWebSocketConnection? connection = null;
        try
        {
            // The WebSocket session must not follow the HTTP request or connection token: Kestrel
            // fires both as soon as the upgrade completes. The connection ends when the peer closes
            // (detected by the receive loop) or the host begins shutdown (lifetime token).
            using var owner = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connection = new ReplEvalWebSocketConnection(socket, hello.Identity, hello.CorrelationId);
            await connection.StartAsync(owner.Token).ConfigureAwait(false);
            slot.Publish(connection);
            await connection.WaitForTerminationAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            slot.Fail(exception);
            throw;
        }
        finally
        {
            slot.Release(connection);
            slots.TryRemove(KeyValuePair.Create(key, slot));
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) == 0)
        {
            try
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
                var failure = new ObjectDisposedException(nameof(ReplEvalWebSocketHost));
                var connections = new List<ReplEvalWebSocketConnection>();
                foreach (var slot in slots.Values)
                {
                    slot.Fail(failure);
                    if (slot.GetConnection() is { } connection) connections.Add(connection);
                }

                await Task.WhenAll(connections.Select(static connection => connection.DisposeAsync().AsTask()))
                    .ConfigureAwait(false);
                disposed.TrySetResult();
            }
            catch (Exception exception)
            {
                disposed.TrySetException(exception);
            }
            finally
            {
                lifetime.Dispose();
            }
        }

        await disposed.Task.ConfigureAwait(false);
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var peerProcessId = ResolvePeerProcessId(context);
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await AttachAsync(socket, peerProcessId, context.RequestAborted).ConfigureAwait(false);
    }

    private static int ResolvePeerProcessId(HttpContext context)
    {
        var peerSocket = context.Features.Get<IConnectionSocketFeature>()?.Socket;
        if (peerSocket is not null &&
            PeerProcessCredentials.TryGetPeerIdentity(peerSocket, out var processId, out _))
            return processId;

        return 0;
    }

    private bool IsVerified(
        ReplEvalIdentity identity,
        int peerProcessId)
    {
        if (peerProcessId > 0 && sessionRegistry.IsOwnedBy(peerProcessId, identity.SessionId)) return true;
        return !string.IsNullOrWhiteSpace(identity.Credential) &&
               credentialRegistry.TryResolve(identity.Credential, out var credentialIdentity) &&
               string.Equals(credentialIdentity, identity.SessionId, StringComparison.Ordinal);
    }

    private static async Task CloseRejectedAsync(WebSocket socket, string description)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.PolicyViolation,
                description,
                CancellationToken.None).ConfigureAwait(false);
    }

    private readonly record struct ReplEvalConnectionKey(string SessionId, int Generation);

    private sealed class ConnectionSlot
    {
        private readonly Lock gate = new();
        private ReplEvalWebSocketConnection? connection;
        private bool reserved;

        internal TaskCompletionSource<ReplEvalWebSocketConnection> Connection { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool TryReserve()
        {
            lock (gate)
            {
                if (reserved || connection is not null) return false;
                reserved = true;
                return true;
            }
        }

        internal void Publish(ReplEvalWebSocketConnection value)
        {
            lock (gate)
            {
                if (!reserved || connection is not null)
                    throw new InvalidOperationException("The REPL eval connection slot is not reserved.");
                connection = value;
            }

            Connection.TrySetResult(value);
        }

        internal void Fail(Exception exception)
        {
            Connection.TrySetException(exception);
        }

        internal void Release(ReplEvalWebSocketConnection? value)
        {
            lock (gate)
            {
                if (value is not null && ReferenceEquals(connection, value)) connection = null;
                reserved = false;
            }
        }

        internal ReplEvalWebSocketConnection? GetConnection()
        {
            lock (gate)
            {
                return connection;
            }
        }
    }
}
