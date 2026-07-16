using System.Text.Json;

namespace Maieutics.Jupyter.Shared;

public readonly record struct JupyterDisplayId
{
    public JupyterDisplayId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A Jupyter display ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static JupyterDisplayId Create() => new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value ?? string.Empty;
}

public static class JupyterDisplayTransient
{
    public const string DisplayIdPropertyName = "display_id";

    public static IReadOnlyDictionary<string, JsonElement> Create(JupyterDisplayId displayId)
    {
        if (string.IsNullOrWhiteSpace(displayId.Value))
        {
            throw new ArgumentException("A Jupyter display ID cannot be empty.", nameof(displayId));
        }

        return new Dictionary<string, JsonElement>
        {
            [DisplayIdPropertyName] = JsonSerializer.SerializeToElement(displayId.Value)
        };
    }

    public static JupyterDisplayId? GetDisplayId(IReadOnlyDictionary<string, JsonElement>? transient)
    {
        if (transient is null || !transient.TryGetValue(DisplayIdPropertyName, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new JupyterProtocolException("Jupyter transient.display_id must be a string.");
        }

        try
        {
            return new JupyterDisplayId(element.GetString() ?? "");
        }
        catch (ArgumentException exception)
        {
            throw new JupyterProtocolException("Jupyter transient.display_id cannot be empty.", exception);
        }
    }
}