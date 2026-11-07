using System.ClientModel;
using System.Text.Json;
using Maieutics.Agent;

namespace Maieutics.Providers;

/// <summary>Normalizes provider failures without copying upstream messages into frontend output.</summary>
internal static class ProviderErrors
{
    internal const int MaximumErrorBodyBytes = 64 * 1024;

    internal static AgentProviderException Normalize(Exception exception)
    {
        if (exception is AgentProviderException provider) return provider;
        if (exception is ClientResultException result)
        {
            string? code = null;
            try
            {
                if (result.GetRawResponse() is { } response && response.Content.ToMemory() is { Length: <= MaximumErrorBodyBytes } body)
                    code = ReadCode(body);
            }
            catch (InvalidOperationException)
            {
                // A disposed or unbuffered SDK response still supplies its HTTP status.
            }
            if (result.Status > 0) return FromResponse(result.Status, code, exception);
        }

        // SDK transports can wrap network failures; retain the outer exception for diagnostics.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } status })
                return FromResponse((int)status, null, exception);
            var kind = current switch
            {
                OperationCanceledException or TimeoutException => AgentProviderFailureKind.Timeout,
                InvalidDataException or JsonException => AgentProviderFailureKind.InvalidResponse,
                HttpRequestException or IOException => AgentProviderFailureKind.Connection,
                _ => AgentProviderFailureKind.Unknown,
            };
            if (kind != AgentProviderFailureKind.Unknown) return new AgentProviderException(kind, exception);
        }
        return new AgentProviderException(exception);
    }

    internal static AgentProviderException FromResponse(int? status, string? code, Exception exception)
    {
        var kind = code switch
        {
            "invalid_api_key" or "authentication_error" => AgentProviderFailureKind.Authentication,
            "permission_denied" or "permission_error" => AgentProviderFailureKind.PermissionDenied,
            "model_not_found" or "not_found_error" => AgentProviderFailureKind.NotFound,
            "context_length_exceeded" => AgentProviderFailureKind.ContextLimit,
            "rate_limit_exceeded" or "rate_limit_error" => AgentProviderFailureKind.RateLimited,
            "insufficient_quota" or "billing_hard_limit_reached" or "billing_error" => AgentProviderFailureKind.QuotaExceeded,
            "overloaded_error" or "server_error" or "api_error" => AgentProviderFailureKind.Unavailable,
            "invalid_request_error" => AgentProviderFailureKind.InvalidRequest,
            _ => AgentProviderFailureKind.Unknown,
        };
        // Unknown codes and all free-form upstream text stay off the wire.
        if (kind == AgentProviderFailureKind.Unknown) code = null;
        if (kind == AgentProviderFailureKind.Unknown || code == "invalid_request_error")
            kind = status switch
            {
                401 => AgentProviderFailureKind.Authentication,
                403 => AgentProviderFailureKind.PermissionDenied,
                404 => AgentProviderFailureKind.NotFound,
                402 => AgentProviderFailureKind.QuotaExceeded,
                408 or 504 => AgentProviderFailureKind.Timeout,
                429 => AgentProviderFailureKind.RateLimited,
                >= 500 => AgentProviderFailureKind.Unavailable,
                >= 400 and < 500 => AgentProviderFailureKind.InvalidRequest,
                _ => kind,
            };
        return new AgentProviderException(kind, exception, status, code);
    }

    internal static string? ReadCode(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            return ReadCode(document.RootElement);
        }
        catch (JsonException)
        {
            return null; // HTML proxy errors and malformed bodies still classify by HTTP status.
        }
    }

    internal static string? ReadCode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object) root = error;
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
            return code.GetString();
        return root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() : null;
    }
}
