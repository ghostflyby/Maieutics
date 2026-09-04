using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Maieutics.Agent;
using Maieutics.Jupyter.Kernel;
using Maieutics.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Maieutics.Control;

/// <summary>
///     Owns the process-wide HTTP and WebSocket control channel shared by all Deno REPL children.
///     The channel is mapped onto the single application host; HTTP requests are attributed through
///     peer process identity, while WebSocket bus connections bind to a session through the
///     <c>control.hello</c> handshake.
/// </summary>
internal sealed partial class ReplControlHost : IDisposable
{
    private const int EnvelopeVersion = 1;
    private const string AuthorizedIdentityItem = "Maieutics.Control.AuthorizedIdentity";
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> comms =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, SessionBusConnection> connections = new(StringComparer.Ordinal);
    private readonly Func<JupyterCommMessage, CancellationToken, ValueTask>? commFrontendSink;
    private readonly ILogger<ReplControlHost> logger;
    private readonly ReplControlCredentialRegistry? credentialRegistry;
    private readonly IWindowsPipeBootstrap? windowsPipeBootstrap;

    private readonly ReplOperationRegistry operations = new();
    private readonly PluginHostManager? pluginHosts;
    private readonly ReplControlSessionRegistry registry;
    private readonly IReadOnlyList<AIFunction> scriptTools;
    private string? controlAddress;

    public ReplControlHost(
        string socketPath,
        ReplControlSessionRegistry registry,
        ILogger<ReplControlHost> logger,
        IReadOnlyList<AIFunction>? scriptTools = null,
        PluginHostManager? pluginHosts = null,
        ReplControlCredentialRegistry? credentials = null,
        IWindowsPipeBootstrap? windowsPipeBootstrap = null,
        Func<JupyterCommMessage, CancellationToken, ValueTask>? commFrontendSink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        SocketPath = socketPath;
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.scriptTools = scriptTools ?? [];
        this.pluginHosts = pluginHosts;
        this.credentialRegistry = credentials;
        this.windowsPipeBootstrap = windowsPipeBootstrap;
        this.commFrontendSink = commFrontendSink;
    }

    /// <summary>Gets the Unix socket path used by the process-wide channel on Unix.</summary>
    public string SocketPath { get; }

    /// <summary>Address REPL and plugin-host children use for the process-wide control channel.</summary>
    public string ControlAddress => controlAddress ?? SocketPath;

    /// <summary>Gets the Windows named pipe used only for credential bootstrap.</summary>
    public string? WindowsPipeName => windowsPipeBootstrap?.PipeName;

