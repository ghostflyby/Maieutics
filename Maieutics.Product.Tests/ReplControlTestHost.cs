using Maieutics.Control;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

/// <summary>
///     Declares the process-wide polling file-watcher flag once per test process, before any
///     test builds a configuration provider. The default JSON configuration sources use
///     FSEvents-backed file watching, which can block in constrained sandboxes; polling is
///     deterministic and matches the executable's config provider. It lives in a module
///     initializer (not a collection fixture) because some <see cref="ReplControlTestHost"/>
///     consumers (unit-style control-host tests) run outside
///     <see cref="ProductIntegrationCollection"/>.
/// </summary>
internal static class ReplControlTestHostInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1");
    }
}

internal static class ReplControlTestHost
{
    public static async Task<(WebApplication Application, ReplControlHost Host)> StartAsync(
        ReplControlSessionRegistry registry,
        CancellationToken cancellationToken,
        IReadOnlyList<AIFunction>? scriptTools = null)
    {
        var socketPath = ReplControlHost.CreateSocketPath();
        var host = new ReplControlHost(
            socketPath,
            registry,
            NullLogger<ReplControlHost>.Instance,
            scriptTools);
        var application = await StartAsync(socketPath, host, cancellationToken);
        return (application, host);
    }

    public static async Task<WebApplication> StartAsync(
        string socketPath,
        ReplControlHost controlHost,
        CancellationToken cancellationToken)
    {
        // The polling flag is declared once by <see cref="ReplControlTestHostInit"/>.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "maieutics-control-test"
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            // The control channel is loopback-only and payload-bounded; slow-body data-rate
            // limits can abort a request mid-stream on a starved CI runner before Kestrel
            // applies its explicit size limit and replies 413.
            options.Limits.MinRequestBodyDataRate = null;
            options.Limits.MinResponseDataRate = null;
            options.ListenUnixSocket(socketPath, listenOptions => { listenOptions.Protocols = HttpProtocols.Http1; });
        });
        var application = builder.Build();
        controlHost.MapEndpoints(application);
        await application.StartAsync(cancellationToken);
        return application;
    }
}