using Maieutics.Configuration;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter;

internal static class MaieuticsCommandLanguage
{
    internal const string Root = "%maieutics";
    internal const string Model = "model";
    internal const string Current = "current";
    internal const string List = "list";
    internal const string Use = "use";
    internal const string Reset = "reset";
    internal const string Available = "available";
    internal const string RefreshFlag = "--refresh";

    private static readonly string[] RootMatches = [Root];
    private static readonly string[] RootCommandMatches = [Model];
    private static readonly string[] ModelCommandMatches = [Current, List, Use, Reset, Available];

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
        while (tokenStart > 0 && !char.IsWhiteSpace(request.Code[tokenStart - 1]))
        {
            tokenStart--;
        }

        var tokenEnd = cursorIndex;
        while (tokenEnd < request.Code.Length && !char.IsWhiteSpace(request.Code[tokenEnd]))
        {
            tokenEnd++;
        }

        var prefix = request.Code[tokenStart..cursorIndex];
        var token = request.Code[tokenStart..tokenEnd];
        var precedingTokens = request.Code[..tokenStart]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = precedingTokens switch
        {
            [] => RootMatches,
            [var root] when root.Equals(Root, StringComparison.OrdinalIgnoreCase) => RootCommandMatches,
            [var root, var model] when root.Equals(Root, StringComparison.OrdinalIgnoreCase) &&
                                       model.Equals(Model, StringComparison.OrdinalIgnoreCase) =>
                ModelCommandMatches,
            [var root, var model, var use]
                when root.Equals(Root, StringComparison.OrdinalIgnoreCase) &&
                     model.Equals(Model, StringComparison.OrdinalIgnoreCase) &&
                     use.Equals(Use, StringComparison.OrdinalIgnoreCase) =>
                profiles
                    .Where(static profile => !profile.IsAutomatic)
                    .SelectMany(static profile => new[] { profile.Id, profile.Model })
                    .Concat(profiles
                        .Where(static profile => profile.IsAutomatic)
                        .Select(static profile => profile.Id))
                    .Concat(automaticProfiles.Select(static profile => profile.Id))
                    .Concat(automaticProfiles
                        .GroupBy(static profile => profile.Model, StringComparer.OrdinalIgnoreCase)
                        .Where(static group => group.Count() == 1)
                        .Select(static group => group.Key)),
            [var root, var model, var available]
                when root.Equals(Root, StringComparison.OrdinalIgnoreCase) &&
                     model.Equals(Model, StringComparison.OrdinalIgnoreCase) &&
                     available.Equals(Available, StringComparison.OrdinalIgnoreCase) =>
                new[] { RefreshFlag }.Concat(sourceIds),
            _ => []
        };

        if (precedingTokens.Length == 0 &&
            prefix.Equals(Root, StringComparison.OrdinalIgnoreCase) &&
            token.Equals(Root, StringComparison.OrdinalIgnoreCase))
        {
            candidates = [$"{Root} {Model}"];
        }

        var matches = candidates
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Where(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new JupyterCompletionResult(
            matches,
            JupyterCursorPosition.FromUtf16Index(request.Code, tokenStart),
            JupyterCursorPosition.FromUtf16Index(request.Code, tokenEnd));
    }
}