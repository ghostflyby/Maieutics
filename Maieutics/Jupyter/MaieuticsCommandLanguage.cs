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
        IReadOnlyList<string> sourceIds)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profiles);
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
                profiles.Select(static profile => profile.Id),
            [var root, var model, var available]
                when root.Equals(Root, StringComparison.OrdinalIgnoreCase) &&
                     model.Equals(Model, StringComparison.OrdinalIgnoreCase) &&
                     available.Equals(Available, StringComparison.OrdinalIgnoreCase) =>
                new[] { RefreshFlag }.Concat(sourceIds),
            _ => []
        };
        var matches = candidates
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