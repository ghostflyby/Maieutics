using System.Text.Json;
using Maieutics.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Maieutics.Control;

/// <summary>
///     The <c>GET /v1/resource</c> endpoint of the control channel (ADR 0026 decision 5):
///     Deno children read virtual resource URIs (workspace, MCP, custom bridges) as raw
///     bytes over the same peer-authenticated surface as script tools.
/// </summary>
internal sealed partial class ReplControlHost
{
    private async Task HandleResourceGetAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var uri = context.Request.Query["uri"].ToString();
        if (string.IsNullOrWhiteSpace(uri) || !ResourceRegistry.TryParseUri(uri, out var parsed))
        {
            await WriteResourceErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "resource_invalid_uri",
                "The request requires an absolute resource URI without a fragment.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var provider = resources!.Resolve(parsed);
        if (provider is null)
        {
            await WriteResourceErrorAsync(
                context,
                StatusCodes.Status404NotFound,
                "resource_unknown_scheme",
                $"No registered provider reads the resource URI scheme '{parsed.Scheme}://'.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        ResourceReadResult read;
        try
        {
            read = await provider.ReadAsync(
                uri,
                new ResourceReadRequest(ReplControlLimits.MaximumResourceBytes),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceException exception)
        {
            await WriteResourceErrorAsync(
                context,
                exception.Code switch
                {
                    "resource_invalid_uri" => StatusCodes.Status400BadRequest,
                    "resource_unknown_scheme" or "resource_not_found" => StatusCodes.Status404NotFound,
                    "resource_too_large" => StatusCodes.Status413PayloadTooLarge,
                    "resource_provider_unavailable" => StatusCodes.Status503ServiceUnavailable,
                    _ => StatusCodes.Status502BadGateway
                },
                exception.Code,
                exception.Message,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (WorkspaceException exception) when (exception.Code == "workspace_path_not_found")
        {
            await WriteResourceErrorAsync(
                context,
                StatusCodes.Status404NotFound,
                exception.Code,
                exception.Message,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Resource read for '{Uri}' via provider {ProviderId} failed.",
                uri,
                provider.Id);
            await WriteResourceErrorAsync(
                context,
                StatusCodes.Status502BadGateway,
                "resource_provider_failed",
                "The resource could not be read from its provider.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = read.MimeType ?? "application/octet-stream";
            await CopyBoundedAsync(
                read.Content,
                context.Response.Body,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceException exception) when (exception.Code == "resource_too_large")
        {
            // A non-seekable body that exceeds the ceiling mid-copy either already
            // truncated the stream (peer sees the cut) or never started (peer gets
            // the typed envelope instead of a silent 200).
            await WriteResourceErrorAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                exception.Code,
                exception.Message,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await read.Content.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Copies the body while enforcing the channel ceiling: providers that know
    /// their length refuse oversized reads themselves, but a non-seekable body (the HTTP
    /// bridge) is cut off here instead of flooding the control channel.</summary>
    private static async Task CopyBoundedAsync(
        Stream source,
        Stream target,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > ReplControlLimits.MaximumResourceBytes)
                throw new ResourceException(
                    "resource_too_large",
                    $"The resource exceeds the {ReplControlLimits.MaximumResourceBytes} byte read limit.");

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteResourceErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            // A bounded-copy failure after the body started cannot be retried with an
            // error envelope; the peer sees the truncated stream and the log records why.
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(
                new BusErrorPayload(code, message),
                ReplControlJsonContext.Default.BusErrorPayload),
            cancellationToken).ConfigureAwait(false);
    }
}
