namespace Maieutics.Providers.Anthropic;

internal sealed class AnthropicSourceOptions
{
    public AnthropicSourceOptions()
    {
    }

    public AnthropicSourceOptions(string apiKey, Uri? endpoint = null)
    {
        ApiKey = apiKey;
        Endpoint = endpoint;
    }

    public string? Provider { get; set; }

    public string ApiKey { get; set; } = string.Empty;

    public Uri? Endpoint { get; set; }

    internal void Validate()
    {
        if (Provider is not null && !string.Equals(Provider, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Anthropic source Provider must be 'Anthropic'.", nameof(Provider));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ApiKey);
        if (Endpoint is not null &&
            (!Endpoint.IsAbsoluteUri || Endpoint.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException(
                "The Anthropic endpoint must be an absolute HTTP or HTTPS URI.",
                nameof(Endpoint));
        }
    }
}