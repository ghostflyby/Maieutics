using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

/// <summary>Ordered registry of content inspectors (ADR 0032 decision 5). The session runs
/// every inspector at the tool envelope and stamps the union of their tags into the result
/// content's inspection metadata. Any inspector failure is fail-open: the content continues
/// with an `inspection-skipped: <inspector-id>` tag so the overlay can still reason about the
/// gap. Inspectors never see or change the conversation state — they observe content.</summary>
public sealed class AgentContentInspectorRegistry
{
    private readonly IAgentContentInspector[] inspectors;

    public AgentContentInspectorRegistry(IEnumerable<IAgentContentInspector> inspectors)
    {
        ArgumentNullException.ThrowIfNull(inspectors);
        this.inspectors = [.. inspectors];
    }

    /// <summary>Gets whether any inspector is registered; sessions without inspectors skip
    /// the pipeline entirely.</summary>
    public bool IsEmpty => inspectors.Length == 0;

    /// <summary>Runs every inspector over one content item and returns the union of tags.
    /// A failing inspector contributes an `inspection-skipped:<id>` tag instead of blocking
    /// the pipeline; the content always flows.</summary>
    public async ValueTask<IReadOnlyList<string>> InspectAsync(
        string origin,
        AIContent content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (inspectors.Length == 0) return [];

        var tags = new List<string>();
        var provenanceOrigin = AgentContentProvenance.TryReadOrigin(content);
        foreach (var inspector in inspectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var inspection = await inspector.InspectAsync(
                    new AgentContentInspectionContext(origin, content,
            provenanceOrigin is { } o ? new AgentContentProvenance(o) : null),
                    cancellationToken).ConfigureAwait(false);
                tags.AddRange(inspection.Tags);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                tags.Add($"inspection-skipped:{inspector.InspectorId}:{exception.GetType().Name}");
            }
        }

        return tags;
    }
}
