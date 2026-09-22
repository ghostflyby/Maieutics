using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;
using Microsoft.Extensions.AI;

namespace Maieutics.Execution;

/// <summary>The model-facing subagent and task-control tools (ADR 0030). <c>agent_spawn</c>
/// starts a tool-style child run — blocking by default, or returning its task:// handle
/// immediately for in-turn fan-out — while <c>task_wait</c> and <c>task_cancel</c> address any
/// live task of the calling session through the task plane's uniform control contract. Spawn
/// permission parameters are requests, not authority: this adapter narrows the child to the
/// parent-registered tools it names, and the permission layer clamps further (ADR 0030
/// decision 3).</summary>
internal sealed class AgentSubagentFunctions
{
    private const int DefaultWaitMilliseconds = 60_000;

    private static readonly JsonSerializerOptions SerializerOptions =
        SubagentFunctionJsonContext.Default.Options;

    private readonly TaskResourceProvider taskResources;

    public AgentSubagentFunctions(TaskResourceProvider taskResources)
    {
        this.taskResources = taskResources ?? throw new ArgumentNullException(nameof(taskResources));
        Functions =
        [
            CreateFunction(
                (Func<string, AIFunctionArguments, string?, string[]?, bool?, CancellationToken, ValueTask<JsonElement>>)SpawnAsync,
                "agent_spawn",
                "Spawns a subagent: a separate Agent run with its own context window that works on one " +
                "composed task and returns one report. The child inherits this session's model and may use " +
                "only the parent-registered tools named in 'tools' (omit for all parent tools). By default " +
                "the call waits and returns the child's report; pass wait=false to return a task://agent/... " +
                "handle immediately, keep working, and later await it with task_wait or cancel it with " +
                "task_cancel. The child cannot ask the user questions — put everything it needs into 'input'. " +
                "A failed child is reported as a typed error the parent can retry from."),
            CreateFunction(
                (Func<string, AIFunctionArguments, int?, CancellationToken, ValueTask<JsonElement>>)WaitAsync,
                "task_wait",
                "Waits for a task:// resource of this process to reach a terminal status (complete, fail, or " +
                "cancel) and returns its snapshot: a subagent run's report, or a timed-out terminal one-shot's " +
                "exit state. Pass timeoutMs to bound the wait (default 60000); on timeout the task keeps " +
                "running and the call fails with a recoverable task_wait_timeout error."),
            CreateFunction(
                (Func<string, AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)CancelAsync,
                "task_cancel",
                "Cancels a task:// resource of the current session — a running subagent run or a timed-out " +
                "terminal one-shot — waits for it to settle, and returns its final snapshot. Idempotent on " +
                "already-terminal tasks. Only tasks of the current session can be cancelled.")
        ];
    }

    internal IReadOnlyList<AIFunction> Functions { get; }

