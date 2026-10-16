using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Immutable;
using Maieutics.Agent;
using Maieutics.Permissions;
using Microsoft.Extensions.AI;

namespace Maieutics.Execution;

/// <summary>
///     Script-only workspace file tools over the IDE bridge (ADR 0042 decision 12): the
///     kernel endpoints the REPL worker's fs shim routes workspace paths to. They are
///     mounted only in the script-tools list — never in a run profile — so the model has
///     no direct surface; the shapes are faithful whole-file operations (not the
///     model-facing bounded windows) because they stand in for the Deno APIs the shim
///     wraps. Two disciplines apply by construction of that role: the session's
///     read/write policy is enforced here — a bridged call never reaches the permission
///     broker, so this tool is the enforcement point, deny-wins like every other path —
///     and an inactive bridge fails typed <c>workspace_bridge_inactive</c>, which the
///     shim absorbs as the per-call fallback to the real Deno API.
/// </summary>
internal sealed class WorkspaceBridgeFunctions
{
    private const int MaximumReadBytes = 8 * 1_024 * 1_024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private static readonly JsonSerializerOptions SerializerOptions =
        new(WorkspaceBridgeJsonSerializerContext.Default.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    private readonly Workspace workspace;
    private readonly IWorkspaceIoBridge? bridge;
    private readonly PermissionPolicyAcquirer? acquirer;

    internal WorkspaceBridgeFunctions(
        Workspace workspace,
        IWorkspaceIoBridge? bridge,
        PermissionPolicyAcquirer? acquirer = null)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.bridge = bridge;
        this.acquirer = acquirer;

        Functions =
        [
            CreateFunction(
                (Func<string, AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)ReadFileAsync,
                "workspace_fs_read",
                "Reads one workspace file's full text through the IDE bridge (script plumbing for the " +
                "REPL file plane, not a model tool)."),
            CreateFunction(
                (Func<string, string, AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)WriteFileAsync,
                "workspace_fs_write",
                "Writes one workspace file's full text through the IDE bridge (script plumbing for the " +
                "REPL file plane, not a model tool)."),
            CreateFunction(
                (Func<string, AIFunctionArguments, CancellationToken, ValueTask<JsonElement>>)ListAsync,
                "workspace_fs_list",
                "Lists one workspace directory through the IDE bridge (script plumbing for the REPL file " +
                "plane, not a model tool).")
        ];
    }

    internal IReadOnlyList<AIFunction> Functions { get; }

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

