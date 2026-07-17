namespace Maieutics.Providers.OpenAI;

/// <summary>Identifies an OpenAI API shape configured for the Maieutics executable.</summary>
public enum OpenAiApiFlavor
{
    /// <summary>Uses the OpenAI Responses API.</summary>
    Responses,

    /// <summary>Uses the OpenAI Chat Completions API.</summary>
    ChatCompletions
}