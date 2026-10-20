using System.Text.Json;
using Maieutics.Agent;
using Microsoft.Extensions.Logging;

namespace Maieutics.Frontend;

/// <summary>
///     Presents tool-approval requests to the frontend and completes them through the shared
///     input endpoint (the same suspend/resume channel REPL stdin and MCP elicitation use).
///     The presenter is a second consumer of that channel, not a new protocol.
/// </summary>
/// <remarks>
///     Every path returns a terminal decision. There is no "hang forever": a run that ends, a
///     session with no live stream, a timeout, and a malformed answer all resolve to a denial
///     with a typed reason. Denying is the only safe default for an action the user did not
///     affirmatively approve.
/// </remarks>
internal sealed class FrontendToolApprovalPresenter : IAgentToolApprovalGate, IAgentChildScopeRegistry
{
    /// <summary>How long a pending approval waits before denying itself. The composition root
    /// supplies the configured value; this is the fallback for a host that supplies none.</summary>
    internal static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(120);

    private readonly IFrontendSessionFramePublisher publisher;
    private readonly ILogger<FrontendToolApprovalPresenter> logger;
    private readonly TimeSpan waitTimeout;
    private readonly Lock gate = new();
    private readonly Dictionary<string, TaskCompletionSource<AgentToolApprovalDecision>> pending =
        new(StringComparer.Ordinal);

    /// <summary>Child session → owning session, so a request raised inside a subagent run is
    /// presented on the parent's stream. A child session is created by the subagent host and
    /// never announces a stream of its own (only frontend-submitted turns do), so presenting
    /// on the child id would find nothing and deny with a false "no frontend" reason. Bounded
    /// by the same discipline as the permission registry's child scopes.</summary>
    private readonly Dictionary<AgentSessionId, AgentSessionId> parents = [];
    private readonly Queue<AgentSessionId> parentOrder = new();
    private const int MaximumParentScopes = 256;

    internal FrontendToolApprovalPresenter(
        IFrontendSessionFramePublisher publisher,
        ILogger<FrontendToolApprovalPresenter> logger,
        TimeSpan? waitTimeout = null)
    {
        this.publisher = publisher;
        this.logger = logger;
        this.waitTimeout = waitTimeout ?? DefaultWaitTimeout;
    }

    /// <summary>Registers a spawned child run's session under its owning session, so an
    /// approval the child raises is presented to the user watching the parent. Mirrors
    /// <c>PermissionOverrideRegistry.RegisterChildScope</c>: unknown parents are harmless
    /// (the request then denies, which is the safe direction).</summary>
    public void RegisterChildScope(AgentSessionId childSessionId, AgentSessionId parentSessionId)
    {
        lock (gate)
        {
            if (!parents.TryAdd(childSessionId, parentSessionId)) return;
            parentOrder.Enqueue(childSessionId);
            while (parentOrder.Count > MaximumParentScopes) parents.Remove(parentOrder.Dequeue());
        }
    }

    /// <summary>Releases one child registration when its run settles.</summary>
    public void ReleaseChildScope(AgentSessionId childSessionId)
    {
        lock (gate)
        {
            parents.Remove(childSessionId);
        }
    }

    /// <summary>The session whose stream presents this request: the parent for a child run,
    /// the session itself otherwise.</summary>
    private AgentSessionId PresentationSession(AgentSessionId sessionId)
    {
        lock (gate)
        {
            return parents.TryGetValue(sessionId, out var parent) ? parent : sessionId;
        }
    }

    /// <summary>The request-id prefix that routes answers here. Distinct from the elicitation
    /// and REPL prefixes, so the input endpoint dispatches by identity rather than by guessing.</summary>
    internal const string RequestIdPrefix = "approve-";

    public async ValueTask<AgentToolApprovalDecision> RequestApprovalAsync(
        AgentToolApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestId = RequestIdPrefix + Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<AgentToolApprovalDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            pending[requestId] = completion;
        }

        bool published;
        try
        {
            published = publisher.TryPublishPresentation(
                PresentationSession(request.SessionId),
                "input.request",
                JsonSerializer.SerializeToElement(
                    new FrontendInputRequest(
                        requestId,
                        BuildPrompt(request),
                        Password: false,
                        Schema: BuildApprovalSchema(request),
                        ServerId: null,
                        Kind: FrontendInputKind.Approval,
                        Tool: request.ToolName,
                        Reason: request.Reason),
                    FrontendJsonContext.Default.FrontendInputRequest));
        }
        catch (Exception exception)
        {
            // A throwing publisher must not leave a pending entry behind: a late answer would
            // otherwise draw a 200 for a decision nobody consumed.
            lock (gate)
            {
                pending.Remove(requestId);
            }

            logger.LogWarning(
                exception,
                "Publishing the approval request for tool '{Tool}' failed.",
                request.ToolName);
            return AgentToolApprovalDecision.Deny(
                "The approval request could not be presented to the frontend.");
        }

