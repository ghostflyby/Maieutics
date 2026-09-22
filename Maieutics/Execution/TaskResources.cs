using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;

namespace Maieutics.Execution;

/// <summary>One authority of the task:// resource space, owned by the subsystem that mints
/// and supervises the tasks under it (ADR 0028). The entry contract is read, wait, and cancel
/// (ADR 0030 decision 5): a source that cannot support waiting and cancellation must not
/// register an authority.</summary>
internal interface ITaskResourceSource
{
    /// <summary>Gets the authority this source owns: task URIs of the form
    /// <c>task://{authority}/...</c>.</summary>
    string Authority { get; }

    /// <summary>Lists the source's live task resources for the <c>list_resources</c> catalog.</summary>
    ImmutableArray<ResourceCatalogEntry> ListTasks(CancellationToken cancellationToken);

    /// <summary>Reads one live task snapshot; returns null when the URI names no live task.</summary>
    ValueTask<TaskResourceSnapshot?> ReadTaskAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>Waits until the task reaches a terminal status and returns its terminal
    /// snapshot. Waiting on an already-terminal task returns immediately.</summary>
    /// <exception cref="ResourceException">
    ///     <c>resource_not_found</c> when the URI names no live task;
    ///     <c>task_wait_timeout</c> when the task stays unsettled past the timeout.
    /// </exception>
    ValueTask<TaskResourceSnapshot> WaitTaskAsync(
        Uri uri,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>Requests cooperative cancellation of the task, waits for its termination, and
    /// returns its terminal snapshot. Cancelling an already-terminal task returns its snapshot
    /// unchanged, so the operation is idempotent.</summary>
    /// <exception cref="ResourceException">
    ///     <c>resource_not_found</c> when the URI names no live task;
    ///     <c>task_cancel_failed</c> when the owning subsystem could not settle the task.
    /// </exception>
    ValueTask<TaskResourceSnapshot> CancelTaskAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>The readable state of one task resource. Reading is a fresh snapshot (the plane
/// has no push); lifecycle progression and progress events stay with the owning subsystem
/// (ADR 0028 decision 2).</summary>
/// <param name="Uri">The task URI that was read.</param>
/// <param name="Kind">The owning authority (for example "terminal" or "agent").</param>
/// <param name="Status">One of <c>working</c>, <c>complete</c>, <c>fail</c>, <c>cancel</c> —
/// the MCP tasks extension's lifecycle vocabulary.</param>
/// <param name="Terminal">Terminal one-shot detail; null for other kinds.</param>
/// <param name="Agent">Subagent run detail; null for other kinds.</param>
internal sealed record TaskResourceSnapshot(
    string Uri,
    string Kind,
    string Status,
    TerminalTaskDetail? Terminal,
    AgentSubagentDetail? Agent = null);

/// <summary>Terminal one-shot detail carried in a task snapshot.</summary>
/// <param name="AgentSessionId">The owning Agent session, as written into the task URI.</param>
/// <param name="SessionId">The terminal session id, usable with the terminal_* tools.</param>
/// <param name="State">The terminal session's wire state ("running", "completed", ...).</param>
/// <param name="ExitCode">The child's exit code once the one-shot settled.</param>
internal sealed record TerminalTaskDetail(
    string AgentSessionId,
    string SessionId,
    string State,
    int? ExitCode);

/// <summary>Subagent run detail carried in a task snapshot (ADR 0030 decisions 4 and 9).
/// Bounded by design: a truncated report preview and usage counts — never the child
/// transcript.</summary>
/// <param name="AgentSessionId">The owning Agent session, as written into the task URI.</param>
/// <param name="RunId">The child run identifier.</param>
/// <param name="Report">The final assistant text once the child completed, truncated to the
/// preview budget.</param>
/// <param name="ReportTruncated">Whether the preview cut the report short.</param>
/// <param name="Usage">Provider-reported token usage once the child settled.</param>
internal sealed record AgentSubagentDetail(
    string AgentSessionId,
    string RunId,
    string? Report,
    bool ReportTruncated,
    SubagentTaskUsage? Usage);

/// <summary>Provider-reported token usage of one settled subagent run.</summary>
internal sealed record SubagentTaskUsage(int? InputTokens, int? OutputTokens, int? TotalTokens);

/// <summary>Serves the reserved task:// scheme (ADR 0028): the agent-visible namespace of
/// running work it spawned, read as fresh snapshot bodies through the ADR 0026 plane, and
/// waited on and cancelled through the uniform control contract (ADR 0030 decision 5). The
/// owning subsystems keep their richer typed tools alongside; the plane's control surface is
/// deliberately narrow.</summary>
internal sealed class TaskResourceProvider : IResourceProvider, IResourceCatalogProvider
{
    internal const string Scheme = "task";

    private const string MimeType = "application/json";

    private readonly IReadOnlyDictionary<string, ITaskResourceSource> sourcesByAuthority;
    private readonly IReadOnlyList<ResourceClaim> claims;

    public TaskResourceProvider(IEnumerable<ITaskResourceSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var materialized = sources.ToArray();
        if (materialized.Length == 0)
            throw new ArgumentException("At least one task resource source is required.", nameof(sources));

        sourcesByAuthority = materialized.ToDictionary(
            static source => source.Authority,
            static source => source,
            StringComparer.OrdinalIgnoreCase);
        claims = [.. materialized.Select(source => new ResourceClaim(Scheme, source.Authority))];
    }

    public string Id => "task";

    public ResourceProviderClass Class => ResourceProviderClass.BuiltIn;

    public IReadOnlyList<ResourceClaim> Claims => claims;

    public async ValueTask<ResourceReadResult> ReadAsync(
        string uri,
        ResourceReadRequest request,
        CancellationToken cancellationToken)
    {
        var parsed = ParseUri(uri);
        var source = ResolveSource(parsed);
        var snapshot = await source.ReadTaskAsync(parsed, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            throw new ResourceException(
                "resource_not_found",
                $"The task resource '{uri}' does not exist.");

        var body = JsonSerializer.SerializeToUtf8Bytes(snapshot, TaskResourceJsonContext.Default.TaskResourceSnapshot);
        if (body.Length > request.MaxBytes)
            throw new ResourceException(
                "resource_too_large",
                $"The task snapshot exceeds the {request.MaxBytes} byte read limit.");

        return new ResourceReadResult(new MemoryStream(body, writable: false), MimeType);
    }

    /// <summary>Waits for one task's terminal snapshot. Waiting keeps read-plane visibility:
    /// any session may wait on any task it can name.</summary>
    public async ValueTask<TaskResourceSnapshot> WaitTaskAsync(
        string uri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
            throw new ResourceException(
                "resource_invalid_uri",
                "The task wait timeout must be positive or infinite.");

        var parsed = ParseUri(uri);
        var source = ResolveSource(parsed);
        try
        {
            return await source.WaitTaskAsync(parsed, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new ResourceException(
                "task_wait_timeout",
                $"The task '{uri}' did not reach a terminal state within {timeout}.",
                exception);
        }
    }

    /// <summary>Cancels one task and returns its terminal snapshot. Cancellation is
    /// session-scoped (ADR 0030 decision 5): both current authorities embed the owning Agent
    /// session as the URI's first path segment, and a caller may only cancel tasks of its own
    /// session. A URI that embeds no session id is therefore uncancellable — denials win.</summary>
    public async ValueTask<TaskResourceSnapshot> CancelTaskAsync(
        string uri,
        AgentSessionId callerSessionId,
        CancellationToken cancellationToken)
    {
        var parsed = ParseUri(uri);
        var source = ResolveSource(parsed);
        if (!TryGetOwnedSession(parsed, out var ownerSessionId) || ownerSessionId != callerSessionId)
            throw new ResourceException(
                "task_forbidden",
                $"The task '{uri}' does not belong to the calling session.");

        return await source.CancelTaskAsync(parsed, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ImmutableArray<ResourceCatalogEntry>> ListAsync(CancellationToken cancellationToken)
    {
        var entries = ImmutableArray.CreateBuilder<ResourceCatalogEntry>();
        foreach (var source in sourcesByAuthority.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.AddRange(source.ListTasks(cancellationToken));
        }

        return ValueTask.FromResult(entries.ToImmutable());
    }

    private static Uri ParseUri(string uri)
    {
        if (!ResourceRegistry.TryParseUri(uri, out var parsed))
            throw new ResourceException("resource_invalid_uri", "The value must be an absolute task URI.");
        return parsed;
    }

    private ITaskResourceSource ResolveSource(Uri parsed)
    {
        if (!sourcesByAuthority.TryGetValue(parsed.Host, out var source))
            throw new ResourceException(
                "resource_not_found",
                $"No task source owns the authority '{parsed.Host}'.");
        return source;
    }

    private static bool TryGetOwnedSession(Uri uri, out AgentSessionId ownerSessionId)
    {
        ownerSessionId = default;
        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 1 && AgentSessionId.TryParse(segments[0], out ownerSessionId);
    }
}

/// <summary>Exposes the registry's timed-out one-shot sessions as task://terminal resources
/// (ADR 0028 decision 3). A one-shot stays readable until it is closed; the session id used
/// by the terminal_* tools remains the richer control handle, while the plane's uniform wait
/// and cancel resolve through the registry's one-shot lifecycle (ADR 0030 decision 5).</summary>
internal sealed class TerminalTaskResourceSource(TerminalRegistry registry) : ITaskResourceSource
{
    internal const string TerminalAuthority = "terminal";

    public string Authority => TerminalAuthority;

    public ImmutableArray<ResourceCatalogEntry> ListTasks(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return
        [
            .. registry.ListOneShotTasks().Select(handle => new ResourceCatalogEntry(
                "task",
                ComposeUri(handle.OwnerSessionId, handle.SessionId),
                $"terminal one-shot {handle.SessionId[..8]}",
                $"A timed-out one-shot terminal command in state '{handle.State}'.",
                "application/json",
                "task",
                null))
        ];
    }

    public ValueTask<TaskResourceSnapshot?> ReadTaskAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = ParseTaskUri(uri);
        if (parsed is not { } target)
            return ValueTask.FromResult<TaskResourceSnapshot?>(null);

        var handle = registry.TryGetOneShotTask(target.ownerSessionId, target.sessionId);
        if (handle is null)
            return ValueTask.FromResult<TaskResourceSnapshot?>(null);

        return ValueTask.FromResult<TaskResourceSnapshot?>(ToSnapshot(handle));
    }

    public async ValueTask<TaskResourceSnapshot> WaitTaskAsync(
        Uri uri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var target = RequireTaskUri(uri);
        var handle = await registry
            .WaitOneShotAsync(target.ownerSessionId, target.sessionId, timeout, cancellationToken)
            .ConfigureAwait(false);
        return ToSnapshot(handle);
    }

    public async ValueTask<TaskResourceSnapshot> CancelTaskAsync(Uri uri, CancellationToken cancellationToken)
    {
        var target = RequireTaskUri(uri);
        TerminalTaskHandle handle;
        try
        {
            handle = await registry
                .CancelOneShotAsync(target.ownerSessionId, target.sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AgentToolException exception)
        {
            throw new ResourceException(
                "task_cancel_failed",
                $"The task '{uri}' could not be cancelled: {exception.Message}",
                exception);
        }

        return ToSnapshot(handle);
    }

    internal static string ComposeUri(AgentSessionId ownerSessionId, string sessionId)
    {
        return $"{TaskResourceProvider.Scheme}://{TerminalAuthority}/{ownerSessionId.Value.ToString("N")}/{sessionId}";
    }

    internal static string MapStatus(TerminalTaskHandle handle)
    {
        return handle.State switch
        {
            "completed" => handle.ExitCode == 0 ? "complete" : "fail",
            "faulted" => "fail",
            "closing" or "closed" => "cancel",
            _ => "working"
        };
    }

    private static (AgentSessionId ownerSessionId, string sessionId)? ParseTaskUri(Uri uri)
    {
        // task://terminal/{agentSessionId}/{terminalSessionId}
        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !AgentSessionId.TryParse(segments[0], out var ownerSessionId))
            return null;

        return (ownerSessionId, segments[1]);
    }

    private static (AgentSessionId ownerSessionId, string sessionId) RequireTaskUri(Uri uri)
    {
        return ParseTaskUri(uri) ??
               throw new ResourceException(
                   "resource_not_found",
                   $"The task resource '{uri}' does not exist.");
    }

    private static TaskResourceSnapshot ToSnapshot(TerminalTaskHandle handle)
    {
        return new TaskResourceSnapshot(
            ComposeUri(handle.OwnerSessionId, handle.SessionId),
            TerminalAuthority,
            MapStatus(handle),
            new TerminalTaskDetail(
                handle.OwnerSessionId.ToString(),
                handle.SessionId,
                handle.State,
                handle.ExitCode));
    }
}

/// <summary>Exposes live subagent runs as task://agent resources (ADR 0030 decision 4): the
/// model addresses a child run it spawned by session and run identifier, with the plane's
/// uniform wait and cancel resolving onto the child run's own lifetime. Snapshots are bounded
/// — lifecycle status, a truncated report preview, and usage — and never carry the child
/// transcript. Children vanish from the plane when their parent run joins them, and later
/// turns refetch reports from the parent transcript instead.</summary>
internal sealed class AgentTaskResourceSource : ITaskResourceSource
{
    internal const string AgentAuthority = "agent";

    private const int ReportPreviewCharacters = 2_000;

    private readonly Func<AgentSessionId, AgentSession?> resolveSession;
    private readonly Func<IReadOnlyList<AgentSessionId>> listLiveSessions;

    public AgentTaskResourceSource(
        Func<AgentSessionId, AgentSession?> resolveSession,
        Func<IReadOnlyList<AgentSessionId>> listLiveSessions)
    {
        this.resolveSession = resolveSession ?? throw new ArgumentNullException(nameof(resolveSession));
        this.listLiveSessions = listLiveSessions ?? throw new ArgumentNullException(nameof(listLiveSessions));
    }

    public string Authority => AgentAuthority;

    public ImmutableArray<ResourceCatalogEntry> ListTasks(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = ImmutableArray.CreateBuilder<ResourceCatalogEntry>();
        foreach (var sessionId in listLiveSessions())
        {
            var session = resolveSession(sessionId);
            if (session is null)
                continue;

            foreach (var record in session.SubagentHost.ListChildren())
                entries.Add(new ResourceCatalogEntry(
                    "task",
                    ComposeUri(sessionId, record.RunId),
                    $"subagent run {record.RunId.Value.ToString("N")[..8]}",
                    record.Completion.IsCompleted
                        ? "A settled subagent run of this process."
                        : "A running subagent run of this process.",
                    "application/json",
                    "task",
                    null));
        }

        return entries.ToImmutable();
    }

    public ValueTask<TaskResourceSnapshot?> ReadTaskAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ParseTaskUri(uri);
        var record = target is null ? null : FindRecord(target.Value);
        if (record is null)
            return ValueTask.FromResult<TaskResourceSnapshot?>(null);

        return ValueTask.FromResult<TaskResourceSnapshot?>(ToSnapshot(target!.Value.sessionId, record));
    }

    public async ValueTask<TaskResourceSnapshot> WaitTaskAsync(
        Uri uri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var target = RequireTaskUri(uri);
        var record = RequireRecord(target);
        var wait = record.Completion;
        if (timeout != Timeout.InfiniteTimeSpan)
            wait = wait.WaitAsync(timeout, cancellationToken);
        await wait.ConfigureAwait(false);
        return ToSnapshot(target.sessionId, record);
    }

    public async ValueTask<TaskResourceSnapshot> CancelTaskAsync(Uri uri, CancellationToken cancellationToken)
    {
        var target = RequireTaskUri(uri);
        var record = RequireRecord(target);
        if (!record.Completion.IsCompleted)
            await record.Run.CancelAsync(cancellationToken).ConfigureAwait(false);
        await record.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        return ToSnapshot(target.sessionId, record);
    }

    internal static string ComposeUri(AgentSessionId sessionId, AgentRunId runId)
    {
        return $"{TaskResourceProvider.Scheme}://{AgentAuthority}/{sessionId}/{runId.Value.ToString("N")}";
    }

    private AgentSubagentHost.ChildRecord? FindRecord((AgentSessionId sessionId, AgentRunId runId) target)
    {
        var session = resolveSession(target.sessionId);
        return session?.SubagentHost.FindChild(target.runId);
    }

    private AgentSubagentHost.ChildRecord RequireRecord((AgentSessionId sessionId, AgentRunId runId) target)
    {
        return FindRecord(target) ??
               throw new ResourceException(
                   "resource_not_found",
                   $"No live subagent run matches '{target.runId.Value.ToString("N")}' on session '{target.sessionId}'.");
    }

    private static (AgentSessionId sessionId, AgentRunId runId)? ParseTaskUri(Uri uri)
    {
        // task://agent/{agentSessionId}/{subagentRunId}
        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 ||
            !AgentSessionId.TryParse(segments[0], out var sessionId) ||
            !Guid.TryParseExact(segments[1], "N", out var runIdValue) ||
            runIdValue == Guid.Empty)
            return null;

        return (sessionId, new AgentRunId(runIdValue));
    }

    private static (AgentSessionId sessionId, AgentRunId runId) RequireTaskUri(Uri uri)
    {
        return ParseTaskUri(uri) ??
               throw new ResourceException(
                   "resource_not_found",
                   $"The task resource '{uri}' does not exist.");
    }

    private static TaskResourceSnapshot ToSnapshot(AgentSessionId sessionId, AgentSubagentHost.ChildRecord record)
    {
        // The mapping task never faults, so a completed record's Result is always available;
        // reading it here is non-blocking by construction.
        var result = record.Completion.IsCompleted ? record.Completion.Result : null;
        var status = result is null
            ? "working"
            : result.Status switch
            {
                AgentSubagentStatus.Completed => "complete",
                AgentSubagentStatus.Failed => "fail",
                _ => "cancel"
            };
        var usage = result?.Usage is null
            ? null
            : new SubagentTaskUsage(
                (int?)result.Usage.InputTokenCount,
                (int?)result.Usage.OutputTokenCount,
                (int?)result.Usage.TotalTokenCount);
        var report = result?.Report;
        var truncated = false;
        if (report is { Length: > ReportPreviewCharacters } overlong)
        {
            report = overlong[..ReportPreviewCharacters];
            truncated = true;
        }

        return new TaskResourceSnapshot(
            ComposeUri(sessionId, record.RunId),
            AgentAuthority,
            status,
            Terminal: null,
            new AgentSubagentDetail(
                sessionId.ToString(),
                record.RunId.Value.ToString("N"),
                report,
                truncated,
                usage));
    }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TaskResourceSnapshot))]
[JsonSerializable(typeof(AgentSubagentDetail))]
[JsonSerializable(typeof(SubagentTaskUsage))]
internal sealed partial class TaskResourceJsonContext : JsonSerializerContext;
