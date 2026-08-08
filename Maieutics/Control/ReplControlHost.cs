using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Maieutics.Agent;
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
internal sealed class ReplControlHost : IDisposable
{
    private const int EnvelopeVersion = 1;
    private const string CredentialHeader = "X-Maieutics-Credential";

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> comms =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, WebSocket> connections = new(StringComparer.Ordinal);
    private readonly ReplControlCredentialRegistry? credentials;
    private readonly ILogger<ReplControlHost> logger;

    private readonly ReplOperationRegistry operations = new();
    private readonly Task<PluginHostManager>? pluginHosts;
    private readonly ReplControlSessionRegistry registry;
    private readonly IReadOnlyList<AIFunction> scriptTools;
    private readonly IWindowsPipeBootstrap? windowsBootstrap;

    public ReplControlHost(
        string socketPath,
        ReplControlSessionRegistry registry,
        ILogger<ReplControlHost> logger,
        IReadOnlyList<AIFunction>? scriptTools = null,
        Task<PluginHostManager>? pluginHosts = null,
        ReplControlCredentialRegistry? credentials = null,
        IWindowsPipeBootstrap? windowsBootstrap = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        SocketPath = socketPath;
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.scriptTools = scriptTools ?? [];
        this.pluginHosts = pluginHosts;
        this.credentials = credentials;
        this.windowsBootstrap = windowsBootstrap;
    }

    /// <summary>Gets the unix domain socket path the channel listens on.</summary>
    public string SocketPath { get; }

    /// <summary>Loopback TCP address on Windows, resolved after the host starts.</summary>
    public string? WindowsControlAddress { get; set; }

    /// <summary>Address REPL children connect to: unix socket path, or TCP host:port on Windows.</summary>
    public string ControlAddress =>
        OperatingSystem.IsWindows() ? WindowsControlAddress ?? string.Empty : SocketPath;

    /// <summary>Named pipe REPL children bootstrap through on Windows.</summary>
    public string? WindowsPipeName => windowsBootstrap?.PipeName;

