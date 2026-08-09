using System.Text;
using Maieutics.Configuration;

namespace Maieutics.Jupyter;

internal static class MaieuticsStatusRenderer
{
    internal static string Render(MaieuticsStatusSnapshot snapshot)
    {
        var output = new StringBuilder("### Maieutics status\n\n");
        output.Append("- Configuration: version ")
            .Append(MarkdownText.CodeSpan(snapshot.Runtime.Version.ToString()));
        var reload = snapshot.Runtime.LastReload;
        if (reload.Outcome == MaieuticsConfigurationReloadOutcome.NotAttempted)
        {
            output.AppendLine("; no reload has completed since startup");
        }
        else
        {
            output.Append("; last reload ")
                .Append(MarkdownText.CodeSpan(ReloadOutcome(reload.Outcome)))
                .Append(" (attempt ")
                .Append(MarkdownText.CodeSpan(reload.Attempt.ToString()))
                .Append(", active version ")
                .Append(MarkdownText.CodeSpan(reload.ActiveVersion.ToString()))
                .AppendLine(")");
        }

        var selection = snapshot.Runtime.ModelSelection;
        if (selection.Profiles.Count == 0)
        {
            output.AppendLine("- Model: not configured");
        }
        else
        {
            var profile = selection.Profiles.Single(static profile => profile.IsSelected);
            var selectionSource = profile.IsAutomatic
                ? "automatic session override"
                : selection.HasSessionOverride
                    ? "session override"
                    : "configured default";
            output.Append("- Model: profile ")
                .Append(MarkdownText.CodeSpan(profile.Id))
                .Append(" (")
                .Append(selectionSource)
                .Append("), source ")
                .Append(MarkdownText.CodeSpan(profile.SourceId))
                .Append(", provider ")
                .Append(MarkdownText.CodeSpan(profile.Provider))
                .Append(", model ")
                .Append(MarkdownText.CodeSpan(profile.Model))
                .AppendLine();
        }

        output.Append("- Workspace: ")
            .Append(snapshot.Workspace.HasSessionOverride ? "session override" : "startup root")
            .Append(" (version ")
            .Append(MarkdownText.CodeSpan(snapshot.Workspace.Version.ToString()))
            .AppendLine("; path redacted)");

        var plugins = snapshot.Plugins;
        output.Append("- Plugins: ")
            .Append(MarkdownText.CodeSpan(plugins.State.ToString()))
            .Append("; discovered ")
            .Append(MarkdownText.CodeSpan(plugins.PluginCount.ToString()))
            .Append(", registrations ")
            .Append(MarkdownText.CodeSpan(plugins.RegistrationCount.ToString()))
            .Append(", control ")
            .AppendLine(plugins.HostProcessRequired
                ? plugins.ControlConnected ? "connected" : "disconnected"
                : "not required");

        if (snapshot.McpServers.Count == 0)
        {
            output.AppendLine("- MCP: no servers enabled");
        }
        else
        {
            output.AppendLine("- MCP:");
            foreach (var server in snapshot.McpServers)
                output.Append("  - ")
                    .Append(MarkdownText.CodeSpan(server.Id))
                    .Append(": ")
                    .Append(MarkdownText.CodeSpan(server.State.ToString()))
                    .Append(" (")
                    .Append(MarkdownText.CodeSpan(server.Tools.Count.ToString()))
                    .AppendLine(" tools)");
        }

        if (snapshot.Repls.Sessions.Count == 0)
        {
            output.AppendLine("- Deno REPLs: no sessions");
        }
        else
        {
            output.AppendLine("- Deno REPLs:");
            foreach (var repl in snapshot.Repls.Sessions)
            {
                output.Append("  - ")
                    .Append(MarkdownText.CodeSpan(repl.SessionId))
                    .Append(": generation ")
                    .Append(MarkdownText.CodeSpan(repl.Generation.ToString()))
                    .Append(", state ")
                    .Append(MarkdownText.CodeSpan(repl.State));
                if (repl.IsDefault) output.Append(" (default)");

                output.AppendLine();
            }
        }

        return output.ToString();
    }

    private static string ReloadOutcome(MaieuticsConfigurationReloadOutcome outcome)
    {
        return outcome switch
        {
            MaieuticsConfigurationReloadOutcome.Unchanged => "unchanged",
            MaieuticsConfigurationReloadOutcome.Applied => "applied",
            MaieuticsConfigurationReloadOutcome.Rejected => "rejected",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown reload outcome.")
        };
    }
}
