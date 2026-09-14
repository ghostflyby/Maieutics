namespace Maieutics.Providers.OpenAI;

/// <summary>The apply_patch tool contract shared by the providers that expose it: the local
/// function name, and the tool-visibility sets that keep one definition per name on the wire.
/// The wire translation itself lives in <see cref="ResponsesApplyPatchChatClient"/>, which uses
/// the OpenAI SDK's native apply_patch support.</summary>
internal static class ApplyPatchFunctions
{
    /// <summary>The local function the runtime registers for patch application.</summary>
    public const string ToolName = "apply_patch";

    /// <summary>The general edit functions the Responses wire replaces with the built-in
    /// apply_patch tool.</summary>
    public static readonly IReadOnlySet<string> ResponsesHiddenToolNames =
        new HashSet<string>(["write_text", "edit_text"], StringComparer.Ordinal);

    /// <summary>The apply_patch function, which only has meaning where the provider-level
    /// adapter can project it.</summary>
    public static readonly IReadOnlySet<string> NonResponsesHiddenToolNames =
        new HashSet<string>([ToolName], StringComparer.Ordinal);
}
