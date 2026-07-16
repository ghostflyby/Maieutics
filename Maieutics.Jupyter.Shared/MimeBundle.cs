using System.Collections.ObjectModel;
using System.Text.Json;

namespace Maieutics.Jupyter.Shared;

public sealed record MimeBundle(IReadOnlyDictionary<string, JsonElement> Data)
{
    private static IReadOnlyDictionary<string, JsonElement> EmptyData { get; } =
        new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>());

    public static MimeBundle Empty { get; } = new(EmptyData);

    public static MimeBundle FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new MimeBundle(new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>
        {
            ["text/plain"] = JsonSerializer.SerializeToElement(text, JupyterJsonContext.Default.String)
        }));
    }

    public static MimeBundle FromMarkdown(string markdown, string? plainText = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return new MimeBundle(new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>
        {
            ["text/markdown"] = JsonSerializer.SerializeToElement(markdown, JupyterJsonContext.Default.String),
            ["text/plain"] = JsonSerializer.SerializeToElement(plainText ?? markdown, JupyterJsonContext.Default.String)
        }));
    }
}