    [Description("Spawns a subagent child run for one composed task.")]
    private async ValueTask<JsonElement> SpawnAsync(
        [Description("The composed task for the subagent. Include every fact and file reference it needs; it cannot ask questions.")]
        string input,
        AIFunctionArguments arguments,
        [Description("System instructions for the subagent. The parent's instructions are not inherited.")]
        string? instructions = null,
        [Description("Parent-registered tool names the subagent may use. Omit to allow all parent tools; an empty list means no tools.")]
        string[]? tools = null,
        [Description("Wait for the subagent to finish and return its report (default true). With false, return the task:// handle immediately.")]
        bool? wait = null,
        CancellationToken cancellationToken = default)
    {
        var context = AgentToolContext.GetRequired(arguments);
        var spawner = AgentSubagentContext.GetRequired(arguments);
        IAgentSubagentHandle handle;
        try
        {
            handle = await spawner.StartChildAsync(
                new AgentSubagentSpec
                {
                    Instructions = instructions,
                    Input = input,
                    Tools = tools
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AgentSubagentBudgetExceededException exception)
        {
            throw new AgentToolException("agent_subagent_budget_exhausted", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            throw new AgentToolException("agent_subagent_disabled", exception.Message);
        }
        catch (ArgumentException exception)
        {
            throw new AgentToolException("agent_spawn_invalid_arguments", exception.Message);
        }

        if (wait is false)
        {
            return Serialize(new SpawnedValue(
                handle.RunId.Value.ToString("N"),
                AgentTaskResourceSource.ComposeUri(context.SessionId, handle.RunId)));
        }

        var result = await handle.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result.Status == AgentSubagentStatus.Failed)
            throw new AgentToolException(
                "task_failed",
                "The subagent run failed before producing a report. Retry with a narrower or better-scoped task.");

        return Serialize(new TaskValue(
            AgentTaskResourceSource.ComposeUri(context.SessionId, handle.RunId),
            AgentTaskResourceSource.AgentAuthority,
            MapStatus(result.Status),
            result.Report,
            result.Truncated,
            ToUsage(result.Usage),
            Terminal: null));
    }

    [Description("Waits for a task:// resource to reach a terminal status and returns its snapshot.")]
    private async ValueTask<JsonElement> WaitAsync(
        [Description("A task://agent/... or task://terminal/... URI, as returned by agent_spawn or terminal_run.")]
        string uri,
        AIFunctionArguments arguments,
        [Description("How long to wait, in milliseconds. Default 60000; the task keeps running past a timeout.")]
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        _ = AgentToolContext.GetRequired(arguments);
        var timeout = timeoutMs is { } milliseconds && milliseconds != 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : TimeSpan.FromMilliseconds(DefaultWaitMilliseconds);
        if (timeout <= TimeSpan.Zero)
            throw new AgentToolException(
                "task_invalid_arguments",
                "timeoutMs must be a positive number of milliseconds.");

        try
        {
            var snapshot = await taskResources.WaitTaskAsync(uri, timeout, cancellationToken).ConfigureAwait(false);
            return Serialize(ToValue(snapshot));
        }
        catch (ResourceException exception)
        {
            throw new AgentToolException(exception.Code, exception.Message);
        }
    }

    [Description("Cancels a task:// resource of the current session and returns its final snapshot.")]
    private async ValueTask<JsonElement> CancelAsync(
        [Description("A task://agent/... or task://terminal/... URI owned by the current session.")]
        string uri,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var context = AgentToolContext.GetRequired(arguments);
        try
        {
            var snapshot = await taskResources
                .CancelTaskAsync(uri, context.SessionId, cancellationToken)
                .ConfigureAwait(false);
            return Serialize(ToValue(snapshot));
        }
        catch (ResourceException exception)
        {
            throw new AgentToolException(exception.Code, exception.Message);
        }
    }

    private static TaskValue ToValue(TaskResourceSnapshot snapshot)
    {
        return new TaskValue(
            snapshot.Uri,
            snapshot.Kind,
            snapshot.Status,
            snapshot.Agent?.Report,
            snapshot.Agent?.ReportTruncated,
            snapshot.Agent?.Usage is { } usage
                ? new SubagentUsageValue(usage.InputTokens, usage.OutputTokens, usage.TotalTokens)
                : null,
            snapshot.Terminal is { } terminal
                ? new TerminalTaskValue(terminal.SessionId, terminal.State, terminal.ExitCode)
                : null);
    }

    private static string MapStatus(AgentSubagentStatus status)
    {
        return status switch
        {
            AgentSubagentStatus.Completed => "complete",
            AgentSubagentStatus.Failed => "fail",
            _ => "cancel"
        };
    }

    private static SubagentUsageValue? ToUsage(UsageDetails? usage)
    {
        return usage is null
            ? null
            : new SubagentUsageValue(
                (int?)usage.InputTokenCount,
                (int?)usage.OutputTokenCount,
                (int?)usage.TotalTokenCount);
    }

    private static JsonElement Serialize(SpawnedValue value)
    {
        return JsonSerializer.SerializeToElement(value, SubagentFunctionJsonContext.Default.SpawnedValue);
    }

    private static JsonElement Serialize(TaskValue value)
    {
        return JsonSerializer.SerializeToElement(value, SubagentFunctionJsonContext.Default.TaskValue);
    }

    private static AIFunction CreateFunction(Delegate method, string name, string description)
    {
        return AIFunctionFactory.Create(
            method,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                SerializerOptions = SerializerOptions
            });
    }
}

/// <summary>The handle returned by a non-waiting spawn: the child run identifier and its
/// task-plane address.</summary>
internal sealed record SpawnedValue(string RunId, string TaskUri);

/// <summary>The terminal snapshot of one task, as returned by task_wait, task_cancel, and a
/// waiting agent_spawn.</summary>
internal sealed record TaskValue(
    string Task,
    string Kind,
    string Status,
    string? Report,
    bool? ReportTruncated,
    SubagentUsageValue? Usage,
    TerminalTaskValue? Terminal);

/// <summary>Provider-reported token usage of one settled subagent run.</summary>
internal sealed record SubagentUsageValue(int? Input, int? Output, int? Total);

/// <summary>Terminal one-shot state in a task snapshot.</summary>
internal sealed record TerminalTaskValue(string SessionId, string State, int? ExitCode);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SpawnedValue))]
[JsonSerializable(typeof(TaskValue))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(JsonElement?))]
internal sealed partial class SubagentFunctionJsonContext : JsonSerializerContext;
