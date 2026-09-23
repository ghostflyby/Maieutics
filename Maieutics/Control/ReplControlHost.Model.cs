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
internal sealed record ModelOrchestrationSurface(
    Maieutics.Commands.MaieuticsAgentSessionManager AgentSessions,
    Func<string, AgentSessionId?> ResolveOwnerSession,
    AgentSessionOptions BaseSessionOptions,
    IReadOnlyList<AIFunction> Tools,
    PermissionOverrideRegistry? Permissions);

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
                      orchestration.BaseSessionOptions,
                      orchestration.Tools,
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

        orchestration.Permissions?.RegisterChildScope(handle.SessionId, ownerSessionId.Value);
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
        if (orchestration!.AgentSessions.Resolve(ownerSessionId) is Maieutics.Agent.AgentSession session)
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
