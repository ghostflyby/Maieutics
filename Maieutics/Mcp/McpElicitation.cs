using System.Text.Json;
using Maieutics.Agent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Maieutics.Mcp;

/// <summary>Presents one MCP elicitation to the user through the frontend input loop and
/// returns the user's answer. Implemented in the presentation layer; the MCP side only
/// sees this seam (ADR 0029 decision 3).</summary>
internal interface IMcpElicitationPresenter
{
    /// <summary>Shows the request and waits for the user. Implementations must return a
    /// terminal answer (never throw for user absence — return cancel) and honour
    /// <paramref name="cancellationToken"/> for shutdown.</summary>
    ValueTask<McpElicitationAnswer> PresentAsync(McpElicitationRequest request, CancellationToken cancellationToken);
}

/// <summary>One elicitation routed to a specific Agent session's frontend.</summary>
/// <param name="ServerId">The configured MCP server id that raised the request.</param>
/// <param name="SessionId">The attributed Agent session, canonical N form.</param>
/// <param name="Message">The server's human-readable prompt (untrusted, size-capped).</param>
/// <param name="Schema">The requested form schema (untrusted, size-capped), or null.</param>
/// <param name="Password">True when the schema asks for secret input.</param>
internal sealed record McpElicitationRequest(
    string ServerId,
    string SessionId,
    string Message,
    JsonElement? Schema,
    bool Password);

/// <summary>The user's terminal answer. <paramref name="Action"/> is one of
/// <c>accept</c>, <c>decline</c>, <c>cancel</c>; content is only meaningful for accept.</summary>
/// <param name="Action">The terminal action.</param>
/// <param name="ContentJson">A JSON object of submitted primitive values, or null.</param>
internal sealed record McpElicitationAnswer(string Action, string? ContentJson);

/// <summary>Attribution and bounding for elicitation requests on one server generation
/// (ADR 0029 decision 3): the elicitation handler asks here which Agent session owns the
/// in-flight tool call, and requests are refused (answered cancel) when attribution is
/// ambiguous or a bound would be exceeded.</summary>
internal sealed class McpElicitationCoordinator
{
    private const int MaximumMessageCharacters = 4 * 1024;
    private const int MaximumSchemaBytes = 16 * 1024;

    private readonly Lock gate = new();
    private readonly Dictionary<AgentToolCallId, AgentSessionId> inFlightCalls = [];
    private readonly IMcpElicitationPresenter? presenter;
    private readonly ILogger logger;
    private readonly TimeSpan waitTimeout;
    private int inFlightElicitations;
    private bool refused;

    public McpElicitationCoordinator(
        IMcpElicitationPresenter? presenter,
        ILogger logger,
        TimeSpan? waitTimeout = null)
    {
        this.presenter = presenter;
        this.logger = logger;
        this.waitTimeout = waitTimeout ?? TimeSpan.FromSeconds(120);
    }

    internal bool IsEnabled => presenter is not null;

    /// <summary>Records one in-flight tool call so a concurrent elicitation can be attributed.</summary>
    public void RegisterCall(AgentToolCallId callId, AgentSessionId sessionId)
    {
        lock (gate)
        {
            inFlightCalls[callId] = sessionId;
        }
    }

    /// <summary>Removes a finished tool call's attribution.</summary>
    public void UnregisterCall(AgentToolCallId callId)
    {
        lock (gate)
        {
            inFlightCalls.Remove(callId);
        }
    }

