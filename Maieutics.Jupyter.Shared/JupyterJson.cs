using System.Text.Json;

namespace Maieutics.Jupyter.Shared;

public static class JupyterJson
{
    public static JsonElement EmptyObject { get; } = ParseObject("{}");

    internal static byte[] EmptyObjectUtf8 { get; } = "{}"u8.ToArray();

    private static JsonElement ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind != JsonValueKind.Object
            ? throw new JsonException("Expected a JSON object.")
            : document.RootElement.Clone();
    }
}