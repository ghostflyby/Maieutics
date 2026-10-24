using Microsoft.Extensions.AI;

namespace Maieutics.Configuration;

/// <summary>
///     The consumer-owned seam behind the IDE capability gate (ADR 0042 decision 9):
///     whether any frontend has announced IDE capability on a live events socket. The
///     interface lives beside its consumer — <see cref="MaieuticsRuntimeConfiguration" />
///     reads it while assembling a run's tools — and the composition root binds the
///     frontend's live connection state to it, following the deferred-presenter pattern
///     that keeps Configuration from referencing Frontend. A null gate in the runtime
///     configuration means the IDE tool family never joins a run profile.
/// </summary>
internal interface IIdeFrontendGate
{
    /// <summary>Gets whether at least one connected events socket announced IDE
    /// capability (process-wide). Profile-level: the family joins a run only when true.</summary>
    bool HasIdeFrontend { get; }

    /// <summary>Gets whether the addressed session has a live socket that announced
    /// IDE capability. Invocation-level: a tool call whose session lost its announcing
    /// socket fails typed instead of waiting out the request timeout.</summary>
    bool IsIdeFrontendConnected(string sessionId);
}

/// <summary>
///     The composition-root carrier for the IDE tool family (ADR 0042 decision 9): the
///     tools as plain <see cref="AIFunction" />s (the Configuration namespace never
///     references the tool class) plus the gate that decides whether a run's profile
///     includes them. Registered unconditionally; a gate reporting no announcing socket
///     keeps the family out of every profile while its names stay reserved against
///     MCP/plugin collisions.
/// </summary>
internal sealed record IdeToolFamily(IReadOnlyList<AIFunction> Tools, IIdeFrontendGate Gate);