    /// <summary>Updates the concrete loopback address after Kestrel binds its Windows endpoint.</summary>
    internal void SetControlAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        controlAddress = address;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.Delete(SocketPath);
        }
        catch
        {
            // Kestrel removes the socket file on close; best-effort cleanup must not mask shutdown.
        }
    }

    /// <summary>Creates a short socket path within the platform unix socket length limit.</summary>
    internal static string CreateSocketPath()
    {
        var name = $"mc-{Guid.NewGuid():N}"[..15] + ".sock";
        return Path.Combine(Path.GetTempPath(), name);
    }

    /// <summary>Creates the platform-specific process-wide control-channel address.</summary>
    internal static string CreateControlAddress()
    {
        return OperatingSystem.IsWindows() ? $"maieutics-{Guid.NewGuid():N}" : CreateSocketPath();
    }

    /// <summary>Maps the control channel middleware and endpoints onto the application.</summary>
    internal void MapEndpoints(WebApplication application)
    {
        application.Use(async (context, next) =>
        {
            // The middleware is path-scoped to the control bus: other planes on the shared
            // host (the frontend API) carry their own authorization.
            if (!IsControlPath(context.Request.Path))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
                bodySize.MaxRequestBodySize = ReplControlLimits.MaximumInboundMessageBytes;

            var identity = await AuthorizeAsync(context).ConfigureAwait(false);
            if (identity is null)
            {
                logger.LogWarning("Rejected control channel connection with an unexpected peer identity.");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Items[AuthorizedIdentityItem] = identity;
            await next(context).ConfigureAwait(false);
        });
        application.UseWebSockets();
        application.MapGet("/health", () => Results.Text("ok"));
        application.Map("/ws", HandleWebSocketAsync);
        application.MapPost("/v1/tool.invoke", HandleToolInvokeAsync);
        MapCommEndpoint(application);
    }

    private static bool IsControlPath(PathString path)
    {
        return path.StartsWithSegments("/health") ||
               path.StartsWithSegments("/ws") ||
               path.StartsWithSegments("/v1/tool.invoke") ||
               path.StartsWithSegments("/comm");
    }

    private async Task<string?> AuthorizeAsync(HttpContext context)
    {
        if (OperatingSystem.IsWindows())
            return TryGetCredentialIdentity(context, out var windowsIdentity)
                ? windowsIdentity
                : null;

        if (!ReplControlPeerProcess.TryGetIdentity(context, out var peerProcessId, out var peerUserId))
            return null;

        if (peerProcessId > 0)
        {
            try
            {
                using var identityWait = CancellationTokenSource.CreateLinkedTokenSource(
                    context.RequestAborted);
                identityWait.CancelAfter(ReplControlLimits.PeerRegistrationWait);
                await registry.WaitForIdentityAsync(peerProcessId, identityWait.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                // The peer never registered; treat it as unowned instead of holding the request open.
                return null;
            }

            if (registry.TryGetSession(peerProcessId, out var sessionId))
                return sessionId;

            if (registry.TryGetPluginHost(peerProcessId, out var hostId))
                return hostId;

            return null;
        }

        return peerUserId > 0 &&
               peerUserId == PeerProcessCredentials.GetCurrentUserId()
            ? string.Empty
            : null;
    }

    private bool TryGetCredentialIdentity(HttpContext context, out string identity)
    {
        identity = string.Empty;
        if (credentialRegistry is null ||
            !context.Request.Headers.TryGetValue("Authorization", out var authorization))
            return false;

        var value = authorization.ToString();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var credential = value[prefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(credential) &&
               credentialRegistry.TryResolve(credential, out identity);
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var peerProcessId = ReplControlPeerProcess.GetProcessId(context);
        var authorizedIdentity = context.Items.TryGetValue(AuthorizedIdentityItem, out var value) &&
                                 value is string identityValue
            ? identityValue
            : null;
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var identity = await ReceiveHelloAsync(
                socket,
                peerProcessId,
                authorizedIdentity,
                context.RequestAborted)
            .ConfigureAwait(false);
        logger.LogDebug("Control identity {Identity} peer {PeerProcessId}.", identity?.Id, peerProcessId);
        if (identity is null)
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket
                    .CloseOutputAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "identity not established",
                        context.RequestAborted)
                    .ConfigureAwait(false);
            return;
        }

        if (identity.Kind == "host")
        {
            if (pluginHosts is { } manager)
            {
                await manager.AttachHostAsync(socket, context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                await socket
                    .CloseAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "plugin host support is not enabled",
                        context.RequestAborted)
                    .ConfigureAwait(false);
            }

            return;
        }

        var sessionId = identity.Id;
        var connection = new SessionBusConnection(socket);
        if (connections.TryGetValue(sessionId, out var previous) && previous.State == WebSocketState.Open)
            await previous
                .CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None)
                .ConfigureAwait(false);

        connections[sessionId] = connection;
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var toolInvocations = new List<Task>();
        try
        {
            await connection.SendAsync(
                new ReplEnvelope(EnvelopeVersion, ReplMessageType.ControlReady),
                context.RequestAborted).ConfigureAwait(false);
            while (socket.State == WebSocketState.Open)
            {
                var text = await ReplControlMessageReader
                    .ReadAsync(socket, context.RequestAborted)
                    .ConfigureAwait(false);
                if (text is null) break;

                ObserveCompleted(toolInvocations);
                logger.LogDebug("Control message received: {Message}.", text);
                await HandleBusMessageAsync(
                    sessionId,
                    connection,
                    text,
                    toolInvocations,
                    owner.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await owner.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(toolInvocations).ConfigureAwait(false);
            connections.TryRemove(KeyValuePair.Create(sessionId, connection));

            comms.TryRemove(sessionId, out _);
        }
    }

    private static void ObserveCompleted(List<Task> tasks)
    {
        for (var index = tasks.Count - 1; index >= 0; index--)
            if (tasks[index].IsCompleted)
            {
                tasks[index].GetAwaiter().GetResult();
                tasks.RemoveAt(index);
            }
    }

    private string? ResolveRequestSessionId(HttpContext context, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        if (OperatingSystem.IsWindows())
        {
            var authorizedIdentity = context.Items.TryGetValue(AuthorizedIdentityItem, out var value) &&
                                     value is string identity
                ? identity
                : null;
            return string.Equals(authorizedIdentity, sessionId, StringComparison.Ordinal) &&
                   registry.ContainsSession(sessionId)
                ? sessionId
                : null;
        }

        var processId = ReplControlPeerProcess.GetProcessId(context);
        if (processId > 0) return registry.IsOwnedBy(processId, sessionId) ? sessionId : null;

        return registry.ContainsSession(sessionId) ? sessionId : null;
    }

    private async Task<HelloIdentity?> ReceiveHelloAsync(
        WebSocket socket,
        int peerProcessId,
        string? authorizedIdentity,
        CancellationToken ct)
    {
        var text = await ReplControlMessageReader.ReadAsync(socket, ct).ConfigureAwait(false);
        if (text is null) return null;

        ReplEnvelope? envelope;
        try
        {
            envelope = ParseEnvelope(text);
        }
        catch (JsonException)
        {
            return null;
        }

        if (envelope.Version != EnvelopeVersion ||
            envelope.Type != ReplMessageType.ControlHello ||
            envelope.Payload is not { } payload)
            return null;

        if (payload.TryGetProperty("hostId", out var host) &&
            host.GetString() is { } hostId && !hostId.IsWhiteSpace())
        {
            if ((peerProcessId > 0 && registry.IsPluginHostOwnedBy(peerProcessId, hostId)) ||
                string.Equals(authorizedIdentity, hostId, StringComparison.Ordinal))
                return new HelloIdentity("host", hostId);

            return null;
        }

        if (!payload.TryGetProperty("sessionId", out var session) ||
            session.GetString() is not { } sessionId || sessionId.IsWhiteSpace())
            return null;

        if ((peerProcessId > 0 && registry.IsOwnedBy(peerProcessId, sessionId)) ||
            string.Equals(authorizedIdentity, sessionId, StringComparison.Ordinal))
            return new HelloIdentity("session", sessionId);

        return peerProcessId <= 0 &&
               authorizedIdentity is null &&
               registry.ContainsSession(sessionId)
            ? new HelloIdentity("session", sessionId)
            : null;
    }

    private async Task HandleBusMessageAsync(
        string sessionId,
        SessionBusConnection connection,
        string text,
        ICollection<Task> toolInvocations,
        CancellationToken ct)
    {
        ReplEnvelope envelope;
        try
        {
            envelope = ParseEnvelope(text);
        }
        catch (JsonException)
        {
            await PushErrorAsync(
                connection,
                "invalid_envelope",
                "The message is not a valid envelope.",
                null,
                ct).ConfigureAwait(false);
            return;
        }

        switch (envelope.Type)
        {
            case ReplMessageType.ControlPing:
                logger.LogDebug("Control ping {CorrelationId}.", envelope.CorrelationId);
                await PushAsync(
                    connection,
                    new ReplEnvelope(EnvelopeVersion, ReplMessageType.ControlPong, envelope.CorrelationId),
                    ct).ConfigureAwait(false);
                break;
            case ReplMessageType.ControlCancel:
                var cancel = ParsePayload<BusCancelPayload>(envelope);
                if (cancel is null || string.IsNullOrWhiteSpace(cancel.CorrelationId))
                {
                    await PushErrorAsync(
                        connection,
                        "invalid_cancel",
                        "The cancel message requires a correlationId.",
                        envelope.CorrelationId,
                        ct).ConfigureAwait(false);
                    break;
                }

                var cancelled = operations.TryCancel(cancel.CorrelationId);
                await PushAsync(
                    connection,
                    new ReplEnvelope(
                        EnvelopeVersion,
                        cancelled ? ReplMessageType.ControlCancelled : ReplMessageType.Error,
                        cancel.CorrelationId,
                        cancelled
                            ? null
                            : Payload(new BusErrorPayload("operation_not_found",
                                "No in-flight operation has this correlationId."))),
                    ct).ConfigureAwait(false);
                break;
            case ReplMessageType.CommOpen:
                var open = ParsePayload<BusCommPayload>(envelope);
                if (open is null || string.IsNullOrWhiteSpace(open.CommId))
                {
                    await PushErrorAsync(
                        connection,
                        "invalid_comm",
                        "The comm.open message requires a commId.",
                        envelope.CorrelationId,
                        ct).ConfigureAwait(false);
                    break;
                }

                GetComms(sessionId).TryAdd(open.CommId, 0);
                await PushAckAsync(connection, open.CommId, true, envelope.CorrelationId, ct).ConfigureAwait(false);
                break;
            case ReplMessageType.CommMsg:
                var message = ParsePayload<BusCommPayload>(envelope);
                if (message is null || !GetComms(sessionId).ContainsKey(message.CommId))
                {
                    await PushErrorAsync(
                        connection,
                        "comm_not_open",
                        "The comm channel must be opened before sending messages.",
                        envelope.CorrelationId,
                        ct).ConfigureAwait(false);
                    break;
                }

                await PushAckAsync(connection, message.CommId, true, envelope.CorrelationId, ct)
                    .ConfigureAwait(false);
                break;
            case ReplMessageType.CommClose:
                var close = ParsePayload<BusCommPayload>(envelope);
                if (close is not null) GetComms(sessionId).TryRemove(close.CommId, out _);

                await PushAckAsync(
                    connection,
                    close?.CommId ?? string.Empty,
                    true,
                    envelope.CorrelationId,
                    ct).ConfigureAwait(false);
                break;
            case ReplMessageType.ToolInvoke:
                if (string.IsNullOrWhiteSpace(envelope.CorrelationId))
                {
                    await PushErrorAsync(
                        connection,
                        "invalid_tool_invoke",
                        "The tool.invoke message requires a correlationId.",
                        envelope.CorrelationId,
                        ct).ConfigureAwait(false);
                    break;
                }

                toolInvocations.Add(HandleBusToolInvokeAsync(sessionId, connection, envelope, ct));
                break;
            default:
                await PushErrorAsync(
                    connection,
                    "unknown_message",
                    $"Unknown message type '{envelope.Type}'.",
                    envelope.CorrelationId,
                    ct).ConfigureAwait(false);
                break;
        }
    }

    private ConcurrentDictionary<string, byte> GetComms(string sessionId)
    {
        return comms.GetOrAdd(sessionId, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
    }

    private async Task PushAckAsync(
        SessionBusConnection connection,
        string commId,
        bool ok,
        string? correlationId,
        CancellationToken ct)
    {
        await PushAsync(
            connection,
            new ReplEnvelope(
                EnvelopeVersion,
                ReplMessageType.CommAck,
                correlationId,
                Payload(new BusAckPayload(commId, ok))),
            ct).ConfigureAwait(false);
    }

    private async Task PushErrorAsync(
        SessionBusConnection connection,
        string code,
        string message,
        string? correlationId,
        CancellationToken ct)
    {
        await PushAsync(
            connection,
            new ReplEnvelope(
                EnvelopeVersion,
                ReplMessageType.Error,
                correlationId,
                Payload(new BusErrorPayload(code, message))),
            ct).ConfigureAwait(false);
    }

    private async Task PushAsync(string sessionId, ReplEnvelope envelope, CancellationToken ct)
    {
        if (!connections.TryGetValue(sessionId, out var connection)) return;
        await PushAsync(connection, envelope, ct).ConfigureAwait(false);
    }

    private static async Task PushAsync(
        SessionBusConnection connection,
        ReplEnvelope envelope,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(envelope, ReplControlJsonContext.Default.ReplEnvelope);
        await connection.SendAsync(Encoding.UTF8.GetBytes(json), ct).ConfigureAwait(false);
    }

    private static ReplEnvelope ParseEnvelope(string text)
    {
        return JsonSerializer.Deserialize(text, ReplControlJsonContext.Default.ReplEnvelope)
               ?? throw new JsonException("The envelope is null.");
    }

    private static T? ParsePayload<T>(ReplEnvelope envelope)
        where T : class
    {
        if (envelope.Payload is not { } payload) return null;

        return (T?)JsonSerializer.Deserialize(payload.GetRawText(), JsonTypeInfoFor<T>());
    }

    private static JsonTypeInfo JsonTypeInfoFor<T>()
    {
        return typeof(T) switch
        {
            _ when typeof(T) == typeof(BusCancelPayload) => ReplControlJsonContext.Default.BusCancelPayload,
            _ when typeof(T) == typeof(BusCommPayload) => ReplControlJsonContext.Default.BusCommPayload,
            _ when typeof(T) == typeof(BusErrorPayload) => ReplControlJsonContext.Default.BusErrorPayload,
            _ when typeof(T) == typeof(BusAckPayload) => ReplControlJsonContext.Default.BusAckPayload,
            _ when typeof(T) == typeof(ToolInvokePayload) => ReplControlJsonContext.Default.ToolInvokePayload,
            _ when typeof(T) == typeof(ToolProgressPayload) => ReplControlJsonContext.Default.ToolProgressPayload,
            _ => throw new InvalidOperationException($"Unsupported bus payload type '{typeof(T).Name}'.")
        };
    }

    private static JsonElement Payload<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, JsonTypeInfoFor<T>());
    }

    private async Task HandleToolInvokeAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        if (context.Request.ContentLength is > ReplControlLimits.MaximumInboundMessageBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        ToolInvokeRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                ReplControlJsonContext.Default.ToolInvokeRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception) when
            (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (request is null ||
            request.Version != EnvelopeVersion ||
            string.IsNullOrWhiteSpace(request.Tool) ||
            request.Arguments.ValueKind != JsonValueKind.Object)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        connections.TryGetValue(
            ResolveRequestSessionId(context, request.SessionId) ?? string.Empty,
            out var progressConnection);
        var envelope = await InvokeToolAsync(
            request.Tool,
            request.Arguments,
            request.CorrelationId,
            progressConnection,
            cancellationToken).ConfigureAwait(false);
        await WriteEnvelopeAsync(context, envelope, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleBusToolInvokeAsync(
        string sessionId,
        SessionBusConnection connection,
        ReplEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = ParsePayload<ToolInvokePayload>(envelope);
            if (request is null ||
                string.IsNullOrWhiteSpace(request.Tool) ||
                request.Arguments.ValueKind != JsonValueKind.Object)
            {
                await PushErrorAsync(
                    connection,
                    "invalid_tool_invoke",
                    "The tool.invoke payload requires a tool name and object arguments.",
                    envelope.CorrelationId,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await InvokeToolAsync(
                request.Tool,
                request.Arguments,
                envelope.CorrelationId,
                connection,
                cancellationToken).ConfigureAwait(false);
            await PushAsync(
                connection,
                new ReplEnvelope(
                    EnvelopeVersion,
                    ReplMessageType.ToolResult,
                    envelope.CorrelationId,
                    result),
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await PushErrorAsync(
                connection,
                "invalid_tool_invoke",
                "The tool.invoke payload is not valid.",
                envelope.CorrelationId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Control WebSocket tool invocation failed for session {SessionId}.",
                sessionId);
            try
            {
                await PushErrorAsync(
                    connection,
                    "tool_invoke_failed",
                    "The tool invocation could not be completed.",
                    envelope.CorrelationId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task<JsonElement> InvokeToolAsync(
        string tool,
        JsonElement requestArguments,
        string? correlationId,
        SessionBusConnection? progressConnection,
        CancellationToken cancellationToken)
    {
        var function = scriptTools.FirstOrDefault(candidate => candidate.Name == tool);
        if (function is null)
            return ToolJson.CreateFailureEnvelope(
                "tool_not_found",
                $"Tool '{tool}' is not available to scripts.");

        var argumentValues = requestArguments.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone());

        using var operation = new CancellationTokenSource();
        using var invoke = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operation.Token);
        var invokeToken = cancellationToken;
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            if (!operations.TryRegister(correlationId, operation))
                return ToolJson.CreateFailureEnvelope(
                    "duplicate_correlation",
                    "The correlationId is already in use.");

            invokeToken = invoke.Token;
        }

        JsonElement envelope;
        try
        {
            argumentValues = await RunPreHooksAsync(tool, argumentValues, correlationId, invokeToken)
                .ConfigureAwait(false);
            var arguments = new AIFunctionArguments(argumentValues);
            if (progressConnection is not null && !string.IsNullOrWhiteSpace(correlationId))
            {
                arguments.Context ??= new Dictionary<object, object?>();
                arguments.Context[typeof(ReplToolProgress)] = new ReplToolProgress((progress, ct) => new ValueTask(
                    PushAsync(
                        progressConnection,
                        new ReplEnvelope(
                            EnvelopeVersion,
                            ReplMessageType.ToolProgress,
                            correlationId,
                            Payload(progress)),
                        ct)));
            }

            var result = await function.InvokeAsync(arguments, invokeToken).ConfigureAwait(false);
            envelope = result switch
            {
                null => ToolJson.CreateSuccessEnvelope(null),
                JsonElement element => ToolJson.CreateSuccessEnvelope(element),
                _ => ToolJson.CreateFailureEnvelope(
                    "tool_invalid_result",
                    "Script tools must return a structured JSON value.")
            };
        }
        catch (OperationCanceledException) when (!string.IsNullOrWhiteSpace(correlationId) &&
                                                 operation.IsCancellationRequested)
        {
            envelope = ToolJson.CreateCancelledEnvelope();
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
            logger.LogWarning(exception, "Script tool '{Tool}' failed.", tool);
            envelope = ToolJson.CreateFailureEnvelope("tool_failed", exception.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(correlationId)) operations.Remove(correlationId);
        }

        await RunPostHooksAsync(tool, argumentValues, correlationId, envelope, cancellationToken)
            .ConfigureAwait(false);
        return envelope;
    }

    /// <summary>
    ///     Runs the pre-invoke hook chain. Hooks may reject the call or replace the arguments;
    ///     failures fail the call rather than silently changing behavior.
    /// </summary>
    private async Task<Dictionary<string, object?>> RunPreHooksAsync(
        string tool,
        Dictionary<string, object?> arguments,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (pluginHosts is not { } hosts) return arguments;

        var registrations = hosts.GetRegistrations(ReplExtensionPointName.ToolPreInvoke);
        var current = arguments;
        foreach (var registration in registrations)
        {
            var context = new ToolHookContextPayload(tool, SerializeArguments(current), correlationId ?? string.Empty);
            var outcome = await hosts
                .InvokeExtensionPointAsync(
                    registration.PluginId,
                    registration.ExportName,
                    ReplExtensionPointName.ToolPreInvoke,
                    JsonSerializer.SerializeToElement(context, ReplControlJsonContext.Default.ToolHookContextPayload),
                    cancellationToken)
                .ConfigureAwait(false);
            if (outcome.IsError)
                throw new AgentToolException(
                    "tool_hook_failed",
                    $"A pre-invoke hook failed: {outcome.Message}");

            var (action, replaced, code, message) = ParseHookDecision(outcome.Value);
            switch (action)
            {
                case "reject":
                    throw new AgentToolException(code, message);
                case "replace":
                    if (replaced is { } replacement) current = DeserializeArguments(replacement);

                    break;
                case "continue":
                    break;
                default:
                    throw new AgentToolException(
                        "tool_hook_invalid",
                        $"A pre-invoke hook returned an unknown decision '{action}'.");
            }
        }

        return current;
    }

    private async Task RunPostHooksAsync(
        string tool,
        Dictionary<string, object?> arguments,
        string? correlationId,
        JsonElement envelope,
        CancellationToken cancellationToken)
    {
        if (pluginHosts is not { } hosts) return;

        var registrations = hosts.GetRegistrations(ReplExtensionPointName.ToolPostInvoke);
        if (registrations.Count == 0) return;

        var status = ResolvePostStatus(envelope);
        var result = ResolvePostResult(envelope);
        foreach (var registration in registrations)
            try
            {
                var context = new ToolPostHookContextPayload(
                    tool,
                    SerializeArguments(arguments),
                    correlationId ?? string.Empty,
                    status,
                    result);
                _ = await hosts
                    .InvokeExtensionPointAsync(
                        registration.PluginId,
                        registration.ExportName,
                        ReplExtensionPointName.ToolPostInvoke,
                        JsonSerializer.SerializeToElement(
                            context,
                            ReplControlJsonContext.Default.ToolPostHookContextPayload),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "A post-invoke hook failed for tool '{Tool}'.", tool);
            }
    }

    private static (string Action, JsonElement? Arguments, string Code, string Message) ParseHookDecision(
        JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } decision ||
            !decision.TryGetProperty("action", out var action) ||
            action.ValueKind != JsonValueKind.String ||
            action.GetString() is not { } actionValue)
            return ("invalid", null, "tool_hook_invalid", "A hook returned a non-object decision.");

        JsonElement? arguments = null;
        if (decision.TryGetProperty("arguments", out var replaced) && replaced.ValueKind == JsonValueKind.Object)
            arguments = replaced;

        var code = "tool_hook_rejected";
        var message = "A hook rejected the tool call.";
        if (!decision.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return (actionValue, arguments, code, message);
        if (error.TryGetProperty("code", out var codeValue)
            && codeValue.ValueKind == JsonValueKind.String
            && codeValue.GetString() is { } s
           )
            code = s;

        if (error.TryGetProperty("message", out var messageValue) &&
            messageValue.ValueKind == JsonValueKind.String
            && messageValue.GetString() is { } mes)
            message = mes;

        return (actionValue, arguments, code, message);
    }

    private static JsonElement SerializeArguments(IReadOnlyDictionary<string, object?> arguments)
    {
        var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in arguments)
            if (pair.Value is JsonElement element)
                copy[pair.Key] = element.Clone();

        return JsonSerializer.SerializeToElement(copy, ReplControlJsonContext.Default.DictionaryStringJsonElement);
    }

    private static Dictionary<string, object?> DeserializeArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return new Dictionary<string, object?>(StringComparer.Ordinal);

        return arguments.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone());
    }

    private static string ResolvePostStatus(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
            return "error";

        return status.GetString() switch
        {
            "ok" => "ok",
            "cancelled" => "cancelled",
            _ => "error"
        };
    }

    private static JsonElement? ResolvePostResult(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("value", out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return value.Clone();
    }

    private static async Task WriteEnvelopeAsync(
        HttpContext context,
        JsonElement envelope,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(envelope.GetRawText(), cancellationToken).ConfigureAwait(false);
    }

    private sealed record HelloIdentity(string Kind, string Id);
}
