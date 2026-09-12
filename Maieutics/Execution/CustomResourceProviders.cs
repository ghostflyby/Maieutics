using System.Text.Json.Serialization;

namespace Maieutics.Execution;

/// <summary>Configuration for the virtual resource plane (ADR 0026 decision 4):
/// user-declared custom providers. Startup-bound like terminal options; declare new
/// schemes by pointing an <c>httpBridge</c> provider at an endpoint that answers
/// <c>GET &lt;endpoint&gt;?uri=&lt;encoded&gt;</c> with the resource body.</summary>
internal sealed class ResourceProviderOptions
{
    internal const string SectionName = "Maieutics:Resources";

    [JsonPropertyName("customProviders")]
    public List<CustomResourceProviderOptions> CustomProviders { get; set; } = [];

    internal void Validate()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in CustomProviders)
        {
            provider.Validate();
            if (!names.Add(provider.Name))
                throw new ArgumentException(
                    $"The custom resource provider name '{provider.Name}' is declared more than once.");
        }
    }
}

internal sealed class CustomResourceProviderOptions
{
    internal const string HttpBridgeKind = "httpBridge";

    /// <summary>The reserved schemes no custom provider may claim (ADR 0026 decision 2).</summary>
    internal static readonly IReadOnlySet<string> ReservedSchemes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "workspace", "mcp", "file" };

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("scheme")]
    public string Scheme { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = HttpBridgeKind;

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; set; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.AsSpan().IndexOfAny([' ', '\t', '/']) >= 0)
            throw new ArgumentException(
                "A custom resource provider requires a name without whitespace or slashes.");

        if (!Uri.CheckSchemeName(Scheme) || !Uri.TryCreate($"{Scheme}://x", UriKind.Absolute, out _))
            throw new ArgumentException(
                $"The custom resource provider '{Name}' declares an invalid URI scheme '{Scheme}'.");

        if (ReservedSchemes.Contains(Scheme))
            throw new ArgumentException(
                $"The custom resource provider '{Name}' cannot claim the reserved scheme '{Scheme}:'.");

        if (!string.Equals(Kind, HttpBridgeKind, StringComparison.Ordinal))
            throw new ArgumentException(
                $"The custom resource provider '{Name}' declares unknown kind '{Kind}'.");

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException(
                $"The custom resource provider '{Name}' requires an absolute http(s) endpoint.");

        if (TimeoutSeconds is { } timeout and (< 1 or > 120))
            throw new ArgumentException(
                $"The custom resource provider '{Name}' timeoutSeconds must be between 1 and 120.");
    }
}

/// <summary>A user-configured provider that resolves one custom scheme by forwarding a
/// GET to its endpoint and streaming the response body (ADR 0026 decision 4). The
/// endpoint is the trust and network boundary, exactly like an MCP `http` server.</summary>
internal sealed class HttpBridgeResourceProvider : IResourceProvider
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly TimeSpan timeout;

    internal HttpBridgeResourceProvider(
        HttpClient client,
        CustomResourceProviderOptions options)
    {
        options.Validate();
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        endpoint = new Uri(options.Endpoint!);
        timeout = TimeSpan.FromSeconds(options.TimeoutSeconds ?? 15);
        Id = $"custom:{options.Name}";
        Claims = [new ResourceClaim(options.Scheme)];
    }

    public string Id { get; }

    public ResourceProviderClass Class => ResourceProviderClass.Custom;

    public IReadOnlyList<ResourceClaim> Claims { get; }

    public async ValueTask<ResourceReadResult> ReadAsync(
        string uri,
        ResourceReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        cancellationToken.ThrowIfCancellationRequested();

        var builder = new UriBuilder(endpoint);
        var encoded = $"uri={Uri.EscapeDataString(uri)}";
        builder.Query = string.IsNullOrEmpty(builder.Query)
            ? encoded
            : $"{builder.Query[1..]}&{encoded}";

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        HttpResponseMessage response;
        try
        {
            response = await client
                .GetAsync(builder.Uri, HttpCompletionOption.ResponseHeadersRead, timeoutCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ResourceException(
                "resource_provider_failed",
                $"The '{Id}' endpoint timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new ResourceException(
                "resource_provider_failed",
                $"The '{Id}' endpoint could not be reached.",
                exception);
        }

        try
        {
            switch ((int)response.StatusCode)
            {
                case 200:
                    var stream = await response.Content
                        .ReadAsStreamAsync(timeoutCancellation.Token)
                        .ConfigureAwait(false);
                    return new ResourceReadResult(
                        new HttpResponseMessageStream(stream, response),
                        response.Content.Headers.ContentType?.ToString());
                case 404:
                    throw new ResourceException(
                        "resource_not_found",
                        $"The '{Id}' endpoint does not serve '{uri}'.");
                case 413:
                    throw new ResourceException(
                        "resource_too_large",
                        $"The '{Id}' endpoint reported '{uri}' as too large.");
                case 400:
                    throw new ResourceException(
                        "resource_invalid_uri",
                        $"The '{Id}' endpoint rejected '{uri}'.");
                default:
                    throw new ResourceException(
                        "resource_provider_failed",
                        $"The '{Id}' endpoint returned status {(int)response.StatusCode}.");
            }
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Wraps the response body stream so disposing the handed-out resource
    /// body also disposes the <see cref="HttpResponseMessage"/> that owns the
    /// connection pool slot.</summary>
    private sealed class HttpResponseMessageStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The resource body stream is read-only.");

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
        }
    }
}
