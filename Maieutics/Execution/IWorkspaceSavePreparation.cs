using Maieutics.Agent;

namespace Maieutics.Execution;

/// <summary>The editor-owned save barrier before a command consumes workspace files.</summary>
internal interface IWorkspaceSavePreparation
{
    /// <summary>Saves all dirty documents in the owning session's IDE workspace.
    /// Failure prevents the dependent command from starting or receiving input.</summary>
    Task SaveAllAsync(AgentSessionId sessionId, CancellationToken cancellationToken);
}
