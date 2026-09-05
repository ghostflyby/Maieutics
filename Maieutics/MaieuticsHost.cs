using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Configuration;
using Maieutics.Control;
using Maieutics.DenoExecution;
using Maieutics.DenoRepl;
using Maieutics.Execution;
using Maieutics.Frontend;
using Maieutics.Mcp;
using Maieutics.Permissions;
using Maieutics.Persistence;
using Maieutics.Plugins;
using Maieutics.Providers;
using Maieutics.Providers.Anthropic;
using Maieutics.Providers.OpenAI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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
                ["--frontend-discovery"] = "Maieutics:Frontend:DiscoveryFile",
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
        var terminalOptions = new TerminalOptions();
        builder.Configuration.GetSection(TerminalOptions.SectionName).Bind(terminalOptions);
        terminalOptions.Validate();
        // Transcript persistence is opt in and startup only: flipping the flag requires a restart.
        var agentPersistenceOptions = new MaieuticsAgentPersistenceOptions();
        builder.Configuration
            .GetSection($"{MaieuticsOptions.SectionName}:Agent:Persistence")
            .Bind(agentPersistenceOptions);
        if (agentPersistenceOptions.Enabled)
        {
            var applicationPaths = ApplicationPaths.Resolve();
            applicationPaths.EnsureAgentRoot();
            builder.Services.AddSingleton(applicationPaths);
            builder.Services.AddSingleton(static services => new ObjectStore(
                services.GetRequiredService<ApplicationPaths>().AgentObjectsRoot));
            builder.Services.AddSingleton<IAgentObjectStore>(static services =>
                services.GetRequiredService<ObjectStore>());
            builder.Services.AddSingleton<IObjectReclaimer>(static services =>
                services.GetRequiredService<ObjectStore>());
            builder.Services.AddSingleton<AgentObjectFunctions>();
        }

        builder.Services.AddSingleton(configurationFile);
        builder.Services.AddSingleton(_ => fileProvider);
        builder.Services.AddSingleton(fileErrors);
        builder.Services.AddSingleton(new McpStartupDirectory(startupCurrentDirectory));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IConfiguredChatClientFactory, OpenAiChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory, AnthropicChatClientFactory>();        builder.Services.AddSingleton<MaieuticsRuntimeConfiguration>();
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
        builder.Services.AddSingleton(terminalOptions);
        builder.Services.AddSingleton<ITerminalProcessFactory, LocalTerminalProcessFactory>();
        // Phase 5 replaces this fixed default with the layered acquisition path; for now every
        // terminal session captures the default policy (ADR 0018 §7, decision 2: run unrestricted).
        builder.Services.AddSingleton(EffectivePolicy.Default);
        builder.Services.AddSingleton<TerminalRegistry>();
        builder.Services.AddSingleton<TerminalFunctions>();
        builder.Services.AddSingleton<FrontendDenoReplPresentationRouter>();
        builder.Services.AddSingleton<IDenoReplPresentationRouter>(static services =>
            services.GetRequiredService<FrontendDenoReplPresentationRouter>());
        builder.Services.AddSingleton<MaieuticsCommandExecutor>(static services => new MaieuticsCommandExecutor(
            services.GetRequiredService<MaieuticsAgentSessionManager>(),
            services.GetService<IMaieuticsRuntimeConfiguration>(),
            services.GetRequiredService<Workspace>(),
            services.GetRequiredService<MaieuticsStatusProvider>(),
            services.GetService<IMaieuticsMcpController>()));
        builder.Services.AddSingleton<ReplControlSessionRegistry>();
        var controlSocketPath = ReplControlHost.CreateSocketPath();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            if (OperatingSystem.IsWindows())
                // .NET 10 rejects dynamic-port binding on `localhost` (both loopback addresses);
                // bind IPv4 loopback explicitly so Kestrel can choose an ephemeral port.
                options.Listen(IPAddress.Loopback, 0, listenOptions => { listenOptions.Protocols = HttpProtocols.Http1; });
            else
                options.ListenUnixSocket(controlSocketPath,
                    listenOptions => { listenOptions.Protocols = HttpProtocols.Http1; });
        });
        builder.Services.AddSingleton<ReplControlCredentialRegistry>();
        builder.Services.AddSingleton<ReplEvalWebSocketHost>();
        builder.Services.AddSingleton<ReplOutputWebSocketHost>();
        var frontendOptions = FrontendOptions.Create(builder.Configuration["Maieutics:Frontend:DiscoveryFile"]);
        builder.Services.AddSingleton(frontendOptions);
        if (frontendOptions.Enabled)
            // Registered after the control listener so the Windows control-address probe keeps
            // observing the control listener first when two TCP addresses exist.
            builder.WebHost.ConfigureKestrel(options => options.Listen(
                IPAddress.Loopback,
                frontendOptions.Port,
                listenOptions => { listenOptions.Protocols = HttpProtocols.Http1; }));
        builder.Services.AddSingleton<FrontendSessionService>(CreateFrontendSessionService);
        builder.Services.AddSingleton<FrontendHost>();
        builder.Services.AddHostedService<FrontendHostedService>();
        if (OperatingSystem.IsWindows())
            builder.Services.AddSingleton<IWindowsPipeBootstrap>(static services =>
                OperatingSystem.IsWindows() ? CreateWindowsBootstrap(services) : throw new UnreachableException()
            );

        builder.Services.AddSingleton<PluginHostModule>();
        builder.Services.TryAddSingleton(ApplicationPaths.Resolve());
        builder.Services.AddSingleton(services => new PluginHostManager(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Maieutics",
                "plugins"),
            services.GetRequiredService<ApplicationPaths>().PluginDataRoot,
            controlSocketPath,
            services.GetRequiredService<DenoReplOptions>(),
            services.GetRequiredService<PluginHostModule>(),
            services.GetRequiredService<ReplControlSessionRegistry>(),
            services.GetRequiredService<ILogger<PluginHostManager>>(),
            services.GetRequiredService<ILoggerFactory>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetService<DenoPermissionBroker>()));
        // The host manager is the kernel-facing REPL policy registrar (ADR 0020 decision 1): the
        // session factory pre-caches a REPL's policy through it before the host derives the REPL.
        builder.Services.AddSingleton<IReplPolicyRegistrar>(static services =>
            services.GetRequiredService<PluginHostManager>());
        builder.Services.AddHostedService(static services => services.GetRequiredService<PluginHostManager>());
        builder.Services.AddSingleton(services => new ReplControlHost(
            controlSocketPath,
            services.GetRequiredService<ReplControlSessionRegistry>(),
            services.GetRequiredService<ILogger<ReplControlHost>>(),
            services.GetRequiredService<WorkspaceFunctions>().Functions,
            services.GetRequiredService<PluginHostManager>(),
            services.GetRequiredService<ReplControlCredentialRegistry>(),
            OperatingSystem.IsWindows()
                ? services.GetRequiredService<IWindowsPipeBootstrap>()
                : null));
        builder.Services.AddSingleton<DenoReplModule>();
        builder.Services.AddSingleton<DenoPermissionBroker>(static services =>
            DenoPermissionBroker.Create(services.GetRequiredService<ILogger<DenoPermissionBroker>>()));
        builder.Services.AddSingleton<IDenoReplSessionFactory>(static services =>
            new LocalDenoReplSessionFactory(
                services.GetRequiredService<DenoReplOptions>(),
                services.GetRequiredService<ReplControlHost>(),
                services.GetRequiredService<DenoReplModule>(),
                services.GetRequiredService<ReplEvalWebSocketHost>(),
                services.GetRequiredService<ReplOutputWebSocketHost>(),
                services.GetRequiredService<ReplControlSessionRegistry>(),
                services.GetRequiredService<ReplControlCredentialRegistry>(),
                services.GetRequiredService<ILogger<DenoReplProcess>>(),
                services.GetRequiredService<DenoPermissionBroker>(),
                services.GetService<IReplPolicyRegistrar>(),
                services.GetService<PluginHostManager>()));
        builder.Services.AddSingleton<DenoReplRegistry>();
        builder.Services.AddSingleton<DenoReplFunctions>();
        // Large binary REPL display payloads are stored content-addressed and referenced
        // by a stable relative URL the frontend fetches natively (frontend-migration-gaps.md #2).
        builder.Services.AddSingleton<IReplDisplayObjectStore>(static services =>
            new ReplDisplayObjectStore(services.GetRequiredService<ObjectStore>()));
        builder.Services.AddHostedService<DenoReplShutdownHostedService>();
        builder.Services.AddHostedService<DenoModuleGraphWarmer>();
        builder.Services.AddSingleton(static services =>
            new WorkspaceFunctions(services.GetRequiredService<Workspace>()));
        builder.Services.AddSingleton<IReadOnlyList<AIFunction>>(static services =>
        [
            .. services.GetRequiredService<WorkspaceFunctions>().Functions,
            .. services.GetRequiredService<DenoReplFunctions>().Functions,
            .. (services.GetService<AgentObjectFunctions>()?.Functions ?? [])
        ]);
        builder.Services.AddSingleton(CreateAgentSessionManager);
        builder.Services.AddSingleton<IAgentSession>(static services =>
            services.GetRequiredService<MaieuticsAgentSessionManager>());
        builder.Services.AddSingleton<MaieuticsStatusProvider>();
        builder.Services.AddHostedService<MaieuticsRuntimeReadinessHostedService>();
        return builder;
    }

    public static WebApplication CreateApplication(
        string[] args,
        Action<WebApplicationBuilder>? configure = null)
    {
        var builder = CreateApplicationBuilder(args);
        configure?.Invoke(builder);
        var application = builder.Build();
        var controlHost = application.Services.GetRequiredService<ReplControlHost>();
        var evalHost = application.Services.GetRequiredService<ReplEvalWebSocketHost>();
        var outputHost = application.Services.GetRequiredService<ReplOutputWebSocketHost>();
        // Terminate eval and output connections before Kestrel begins its shutdown window so
        // upgraded WebSocket requests finish immediately instead of blocking the shutdown timeout.
        application.Lifetime.ApplicationStopping.Register(evalHost.BeginShutdown);
        application.Lifetime.ApplicationStopping.Register(outputHost.BeginShutdown);
        var frontendOptions = application.Services.GetRequiredService<FrontendOptions>();
        if (frontendOptions.Enabled)
        {
            // Map the frontend first: its bearer middleware then runs ahead of the control
            // bus's peer-identity middleware, which only guards its own paths.
            application.Services.GetRequiredService<FrontendHost>().MapEndpoints(application);
        }

        if (OperatingSystem.IsWindows())
            application.Lifetime.ApplicationStarted.Register(() =>
            {
                var addresses = application.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>();
                var address = addresses?.Addresses.FirstOrDefault();
                if (address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri))
                    controlHost.SetControlAddress($"{uri.Host}:{uri.Port}");
            });

        application.Services.GetRequiredService<ReplEvalWebSocketHost>().MapEndpoint(application);
        application.Services.GetRequiredService<ReplOutputWebSocketHost>().MapEndpoint(application);
        controlHost.MapEndpoints(application);
        return application;
    }

    private static FrontendSessionService CreateFrontendSessionService(IServiceProvider services)
    {
        var runtimeConfiguration = services.GetService<IMaieuticsRuntimeConfiguration>();
        return new FrontendSessionService(
            services.GetRequiredService<MaieuticsAgentSessionManager>(),
            services.GetRequiredService<MaieuticsCommandExecutor>(),
            services.GetRequiredService<FrontendDenoReplPresentationRouter>(),
            services.GetRequiredService<ILogger<FrontendSessionService>>(),
            runtimeConfiguration,
            services.GetRequiredService<MaieuticsStatusProvider>());
    }

    private static MaieuticsAgentSessionManager CreateAgentSessionManager(IServiceProvider services)
    {
        var profileProvider = services.GetRequiredService<IAgentRunProfileProvider>();
        var paths = services.GetService<ApplicationPaths>();
        if (paths is null)
        {
            return new MaieuticsAgentSessionManager(profileProvider, familiesRoot: null, storeFactory: null);
        }

        return new MaieuticsAgentSessionManager(
            profileProvider,
            paths.AgentFamiliesRoot,
            familyId => new SqliteTranscriptStore(
                SqliteTranscriptStore.FamilyDatabasePath(paths.AgentFamiliesRoot, familyId)),
            services.GetService<IAgentObjectStore>(),
            services.GetService<IObjectReclaimer>(),
            paths.AgentViewSessionsRoot,
            paths.AgentObjectsRoot);
    }

    [SupportedOSPlatform("windows")]
    private static WindowsPipeBootstrap CreateWindowsBootstrap(IServiceProvider services)
    {
        return new WindowsPipeBootstrap(
            $"maieutics-{Guid.NewGuid():N}",
            services.GetRequiredService<ReplControlSessionRegistry>(),
            services.GetRequiredService<ReplControlCredentialRegistry>(),
            services.GetRequiredService<ILogger<WindowsPipeBootstrap>>());
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
        if (!string.IsNullOrWhiteSpace(value)) aliases.Add(key, value);
    }

    private static string? GetMcpConfigurationPath(MaieuticsConfigurationFile configurationFile)
    {
        return configurationFile.Path is null
            ? null
            : Path.Combine(
                Path.GetDirectoryName(configurationFile.Path)
                ?? throw new InvalidOperationException(
                    $"Cannot resolve the directory for '{configurationFile.Path}'."),
                "mcp.json");
    }

    private static void ValidateInitialConfigurationFile(
        MaieuticsConfigurationFile configurationFile,
        string? mcpConfigurationPath)
    {
        if (configurationFile.Path is not null)
        {
            if (!File.Exists(configurationFile.Path))
            {
                if (configurationFile.Required)
                    throw new FileNotFoundException("The selected Maieutics configuration file does not exist.",
                        configurationFile.Path);
            }
            else
            {
                using var stream = File.OpenRead(configurationFile.Path);
                using var _ = JsonDocument.Parse(stream);
            }
        }

        if (mcpConfigurationPath is null || !File.Exists(mcpConfigurationPath)) return;
        {
            using var stream = File.OpenRead(mcpConfigurationPath);
            using var _ = JsonDocument.Parse(stream);
        }
    }
}