    [Description("Reads one workspace file's full text through the IDE bridge.")]
    private async ValueTask<JsonElement> ReadFileAsync(
        [Description("A workspace://local URI.")] string uri,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var (channel, logical) = Acquire(uri, arguments);
        EnforcePolicy(PermissionKind.Read, logical, arguments);
        string text;
        try
        {
            text = await channel.ReadTextAsync(logical.Segments, MaximumReadBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkspaceBridgeCoverageException)
        {
            throw Uncovered(logical.DisplayPath);
        }

        return JsonSerializer.SerializeToElement(
            new WorkspaceFsReadValue(logical.Uri, text),
            WorkspaceBridgeJsonSerializerContext.Default.WorkspaceFsReadValue);
    }

    [Description("Writes one workspace file's full text through the IDE bridge.")]
    private async ValueTask<JsonElement> WriteFileAsync(
        [Description("A workspace://local URI; missing parents are created.")] string uri,
        [Description("The complete file content, written verbatim as UTF-8 without a byte order mark.")]
        string content,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var (channel, logical) = Acquire(uri, arguments);
        EnforcePolicy(PermissionKind.Write, logical, arguments);
        GateInstructionSurface(WorkspaceSnapshot.ToDisplayPath(logical.Segments), arguments);
        var bytes = EncodeUtf8(content);
        if (bytes.Length > MaximumReadBytes)
            throw new WorkspaceException(
                "workspace_file_too_large",
                $"The bridge writes at most {MaximumReadBytes} UTF-8 bytes per file.");

        // A script write carries no kernel-side before state, so the precondition is
        // omitted; the dirty gate still refuses to land under an editor being typed in.
        try
        {
            await channel.EnsureCleanAsync(logical.Segments, cancellationToken).ConfigureAwait(false);
            await channel
                .WriteAsync(logical.Segments, content, null, createExclusive: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkspaceBridgeCoverageException)
        {
            throw Uncovered(logical.DisplayPath);
        }
        return JsonSerializer.SerializeToElement(
            new WorkspaceFsWriteValue(logical.Uri, "written"),
            WorkspaceBridgeJsonSerializerContext.Default.WorkspaceFsWriteValue);
    }

    [Description("Lists one workspace directory through the IDE bridge.")]
    private async ValueTask<JsonElement> ListAsync(
        [Description("A workspace://local directory URI; the root lists the workspace root.")] string? uri,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var (channel, logical) = Acquire(uri, arguments, allowRoot: true);
        EnforcePolicy(PermissionKind.Read, logical, arguments);
        WorkspaceBridgeListing listing;
        try
        {
            listing = await channel.ListAsync(logical.Segments, 10_000, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceBridgeCoverageException)
        {
            throw Uncovered(logical.DisplayPath);
        }
        if (listing.Truncated)
            throw new WorkspaceException(
                "workspace_directory_too_large",
                "A directory cannot contain more than 10000 visible entries.");

        return JsonSerializer.SerializeToElement(
            new WorkspaceFsListValue(
                logical.Uri,
                [.. listing.Entries.Select(static entry => new WorkspaceFsEntryValue(
                    entry.Name,
                    entry.Kind,
                    entry.SizeBytes))],
                false),
            WorkspaceBridgeJsonSerializerContext.Default.WorkspaceFsListValue);
    }

    private (WorkspaceBridgeChannel Channel, WorkspaceLogicalPath Logical) Acquire(
        string? uri,
        AIFunctionArguments arguments,
        bool allowRoot = false)
    {
        var channel = WorkspaceToolCalls.AcquireBridge(bridge, arguments);
        if (channel is null)
            throw new AgentToolException(
                "workspace_bridge_inactive",
                "No IDE frontend is serving the workspace file system; the caller falls back to direct file I/O.");

        return (channel, workspace.Capture().ResolveLogical(uri, allowRoot));
    }

    private void EnforcePolicy(
        PermissionKind kind,
        WorkspaceLogicalPath logical,
        AIFunctionArguments arguments)
    {
        if (acquirer is null) return;

        var sessionId = TryParseSession(arguments);
        if (sessionId is null)
        {
            if (kind == PermissionKind.Write && InstructionSurface.IsInstructionSurface(logical.DisplayPath))
                throw new AgentToolException(
                    "instructions_write_forbidden",
                    $"'{logical.DisplayPath}' is an instruction surface: writes require a session-bound " +
                    "policy that explicitly allows them.");

            return;
        }

        // Broker parity: the broker matches the physical path a direct Deno call would
        // open, so the bridged path is judged against the same absolute address.
        var physical = workspace.Capture().ToPhysicalPath(logical.Segments);
        var policy = acquirer.Acquire(PermissionLayer.Empty, sessionId);
        var rules = policy.For(kind);
        var allowed = rules.AllowAll ||
                      rules.Allow.Any(pattern => PermissionMatching.MatchesPath(pattern, physical));
        var denied = rules.DenyAll ||
                     rules.Deny.Any(pattern => PermissionMatching.MatchesPath(pattern, physical));
        if (denied || !allowed)
            throw new AgentToolException(
                kind == PermissionKind.Write ? "workspace_write_forbidden" : "workspace_read_forbidden",
                $"The session's permission policy denies this bridged {kind.ToString().ToLowerInvariant()} " +
                $"of '{logical.DisplayPath}'.");
    }

    private void GateInstructionSurface(string relativePath, AIFunctionArguments arguments)
    {
        if (acquirer is null || !InstructionSurface.IsInstructionSurface(relativePath)) return;

        var sessionId = TryParseSession(arguments);
        if (sessionId is null)
            throw new AgentToolException(
                "instructions_write_forbidden",
                $"'{relativePath}' is an instruction surface: model-initiated writes require an explicit " +
                "allow in the calling session's permission policy.");

        var policy = acquirer.Acquire(PermissionLayer.Empty, sessionId);
        var rules = policy.For(PermissionKind.Write);
        if (rules.Deny.Any(pattern => PermissionMatching.MatchesPath(pattern, relativePath)) ||
            !rules.Allow.Any(pattern => PermissionMatching.MatchesPath(pattern, relativePath)))
            throw new AgentToolException(
                "instructions_write_forbidden",
                $"'{relativePath}' is an instruction surface: writes require an explicit allow in the " +
                "calling session's permission policy.");
    }

    private static AgentSessionId? TryParseSession(AIFunctionArguments arguments)
    {
        return WorkspaceToolCalls.TryGetSessionId(arguments) is { } text &&
               AgentSessionId.TryParse(text, out var sessionId)
            ? sessionId
            : null;
    }

    /// <summary>The coverage ruling for the shim's endpoints: a path outside the
    /// frontend's folders is not a bridge failure — it is the shim's fallback signal,
    /// because the caller (the Deno fs shim) serves uncovered paths natively.</summary>
    private static AgentToolException Uncovered(string displayPath)
    {
        return new AgentToolException(
            "workspace_bridge_inactive",
            $"The IDE frontend's workspace folders do not cover '{displayPath}'; the caller falls back to " +
            "direct file I/O.");
    }

    private static byte[] EncodeUtf8(string content)
    {
        try
        {
            return StrictUtf8.GetBytes(content);
        }
        catch (EncoderFallbackException exception)
        {
            throw new WorkspaceException("workspace_invalid_utf8", "The content is not valid UTF-8 text.", exception);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(WorkspaceFsReadValue))]
[JsonSerializable(typeof(WorkspaceFsWriteValue))]
[JsonSerializable(typeof(WorkspaceFsListValue))]
[JsonSerializable(typeof(WorkspaceFsEntryValue))]
internal sealed partial class WorkspaceBridgeJsonSerializerContext : JsonSerializerContext;

internal sealed record WorkspaceFsReadValue(string Uri, string Content);

internal sealed record WorkspaceFsWriteValue(string Uri, string Status);

internal sealed record WorkspaceFsListValue(
    string Uri,
    ImmutableArray<WorkspaceFsEntryValue> Entries,
    bool Truncated);

internal sealed record WorkspaceFsEntryValue(string Name, string Kind, long? SizeBytes);
