namespace Maieutics.Execution;

/// <summary>Narrow seam through which the permission module reads workspace-derived variable
/// values. Declared in this owning layer so <see cref="Workspace"/> can implement it without
/// depending on <c>Maieutics.Permissions</c> (ADR 0018 §5; AGENTS.md: put a small interface in
/// the lower-level owning layer). Nothing else computes "the workspace path" for permission
/// purposes; <see cref="WorkspaceSnapshot"/> remains authoritative for workspace semantics.</summary>
internal interface IPermissionVariableSource
{
    /// <summary>Returns the current value of a <c>var.*</c> variable, or null when the name is
    /// unknown. The permission module treats a null as an unknown-variable expansion error.</summary>
    string? GetVariable(string name);
}
