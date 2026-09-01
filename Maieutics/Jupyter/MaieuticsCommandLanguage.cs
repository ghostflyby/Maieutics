using Maieutics.Configuration;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter;

internal static class MaieuticsCommandLanguage
{
    internal const string LegacyRoot = "%maieutics";
    private const string CanonicalMcpCommand = "%mcp";
    private const string CanonicalModelCommand = "%model";
    private const string CanonicalSessionCommand = "%session";
    private const string CanonicalStatusCommand = "%status";
    private const string CanonicalWorkspaceCommand = "%workspace";
    private const string SlashLeader = "/";
    internal const string Mcp = "mcp";
    internal const string Model = "model";
    internal const string Session = "session";
    internal const string Status = "status";
    internal const string Workspace = "workspace";
    internal const string Current = "current";
    internal const string List = "list";
    internal const string Use = "use";
    internal const string Reset = "reset";
    internal const string New = "new";
    internal const string Resume = "resume";
    internal const string Available = "available";
    internal const string RefreshFlag = "--refresh";

    private static readonly string[] CommandPrefixes =
    [
        CanonicalMcpCommand, CanonicalModelCommand, CanonicalSessionCommand, CanonicalStatusCommand,
        CanonicalWorkspaceCommand, LegacyRoot
    ];

    private static readonly string[] RootCompletionMatches =
    [
        CanonicalMcpCommand, CanonicalModelCommand, CanonicalSessionCommand, CanonicalStatusCommand,
        CanonicalWorkspaceCommand, LegacyRoot
    ];

    private static readonly string[] SlashCompletionMatches =
        [CanonicalMcpCommand, CanonicalModelCommand, CanonicalSessionCommand, CanonicalStatusCommand, CanonicalWorkspaceCommand];

    private static readonly string[] RootCommandMatches = [Mcp, Model, Session, Workspace];
    private static readonly string[] McpCommandMatches = [List];
    private static readonly string[] ModelCommandMatches = [Current, List, Use, Reset, Available];
    private static readonly string[] SessionCommandMatches = [Current, List, Resume, New];
    private static readonly string[] WorkspaceCommandMatches = [Current, Use, Reset];

    internal static bool IsCommandCell(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        var trimmed = code.AsSpan().TrimStart();
        if (trimmed.IsEmpty) return false;

        var firstTokenEnd = 0;
        while (firstTokenEnd < trimmed.Length && !char.IsWhiteSpace(trimmed[firstTokenEnd])) firstTokenEnd++;

        var firstToken = trimmed[..firstTokenEnd];
        foreach (var prefix in CommandPrefixes)
            if (firstToken.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    internal static string[]? NormalizeCommandArguments(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Length == 0) return null;

        if (arguments[0].Equals(CanonicalModelCommand, StringComparison.OrdinalIgnoreCase))
            return [LegacyRoot, Model, .. arguments[1..]];

        if (arguments[0].Equals(CanonicalMcpCommand, StringComparison.OrdinalIgnoreCase))
            return [LegacyRoot, Mcp, .. arguments[1..]];

        if (arguments[0].Equals(CanonicalStatusCommand, StringComparison.OrdinalIgnoreCase))
            return [LegacyRoot, Status, .. arguments[1..]];

        if (arguments[0].Equals(CanonicalSessionCommand, StringComparison.OrdinalIgnoreCase))
            return [LegacyRoot, Session, .. arguments[1..]];

        if (arguments[0].Equals(CanonicalWorkspaceCommand, StringComparison.OrdinalIgnoreCase))
            return [LegacyRoot, Workspace, .. arguments[1..]];

        if (arguments[0].Equals(LegacyRoot, StringComparison.OrdinalIgnoreCase))
            if (arguments.Length >= 2 &&
                (arguments[1].Equals(Mcp, StringComparison.OrdinalIgnoreCase) ||
                 arguments[1].Equals(Model, StringComparison.OrdinalIgnoreCase) ||
                 arguments[1].Equals(Session, StringComparison.OrdinalIgnoreCase) ||
                 arguments[1].Equals(Workspace, StringComparison.OrdinalIgnoreCase)))
                return [.. arguments];

        return null;
    }

    internal static JupyterCompletionResult Complete(
        JupyterCompleteRequest request,
        IReadOnlyList<MaieuticsModelProfileInfo> profiles,
        IReadOnlyList<MaieuticsModelProfileInfo> automaticProfiles,
        IReadOnlyList<string> sourceIds)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(automaticProfiles);
        ArgumentNullException.ThrowIfNull(sourceIds);

        var cursorIndex = JupyterCursorPosition.ToUtf16Index(request.Code, request.CursorPosition);
        var tokenStart = cursorIndex;
        while (tokenStart > 0 && !char.IsWhiteSpace(request.Code[tokenStart - 1])) tokenStart--;

        var tokenEnd = cursorIndex;
        while (tokenEnd < request.Code.Length && !char.IsWhiteSpace(request.Code[tokenEnd])) tokenEnd++;

        var prefix = request.Code[tokenStart..cursorIndex];
        var token = request.Code[tokenStart..tokenEnd];
        var precedingTokens = request.Code[..tokenStart]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var matches = precedingTokens.Length == 0 && token.StartsWith(SlashLeader, StringComparison.Ordinal)
            ? CompleteSlashDiscovery(token)
            : CompletePrefixed(precedingTokens, prefix, token, profiles, automaticProfiles, sourceIds);

        return new JupyterCompletionResult(
            matches,
            JupyterCursorPosition.FromUtf16Index(request.Code, tokenStart),
            JupyterCursorPosition.FromUtf16Index(request.Code, tokenEnd));
    }

