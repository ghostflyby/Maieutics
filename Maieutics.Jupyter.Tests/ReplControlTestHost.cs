using Maieutics.Control;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

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
        // The default JSON configuration sources use FSEvents-backed file watching, which can block
        // in constrained sandboxes. Polling is deterministic and matches the executable's config provider.
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "maieutics-control-test"
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.ListenUnixSocket(socketPath, listenOptions => { listenOptions.Protocols = HttpProtocols.Http1; });
        });
        var application = builder.Build();
        controlHost.MapEndpoints(application);
        await application.StartAsync(cancellationToken);
        return application;
    }
}