        if (!published)
        {
            lock (gate)
            {
                pending.Remove(requestId);
            }

            // No live stream to present on (the run already settled, or the frontend is
            // detached): deny rather than execute an unapproved action.
            return AgentToolApprovalDecision.Deny(
                "No frontend is connected to approve this tool call.");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(waitTimeout);
        try
        {
            return await completion.Task.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run ended while waiting. Denial keeps the tool from executing after the
            // user's turn was cancelled. Claiming the entry first means an answer that raced
            // this cancellation keeps its own outcome instead of being overwritten.
            if (!TryClaim(requestId)) return await completion.Task.ConfigureAwait(false);
            return AgentToolApprovalDecision.Deny("The run ended before the call was approved.");
        }
        catch (OperationCanceledException)
        {
            // The wait budget expired. The claim is what decides the race: an answer that
            // removed the entry first already decided this request, and its decision stands;
            // only the loser of that race reports a timeout. Without this, the user would see
            // their answer accepted (HTTP 200) while the model saw a timeout.
            if (!TryClaim(requestId)) return await completion.Task.ConfigureAwait(false);

            logger.LogWarning(
                "The approval request for tool '{Tool}' timed out after {Seconds:0.#} seconds; denied.",
                request.ToolName,
                waitTimeout.TotalSeconds);
            return AgentToolApprovalDecision.Deny(
                $"The approval request timed out after {waitTimeout.TotalSeconds:0.#} seconds.");
        }
        finally
        {
            TryClaim(requestId);
        }
    }

    /// <summary>Removes the request from the pending map, returning true when this caller
    /// removed it. The removal is the arbiter between a racing answer and the wait's own
    /// termination: exactly one of them wins, so neither can report a decision the other
    /// discarded.</summary>
    private bool TryClaim(string requestId)
    {
        lock (gate)
        {
            return pending.Remove(requestId);
        }
    }

    /// <summary>Completes a pending approval from the shared input endpoint. The action is
    /// the elicitation vocabulary reused: <c>accept</c> approves, anything else denies.
    /// <c>always</c> is an accept that also remembers the tool for this session.
    /// Returns false when the request already settled — answered, timed out, or cancelled —
    /// so the caller answers 404 instead of claiming a decision was recorded.</summary>
    internal bool TryCompleteInput(string requestId, string? action, string value)
    {
        TaskCompletionSource<AgentToolApprovalDecision>? completion;
        lock (gate)
        {
            // The map removal is the claim: whoever removes the entry owns the outcome, so an
            // answer that races the wait's own timeout either wins outright or draws the 404.
            if (!pending.Remove(requestId, out completion)) return false;
        }

        return completion.TrySetResult(MapDecision(action, value));
    }

    /// <summary>Whether the id belongs to this presenter. The input endpoint uses it to pick
    /// the consumer before any lookup, so a mismatched prefix is a 404 rather than a cross-wire.</summary>
    internal static bool OwnsRequestId(string requestId) =>
        requestId.StartsWith(RequestIdPrefix, StringComparison.Ordinal);

    private static AgentToolApprovalDecision MapDecision(string? action, string value)
    {
        var normalized = action?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "accept" or "allow" or "approve" => new AgentToolApprovalDecision(
                true,
                "Approved by the user.",
                AlwaysAllow: false),
            "always" or "always-allow" => new AgentToolApprovalDecision(
                true,
                "Approved by the user for the rest of this session.",
                AlwaysAllow: true),
            "decline" or "deny" or "reject" => AgentToolApprovalDecision.Deny(
                string.IsNullOrWhiteSpace(value) ? "Denied by the user." : value),
            "cancel" => AgentToolApprovalDecision.Deny("The approval request was cancelled."),
            // An unknown or empty action is a client bug; denying is the safe reading.
            _ => AgentToolApprovalDecision.Deny("The approval answer carried no decision."),
        };
    }

    private static string BuildPrompt(AgentToolApprovalRequest request)
    {
        var title = request.Classification is { Destructive: true }
            ? $"Allow '{request.ToolName}' to run? It may destroy data."
            : $"Allow '{request.ToolName}' to run?";
        return $"{title}\n\n{request.Reason}";
    }

    /// <summary>A minimal form the existing frontend can render: the tool's arguments as
    /// read-only context. The decision itself rides the answer's action, not a field.</summary>
    private static JsonElement BuildApprovalSchema(AgentToolApprovalRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            writer.WriteEndObject();

            // Arguments ride the schema's description rather than as editable fields: the user
            // approves the call the model made, and editing it here would create a second,
            // unreviewed argument source.
            var arguments = request.Arguments.ValueKind == JsonValueKind.Object
                ? request.Arguments.GetRawText()
                : "{}";
            if (arguments.Length <= 2048) writer.WriteString("description", arguments);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
