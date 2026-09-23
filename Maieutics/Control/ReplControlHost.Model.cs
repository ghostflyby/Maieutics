using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Permissions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace Maieutics.Control;

/// <summary>The model-orchestration surface's kernel-side dependencies (ADR 0031): the live
/// session registry, the Deno-process-to-owner-session resolver, the session-level subagent
/// configuration and tool set for detached spawns, and the permission registry that scopes
/// spawned children under their owning session.</summary>
/// <summary>Every dependency is a lazy factory resolved at request time: the surface is
/// constructed during composition-root registration, and eager resolution here re-enters the
/// container while singletons it transitively needs are still being constructed — the startup
/// deadlock class (the published-binary smoke catches it; sample the hung process to see the
/// re-entrant resolution chain).</summary>
internal sealed record ModelOrchestrationSurface(
    Func<Maieutics.Commands.MaieuticsAgentSessionManager> AgentSessions,
    Func<string, AgentSessionId?> ResolveOwnerSession,
    Func<AgentSessionOptions> BaseSessionOptions,
    Func<IReadOnlyList<AIFunction>> Tools,
    Func<PermissionOverrideRegistry?> Permissions)
{
    /// <summary>The task plane this surface waits on and cancels through: one provider
    /// composes every authority, so the generic /v1/tasks endpoints address all task kinds
    /// uniformly. Resolved lazily at request time.</summary>
    internal required Func<Execution.TaskResourceProvider> TaskResources { get; init; }
}

