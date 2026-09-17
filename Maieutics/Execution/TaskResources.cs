using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;

namespace Maieutics.Execution;

/// <summary>One authority of the task:// resource space, owned by the subsystem that mints
/// and supervises the tasks under it (ADR 0028).</summary>
internal interface ITaskResourceSource
{
    /// <summary>Gets the authority this source owns: task URIs of the form
    /// <c>task://{authority}/...</c>.</summary>
    string Authority { get; }

    /// <summary>Lists the source's live task resources for the <c>list_resources</c> catalog.</summary>
    ImmutableArray<ResourceCatalogEntry> ListTasks(CancellationToken cancellationToken);

    /// <summary>Reads one live task snapshot; returns null when the URI names no live task.</summary>
    ValueTask<TaskResourceSnapshot?> ReadTaskAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>The readable state of one task resource. Reading is a fresh snapshot (the plane
/// has no push); lifecycle progression and progress events stay with the owning subsystem
/// (ADR 0028 decision 2).</summary>
/// <param name="Uri">The task URI that was read.</param>
/// <param name="Kind">The owning authority (for example "terminal").</param>
/// <param name="Status">One of <c>working</c>, <c>complete</c>, <c>fail</c>, <c>cancel</c> —
/// the MCP tasks extension's lifecycle vocabulary.</param>
/// <param name="Terminal">Terminal one-shot detail; null for other kinds.</param>
internal sealed record TaskResourceSnapshot(
    string Uri,
    string Kind,
    string Status,
    TerminalTaskDetail? Terminal);

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

/// <summary>Serves the reserved task:// scheme (ADR 0028): the agent-visible namespace of
/// running work it spawned, read as fresh snapshot bodies through the ADR 0026 plane.
/// Control of a task (interrupt, close, input) stays with the owning subsystem's tools;
/// this provider is read-only by construction.</summary>
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
        if (!ResourceRegistry.TryParseUri(uri, out var parsed))
            throw new ResourceException("resource_invalid_uri", "The value must be an absolute task URI.");

        if (!sourcesByAuthority.TryGetValue(parsed.Host, out var source))
            throw new ResourceException(
                "resource_not_found",
                $"No task source owns the authority '{parsed.Host}'.");

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
}

/// <summary>Exposes the registry's timed-out one-shot sessions as task://terminal resources
/// (ADR 0028 decision 3). A one-shot stays readable until it is closed; the session id used
/// by the terminal_* tools remains the control handle.</summary>
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
        // task://terminal/{agentSessionId}/{terminalSessionId}
        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !AgentSessionId.TryParse(segments[0], out var ownerSessionId))
            return ValueTask.FromResult<TaskResourceSnapshot?>(null);

        var handle = registry.TryGetOneShotTask(ownerSessionId, segments[1]);
        if (handle is null)
            return ValueTask.FromResult<TaskResourceSnapshot?>(null);

        return ValueTask.FromResult<TaskResourceSnapshot?>(new TaskResourceSnapshot(
            ComposeUri(ownerSessionId, handle.SessionId),
            Authority,
            MapStatus(handle),
            new TerminalTaskDetail(ownerSessionId.ToString(), handle.SessionId, handle.State, handle.ExitCode)));
    }

    internal static string ComposeUri(AgentSessionId ownerSessionId, string sessionId)
    {
        return $"{TaskResourceProvider.Scheme}://{TerminalAuthority}/{ownerSessionId.Value.ToString("N")}/{sessionId}";
    }

    private static string MapStatus(TerminalTaskHandle handle)
    {
        return handle.State switch
        {
            "completed" => handle.ExitCode == 0 ? "complete" : "fail",
            "faulted" => "fail",
            "closing" or "closed" => "cancel",
            _ => "working"
        };
    }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TaskResourceSnapshot))]
internal sealed partial class TaskResourceJsonContext : JsonSerializerContext;