    /// <summary>Handles one elicitation/create request: attribute, bound, present, and map
    /// to an ElicitResult. Every refusal path answers cancel — never an exception into the
    /// SDK's message loop, never a form shown to a guessed session.</summary>
    public async ValueTask<ElicitResult> HandleElicitationAsync(
        string serverId,
        ElicitRequestParams request,
        CancellationToken cancellationToken)
    {
        if (presenter is null || Volatile.Read(ref refused))
            return CancelResult();

        // One in-flight elicitation per server: a second concurrent request is refused rather
        // than queued, so a chatty server cannot stack prompts.
        if (Interlocked.CompareExchange(ref inFlightElicitations, 1, 0) != 0)
        {
            logger.LogWarning(
                "MCP server {ServerId} raised an elicitation while another is pending; answered cancel.",
                serverId);
            return CancelResult();
        }

        try
        {
            var message = request.Message ?? string.Empty;
            if (message.Length > MaximumMessageCharacters)
            {
                logger.LogWarning(
                    "MCP server {ServerId} sent an oversized elicitation message; answered cancel.",
                    serverId);
                return CancelResult();
            }

            // Form-mode schemas carry no secret marker in the protocol (StringSchema.Format only
            // allows email/uri/date/date-time), so the presenter never gets a password hint here;
            // sensitive input belongs to URL-mode elicitation, which surfaces as a plain prompt.
            JsonElement? schema = null;
            if (request.RequestedSchema is { } requested)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(
                    requested,
                    McpJsonUtilities.GetTypeInfo<ElicitRequestParams.RequestSchema>(McpJsonUtilities.DefaultOptions));
                if (bytes.Length > MaximumSchemaBytes)
                {
                    logger.LogWarning(
                        "MCP server {ServerId} sent an oversized elicitation schema; answered cancel.",
                        serverId);
                    return CancelResult();
                }

                using var schemaDocument = JsonDocument.Parse(bytes);
                if (schemaDocument.RootElement.ValueKind != JsonValueKind.Object)
                {
                    logger.LogWarning(
                        "MCP server {ServerId} sent a non-object elicitation schema; answered cancel.",
                        serverId);
                    return CancelResult();
                }

                schema = schemaDocument.RootElement.Clone();
            }

            var sessionId = AttributeSession(serverId);
            if (sessionId is null)
                return CancelResult();

            // The wait is bounded (ADR 0029 decision 3): an absent user answers cancel inside
            // the budget, and the wait still honours the caller's cancellation for shutdown.
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(waitTimeout);
            McpElicitationAnswer answer;
            try
            {
                // The bounded wait is the coordinator's responsibility, not the presenter's:
                // an implementation that ignores the token still cannot hang this request.
                answer = await presenter.PresentAsync(
                    new McpElicitationRequest(serverId, sessionId.Value.ToString(), message, schema, Password: false),
                    waitCts.Token).AsTask().WaitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (waitCts.IsCancellationRequested)
            {
                logger.LogInformation(
                    "MCP server {ServerId} elicitation timed out unanswered; answered cancel.",
                    serverId);
                return CancelResult();
            }

            return MapAnswer(answer, serverId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CancelResult();
        }
        catch (Exception exception)
        {
            // A presenter that fails outside a terminal answer is refused for the rest of the
            // generation so a broken frontend cannot be probed repeatedly.
            Refuse();
            logger.LogWarning(
                exception,
                "MCP server {ServerId} elicitation presentation failed; answered cancel and refusing further requests.",
                serverId);
            return CancelResult();
        }
        finally
        {
            Interlocked.Exchange(ref inFlightElicitations, 0);
        }
    }

    /// <summary>Refuses elicitation for the rest of this generation after the presenter fails
    /// outside a terminal answer, so a broken frontend cannot be probed repeatedly.</summary>
    internal void Refuse()
    {
        Volatile.Write(ref refused, true);
    }

    /// <summary>Returns the single Agent session with an in-flight call on this generation,
    /// or null when nothing is in flight or attribution is ambiguous.</summary>
    private AgentSessionId? AttributeSession(string serverId)
    {
        AgentSessionId? attributed = null;
        lock (gate)
        {
            foreach (var sessionId in inFlightCalls.Values.Distinct())
            {
                if (attributed is not null)
                {
                    logger.LogWarning(
                        "MCP server {ServerId} raised an elicitation with calls in flight from several sessions; answered cancel.",
                        serverId);
                    return null;
                }

                attributed = sessionId;
            }
        }

        if (attributed is null)
            logger.LogWarning(
                "MCP server {ServerId} raised an elicitation outside any in-flight tool call; answered cancel.",
                serverId);

        return attributed;
    }

    private ElicitResult MapAnswer(McpElicitationAnswer answer, string serverId)
    {
        if (!string.Equals(answer.Action, "accept", StringComparison.Ordinal))
        {
            var action = string.Equals(answer.Action, "decline", StringComparison.Ordinal)
                ? "decline"
                : "cancel";
            logger.LogInformation(
                "MCP server {ServerId} elicitation answered {Action}.",
                serverId,
                action);
            return new ElicitResult { Action = action };
        }

        var content = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (answer.ContentJson is { } json)
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number
                        or JsonValueKind.True or JsonValueKind.False)
                        content[property.Name] = property.Value.Clone();
            }
        }

        logger.LogInformation(
            "MCP server {ServerId} elicitation accepted with {FieldCount} field(s).",
            serverId,
            content.Count);
        return new ElicitResult { Action = "accept", Content = content };
    }

    private static ElicitResult CancelResult()
    {
        return new ElicitResult { Action = "cancel" };
    }
}