    public void Dispose()
    {
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

    /// <summary>Maps the control channel middleware and endpoints onto the application.</summary>
    internal void MapEndpoints(WebApplication application)
    {
        application.Use(async (context, next) =>
        {
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
                bodySize.MaxRequestBodySize = ReplControlLimits.MaximumInboundMessageBytes;

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

    private bool Authorize(HttpContext context)
    {
        if (OperatingSystem.IsWindows())
            // Windows cannot resolve peer credentials on loopback TCP; the named-pipe bootstrap
            // issued a session-bound credential that each request carries in a header.
            return ResolveCredentialIdentity(context) is not null;

        var peerSocket = GetPeerSocket(context);
        if (peerSocket is null ||
            !PeerProcessCredentials.TryGetPeerIdentity(peerSocket, out var peerProcessId, out var peerUserId))
            return false;

        if (peerProcessId > 0)
            return registry.TryGetSession(peerProcessId, out _) ||
                   registry.TryGetPluginHost(peerProcessId, out _);

        return peerUserId > 0 && peerUserId == PeerProcessCredentials.GetCurrentUserId();
    }

    private static Socket? GetPeerSocket(HttpContext context)
    {
        return context.Features.Get<IConnectionSocketFeature>()?.Socket;
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
        var identity = await ReceiveHelloAsync(socket, peerProcessId, context.RequestAborted).ConfigureAwait(false);
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
            if (pluginHosts is not null)
            {
                var manager = await pluginHosts.ConfigureAwait(false);
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
        if (connections.TryGetValue(sessionId, out var previous) && previous.State == WebSocketState.Open)
            await previous
                .CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None)
                .ConfigureAwait(false);

        connections[sessionId] = socket;
        try
        {
            await PushAsync(
                sessionId,
                new ReplEnvelope(EnvelopeVersion, ReplMessageType.ControlReady),
                context.RequestAborted).ConfigureAwait(false);
            while (socket.State == WebSocketState.Open)
            {
                var text = await ReplControlMessageReader
                    .ReadAsync(socket, context.RequestAborted)
                    .ConfigureAwait(false);
                if (text is null) break;

                await HandleBusMessageAsync(sessionId, text, context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            if (connections.TryGetValue(sessionId, out var current) && ReferenceEquals(current, socket))
                connections.TryRemove(sessionId, out _);

            comms.TryRemove(sessionId, out _);
        }
    }

    private static int ResolvePeerProcessId(HttpContext context)
    {
        var peerSocket = GetPeerSocket(context);
        if (peerSocket is not null &&
            PeerProcessCredentials.TryGetPeerIdentity(peerSocket, out var processId, out _))
            return processId;

        return 0;
    }

    private string? ResolveRequestSessionId(HttpContext context, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        if (OperatingSystem.IsWindows())
            return string.Equals(
                ResolveCredentialIdentity(context),
                sessionId,
                StringComparison.Ordinal)
                ? sessionId
                : null;

        var processId = ResolvePeerProcessId(context);
        if (processId > 0) return registry.IsOwnedBy(processId, sessionId) ? sessionId : null;

        return registry.ContainsSession(sessionId) ? sessionId : null;
    }

    private async Task<HelloIdentity?> ReceiveHelloAsync(WebSocket socket, int peerProcessId, CancellationToken ct)
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
            if (OperatingSystem.IsWindows())
                return string.Equals(ResolveCredentialIdentity(payload), hostId, StringComparison.Ordinal)
                    ? new HelloIdentity("host", hostId)
                    : null;

            if (peerProcessId > 0 && registry.IsPluginHostOwnedBy(peerProcessId, hostId))
                return new HelloIdentity("host", hostId);

            return null;
        }

        if (!payload.TryGetProperty("sessionId", out var session) ||
            session.GetString() is not { } sessionId || sessionId.IsWhiteSpace())
            return null;

        if (OperatingSystem.IsWindows())
            return string.Equals(ResolveCredentialIdentity(payload), sessionId, StringComparison.Ordinal)
                ? new HelloIdentity("session", sessionId)
                : null;

        if (peerProcessId > 0)
            return registry.IsOwnedBy(peerProcessId, sessionId) ? new HelloIdentity("session", sessionId) : null;

        return registry.ContainsSession(sessionId) ? new HelloIdentity("session", sessionId) : null;
    }

    private string? ResolveCredentialIdentity(JsonElement payload)
    {
        if (credentials is null ||
            !payload.TryGetProperty("credential", out var credential) ||
            credential.ValueKind != JsonValueKind.String ||
            credential.GetString() is not { } s || s.IsWhiteSpace())
            return null;

        return credentials.TryResolve(s, out var identity) ? identity : null;
    }

    private string? ResolveCredentialIdentity(HttpContext context)
    {
        if (credentials is null) return null;

        var credential = context.Request.Headers[CredentialHeader].ToString();
        return !string.IsNullOrWhiteSpace(credential) &&
               credentials.TryResolve(credential, out var identity)
            ? identity
            : null;
    }

    private async Task HandleBusMessageAsync(string sessionId, string text, CancellationToken ct)
    {
        ReplEnvelope envelope;
        try
        {
            envelope = ParseEnvelope(text);
        }
        catch (JsonException)
        {
            await PushErrorAsync(
                sessionId,
                "invalid_envelope",
                "The message is not a valid envelope.",
                null,
                ct).ConfigureAwait(false);
            return;
        }

        switch (envelope.Type)
        {
            case ReplMessageType.ControlPing:
                await PushAsync(
                    sessionId,
                    new ReplEnvelope(EnvelopeVersion, ReplMessageType.ControlPong, envelope.CorrelationId),
                    ct).ConfigureAwait(false);
                break;
            case ReplMessageType.ControlCancel:
                var cancel = ParsePayload<BusCancelPayload>(envelope);
                if (cancel is null || string.IsNullOrWhiteSpace(cancel.CorrelationId))
                {
                    await PushErrorAsync(
                        sessionId,
                        "invalid_cancel",
                        "The cancel message requires a correlationId.",
                        envelope.CorrelationId,
                        ct).ConfigureAwait(false);
                    break;
                }

                var cancelled = operations.TryCancel(cancel.CorrelationId);
                await PushAsync(
                    sessionId,
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
                        sessionId,
                        "invalid_comm",
                        "The comm.open message requires a commId.",
                        envelope.CorrelationId,
                        ct).ConfigureAwait(false);
                    break;
                }

                GetComms(sessionId).TryAdd(open.CommId, 0);
                await PushAckAsync(sessionId, open.CommId, true, envelope.CorrelationId, ct).ConfigureAwait(false);
                break;
            case ReplMessageType.CommMsg:
                var message = ParsePayload<BusCommPayload>(envelope);
                if (message is null || !GetComms(sessionId).ContainsKey(message.CommId))
                {
                    await PushErrorAsync(
                        sessionId,
                        "comm_not_open",
                        "The comm channel must be opened before sending messages.",
                        envelope.CorrelationId,
                        ct).ConfigureAwait(false);
                    break;
                }

                await PushAckAsync(sessionId, message.CommId, true, envelope.CorrelationId, ct)
                    .ConfigureAwait(false);
                break;
            case ReplMessageType.CommClose:
                var close = ParsePayload<BusCommPayload>(envelope);
                if (close is not null) GetComms(sessionId).TryRemove(close.CommId, out _);

                await PushAckAsync(
                    sessionId,
                    close?.CommId ?? string.Empty,
                    true,
                    envelope.CorrelationId,
                    ct).ConfigureAwait(false);
                break;
            default:
                await PushErrorAsync(
                    sessionId,
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
        string sessionId,
        string commId,
        bool ok,
        string? correlationId,
        CancellationToken ct)
    {
        await PushAsync(
            sessionId,
            new ReplEnvelope(
                EnvelopeVersion,
                ReplMessageType.CommAck,
                correlationId,
                Payload(new BusAckPayload(commId, ok))),
            ct).ConfigureAwait(false);
    }

    private async Task PushErrorAsync(
        string sessionId,
        string code,
        string message,
        string? correlationId,
        CancellationToken ct)
    {
        await PushAsync(
            sessionId,
            new ReplEnvelope(
                EnvelopeVersion,
                ReplMessageType.Error,
                correlationId,
                Payload(new BusErrorPayload(code, message))),
            ct).ConfigureAwait(false);
    }

    private async Task PushAsync(string sessionId, ReplEnvelope envelope, CancellationToken ct)
    {
        if (!connections.TryGetValue(sessionId, out var socket) || socket.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(envelope, ReplControlJsonContext.Default.ReplEnvelope);
        await socket
            .SendAsync(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                true,
                ct)
            .ConfigureAwait(false);
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

        var correlationId = request.CorrelationId;
        var sessionId = ResolveRequestSessionId(context, request.SessionId);
        var argumentValues = request.Arguments.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone());

        using var operation = new CancellationTokenSource();
        var invokeToken = cancellationToken;
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            if (!operations.TryRegister(correlationId, operation))
            {
                await WriteEnvelopeAsync(
                    context,
                    ToolJson.CreateFailureEnvelope(
                        "duplicate_correlation",
                        "The correlationId is already in use."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            invokeToken = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken, operation.Token).Token;
        }

        JsonElement envelope;
        try
        {
            argumentValues = await RunPreHooksAsync(request.Tool, argumentValues, correlationId, invokeToken)
                .ConfigureAwait(false);
            var arguments = new AIFunctionArguments(argumentValues);
            if (sessionId is not null && !string.IsNullOrWhiteSpace(correlationId))
            {
                arguments.Context ??= new Dictionary<object, object?>();
                arguments.Context[typeof(ReplToolProgress)] = new ReplToolProgress((progress, ct) => new ValueTask(
                    PushAsync(
                        sessionId,
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
            logger.LogWarning(exception, "Script tool '{Tool}' failed.", request.Tool);
            envelope = ToolJson.CreateFailureEnvelope("tool_failed", exception.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(correlationId)) operations.Remove(correlationId);
        }

        await RunPostHooksAsync(request.Tool, argumentValues, correlationId, envelope, cancellationToken)
            .ConfigureAwait(false);
        await WriteEnvelopeAsync(context, envelope, cancellationToken).ConfigureAwait(false);
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
        if (pluginHosts is not { } pluginHostsTask) return arguments;

        var hosts = await pluginHostsTask.ConfigureAwait(false);
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
        if (pluginHosts is not { } pluginHostsTask) return;

        var hosts = await pluginHostsTask.ConfigureAwait(false);
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
