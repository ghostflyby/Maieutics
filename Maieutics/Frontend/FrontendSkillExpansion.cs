using System.Collections.Immutable;
using System.Text;
using Maieutics.Execution;
using Maieutics.Skills;
using Microsoft.Extensions.AI;

namespace Maieutics.Frontend;

/// <summary>Expands parsed skill-reference markers into turn content parts at submission
/// time: each distinct name is resolved against the live catalog (unknown names fail typed
/// before a run starts) and its body is read fresh through the skill:// plane — the same
/// provider, bounds, and containment the model's own reads use, so the user-selected body
/// is byte-for-byte what a later read would serve. Parts are framed as user-selected
/// instructions; the per-turn byte budget fails the whole submission rather than
/// truncating (a silently clipped skill is worse than an explicit refusal).</summary>
internal static class FrontendSkillExpansion
{
    /// <summary>The per-marker body bound: the skill plane's own limit, restated here so
    /// the request asks for exactly what can be served.</summary>
    internal const long MaximumSkillBodyBytes = 4 * 1024 * 1024;

    /// <summary>The total body bytes one turn may carry across all its skill references;
    /// exceeding it fails the submission with <see cref="FrontendErrors.SkillBudgetExceeded" />.</summary>
    internal const long MaximumTurnSkillBytes = 8 * 1024 * 1024;

    internal static async Task<ImmutableArray<AIContent>> ExpandAsync(
        SkillResourceProvider provider,
        IReadOnlyList<FrontendSkillMarkers.Marker> markers,
        CancellationToken cancellationToken)
    {        var contents = ImmutableArray.CreateBuilder<AIContent>();
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var totalBytes = 0L;
        foreach (var marker in markers)
        {
            if (!expanded.Add(marker.Name)) continue;

            var remaining = MaximumTurnSkillBytes - totalBytes;
            ResourceReadResult read;
            try
            {
                read = await provider
                    .ReadAsync($"skill://{marker.Name}", new ResourceReadRequest(Math.Min(MaximumSkillBodyBytes, remaining)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ResourceException exception) when (exception.Code == "resource_too_large")
            {
                throw new FrontendFailureException(
                    FrontendErrors.SkillBudgetExceeded,
                    $"The skill '{marker.Name}' exceeds the per-turn skill body budget; the submission was refused.");
            }
            catch (ResourceException exception) when (exception.Code == "resource_not_found")
            {
                throw new FrontendFailureException(
                    FrontendErrors.SkillUnknown,
                    $"The skill '{marker.Name}' is not in the current catalog (pruned, renamed, or disabled).");
            }

            using (read.Content)
            {
                var body = new MemoryStream((int)Math.Min(read.Content.Length, int.MaxValue));
                await read.Content.CopyToAsync(body, cancellationToken).ConfigureAwait(false);
                totalBytes += body.Length;
                var text = Encoding.UTF8.GetString(body.ToArray());
                contents.Add(new TextContent(
                    $"[{marker.Text}](skill://{marker.Name}) — the user explicitly selected this skill; " +
                    $"treat its body, read at submission time, as user-provided instructions.\n{text}"));
            }
        }

        return contents.ToImmutable();
    }
}
