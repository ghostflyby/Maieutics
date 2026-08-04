using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.Control;
using Maieutics.Execution;
using Maieutics.Jupyter;
using Maieutics.Jupyter.Kernel;
using Maieutics.Mcp;
using Maieutics.Providers;
using Maieutics.Providers.Anthropic;
using Maieutics.Providers.OpenAI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maieutics;

public static class MaieuticsHost
{
    public static WebApplicationBuilder CreateApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        // The default JSON configuration sources use FSEvents-backed file watching, which can block
        // in constrained sandboxes. Polling is deterministic and matches the executable's config provider.
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1");
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environments.Production;
        var startupCurrentDirectory = Directory.GetCurrentDirectory();
        var configurationFile = MaieuticsConfigurationFile.Resolve(
            args,
            Environment.GetEnvironmentVariable,
            AppContext.BaseDirectory,
            startupCurrentDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        var mcpConfigurationPath = GetMcpConfigurationPath(configurationFile);
        ValidateInitialConfigurationFile(configurationFile, mcpConfigurationPath);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(MaieuticsHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = environmentName,
            Args = args
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        var fileErrors = new MaieuticsConfigurationFileErrors();
        var fileProvider = MaieuticsConfigurationFileProvider.Create(configurationFile.Path);
        builder.Configuration.AddJsonFile(source =>
        {
            source.FileProvider = fileProvider.Provider;
            source.Path = fileProvider.RelativePath;
            source.Optional = !configurationFile.Required;
            source.ReloadOnChange = true;
            source.OnLoadException = context =>
            {
                fileErrors.Record(context.Exception);
                context.Ignore = true;
            };
        });
        builder.Configuration.AddJsonFile(source =>
        {
            source.FileProvider = fileProvider.Provider;
            source.Path = "mcp.json";
            source.Optional = true;
            source.ReloadOnChange = true;
            source.OnLoadException = context =>
            {
                fileErrors.Record(context.Exception);
                context.Ignore = true;
            };
        });
        builder.Configuration
            .AddInMemoryCollection(GetEnvironmentAliases())
            .AddEnvironmentVariables()
            .AddCommandLine(args, new Dictionary<string, string>
            {
                ["--config"] = "Maieutics:ConfigurationFile",
                ["--connection-file"] = "Maieutics:Jupyter:ConnectionFile",
                ["--workspace"] = "Maieutics:Workspace:Root",
                ["--profile"] = "Maieutics:DefaultProfile",
                ["--provider"] = "Maieutics:Model:Provider",
                ["--model"] = "Maieutics:Model:Name",
                ["--openai-api"] = "Maieutics:Sources:openai:ApiFlavor"
            });

        builder.Logging
            .AddConfiguration(builder.Configuration.GetSection("Logging"))
            .AddSimpleConsole();
        var denoReplOptions = new DenoReplOptions();
        builder.Configuration.GetSection(DenoReplOptions.SectionName).Bind(denoReplOptions);
        denoReplOptions.Validate();
        builder.Services.AddSingleton(configurationFile);
        builder.Services.AddSingleton(_ => fileProvider);
        builder.Services.AddSingleton(fileErrors);
        builder.Services.AddSingleton(new McpStartupDirectory(startupCurrentDirectory));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IConfiguredChatClientFactory, OpenAiChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory, AnthropicChatClientFactory>();
        builder.Services.AddSingleton<MaieuticsRuntimeConfiguration>();
        builder.Services.AddSingleton<IMaieuticsRuntimeConfiguration>(static services =>
            services.GetRequiredService<MaieuticsRuntimeConfiguration>());
        builder.Services.AddSingleton<IAgentRunProfileProvider>(static services =>
            services.GetRequiredService<MaieuticsRuntimeConfiguration>());
        builder.Services.AddSingleton<IMaieuticsMcpController>(static services =>
            services.GetRequiredService<MaieuticsRuntimeConfiguration>());
        builder.Services.AddSingleton(Workspace.Create(
            builder.Configuration["Maieutics:Workspace:Root"],
            startupCurrentDirectory));
        builder.Services.AddSingleton(denoReplOptions);
        builder.Services.AddSingleton<JupyterDenoReplPresentationRouter>();
        builder.Services.AddSingleton<IDenoReplPresentationRouter>(static services =>
            services.GetRequiredService<JupyterDenoReplPresentationRouter>());
        builder.Services.AddSingleton<ReplControlSessionRegistry>();
        var controlSocketPath = ReplControlHost.CreateSocketPath();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.ListenUnixSocket(controlSocketPath, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });
        builder.Services.AddSingleton(services => new ReplControlHost(
            controlSocketPath,
            services.GetRequiredService<ReplControlSessionRegistry>(),
            services.GetRequiredService<ILogger<ReplControlHost>>(),
            services.GetRequiredService<WorkspaceFunctions>().Functions));
        builder.Services.AddSingleton<ReplClientModule>();
        builder.Services.AddSingleton<IDenoReplSessionFactory, LocalDenoReplSessionFactory>();
        builder.Services.AddSingleton<DenoReplRegistry>();
        builder.Services.AddSingleton<DenoReplFunctions>();
        builder.Services.AddSingleton(static services =>
            new WorkspaceFunctions(services.GetRequiredService<Workspace>()));
        builder.Services.AddSingleton<IReadOnlyList<AIFunction>>(static services =>
            services.GetRequiredService<WorkspaceFunctions>().Functions
                .Concat(services.GetRequiredService<DenoReplFunctions>().Functions)
                .ToArray());
        builder.Services.AddSingleton(CreateAgentSession);
        builder.Services.AddSingleton(CreateKernelApplication);
        builder.Services.AddHostedService<MaieuticsRuntimeReadinessHostedService>();
        builder.Services.AddHostedService<JupyterKernelHostedService>();
        builder.Services.AddSingleton<KernelInterruptCoordinator>();
        builder.Services.AddSingleton<IKernelInterruptCoordinator>(static services =>
            services.GetRequiredService<KernelInterruptCoordinator>());
        // SIGINT interrupts the current kernel execution; SIGQUIT/SIGTERM keep graceful shutdown.
        builder.Services.RemoveAll<IHostLifetime>();
        builder.Services.AddSingleton<IHostLifetime, JupyterKernelLifetime>();
        return builder;
    }

