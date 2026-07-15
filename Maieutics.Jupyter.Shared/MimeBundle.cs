using System.Text.Json.Nodes;

namespace Maieutics.Jupyter.Shared;

public sealed record MimeBundle(IReadOnlyDictionary<string, JsonNode?> Data)
{
    public static MimeBundle FromJsonObject(JsonObject? value)
    {
        if (value is null)
        {
            return Empty;
        }

        return new MimeBundle(value.ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.DeepClone()));
    }

    public static MimeBundle Empty { get; } = new(new Dictionary<string, JsonNode?>());
}