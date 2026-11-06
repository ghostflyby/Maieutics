using System.Text.Json;

namespace Maieutics.Plugins.Contributions;

/// <summary>Composes code-shipped declarative contributions with the approved plugin hook source.
/// Built-ins have no disk-loaded plugin identity and never confer an approval exemption on plugins.</summary>
internal sealed class SystemPromptCatalog(
    IReadOnlyList<SystemPromptContribution> builtIns, ISystemPromptSource? plugins = null) : ISystemPromptSource
{
    public async Task<IReadOnlyList<SystemPromptContribution>> GetSystemPromptsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contributed = plugins is null ? [] : await plugins.GetSystemPromptsAsync(cancellationToken).ConfigureAwait(false);
        return [.. builtIns, .. contributed];
    }

    /// <summary>Loads a code-shipped manifest's static SystemPrompt entries using the plugin grammar.</summary>
    internal static IReadOnlyList<SystemPromptContribution> ReadEmbeddedDeclaration(string resourceName, string source)
    {
        using var stream = typeof(SystemPromptCatalog).Assembly.GetManifestResourceStream(resourceName) ??
            throw new InvalidOperationException($"The built-in SystemPrompt declaration '{resourceName}' is missing.");
        using var document = JsonDocument.Parse(stream);
        var declared = document.RootElement.GetProperty("extensions").GetProperty(ReplPoint);
        if (!SystemPromptContributionKind.TryRead(declared, source, out var entries, out var error))
            throw new InvalidOperationException(error);
        return entries;
    }

    private const string ReplPoint = "SystemPrompt";
}
