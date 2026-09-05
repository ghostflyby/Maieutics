using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.DenoRepl;

/// <summary>
///     Identifies a REPL rich display across tracked updates. The frontend maps a display id
///     onto one updatable output; the value format matches the kernel-side identity so a
///     display survives the adapter boundary unchanged.
/// </summary>
internal readonly record struct ReplDisplayId
{
    public ReplDisplayId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A REPL display ID cannot be empty.", nameof(value));

        Value = value;
    }

    public string Value { get; }

    public static ReplDisplayId Create()
    {
        return new ReplDisplayId(Guid.NewGuid().ToString("N"));
    }

    public override string ToString()
    {
        return Value ?? string.Empty;
    }
}

/// <summary>
///     The provider-neutral rich display bundle a REPL execution presents: mime type to
///     payload. Text mimes carry string values; structured mimes carry JSON values. Binary
///     mimes are frontend-encoding concerns and never travel as base64 through this model.
/// </summary>
internal sealed record ReplDisplayBundle(IReadOnlyDictionary<string, JsonElement> Data)
{
    private static IReadOnlyDictionary<string, JsonElement> EmptyData { get; } =
        new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>());

    public static ReplDisplayBundle Empty { get; } = new(EmptyData);

    public static ReplDisplayBundle FromText(string text)
    {
        return new ReplDisplayBundle(new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>
        {
            ["text/plain"] = JsonSerializer.SerializeToElement(text, ReplJsonContext.Default.String)
        }));
    }

    public static ReplDisplayBundle FromMarkdown(string markdown, string? plainText = null)
    {
        return new ReplDisplayBundle(new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>
        {
            ["text/markdown"] = JsonSerializer.SerializeToElement(markdown, ReplJsonContext.Default.String),
            ["text/plain"] = JsonSerializer.SerializeToElement(
                plainText ?? markdown,
                ReplJsonContext.Default.String)
        }));
    }
}

/// <summary>The comm message kind: open, message, or close.</summary>
internal enum ReplCommKind
{
    Open,
    Message,
    Close
}

/// <summary>
///     One comm message between a REPL child and the frontend surface. Comm carries
///     widget-like frontend↔REPL traffic; the body is the JSON content plus the raw binary
///     buffers that traveled with it. This is the transport-neutral shape — protocol
///     adapters translate to their wire representations at their own boundaries.
/// </summary>
/// <param name="Kind">The comm message kind.</param>
/// <param name="CommId">The comm channel identifier.</param>
/// <param name="TargetName">The comm target name; present only on open.</param>
/// <param name="Data">The JSON data payload, or null when the message carried none.</param>
/// <param name="Metadata">Protocol metadata that traveled with the message.</param>
/// <param name="Buffers">The binary buffers that traveled with the message.</param>
internal sealed record ReplCommMessage(
    ReplCommKind Kind,
    string CommId,
    string? TargetName,
    JsonElement? Data,
    JsonElement? Metadata,
    IReadOnlyList<byte[]> Buffers);

/// <summary>A reference to a large binary display payload stored in the object bypass:
/// the frontend dereferences it via the object endpoint (invariant 26).</summary>
internal sealed record ReplObjectReference(
    [property: System.Text.Json.Serialization.JsonPropertyName("$object")] string Sha256,
    [property: System.Text.Json.Serialization.JsonPropertyName("byteLength")] long ByteLength);

/// <summary>Source-generated JSON binding for REPL display and comm payloads.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ReplObjectReference))]
internal sealed partial class ReplJsonContext : JsonSerializerContext;
