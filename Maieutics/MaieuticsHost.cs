using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.Execution;
using Maieutics.Jupyter;
using Maieutics.Jupyter.Kernel;
using Maieutics.Providers;
using Maieutics.Providers.Anthropic;
using Maieutics.Providers.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maieutics;

public static class MaieuticsHost
{
    public static HostApplicationBuilder CreateApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environments.Production;
        var startupCurrentDirectory = Directory.GetCurrentDirectory();
        var configurationFile = MaieuticsConfigurationFile.Resolve(
            args,
            Environment.GetEnvironmentVariable,
            AppContext.BaseDirectory,
            startupCurrentDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        ValidateInitialConfigurationFile(configurationFile);

        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = typeof(MaieuticsHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
            EnvironmentName = environmentName
        });
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
        builder.Services.AddSingleton(configurationFile);
        builder.Services.AddSingleton(_ => fileProvider);
        builder.Services.AddSingleton(fileErrors);
        builder.Services.AddSingleton<IConfiguredChatClientFactory, OpenAiChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory, AnthropicChatClientFactory>();
        builder.Services.AddSingleton<MaieuticsRuntimeConfiguration>();
        builder.Services.AddSingleton<IMaieuticsRuntimeConfiguration>(static services =>
            services.GetRequiredService<MaieuticsRuntimeConfiguration>());
        builder.Services.AddSingleton<IAgentRunProfileProvider>(static services =>
            services.GetRequiredService<MaieuticsRuntimeConfiguration>());
        builder.Services.AddSingleton(Workspace.Create(
            builder.Configuration["Maieutics:Workspace:Root"],
            startupCurrentDirectory));
        builder.Services.AddSingleton(static services =>
            new WorkspaceFunctions(services.GetRequiredService<Workspace>()));
        builder.Services.AddSingleton<IReadOnlyList<AIFunction>>(static services =>
            services.GetRequiredService<WorkspaceFunctions>().Functions);
        builder.Services.AddSingleton(CreateAgentSession);
        builder.Services.AddSingleton(CreateKernelApplication);
        builder.Services.AddHostedService<JupyterKernelHostedService>();
        return builder;
    }

    private static IAgentSession CreateAgentSession(IServiceProvider services) =>
        new AgentSession(
            services.GetRequiredService<IAgentRunProfileProvider>(),
            services.GetRequiredService<IReadOnlyList<AIFunction>>());

    private static IJupyterKernelApplication CreateKernelApplication(IServiceProvider services)
    {
        var runtimeConfiguration = services.GetRequiredService<IMaieuticsRuntimeConfiguration>();
        return new MaieuticsAgentKernelApplication(
            services.GetRequiredService<IAgentSession>(),
            runtimeConfiguration.GetKernelOptions,
            runtimeConfiguration,
            services.GetRequiredService<ILogger<MaieuticsAgentKernelApplication>>(),
            workspace: services.GetRequiredService<Workspace>());
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

    private static void ValidateInitialConfigurationFile(MaieuticsConfigurationFile configurationFile)
    {
        if (configurationFile.Path is null)
        {
            return;
        }

        if (!File.Exists(configurationFile.Path))
        {
            if (configurationFile.Required)
            {
                throw new FileNotFoundException("The selected Maieutics configuration file does not exist.",
                    configurationFile.Path);
            }

            return;
        }

        using var stream = File.OpenRead(configurationFile.Path);
        using var _ = JsonDocument.Parse(stream);
    }
}