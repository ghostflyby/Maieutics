using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Maieutics.Agent;
using Microsoft.Extensions.AI;

namespace Maieutics.Execution;

/// <summary>
///     The dynamic IDE bridge for the workspace repository file system (ADR 0042
///     decision 12): while the calling session has a live ide-announcing frontend able
///     to serve the workspace domain, workspace tool I/O forwards to that frontend
///     instead of the kernel's pinned local walk. The interface lives beside its
///     consumer — the workspace tools in Execution — and the composition root binds the
///     frontend's bridge implementation to it; a null acquire means the local channel
///     (the pre-bridge behavior, unchanged). Multiple attached IDEs are resolved by the
///     implementation's authority election: the bridge serves a call from one elected
///     announcer, never from a broadcast race.
/// </summary>
internal interface IWorkspaceIoBridge
{
    /// <summary>Acquires the bridge channel for one operation from the named session
    /// (null for context-less script calls), or null when no authoritative frontend
    /// serves it and the tools fall back to local disk I/O.</summary>
    WorkspaceBridgeChannel? Acquire(string? sessionId);
}

/// <summary>
///     One acquired bridge channel: the workspace file operations the tools perform,
///     served by the elected IDE frontend. Implementations answer with the workspace
///     tools' own typed failures — <see cref="WorkspaceException" /> with the local
///     channel's codes (<c>workspace_path_not_found</c>, <c>workspace_file_too_large</c>,
///     <c>workspace_invalid_utf8</c>, <c>workspace_access_denied</c>,
///     <c>workspace_write_conflict</c>), so the tools need no remapping — plus the
///     bridge-level <c>ide_*</c> and <c>workspace_bridge_inactive</c> tool errors. A
///     channel is stateless beyond its pinned announcer; acquire one per operation.
/// </summary>
internal abstract class WorkspaceBridgeChannel
{
    /// <summary>Reads the whole file as text under the byte bound, answering the
    /// IDE's authoritative text state — a dirty document's buffer, else the decoded
    /// disk bytes. Fails <c>workspace_path_not_found</c>,
    /// <c>workspace_file_too_large</c>, or <c>workspace_invalid_utf8</c>.</summary>
    public abstract Task<string> ReadTextAsync(
        IReadOnlyList<string> segments,
        int maximumBytes,
        CancellationToken cancellationToken);

    /// <summary>Lists one directory's entries, names sorted, bounded by
    /// <paramref name="maximumEntries" /> (truncation is reported, not an error — the
    /// caller turns it into the directory-too-large failure). Fails
    /// <c>workspace_path_not_found</c> / <c>workspace_not_directory</c>.</summary>
    public abstract ValueTask<WorkspaceBridgeListing> ListAsync(
        IReadOnlyList<string> segments,
        int maximumEntries,
        CancellationToken cancellationToken);

    /// <summary>Writes one file through the frontend (parents created). The
    /// precondition is the SHA-256 of the before text the caller itself read through
    /// this channel — a mismatch fails <c>workspace_write_conflict</c> instead of
    /// silently overwriting a state the caller never saw. When
    /// <paramref name="createExclusive" /> is set, an existing target fails
    /// <c>workspace_path_exists</c>.</summary>
    public abstract Task WriteAsync(
        IReadOnlyList<string> segments,
        string content,
        string? expectedContentHash,
        bool createExclusive,
        CancellationToken cancellationToken);

    /// <summary>Deletes one regular file through the frontend. Directories are not
    /// deletable through this surface.</summary>
    public abstract Task DeleteAsync(IReadOnlyList<string> segments, CancellationToken cancellationToken);

    /// <summary>Decision 7's dirty gate over the bridge: fails
    /// <c>ide_document_dirty</c> when any named file has unsaved changes in the
    /// serving frontend, so a write computed against the answered state cannot land
    /// under an editor the user is still typing in.</summary>
    public abstract Task EnsureCleanAsync(IReadOnlyList<string> segments, CancellationToken cancellationToken);

    /// <summary>The hash the write precondition carries: lowercase hex SHA-256 of
    /// the UTF-8 bytes of the text both sides read through the channel.</summary>
    internal static string ContentHash(string text)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>One directory entry as the bridge answers it: the name, the
/// workspace tool's kind string (<c>file</c>, <c>directory</c>,
/// <c>symbolic_link</c>, <c>other</c>), and the size when the frontend
/// reports one (bridge listings mostly do not).</summary>
internal sealed record WorkspaceBridgeEntry(string Name, string Kind, long? SizeBytes);

/// <summary>One bridged directory listing: the bounded entries and whether the
/// directory held more than the requested bound.</summary>
internal sealed record WorkspaceBridgeListing(ImmutableArray<WorkspaceBridgeEntry> Entries, bool Truncated);

/// <summary>The elected authority's workspace folders do not cover the target path
/// (ADR 0042 decision 12's coverage ruling): the bridge only serves files the
/// frontend actually opens — outside them the "IDE authoritative text state"
/// claim is false, so the operation falls back to the local channel. Thrown by
/// the channel before any mutation; single-target flows re-run the whole
/// operation locally, and apply_patch falls back for that file only.</summary>
internal sealed class WorkspaceBridgeCoverageException : Exception
{
    internal WorkspaceBridgeCoverageException(string path)
        : base($"The IDE frontend's workspace folders do not cover '{path}'; the operation is served locally.")
    {
        Path = path;
    }

    internal string Path { get; }
}

/// <summary>Call-site helpers shared by the workspace tools: the calling session for
/// session-scoped gates — the agent run context when the model called the tool, the
/// script-invocation context the control host bound when the REPL or a plugin relay
/// called it (ADR 0042 decision 12), else null — and the bridge acquire.</summary>
internal static class WorkspaceToolCalls
{
    internal static string? TryGetSessionId(AIFunctionArguments arguments)
    {
        if (arguments.Context?.TryGetValue(typeof(AgentToolContext), out var value) == true &&
            value is AgentToolContext context)
            return context.SessionId.Value.ToString("N");

        return ScriptInvocationContext.TryGet(arguments)?.SessionId;
    }

    internal static WorkspaceBridgeChannel? AcquireBridge(
        IWorkspaceIoBridge? bridge,
        AIFunctionArguments arguments)
    {
        return bridge?.Acquire(TryGetSessionId(arguments));
    }
}
