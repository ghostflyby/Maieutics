using System.ClientModel;
using Maieutics.Agent;
using Maieutics.Agent.Jupyter;
using Maieutics.Jupyter.Kernel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace Maieutics;

public static class MaieuticsHost
{
    public static HostApplicationBuilder CreateApplicationBuilder(string[] args)
    {
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environments.Production;
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = typeof(MaieuticsHost).Assembly.GetName().Name,
            ContentRootPath = Directory.GetCurrentDirectory(),
            DisableDefaults = true,
            EnvironmentName = environmentName
        });
        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args, new Dictionary<string, string>
        {
            ["--connection-file"] = "Maieutics:Jupyter:ConnectionFile",
            ["--model"] = "Maieutics:Model"
        });
        ApplyEnvironmentAlias(builder.Configuration, "OPENAI_API_KEY", "Maieutics:OpenAI:ApiKey");
        ApplyEnvironmentAlias(builder.Configuration, "OPENAI_BASE_URL", "Maieutics:OpenAI:Endpoint");
        ApplyEnvironmentAlias(builder.Configuration, "MAIEUTICS_MODEL", "Maieutics:Model");

        builder.Logging
            .AddConfiguration(builder.Configuration.GetSection("Logging"))
            .AddSimpleConsole();
        builder.Services
            .AddOptions<MaieuticsOptions>()
            .Bind(builder.Configuration.GetSection(MaieuticsOptions.SectionName))
            .Validate(MaieuticsOptions.IsValid, MaieuticsOptions.ValidationMessage)
            .ValidateOnStart();
        builder.Services.AddSingleton<IChatClient>(CreateChatClient);
        builder.Services.AddSingleton<IAgentSession>(CreateAgentSession);
        builder.Services.AddSingleton<IJupyterKernelApplication>(CreateKernelApplication);
        builder.Services.AddHostedService<JupyterKernelHostedService>();
        return builder;
    }

    private static IChatClient CreateChatClient(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<MaieuticsOptions>>().Value;
        var credential = new ApiKeyCredential(options.OpenAI.ApiKey);
        var client = options.OpenAI.Endpoint is null
            ? new ChatClient(options.Model, credential)
            : new ChatClient(
                options.Model,
                credential,
                new OpenAIClientOptions { Endpoint = options.OpenAI.Endpoint });
        return client.AsIChatClient();
    }

    private static IAgentSession CreateAgentSession(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<MaieuticsOptions>>().Value;
        return new AgentSession(
            services.GetRequiredService<IChatClient>(),
            new AgentSessionOptions
            {
                SystemPrompt = options.SystemPrompt,
                MaxRetainedTurns = options.Agent.MaxRetainedTurns,
                MaxHistoryCharacters = options.Agent.MaxHistoryCharacters,
                MaxInputCharacters = options.Agent.MaxInputCharacters,
                MaxResponseCharacters = options.Agent.MaxResponseCharacters
            });
    }

    private static IJupyterKernelApplication CreateKernelApplication(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<MaieuticsOptions>>().Value;
        return new MaieuticsAgentKernelApplication(
            services.GetRequiredService<IAgentSession>(),
            new MaieuticsAgentKernelOptions
            {
                FlushInterval = options.Jupyter.FlushInterval,
                FlushCharacters = options.Jupyter.FlushCharacters
            },
            services.GetRequiredService<ILogger<MaieuticsAgentKernelApplication>>());
    }

    private static void ApplyEnvironmentAlias(IConfiguration configuration, string environmentVariable, string key)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(configuration[key]) && !string.IsNullOrWhiteSpace(value))
        {
            configuration[key] = value;
        }
    }
}