using System.Text.Json;
using Maieutics.Control;

namespace Maieutics.Plugins.Contributions;

/// <summary>One named system instruction supplied by a built-in or approved plugin.</summary>
internal sealed record SystemPromptContribution(string Source, string Name, string Content);

/// <summary>Captures instructions for one run; acquired profiles retain their immutable copy.</summary>
internal interface ISystemPromptSource
{
    Task<IReadOnlyList<SystemPromptContribution>> GetSystemPromptsAsync(CancellationToken cancellationToken);
}

/// <summary>Static manifest entries and per-run worker hooks share one bounded payload grammar.</summary>
internal sealed class SystemPromptContributionKind : ContributionKindContract
{
    public static readonly SystemPromptContributionKind Instance = new();
    internal const int MaximumEntries = 32;
    internal const int MaximumEntryCharacters = 16_384;
    internal const int MaximumSourceCharacters = 32_768;
    internal const int MaximumTotalCharacters = 65_536;
    internal static readonly TimeSpan CollectionWaitBudget = TimeSpan.FromSeconds(5);
    public override string KindName => ReplExtensionPointName.SystemPrompt;
    public override string? DataEntryName => null;
    public override string ExtensionPointName => ReplExtensionPointName.SystemPrompt;
    public override bool HasComputeForm => true;
    public override bool HasPublishForm => false;
    public override string? PublishCapability => null;
    public override bool HasDeclarativeRegistrations => false;
    public override ContributionKindMetadata Metadata { get; } = new(
        MaximumEntries, MaximumEntries, false, false, null, false);

    internal static bool TryRead(JsonElement value, string source,
        out IReadOnlyList<SystemPromptContribution> entries, out string? error)
    {
        entries = [];
        error = "Expected a bounded array of unique {name, content} system instructions.";
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumEntries) return false;
        var result = new List<SystemPromptContribution>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var characters = 0;
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String ||
                nameValue.GetString() is not { Length: > 0 and <= 64 } name ||
                name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')) ||
                !names.Add(name) ||
                !entry.TryGetProperty("content", out var contentValue) || contentValue.ValueKind != JsonValueKind.String ||
                contentValue.GetString() is not { Length: > 0 and <= MaximumEntryCharacters } content ||
                string.IsNullOrWhiteSpace(content)) return false;
            characters += content.Length;
            if (characters > MaximumSourceCharacters) return false;
            result.Add(new(source, name, content));
        }
        entries = result.ToArray();
        error = null;
        return true;
    }
}
