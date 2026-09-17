using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Maieutics.Agent;
using Maieutics.Mcp;

namespace Maieutics.Frontend;

/// <summary>Publishes one session-scoped presentation frame onto the session's current run
/// stream. Implemented by <see cref="FrontendSessionService" />; the elicitation presenter
/// depends on this seam only, so attribution and frame delivery stay decoupled.</summary>
internal interface IFrontendSessionFramePublisher
{
    /// <summary>Publishes an unsequenced presentation frame on the session's latest run
    /// stream; false when the session has no live stream.</summary>
    bool TryPublishPresentation(AgentSessionId sessionId, string type, JsonElement data);
}

/// <summary>Shows MCP elicitation requests as <c>input.request</c> frames on the attributed
/// session's stream and completes them through the shared input endpoint (ADR 0029 decision
/// 3). Answers carry the terminal action plus a JSON object of field values for accept.</summary>
internal sealed class FrontendElicitationPresenter(IFrontendSessionFramePublisher publisher)
    : IMcpElicitationPresenter
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, TaskCompletionSource<McpElicitationAnswer>> pending = [];

    public ValueTask<McpElicitationAnswer> PresentAsync(
        McpElicitationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessionId = new AgentSessionId(Guid.ParseExact(request.SessionId, "N"));
        var requestId = $"elicit-{Guid.NewGuid().ToString("N")}";
        var completion = new TaskCompletionSource<McpElicitationAnswer>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            pending[requestId] = completion;
        }

        var published = publisher.TryPublishPresentation(
            sessionId,
            "input.request",
            JsonSerializer.SerializeToElement(
                new FrontendInputRequest(
                    requestId,
                    request.Message,
                    request.Password,
                    request.Schema,
                    request.ServerId),
                FrontendJsonContext.Default.FrontendInputRequest));
        if (!published)
        {
            lock (gate)
            {
                pending.Remove(requestId);
            }

            // No live stream to present on (the run already settled): cancel, never hang.
            return ValueTask.FromResult(new McpElicitationAnswer("cancel", null));
        }

        return WaitAsync(requestId, completion, cancellationToken);
    }

    private async ValueTask<McpElicitationAnswer> WaitAsync(
        string requestId,
        TaskCompletionSource<McpElicitationAnswer> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new McpElicitationAnswer("cancel", null);
        }
        finally
        {
            lock (gate)
            {
                pending.Remove(requestId);
            }
        }
    }

    /// <summary>Completes a pending elicitation from the shared input endpoint. The action
    /// defaults to accept (a plain value answer); accept values must be a JSON object of
    /// field values.</summary>
    public bool TryCompleteInput(string requestId, string? action, string value)
    {
        TaskCompletionSource<McpElicitationAnswer>? completion;
        lock (gate)
        {
            if (!pending.Remove(requestId, out completion)) return false;
        }

        var normalizedAction =
            string.Equals(action, "decline", StringComparison.Ordinal) ? "decline" :
            string.Equals(action, "cancel", StringComparison.Ordinal) ? "cancel" :
            "accept";
        string? contentJson = null;
        if (normalizedAction == "accept")
        {
            // A schema'd form must be answered with a JSON object of field values; malformed
            // or non-object answers are client errors, downgraded to cancel, never an exception.
            try
            {
                using var document = JsonDocument.Parse(value);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    completion.TrySetResult(new McpElicitationAnswer("cancel", null));
                    return true;
                }

                contentJson = document.RootElement.GetRawText();
            }
            catch (JsonException)
            {
                completion.TrySetResult(new McpElicitationAnswer("cancel", null));
                return true;
            }
        }

        completion.TrySetResult(new McpElicitationAnswer(normalizedAction, contentJson));
        return true;
    }
}

/// <summary>Deferred composition wrapper: MaieuticsRuntimeConfiguration needs an
/// IMcpElicitationPresenter at construction time, but the real presenter depends on
/// FrontendSessionService, which depends back on the runtime configuration. Deferring the
/// resolution to first use breaks that construction cycle; every caller resolves lazily
/// per elicitation, long after composition has settled.</summary>
internal sealed class DeferredElicitationPresenter(IServiceProvider services) : IMcpElicitationPresenter
{
    public ValueTask<McpElicitationAnswer> PresentAsync(
        McpElicitationRequest request,
        CancellationToken cancellationToken)
    {
        return services.GetRequiredService<FrontendElicitationPresenter>()
            .PresentAsync(request, cancellationToken);
    }
}
