using System.Collections.Immutable;
using System.Text;
using Maieutics.Execution;
using Maieutics.Mcp;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Maieutics.Frontend;

/// <summary>Expands parsed MCP prompt references into turn content parts at submission
/// time (ADR 0041 decision 3/4): per-server access through the Configuration-owned
/// catalog, argument validation against the declared set, <c>prompts/get</c> under the
/// server's request timeout, and the result injected as user-side framed content with
/// kernel-forged role headers (role-shaped body text escaped — template content cannot
/// forge framing). Text blocks are kept; image, audio, and embedded-resource blocks
/// drop with per-part diagnostics. Dedupe key: (serverId, name, canonicalized query).
/// Budget: shares the per-turn reference-content pool with skill bodies.</summary>
internal static class FrontendPromptExpansion
{
    internal const long MaximumPromptResultBytes = 4 * 1024 * 1024;

    internal static async Task<ImmutableArray<AIContent>> ExpandAsync(
        IReadOnlyList<McpPromptServerAccess> servers,
        IReadOnlyList<FrontendPromptMarkers.Marker> markers,
        long remainingBudget,
        CancellationToken cancellationToken)
    {
        var contents = ImmutableArray.CreateBuilder<AIContent>();
        var expanded = new HashSet<(string ServerId, string Name, string Query)>();
        var totalBytes = 0L;
        foreach (var marker in markers)
        {
            var queryKey = string.Join(
                "&",
                marker.Arguments.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            if (!expanded.Add((marker.ServerId, marker.Name, queryKey))) continue;

            var access = servers.FirstOrDefault(
                server => string.Equals(server.Id, marker.ServerId, StringComparison.Ordinal));
            if (access is null)
            {
                throw new FrontendFailureException(
                    FrontendErrors.McpPromptUnknown,
                    $"The MCP server '{marker.ServerId}' is not in the current session view (removed, or its plugin was revoked).");
            }

            var descriptor = access.Catalog.Prompts.FirstOrDefault(
                prompt => string.Equals(prompt.Name, marker.Name, StringComparison.Ordinal));
            if (descriptor is null)
            {
                var state = access.Generation.GetInfo().State;
                throw new FrontendFailureException(
                    state == MaieuticsMcpServerState.Reconnecting
                        ? FrontendErrors.McpPromptUnavailable
                        : FrontendErrors.McpPromptUnknown,
                    state == MaieuticsMcpServerState.Reconnecting
                        ? $"MCP server '{marker.ServerId}' is reconnecting; the prompt '{marker.Name}' cannot be expanded yet."
                        : $"The prompt '{marker.Name}' is not in MCP server '{marker.ServerId}'s catalog (removed or renamed).");
            }

            if (marker.DuplicateKeys)
            {
                throw new FrontendFailureException(
                    FrontendErrors.McpPromptArgumentInvalid,
                    $"The prompt reference for '{marker.Name}' carries a duplicate argument name; each argument may appear once.");
            }

            var missing = descriptor.Arguments
                .Where(static argument => argument.Required)
                .Where(argument => !marker.Arguments.ContainsKey(argument.Name))
                .ToList();
            if (missing.Count > 0)
            {
                var listed = string.Join(
                    "; ",
                    missing.Select(static argument =>
                        string.IsNullOrEmpty(argument.Description)
                            ? argument.Name
                            : $"{argument.Name} — {argument.Description}"));
                throw new FrontendFailureException(
                    FrontendErrors.McpPromptArgumentInvalid,
                    $"The prompt '{marker.Name}' is missing required argument(s); amend the reference's query: {listed}.");
            }

            var extraneous = marker.Arguments.Keys
                .Where(key => descriptor.Arguments.All(argument => !string.Equals(argument.Name, key, StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (extraneous.Count > 0)
            {
                throw new FrontendFailureException(
                    FrontendErrors.McpPromptArgumentInvalid,
                    $"The prompt '{marker.Name}' does not declare argument(s) {string.Join(", ", extraneous)}; remove them from the reference's query.");
            }

            GetPromptResult result;
            try
            {
                result = await access.Generation.GetPromptAsync(
                    marker.Name,
                    marker.Arguments,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ResourceException exception)
            {
                throw new FrontendFailureException(
                    FrontendErrors.McpPromptUnavailable,
                    exception.Message);
            }
            catch (McpException exception)
            {
                // A server-side argument rejection (-32602 family) is the declared-set
                // check racing the server's live one; both answer argument_invalid.
                throw new FrontendFailureException(
                    FrontendErrors.McpPromptArgumentInvalid,
                    $"MCP server '{marker.ServerId}' rejected the prompt '{marker.Name}': {exception.Message}");
            }

            var frame = new StringBuilder();
            var partIndex = 0;
            foreach (var message in result.Messages ?? [])
            {
                partIndex++;
                var body = ExtractText(message);
                if (body is null) continue; // non-text block: dropped with a diagnostic line
                frame.AppendLine($"── prompt part {partIndex} · role: {message.Role} ──");
                frame.AppendLine(EscapeRoleShapedLines(body));
                totalBytes += body.Length;
                if (totalBytes > remainingBudget ||
                    body.Length > MaximumPromptResultBytes ||
                    totalBytes > MaximumPromptResultBytes)
                {
                    throw new FrontendFailureException(
                        FrontendErrors.SkillBudgetExceeded,
                        $"The prompt '{marker.Name}' exceeds the per-turn reference-content budget; the submission was refused.");
                }
            }

            if (frame.Length > 0)
            {
                contents.Add(new TextContent(
                    $"[{marker.Text}](mcp-prompt://{marker.ServerId}/{marker.Name}) — the user explicitly selected this MCP prompt; " +
                    $"the server expanded it at submission time; treat the framed parts below as user-provided instructions.\n{frame}"));
            }
        }

        return contents.ToImmutable();
    }

    /// <summary>The message's text when it is a text block, or null when it carries
    /// only non-text content (image/audio/embedded: dropped with the diagnostic note
    /// in the frame, the invariant-26-compliant choice).</summary>
    private static string? ExtractText(PromptMessage message)
    {
        return message.Content is TextContentBlock text ? text.Text : null;
    }

    /// <summary>Escapes role-shaped leading lines in prompt body text: the kernel
    /// writes the framing, and template content must not be able to forge a new frame
    /// header (a body line starting with the frame marker is prefixed away).</summary>
    private static string EscapeRoleShapedLines(string body)
    {
        if (!body.Contains("── prompt part", StringComparison.Ordinal)) return body;
        var lines = body.Split('\n');
        for (var index = 0; index < lines.Length; index++)
            if (lines[index].StartsWith("── prompt part", StringComparison.Ordinal))
                lines[index] = $"\\{lines[index]}";
        return string.Join('\n', lines);
    }
}