    public static WebApplication CreateApplication(
        string[] args,
        Action<WebApplicationBuilder>? configure = null)
    {
        var builder = CreateApplicationBuilder(args);
        configure?.Invoke(builder);
        var application = builder.Build();
        application.Services.GetRequiredService<ReplControlHost>().MapEndpoints(application);
        return application;
    }

    private static IAgentSession CreateAgentSession(IServiceProvider services) =>
        new AgentSession(services.GetRequiredService<IAgentRunProfileProvider>());

    private static IJupyterKernelApplication CreateKernelApplication(IServiceProvider services)
    {
        var runtimeConfiguration = services.GetRequiredService<IMaieuticsRuntimeConfiguration>();
        return new MaieuticsAgentKernelApplication(
            services.GetRequiredService<IAgentSession>(),
            runtimeConfiguration.GetKernelOptions,
            runtimeConfiguration,
            services.GetRequiredService<ILogger<MaieuticsAgentKernelApplication>>(),
            workspace: services.GetRequiredService<Workspace>(),
            replPresentationRouter: services.GetRequiredService<JupyterDenoReplPresentationRouter>(),
            mcpController: services.GetRequiredService<IMaieuticsMcpController>());
    }

    private static IReadOnlyDictionary<string, string?> GetEnvironmentAliases()
    {
        var aliases = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        AddAlias(aliases, "MAIEUTICS_PROFILE", "Maieutics:DefaultProfile");
        AddAlias(aliases, "MAIEUTICS_PROVIDER", "Maieutics:Model:Provider");
        AddAlias(aliases, "MAIEUTICS_MODEL", "Maieutics:Model:Name");
        AddAlias(aliases, "MAIEUTICS_WORKSPACE", "Maieutics:Workspace:Root");
        AddAlias(aliases, "MAIEUTICS_OPENAI_API", "Maieutics:Sources:openai:ApiFlavor");
        AddAlias(aliases, "OPENAI_API_KEY", "Maieutics:Sources:openai:ApiKey");
        AddAlias(aliases, "OPENAI_BASE_URL", "Maieutics:Sources:openai:Endpoint");
        AddAlias(aliases, "ANTHROPIC_API_KEY", "Maieutics:Sources:anthropic:ApiKey");
        AddAlias(aliases, "ANTHROPIC_BASE_URL", "Maieutics:Sources:anthropic:Endpoint");
        return aliases;
    }

    private static void AddAlias(IDictionary<string, string?> aliases, string environmentVariable, string key)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            aliases.Add(key, value);
        }
    }

    private static string? GetMcpConfigurationPath(MaieuticsConfigurationFile configurationFile) =>
        configurationFile.Path is null
            ? null
            : Path.Combine(Path.GetDirectoryName(configurationFile.Path)!, "mcp.json");

    private static void ValidateInitialConfigurationFile(
        MaieuticsConfigurationFile configurationFile,
        string? mcpConfigurationPath)
    {
        if (configurationFile.Path is not null)
        {
            if (!File.Exists(configurationFile.Path))
            {
                if (configurationFile.Required)
                {
                    throw new FileNotFoundException("The selected Maieutics configuration file does not exist.",
                        configurationFile.Path);
                }
            }
            else
            {
                using var stream = File.OpenRead(configurationFile.Path);
                using var _ = JsonDocument.Parse(stream);
            }
        }

        if (mcpConfigurationPath is not null && File.Exists(mcpConfigurationPath))
        {
            using var stream = File.OpenRead(mcpConfigurationPath);
            using var _ = JsonDocument.Parse(stream);
        }
    }
}
