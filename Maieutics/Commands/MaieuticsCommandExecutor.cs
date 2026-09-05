using System.Globalization;
using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.Execution;
using Maieutics.Mcp;

namespace Maieutics.Commands;

/// <summary>
///     Executes Maieutics command cells (<c>%model</c>, <c>%mcp</c>, <c>%session</c>,
///     <c>%status</c>, <c>%workspace</c>, legacy <c>%maieutics</c>) and renders their markdown
///     answer. This is the shared control surface every frontend adapter delegates to, so
///     command semantics cannot drift between the Jupyter kernel and the web frontend.
///     Unavailable subsystems and expected failures surface as
///     <see cref="MaieuticsCommandException" />.
/// </summary>
internal sealed class MaieuticsCommandExecutor(
    MaieuticsAgentSessionManager? sessionManager,
    IMaieuticsRuntimeConfiguration? runtimeConfiguration,
    Workspace? workspace,
    MaieuticsStatusProvider? statusProvider,
    IMaieuticsMcpController? mcpController)
{
    /// <summary>Executes one command cell and returns its markdown rendering.</summary>
    /// <exception cref="MaieuticsCommandException">The text is not a valid command or the
    /// command failed in an expected way.</exception>
    public async Task<string> ExecuteAsync(string code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var originalArguments = code.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arguments = MaieuticsCommandLanguage.NormalizeCommandArguments(originalArguments);
        if (arguments is null)
            throw new MaieuticsCommandException(
                MaieuticsCommandException.CommandError, "Unknown Maieutics command.");

        try
        {
            if (string.Equals(arguments[1], MaieuticsCommandLanguage.Workspace, StringComparison.OrdinalIgnoreCase))
            {
                var pathTokenCount = originalArguments[0].Equals(
                    MaieuticsCommandLanguage.LegacyRoot,
                    StringComparison.OrdinalIgnoreCase)
                    ? 3
                    : 2;
                return ExecuteWorkspaceCommand(code, arguments, pathTokenCount);
            }

            if (string.Equals(arguments[1], MaieuticsCommandLanguage.Model, StringComparison.OrdinalIgnoreCase))
                return await ExecuteModelCommandAsync(arguments, cancellationToken).ConfigureAwait(false);

            if (string.Equals(arguments[1], MaieuticsCommandLanguage.Mcp, StringComparison.OrdinalIgnoreCase))
                return ExecuteMcpCommand(arguments);

            if (string.Equals(arguments[1], MaieuticsCommandLanguage.Status, StringComparison.OrdinalIgnoreCase))
                return ExecuteStatusCommand(arguments);

            if (string.Equals(arguments[1], MaieuticsCommandLanguage.Session, StringComparison.OrdinalIgnoreCase))
                return ExecuteSessionCommand(arguments);

            throw new MaieuticsCommandException(
                MaieuticsCommandException.CommandError, "Unknown Maieutics command.");
        }
        catch (MaieuticsCommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                              UnauthorizedAccessException or NotSupportedException)
        {
            throw new MaieuticsCommandException(
                MaieuticsCommandException.CommandError, exception.Message);
        }
    }

    private async Task<string> ExecuteModelCommandAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        if (runtimeConfiguration is null)
            throw new MaieuticsCommandException(
                MaieuticsCommandException.Unavailable, "Model profile commands are not available in this host.");

        if (arguments.Length >= 3 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.Available, StringComparison.OrdinalIgnoreCase))
        {
            var refresh = arguments.Contains(MaieuticsCommandLanguage.RefreshFlag,
                StringComparer.OrdinalIgnoreCase);
            var sourceId = arguments.FirstOrDefault(arg =>
                !string.Equals(arg, MaieuticsCommandLanguage.RefreshFlag, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(arg, MaieuticsCommandLanguage.LegacyRoot, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(arg, MaieuticsCommandLanguage.Model, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(arg, MaieuticsCommandLanguage.Available, StringComparison.OrdinalIgnoreCase));
            var groups = await runtimeConfiguration.GetDiscoveredModelsAsync(
                sourceId, refresh, cancellationToken).ConfigureAwait(false);
            return RenderAvailable(groups, runtimeConfiguration.GetModelProfileSelection());
        }

        if (arguments.Length == 2 ||
            (arguments.Length == 3 && string.Equals(
                arguments[2],
                MaieuticsCommandLanguage.Current,
                StringComparison.OrdinalIgnoreCase)))
            return RenderCurrent(runtimeConfiguration.GetModelProfileSelection());

        if (arguments.Length == 3 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.List, StringComparison.OrdinalIgnoreCase))
            return RenderList(runtimeConfiguration.GetModelProfileSelection());

        if (arguments.Length == 4 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.Use, StringComparison.OrdinalIgnoreCase))
        {
            await runtimeConfiguration.SelectModelProfileAsync(arguments[3], cancellationToken).ConfigureAwait(false);
            return RenderCurrent(runtimeConfiguration.GetModelProfileSelection());
        }

        if (arguments.Length == 3 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.Reset, StringComparison.OrdinalIgnoreCase))
        {
            runtimeConfiguration.ResetModelProfile();
            return RenderCurrent(runtimeConfiguration.GetModelProfileSelection());
        }

        throw new MaieuticsCommandException(
            MaieuticsCommandException.CommandError, "Unknown model command or invalid arguments.");
    }

    private string ExecuteMcpCommand(string[] arguments)
    {
        if (mcpController is null)
            throw new MaieuticsCommandException(
                MaieuticsCommandException.Unavailable, "MCP commands are not available in this host.");

        if (arguments.Length == 3 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.List, StringComparison.OrdinalIgnoreCase))
            return RenderMcpList(mcpController.GetMcpServers());

        throw new MaieuticsCommandException(
            MaieuticsCommandException.CommandError, "Unknown MCP command or invalid arguments.");
    }

    private string ExecuteWorkspaceCommand(string code, string[] arguments, int pathTokenCount)
    {
        if (workspace is null)
            throw new MaieuticsCommandException(
                MaieuticsCommandException.Unavailable, "Workspace commands are not available in this host.");

        WorkspaceSnapshot selection;
        if (arguments.Length == 2 ||
            (arguments.Length == 3 && string.Equals(
                arguments[2],
                MaieuticsCommandLanguage.Current,
                StringComparison.OrdinalIgnoreCase)))
        {
            selection = workspace.Capture();
        }
        else if (arguments.Length == 3 &&
                 string.Equals(arguments[2], MaieuticsCommandLanguage.Reset, StringComparison.OrdinalIgnoreCase))
        {
            selection = workspace.Reset();
        }
        else if (arguments.Length >= 4 &&
                 string.Equals(arguments[2], MaieuticsCommandLanguage.Use, StringComparison.OrdinalIgnoreCase))
        {
            var path = GetRemainderAfterTokens(code, pathTokenCount);
            if (path.Length == 0 || path.IndexOfAny(['\r', '\n']) >= 0)
                throw new ArgumentException("A single-line workspace path is required.");

            selection = workspace.Use(path);
        }
        else
        {
            throw new MaieuticsCommandException(
                MaieuticsCommandException.CommandError, "Unknown workspace command or invalid arguments.");
        }

        return RenderWorkspace(selection);
    }

    private string ExecuteStatusCommand(string[] arguments)
    {
        if (arguments.Length != 2)
            throw new MaieuticsCommandException(
                MaieuticsCommandException.CommandError, "The status command does not accept arguments.");

        if (statusProvider is null)
            throw new MaieuticsCommandException(
                MaieuticsCommandException.Unavailable, "Status is not available in this host.");

        return MaieuticsStatusRenderer.Render(statusProvider.Capture());
    }

    private string ExecuteSessionCommand(string[] arguments)
    {
        if (sessionManager is null)
            throw new MaieuticsCommandException(
                MaieuticsCommandException.Unavailable, "Session commands are not available in this host.");

        if (arguments.Length == 2 ||
            (arguments.Length == 3 && string.Equals(
                arguments[2],
                MaieuticsCommandLanguage.Current,
                StringComparison.OrdinalIgnoreCase)))
            return RenderSession(sessionManager);

        if (arguments.Length == 3 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.List, StringComparison.OrdinalIgnoreCase))
            return RenderStoredSessions(sessionManager);

        if (arguments.Length == 3 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.New, StringComparison.OrdinalIgnoreCase))
        {
            sessionManager.StartNew();
            return RenderSession(sessionManager);
        }

        if (arguments.Length == 4 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.Resume, StringComparison.OrdinalIgnoreCase))
        {
            var sessionId = ResolveStoredSessionId(sessionManager, arguments[3]);
            try
            {
                sessionManager.Resume(sessionId);
            }
            catch (AgentException exception)
            {
                throw new MaieuticsCommandException(
                    MaieuticsCommandException.CommandError, exception.Message);
            }

            return RenderSession(sessionManager);
        }

        if (arguments.Length is 3 or 4 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.Gc, StringComparison.OrdinalIgnoreCase))
        {
            var graceHours = 24;
            if (arguments.Length == 4 &&
                (!int.TryParse(arguments[3], out graceHours) || graceHours < 0))
                throw new MaieuticsCommandException(
                    MaieuticsCommandException.CommandError,
                    "The GC grace period must be a non-negative number of hours.");

            return RenderGc(sessionManager, graceHours);
        }

        if (arguments.Length == 3 &&
            string.Equals(arguments[2], MaieuticsCommandLanguage.Repair, StringComparison.OrdinalIgnoreCase))
        {
            var links = sessionManager.RepairObjectView();
            return $"**View** ensured {links} object link(s) under view/sessions.";
        }

        throw new MaieuticsCommandException(
            MaieuticsCommandException.CommandError, "Unknown session command or invalid arguments.");
    }

    private static string RenderGc(MaieuticsAgentSessionManager manager, int graceHours)
    {
        var removed = manager.PruneObjects(TimeSpan.FromHours(graceHours));
        return $"**GC** removed {removed} unreferenced object(s) (grace {graceHours} h).";
    }

    private static AgentSessionId ResolveStoredSessionId(MaieuticsAgentSessionManager manager, string input)
    {
        if (Guid.TryParse(input, out var parsed)) return new AgentSessionId(parsed);

        var matches = manager.ListStoredSessions()
            .Where(session => session.Id.Value.ToString("N").StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0].Id,
            0 => throw new MaieuticsCommandException(
                MaieuticsCommandException.CommandError, $"No stored session matches '{input}'."),
            _ => throw new MaieuticsCommandException(
                MaieuticsCommandException.CommandError,
                $"'{input}' matches multiple stored sessions; use a longer prefix.")
        };
    }

    private static string RenderSession(MaieuticsAgentSessionManager manager)
    {
        var turns = manager.GetTranscriptSnapshot().Turns.Length;
        var persistence = manager.PersistenceEnabled ? "enabled" : "disabled";
        return $"**Session** `{manager.Id.Value.ToString("N")}` — {turns} turn(s) in memory · persistence {persistence}.";
    }

    private static string RenderStoredSessions(MaieuticsAgentSessionManager manager)
    {
        if (!manager.PersistenceEnabled)
            return "Transcript persistence is disabled (`Maieutics:Agent:Persistence:Enabled`).";

        var sessions = manager.ListStoredSessions();
        if (sessions.Count == 0) return "No stored sessions yet.";

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("| Session | Turns | Created (UTC) | Last activity (UTC) |");
        builder.Append("|---|---:|---|---|");
        foreach (var session in sessions)
        {
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"| `{session.Id.Value.ToString("N")[..12]}` | {session.TurnCount} | {session.CreatedAt.ToUniversalTime():yyyy-MM-dd HH:mm} | {session.LastActivityAt.ToUniversalTime():yyyy-MM-dd HH:mm} |");
        }

        return builder.ToString();
    }

    private static string GetRemainderAfterTokens(string code, int tokenCount)
    {
        var index = 0;
        for (var token = 0; token < tokenCount; token++)
        {
            while (index < code.Length && char.IsWhiteSpace(code[index])) index++;

            while (index < code.Length && !char.IsWhiteSpace(code[index])) index++;
        }

        while (index < code.Length && char.IsWhiteSpace(code[index])) index++;

        return code[index..].TrimEnd();
    }

    private static string RenderCurrent(MaieuticsModelProfileSelection selection)
    {
        if (selection.Profiles.Count == 0) return "### Current model\n\nNo model profile is configured.";

        var profile = selection.Profiles.Single(profile => profile.IsSelected);
        var selectionSource = profile.IsAutomatic
            ? "automatic session override"
            : selection.HasSessionOverride
                ? "session override"
                : "configured default";
        return $"""
                ### Current model

                - Profile: {MarkdownText.CodeSpan(profile.Id)} ({selectionSource})
                - Source: {MarkdownText.CodeSpan(profile.SourceId)}
                - Provider: {MarkdownText.CodeSpan(profile.Provider)}
                - Model: {MarkdownText.CodeSpan(profile.Model)}
                """;
    }

    private static string RenderWorkspace(WorkspaceSnapshot selection)
    {
        var selectionSource = selection.HasSessionOverride ? "session override" : "startup root";
        return $"""
                ### Current workspace

                - Root: {MarkdownText.CodeSpan(selection.RootPath)} ({selectionSource})
                """;
    }

    private static string RenderMcpList(IReadOnlyList<MaieuticsMcpServerInfo> servers)
    {
        if (servers.Count == 0) return "### MCP servers\n\nNo MCP servers are enabled.";

        var output = new System.Text.StringBuilder("### MCP servers\n\n");
        foreach (var server in servers)
        {
            output.Append("- ")
                .Append(MarkdownText.CodeSpan(server.Id))
                .Append(" — transport ")
                .Append(MarkdownText.CodeSpan(server.Transport))
                .Append(", state ")
                .Append(MarkdownText.CodeSpan(server.State.ToString()));
            if (server is { State: MaieuticsMcpServerState.Reconnecting, NextReconnectDelay: { } reconnectDelay })
                output.Append(", next reconnect in ")
                    .Append(MarkdownText.CodeSpan(reconnectDelay.ToString()));

            output.AppendLine();
            foreach (var tool in server.Tools)
            {
                output.Append("  - ")
                    .Append(MarkdownText.CodeSpan(tool.RemoteName))
                    .Append(" → ")
                    .Append(MarkdownText.CodeSpan(tool.ExposedName));
                if (!tool.Available) output.Append(" (unavailable)");

                output.AppendLine();
            }
        }

        return output.ToString();
    }

    private static string RenderList(MaieuticsModelProfileSelection selection)
    {
        if (selection.Profiles.Count == 0) return "### Model profiles\n\nNo model profiles are configured.";

        var output = new System.Text.StringBuilder("### Model profiles\n\n");
        foreach (var profile in selection.Profiles)
        {
            var markers = new List<string>(2);
            if (profile.IsSelected) markers.Add("selected");

            if (profile.IsDefault) markers.Add("default");

            if (profile.IsAutomatic) markers.Add("automatic");

            var suffix = markers.Count == 0 ? string.Empty : $" ({string.Join(", ", markers)})";
            output.Append("- ")
                .Append(MarkdownText.CodeSpan(profile.Id))
                .Append(": ")
                .Append(MarkdownText.CodeSpan(profile.Provider))
                .Append(" / ")
                .Append(MarkdownText.CodeSpan(profile.Model))
                .Append(", source ")
                .Append(MarkdownText.CodeSpan(profile.SourceId))
                .AppendLine(suffix);
        }

        return output.ToString();
    }

    private static string RenderAvailable(
        IReadOnlyList<DiscoveredModelGroup> groups,
        MaieuticsModelProfileSelection profiles)
    {
        if (groups.Count == 0)
        {
            var message = profiles.Profiles.Count == 0
                ? "No model sources are configured."
                : "No model sources support automatic discovery.";
            return $"### Available models\n\n{message}";
        }

        var output = new System.Text.StringBuilder("### Available models\n\n");
        var configuredModelIds = profiles.Profiles
            .Where(static profile => !profile.IsAutomatic)
            .Select(static p => (p.SourceId, p.Model))
            .ToHashSet(TupleComparer.Instance);

        foreach (var group in groups)
        {
            output.Append(MarkdownText.CodeSpan(group.Provider))
                .Append(" (source: ")
                .Append(MarkdownText.CodeSpan(group.SourceId))
                .Append(')');

            if (group.Failure is not null)
            {
                output.AppendLine()
                    .Append("  ❌ ")
                    .Append(MarkdownText.PlainText(
                        "The provider could not return available models. Check the Maieutics logs for details."))
                    .Append(" (")
                    .Append(MarkdownText.CodeSpan(ModelDiscoveryFailureCode(group.Failure.Value)))
                    .AppendLine(")");
                continue;
            }

            if (group.Models.Count == 0)
            {
                output.AppendLine()
                    .AppendLine("  _No models returned._");
                continue;
            }

            output.AppendLine();
            foreach (var model in group.Models)
            {
                output.Append("- ").Append(MarkdownText.CodeSpan(model.Id));
                if (model.ContextWindow.HasValue)
                    output.Append(" (").Append(model.ContextWindow.Value).Append(" context)");

                var configured = configuredModelIds
                    .Contains((group.SourceId, model.Id));
                if (!configured)
                {
                    var selector = MaieuticsAutomaticProfileSelector.Format(group.SourceId, model.Id);
                    output.Append(" — automatic profile ")
                        .Append(MarkdownText.CodeSpan(selector));
                }

                output.AppendLine();
            }
        }

        var missingFromApi = profiles.Profiles
            .Where(static profile => !profile.IsAutomatic)
            .Where(profile => !groups.Any(g =>
                string.Equals(g.SourceId, profile.SourceId, StringComparison.OrdinalIgnoreCase) &&
                g.Failure is null &&
                g.Models.Any(m => string.Equals(m.Id, profile.Model, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
        if (missingFromApi.Length > 0)
        {
            output.AppendLine();
            output.AppendLine("> ⚠️ The following configured models were not found in API results:");
            foreach (var profile in missingFromApi)
                output.Append("- ").Append(MarkdownText.CodeSpan(profile.Id))
                    .Append(": ").Append(MarkdownText.CodeSpan(profile.Model))
                    .Append(" (source ").Append(MarkdownText.CodeSpan(profile.SourceId)).AppendLine(")");
        }

        return output.ToString();
    }

    private static string ModelDiscoveryFailureCode(ModelDiscoveryFailureKind failure)
    {
        return failure switch
        {
            ModelDiscoveryFailureKind.ProviderError => "provider_error",
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown discovery failure.")
        };
    }

    private sealed class TupleComparer : IEqualityComparer<(string SourceId, string Model)>
    {
        internal static readonly TupleComparer Instance = new();

        public bool Equals((string SourceId, string Model) x, (string SourceId, string Model) y)
        {
            return string.Equals(x.SourceId, y.SourceId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.Model, y.Model, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string SourceId, string Model) obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Model));
        }
    }
}