internal sealed partial class ReplControlHost
{
    private async Task HandleSubagentSpawnAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        if (context.Request.ContentLength is > ReplControlLimits.MaximumInboundMessageBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        SubagentSpawnRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                ReplControlJsonContext.Default.SubagentSpawnRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (request is null ||
            request.Version != EnvelopeVersion ||
            string.IsNullOrWhiteSpace(request.Input))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (orchestration is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var ownerSessionId = ResolveOwnerSessionOrRespond(context, request.SessionId);
        if (ownerSessionId is null) return;
        if (ResolveAgentSession(context, ownerSessionId.Value) is not { } session) return;

        var spec = new AgentSubagentSpec
        {
            Instructions = request.Instructions,
            Input = request.Input,
            Tools = request.Tools
        };
        IAgentSubagentHandle handle;
        try
        {
            // Run-first: while the owning session's run is executing (the model orchestrating
            // from a REPL cell), the child is a run-owned child with the full ADR 0030
            // semantics; otherwise it falls back to a detached, session-scoped child.
            var spawner = session.TryCreateActiveRunSpawner();
            handle = spawner is not null
                ? await spawner.StartChildAsync(spec, cancellationToken).ConfigureAwait(false)
                : await session.SubagentHost.StartDetachedChildAsync(
                      spec,
                      orchestration.BaseSessionOptions(),
                      orchestration.Tools(),
                      cancellationToken)
                  .ConfigureAwait(false);
        }
        catch (AgentSubagentBudgetExceededException exception)
        {
            await WriteSubagentErrorAsync(
                context, StatusCodes.Status409Conflict, "agent_subagent_budget_exhausted",
                exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (ArgumentException exception)
        {
            await WriteSubagentErrorAsync(
                context, StatusCodes.Status400BadRequest, "agent_spawn_invalid_arguments",
                exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception)
        {
            await WriteSubagentErrorAsync(
                context, StatusCodes.Status409Conflict, "agent_subagent_disabled",
                exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        orchestration.Permissions()?.RegisterChildScope(handle.SessionId, ownerSessionId.Value);
        await WriteJsonAsync(context, new SubagentSpawnedPayload(
            handle.SessionId.Value.ToString("N"),
            handle.RunId.Value.ToString("N"),
            Execution.AgentTaskResourceSource.ComposeUri(ownerSessionId.Value, handle.RunId),
            "working"), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSubagentWaitAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        if (orchestration is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var runIdText = context.Request.RouteValues["runId"]?.ToString();
        if (!Guid.TryParseExact(runIdText, "N", out var runIdValue))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var ownerSessionId = ResolveOwnerSessionOrRespond(context, context.Request.Query["session"].ToString());
        if (ownerSessionId is null) return;
        if (ResolveAgentSession(context, ownerSessionId.Value) is not { } session) return;

        var timeout = ReadBoundedTimeout(context) ?? TimeSpan.FromSeconds(60);
        try
        {
            var result = await session.SubagentHost
                .WaitChildByIdAsync(new AgentRunId(runIdValue), timeout, cancellationToken)
                .ConfigureAwait(false);
            await WriteSubagentResultAsync(context, ownerSessionId.Value, result, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AgentSubagentNotFoundException exception)
        {
            await WriteSubagentErrorAsync(
                context, StatusCodes.Status404NotFound, "resource_not_found",
                exception.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentSubagentWaitTimeoutException)
        {
            await WriteSubagentErrorAsync(
                context, StatusCodes.Status408RequestTimeout, "task_wait_timeout",
                $"The subagent run '{runIdText}' did not reach a terminal state within {timeout}.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleSubagentCancelAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        if (orchestration is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var runIdText = context.Request.RouteValues["runId"]?.ToString();
        if (!Guid.TryParseExact(runIdText, "N", out var runIdValue))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var ownerSessionId = ResolveOwnerSessionOrRespond(context, context.Request.Query["session"].ToString());
        if (ownerSessionId is null) return;
        if (ResolveAgentSession(context, ownerSessionId.Value) is not { } session) return;

        try
        {
            var result = await session.SubagentHost
                .CancelChildByIdAsync(new AgentRunId(runIdValue), cancellationToken)
                .ConfigureAwait(false);
            await WriteSubagentResultAsync(context, ownerSessionId.Value, result, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AgentSubagentNotFoundException exception)
        {
            await WriteSubagentErrorAsync(
                context, StatusCodes.Status404NotFound, "resource_not_found",
                exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits on any task:// URI (bounded): the general task-plane addressing form
    /// (ADR 0031). Ownership is not required for waiting — the plane keeps read visibility —
    /// but the caller still presents its REPL session for peer attribution.</summary>
    private async Task HandleTaskWaitAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        if (orchestration is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var uri = context.Request.Query["uri"].ToString();
        // task://{authority}/...: the authority varies per kind, so validate the scheme.
        if (!Execution.ResourceRegistry.TryParseUri(uri, out var parsed) ||
            !string.Equals(parsed.Scheme, "task", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var timeout = ReadBoundedTimeout(context) ?? TimeSpan.FromSeconds(60);
        try
        {
            var snapshot = await orchestration.TaskResources()
                .WaitTaskAsync(uri, timeout, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(context, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await WriteSubagentErrorAsync(
                context, StatusCodes.Status408RequestTimeout, "task_wait_timeout",
                $"The task '{uri}' did not reach a terminal state within {timeout}.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Execution.ResourceException exception)
        {
            await WriteSubagentErrorAsync(
                context, exception.Code switch
                {
                    "resource_not_found" => StatusCodes.Status404NotFound,
                    "resource_invalid_uri" => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status502BadGateway
                }, exception.Code, exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels any task:// URI owned by the calling session: the request's presented
    /// session resolves to the owning Agent session, which the task plane requires for
    /// cancellation (denials win; other sessions' tasks are not cancellable).</summary>
    private async Task HandleTaskCancelAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        if (orchestration is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var body = await JsonSerializer.DeserializeAsync(
            context.Request.Body,
            ReplControlJsonContext.Default.TaskCancelRequest,
            cancellationToken).ConfigureAwait(false);
        var uri = body?.Uri;
        if (string.IsNullOrWhiteSpace(uri) ||
            !Execution.ResourceRegistry.TryParseUri(uri, out var parsed) ||
            !string.Equals(parsed.Scheme, "task", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var ownerSessionId = ResolveOwnerSessionOrRespond(context, body!.SessionId);
        if (ownerSessionId is null) return;

        try
        {
            var snapshot = await orchestration.TaskResources()
                .CancelTaskAsync(uri, ownerSessionId.Value, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(context, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Execution.ResourceException exception)
        {
            await WriteSubagentErrorAsync(
                context, exception.Code switch
                {
                    "resource_not_found" => StatusCodes.Status404NotFound,
                    "task_forbidden" => StatusCodes.Status403Forbidden,
                    "task_cancel_failed" => StatusCodes.Status502BadGateway,
                    _ => StatusCodes.Status502BadGateway
                }, exception.Code, exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private AgentSessionId? ResolveOwnerSessionOrRespond(HttpContext context, string? presentedSessionId)
    {
        var replSessionId = ResolveRequestSessionId(context, presentedSessionId);
        if (replSessionId is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return null;
        }

        var owner = orchestration!.ResolveOwnerSession(replSessionId);
        if (owner is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return null;
        }

        return owner;
    }

    private Maieutics.Agent.AgentSession? ResolveAgentSession(HttpContext context, AgentSessionId ownerSessionId)
    {
        if (orchestration!.AgentSessions().Resolve(ownerSessionId) is Maieutics.Agent.AgentSession session)
            return session;

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return null;
    }

    private static TimeSpan? ReadBoundedTimeout(HttpContext context)
    {
        var raw = context.Request.Query["timeoutMs"].ToString();
        return int.TryParse(raw, out var milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;
    }

    private async Task WriteSubagentResultAsync(
        HttpContext context,
        AgentSessionId ownerSessionId,
        AgentSubagentResult result,
        CancellationToken cancellationToken)
    {
        var status = result.Status switch
        {
            AgentSubagentStatus.Completed => "complete",
            AgentSubagentStatus.Failed => "fail",
            _ => "cancel"
        };
        await WriteJsonAsync(context, new SubagentResultPayload(
            result.SessionId.Value.ToString("N"),
            result.RunId.Value.ToString("N"),
            status,
            result.Report,
            result.Truncated,
            result.Usage is null
                ? null
                : new SubagentUsagePayload(
                    (int?)result.Usage.InputTokenCount,
                    (int?)result.Usage.OutputTokenCount,
                    (int?)result.Usage.TotalTokenCount)), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteJsonAsync(HttpContext context, Execution.TaskResourceSnapshot value, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            value,
            Execution.TaskResourceJsonContext.Default.TaskResourceSnapshot,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteSubagentErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        await WriteResourceErrorAsync(context, statusCode, code, message, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteJsonAsync(HttpContext context, SubagentSpawnedPayload value, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            value,
            ReplControlJsonContext.Default.SubagentSpawnedPayload,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteJsonAsync(HttpContext context, SubagentResultPayload value, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            value,
            ReplControlJsonContext.Default.SubagentResultPayload,
            cancellationToken).ConfigureAwait(false);
    }
}
