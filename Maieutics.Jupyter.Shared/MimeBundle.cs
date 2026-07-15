using System.Text.Json;

namespace Maieutics.Jupyter.Shared;

public sealed record MimeBundle(IReadOnlyDictionary<string, JsonElement> Data)
{
    public static MimeBundle Empty { get; } = new(new Dictionary<string, JsonElement>());
}