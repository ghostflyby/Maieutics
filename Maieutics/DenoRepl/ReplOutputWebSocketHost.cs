using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Maieutics.Control;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace Maieutics.DenoRepl;

/// <summary>
///     Hosts the dedicated binary REPL output endpoint (<c>/v1/repl/output/ws</c>). The endpoint
///     is half-duplex (process -&gt; host only) and carries every non-comm output event
///     (console/display/updateDisplay/clearOutput) as native binary frames. Unlike the eval
///     endpoint there is no hello handshake: the TS client connects and starts sending, so
///     authentication relies on the peer process identity (Unix) or the bearer credential header
///     (Windows), resolved against the shared <see cref="ReplControlSessionRegistry" /> and
///     <see cref="ReplControlCredentialRegistry" />. A session can hold at most one output
///     connection; each generation of a session produces a fresh connection that replaces the
///     previous one (a session is serialized and the old generation is disposed before the next
///     starts).
/// </summary>
internal sealed class ReplOutputWebSocketHost(
    ReplControlSessionRegistry sessionRegistry,
    ReplControlCredentialRegistry credentialRegistry) : IAsyncDisposable
{
    private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ReplControlCredentialRegistry credentialRegistry =
        credentialRegistry ?? throw new ArgumentNullException(nameof(credentialRegistry));
    private readonly CancellationTokenSource lifetime = new();
    private readonly ReplControlSessionRegistry sessionRegistry =
        sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
    private readonly ConcurrentDictionary<ReplOutputConnectionKey, ConnectionSlot> slots = new();
    private int disposeState;
    private int shutdownState;

    /// <summary>
    ///     Terminates every output connection so Kestrel shutdown does not wait for the upgraded
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
        application.Map(ReplOutputProtocol.OutputPath, branch =>
        {
            branch.UseWebSockets();
            branch.Run(HandleWebSocketAsync);
        });
    }

    internal async Task<ReplOutputWebSocketConnection> WaitForConnectionAsync(
        string sessionId,
        int generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        var slot = slots.GetOrAdd(new ReplOutputConnectionKey(sessionId, generation), static _ => new ConnectionSlot());
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        return await slot.Connection.Task.WaitAsync(wait.Token).ConfigureAwait(false);
    }

    internal async Task AttachAsync(
        WebSocket socket,
        int peerProcessId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);

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

        // The first frame is a JSON hello declaring the session id and generation (mirrors the
        // comm endpoint). Resolve both before accepting the connection so the slot is keyed by
        // the exact (session, generation) pair — a restart must never route a new generation's
        // frames into a stale connection from the previous generation.
        var (sessionId, generation) = await ReadHelloAsync(socket, context, peerProcessId, cancellationToken)
            .ConfigureAwait(false);
        if (sessionId is null)
        {
            await CloseRejectedAsync(socket, "REPL output identity is not verified").ConfigureAwait(false);
            return;
        }

        var key = new ReplOutputConnectionKey(sessionId, generation);
        var slot = slots.GetOrAdd(key, static _ => new ConnectionSlot());
        if (!slot.TryReserve())
        {
            await CloseRejectedAsync(socket, "REPL output connection already attached").ConfigureAwait(false);
            return;
        }

        ReplOutputWebSocketConnection? connection = null;
        try
        {
            // The WebSocket session must not follow the HTTP request or connection token: Kestrel
            // fires both as soon as the upgrade completes. The connection ends when the peer closes
            // (detected by the receive loop) or the host begins shutdown (lifetime token).
            using var owner = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connection = new ReplOutputWebSocketConnection(socket);
            connection.Start(owner.Token);
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
                var failure = new ObjectDisposedException(nameof(ReplOutputWebSocketHost));
                var connections = new List<ReplOutputWebSocketConnection>();
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
        await AttachAsync(socket, peerProcessId, context, context.RequestAborted).ConfigureAwait(false);
    }

    private static int ResolvePeerProcessId(HttpContext context)
    {
        var peerSocket = context.Features.Get<IConnectionSocketFeature>()?.Socket;
        if (peerSocket is not null &&
            PeerProcessCredentials.TryGetPeerIdentity(peerSocket, out var processId, out _))
            return processId;

        return 0;
    }

    /// <summary>
    ///     Reads the connection's first frame — a JSON hello declaring the session id and
    ///     generation (the process-to-host side mirrors the comm endpoint's hello). The declared
    ///     session must be consistent with the peer process identity (Unix) or the bearer
    ///     credential header (Windows): when both are present they must agree, and a credential is
    ///     required on Windows where peer process identity is unavailable over loopback TCP.
    /// </summary>
    private async Task<(string? SessionId, int Generation)> ReadHelloAsync(
        WebSocket socket,
        HttpContext context,
        int peerProcessId,
        CancellationToken cancellationToken)
    {
        var text = await Maieutics.Control.ReplControlMessageReader.ReadAsync(socket, cancellationToken)
            .ConfigureAwait(false);
        if (text is null) return (null, 0);

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, 0);
            if (!root.TryGetProperty("sessionId", out var session) ||
                session.GetString() is not { } sessionId || sessionId.IsWhiteSpace())
                return (null, 0);
            if (!root.TryGetProperty("generation", out var generationElement) ||
                !generationElement.TryGetInt32(out var generation) || generation < 0)
                return (null, 0);

            string byProcess = string.Empty;
            if (peerProcessId > 0 && sessionRegistry.TryGetSession(peerProcessId, out var byProcessValue))
                byProcess = byProcessValue;
            var hasProcess = byProcess.Length > 0;
            var hasCredential = TryGetCredentialSessionId(context, out var byCredential);
            if (hasProcess && hasCredential)
            {
                return string.Equals(byProcess, byCredential, StringComparison.Ordinal) &&
                       string.Equals(byProcess, sessionId, StringComparison.Ordinal)
                    ? (sessionId, generation)
                    : (null, 0);
            }

            if (hasProcess)
            {
                return string.Equals(byProcess, sessionId, StringComparison.Ordinal)
                    ? (sessionId, generation)
                    : (null, 0);
            }

            if (hasCredential)
            {
                return string.Equals(byCredential, sessionId, StringComparison.Ordinal)
                    ? (sessionId, generation)
                    : (null, 0);
            }

            return (null, 0);
        }
        catch (JsonException)
        {
            return (null, 0);
        }
    }

    private bool TryGetCredentialSessionId(HttpContext context, out string sessionId)
    {
        sessionId = string.Empty;
        if (!context.Request.Headers.TryGetValue("Authorization", out var authorization))
            return false;

        var value = authorization.ToString();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var credential = value[prefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(credential) &&
               credentialRegistry.TryResolve(credential, out sessionId);
    }

    private static async Task CloseRejectedAsync(WebSocket socket, string description)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.PolicyViolation,
                description,
                CancellationToken.None).ConfigureAwait(false);
    }

    private readonly record struct ReplOutputConnectionKey(string SessionId, int Generation);

    private sealed class ConnectionSlot
    {
        private readonly Lock gate = new();
        private ReplOutputWebSocketConnection? connection;
        private bool reserved;

        internal TaskCompletionSource<ReplOutputWebSocketConnection> Connection { get; } =
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

        internal void Publish(ReplOutputWebSocketConnection value)
        {
            lock (gate)
            {
                if (!reserved || connection is not null)
                    throw new InvalidOperationException("The REPL output connection slot is not reserved.");
                connection = value;
            }

            Connection.TrySetResult(value);
        }

        internal void Fail(Exception exception)
        {
            Connection.TrySetException(exception);
        }

        internal void Release(ReplOutputWebSocketConnection? value)
        {
            lock (gate)
            {
                if (value is not null && ReferenceEquals(connection, value)) connection = null;
                reserved = false;
            }
        }

        internal ReplOutputWebSocketConnection? GetConnection()
        {
            lock (gate)
            {
                return connection;
            }
        }
    }
}
