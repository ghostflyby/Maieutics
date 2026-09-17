using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Maieutics.Mcp;

/// <summary>Wraps one discovered MCP tool so the server's progress notifications reach the owning
/// Agent tool call as bounded progress content (the same channel built-in tools report through).
/// The wrapper is inert outside a Maieutics tool call: script-tool invocations carry no
/// <see cref="AgentToolContext"/>, so those calls keep the plain transport behavior.</summary>
internal sealed class ProgressReportingAIFunction(McpClientTool tool, string serverId, ILogger logger)
    : DelegatingAIFunction(tool)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Context?.TryGetValue(typeof(AgentToolContext), out var value) != true ||
            value is not AgentToolContext toolContext)
            return await tool.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);

        // WithProgress copies the tool with one progress sink attached; a per-invocation copy
        // binds the sink to this call's context (the discovered tool instance is shared across
        // runs), and the SDK sends a progress token only while a sink is attached.
        var forwarder = new McpToolProgressForwarder(toolContext, serverId, logger);
        var result = await tool.WithProgress(forwarder)
            .InvokeAsync(arguments, cancellationToken)
            .ConfigureAwait(false);

        // Drain notifications the SDK already dispatched before returning: their frames then
        // reach the run channel before tool.finished. The SDK's per-call progress registration
        // is disposed when the response is processed, and each inbound message is processed
        // independently, so a notification racing the response can be dropped by the SDK
        // (observed deterministically on slow CI runners) — draining narrows that window to
        // notifications simultaneous with the response, whose progress is superseded anyway.
        await forwarder.FlushAsync().ConfigureAwait(false);
        return result;
    }
}

/// <summary>Forwards one call's server progress notifications into the Agent tool-call context.
/// Reports are serialized (never concurrent writes, so run sequence allocation stays
/// consistent) in the order the SDK delivers them; the first failure (progress limit reached,
/// run completed, cancellation) silences the sink instead of throwing out of <see cref="Report"/>
/// — the SDK invokes it on its receive loop, which must never observe a throwing report. Chain
/// links start outside the sink lock and complete their successor's task without faulting, so
/// one bad notification can never break the chain.</summary>
internal sealed class McpToolProgressForwarder(
    AgentToolContext context,
    string serverId,
    ILogger logger) : IProgress<ProgressNotificationValue>
{
    private readonly Lock gate = new();
    private Task pendingTask = Task.CompletedTask;
    private long version;
    private bool silenced;

    public void Report(ProgressNotificationValue value)
    {
        Task antecedent;
        var successor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (silenced) return;
            antecedent = pendingTask;
            pendingTask = successor.Task;
            version++;
        }

        _ = RunLinkAsync(value, antecedent, successor);
    }

    /// <summary>Waits until every notification delivered before this call has been forwarded.</summary>
    internal async ValueTask FlushAsync()
    {
        while (true)
        {
            long observed;
            Task tail;
            lock (gate)
            {
                observed = version;
                tail = pendingTask;
            }

            await tail.ConfigureAwait(false);
            lock (gate)
            {
                if (version == observed) return;
            }
        }
    }

    /// <summary>Runs one link: waits for its predecessor, reports unless the sink was silenced
    /// by an earlier link, and always completes the successor task so the chain keeps draining.</summary>
    private async Task RunLinkAsync(ProgressNotificationValue value, Task antecedent, TaskCompletionSource successor)
    {
        try
        {
            await antecedent.ConfigureAwait(false);
            if (Volatile.Read(ref silenced)) return;

            var payload = new McpToolProgress(value.Progress, value.Total, value.Message);
            var content = new DataContent(
                JsonSerializer.SerializeToUtf8Bytes(payload, McpToolProgressJsonContext.Default.McpToolProgress),
                "application/json");
            await context.ReportProgressAsync(content).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is AgentException or OperationCanceledException or ObjectDisposedException)
        {
            // A late notification after the run completed, the per-call progress budget, or
            // shutdown: expected and terminal for this sink.
            Silence(exception, LogLevel.Debug);
        }
        catch (Exception exception)
        {
            Silence(exception, LogLevel.Warning);
        }
        finally
        {
            successor.TrySetResult();
        }
    }

    private void Silence(Exception exception, LogLevel level)
    {
        lock (gate) silenced = true;
        logger.Log(
            level,
            exception,
            "MCP server {ServerId} progress forwarding stopped after {FailureType}.",
            serverId,
            exception.GetType().Name);
    }
}

/// <summary>The forwarded progress body: one JSON object mirroring the MCP notification
/// fields the server sent, unmodified apart from serialization.</summary>
/// <param name="Progress">The server-reported progress value (percentage or items completed).</param>
/// <param name="Total">The total the progress value counts against, when the server sent one.</param>
/// <param name="Message">The server's human-readable progress message, when present.</param>
internal sealed record McpToolProgress(double? Progress, double? Total, string? Message);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(McpToolProgress))]
internal sealed partial class McpToolProgressJsonContext : JsonSerializerContext;