    private static string[] CompleteSlashDiscovery(string token)
    {
        var commandName = token[SlashLeader.Length..];
        return SlashCompletionMatches
            .Where(candidate => candidate.AsSpan(1).StartsWith(commandName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static string[] CompletePrefixed(
        string[] precedingTokens,
        string prefix,
        string token,
        IReadOnlyList<MaieuticsModelProfileInfo> profiles,
        IReadOnlyList<MaieuticsModelProfileInfo> automaticProfiles,
        IReadOnlyList<string> sourceIds)
    {
        var candidates = CompleteCandidates(precedingTokens, profiles, automaticProfiles, sourceIds);
        if (precedingTokens.Length == 0 &&
            prefix.Equals(LegacyRoot, StringComparison.OrdinalIgnoreCase) &&
            token.Equals(LegacyRoot, StringComparison.OrdinalIgnoreCase))
            candidates = RootCommandMatches.Select(command => $"{LegacyRoot} {command}");

        return candidates
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Where(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> CompleteCandidates(
        string[] precedingTokens,
        IReadOnlyList<MaieuticsModelProfileInfo> profiles,
        IReadOnlyList<MaieuticsModelProfileInfo> automaticProfiles,
        IReadOnlyList<string> sourceIds)
    {
        return precedingTokens switch
        {
            [] => RootCompletionMatches,
            [var command] when command.Equals(CanonicalMcpCommand, StringComparison.OrdinalIgnoreCase) =>
                McpCommandMatches,
            [var command] when command.Equals(CanonicalModelCommand, StringComparison.OrdinalIgnoreCase) =>
                ModelCommandMatches,
            [var command] when command.Equals(CanonicalSessionCommand, StringComparison.OrdinalIgnoreCase) =>
                SessionCommandMatches,
            [var command] when command.Equals(CanonicalWorkspaceCommand, StringComparison.OrdinalIgnoreCase) =>
                WorkspaceCommandMatches,
            [var command, var subcommand]
                when command.Equals(CanonicalModelCommand, StringComparison.OrdinalIgnoreCase) &&
                     subcommand.Equals(Use, StringComparison.OrdinalIgnoreCase) =>
                ProfileCandidates(profiles, automaticProfiles),
            [var command, var subcommand]
                when command.Equals(CanonicalModelCommand, StringComparison.OrdinalIgnoreCase) &&
                     subcommand.Equals(Available, StringComparison.OrdinalIgnoreCase) =>
                new[] { RefreshFlag }.Concat(sourceIds),
            [var command] when command.Equals(LegacyRoot, StringComparison.OrdinalIgnoreCase) =>
                RootCommandMatches,
            [var command, var family]
                when command.Equals(LegacyRoot, StringComparison.OrdinalIgnoreCase) &&
                     family.Equals(Mcp, StringComparison.OrdinalIgnoreCase) =>
                McpCommandMatches,
            [var command, var family]
                when command.Equals(LegacyRoot, StringComparison.OrdinalIgnoreCase) &&
                     family.Equals(Model, StringComparison.OrdinalIgnoreCase) =>
                ModelCommandMatches,
            [var command, var family]
                when command.Equals(LegacyRoot, StringComparison.OrdinalIgnoreCase) &&
                     family.Equals(Session, StringComparison.OrdinalIgnoreCase) =>
                SessionCommandMatches,
            [var command, var family]
                when command.Equals(LegacyRoot, StringComparison.OrdinalIgnoreCase) &&
                     family.Equals(Workspace, StringComparison.OrdinalIgnoreCase) =>
                WorkspaceCommandMatches,
            [var command, var family, var subcommand]
                when command.Equals(LegacyRoot, StringComparison.OrdinalIgnoreCase) &&
                     family.Equals(Model, StringComparison.OrdinalIgnoreCase) &&
                     subcommand.Equals(Use, StringComparison.OrdinalIgnoreCase) =>
                ProfileCandidates(profiles, automaticProfiles),
            [var command, var family, var subcommand]
                when command.Equals(LegacyRoot, StringComparison.OrdinalIgnoreCase) &&
                     family.Equals(Model, StringComparison.OrdinalIgnoreCase) &&
                     subcommand.Equals(Available, StringComparison.OrdinalIgnoreCase) =>
                new[] { RefreshFlag }.Concat(sourceIds),
            _ => []
        };
    }

    private static IEnumerable<string> ProfileCandidates(
        IReadOnlyList<MaieuticsModelProfileInfo> profiles,
        IReadOnlyList<MaieuticsModelProfileInfo> automaticProfiles)
    {
        return profiles
            .Where(static profile => !profile.IsAutomatic)
            .SelectMany(static profile => new[] { profile.Id, profile.Model })
            .Concat(profiles
                .Where(static profile => profile.IsAutomatic)
                .Select(static profile => profile.Id))
            .Concat(automaticProfiles.Select(static profile => profile.Id))
            .Concat(automaticProfiles
                .GroupBy(static profile => profile.Model, StringComparer.OrdinalIgnoreCase)
                .Where(static group => group.Count() == 1)
                .Select(static group => group.Key));
    }
}
