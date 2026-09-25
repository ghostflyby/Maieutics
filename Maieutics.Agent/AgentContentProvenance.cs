using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

/// <summary>Well-known content origins (ADR 0032 decision 1). The set is open: future kinds
/// declare their own constant and consumers tolerate unknown values.</summary>
public static class AgentContentOrigins
{
    /// <summary>Kernel/config instructions.</summary>
    public const string System = "system";

    /// <summary>Human turn or human-attached content.</summary>
    public const string User = "user";

    /// <summary>Observed data from any tool, resource read, or MCP call.</summary>
    public const string Tool = "tool";

    /// <summary>Model-generated content from a subagent child run.</summary>
    public const string Agent = "agent";
}

/// <summary>Provenance metadata for one canonical content item: the origin it entered the
/// context from plus the derivation chain that produced it (ADR 0032 decision 1). Assigned at
/// the content's entry choke point and immutable afterwards; downstream consumers (overlay
/// predicates, sandbox grading, origin-labeled rendering, inspection) read it to reason about
/// the chain. The derivation chain is opaque to the runtime — it is a policy-addressable string
/// whose format the spawning surface defines.</summary>
public sealed record AgentContentProvenance(string Origin, string? Derivation = null)
{
    /// <summary>The additional-properties key the provenance rides on.</summary>
    public const string PropertyKey = "maieutics.provenance";

    /// <summary>Attaches provenance to one content item, replacing any previous value. The
    /// provenance is stored as a JSON string so it survives MEAI source-gen serialization in
    /// provider requests and transcript persistence without a dedicated JsonTypeInfo.</summary>
    public static void Attach(AIContent content, AgentContentProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(provenance);
        if (string.IsNullOrWhiteSpace(provenance.Origin))
            throw new ArgumentException("The content provenance origin is required.", nameof(provenance));

        content.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        content.AdditionalProperties[PropertyKey] = JsonSerializer.Serialize(
            new { origin = provenance.Origin, derivation = provenance.Derivation });
    }

    /// <summary>Reads the provenance from one content item, or null when the item carries none.</summary>
    public static AgentContentProvenance? TryRead(AIContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.AdditionalProperties?.TryGetValue(PropertyKey, out var value) != true ||
            value is not string json)
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new AgentContentProvenance(
                root.GetProperty("origin").GetString() ?? "",
                root.TryGetProperty("derivation", out var derivation) ? derivation.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Inspection context passed to one content inspector: the content item under
/// observation with its provenance already attached.</summary>
public sealed record AgentContentInspectionContext(
    string Origin,
    AIContent Content,
    AgentContentProvenance? Provenance);

/// <summary>The outcome of one content inspection: annotations only. Inspectors never rewrite
/// or drop content (ADR 0032 decision 5 — enforcement stays in the permission overlay, which
/// reads the tags through its own rules).</summary>
public sealed record AgentContentInspection(
    string InspectorId,
    IReadOnlyList<string> Tags)
{
    /// <summary>An empty inspection: no tags, nothing annotated.</summary>
    public static AgentContentInspection Clean { get; } = new("clean", []);

    /// <summary>Creates an inspection with the given tags; an empty list means clean.</summary>
    public static AgentContentInspection For(string inspectorId, params string[] tags) =>
        new(inspectorId, tags);
}

/// <summary>Observes content entering the transcript at the tool envelope and returns tags
/// that the permission overlay's rules can reference. Inspectors never rewrite or drop
/// content; enforcement stays in the overlay. A failing inspector is skipped with an
/// `inspection-skipped` annotation rather than blocking the pipeline (ADR 0032 decision 5).</summary>
public interface IAgentContentInspector
{
    /// <summary>Gets the stable identifier this inspector stamps into its annotations.</summary>
    string InspectorId { get; }

    /// <summary>Inspects one content item and returns its tags.</summary>
    ValueTask<AgentContentInspection> InspectAsync(
        AgentContentInspectionContext context,
        CancellationToken cancellationToken);
}
