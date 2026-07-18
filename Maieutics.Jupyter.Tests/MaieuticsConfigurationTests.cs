using System.Text.Json.Nodes;
using FluentAssertions;
using Maieutics.Configuration;
using Maieutics.Jupyter.Shared;
using Maieutics.Providers;
using Maieutics.Providers.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maieutics.Jupyter.Tests;

[Collection(JupyterSocketIntegrationCollection.Name)]
public sealed class MaieuticsConfigurationTests
{
    [Fact]
    public void ConfigurationPathResolutionUsesExplicitEnvironmentPortableThenUserPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-config-path-{Guid.NewGuid():N}");
        var applicationBase = Path.Combine(root, "app");
        var currentDirectory = Path.Combine(root, "working");
        var applicationData = Path.Combine(root, "config");
        Directory.CreateDirectory(applicationBase);
        Directory.CreateDirectory(currentDirectory);
        Directory.CreateDirectory(applicationData);

        try
        {
            var explicitFile = MaieuticsConfigurationFile.Resolve(
                ["--config", "explicit.json"],
                name => name == "MAIEUTICS_CONFIG" ? "environment.json" : null,
                applicationBase,
                currentDirectory,
                applicationData);
            explicitFile.Path.Should().Be(Path.Combine(currentDirectory, "explicit.json"));
            explicitFile.Required.Should().BeTrue();
            explicitFile.Source.Should().Be("command line");

            var environmentFile = MaieuticsConfigurationFile.Resolve(
                [],
                name => name == "MAIEUTICS_CONFIG" ? "environment.json" : null,
                applicationBase,
                currentDirectory,
                applicationData);
            environmentFile.Path.Should().Be(Path.Combine(currentDirectory, "environment.json"));
            environmentFile.Required.Should().BeTrue();

            var portablePath = Path.Combine(applicationBase, "maieutics.json");
            File.WriteAllText(portablePath, "{}");
            var portableFile = MaieuticsConfigurationFile.Resolve(
                [],
                _ => null,
                applicationBase,
                currentDirectory,
                applicationData);
            portableFile.Path.Should().Be(portablePath);
            portableFile.Source.Should().Be("portable");

            File.Delete(portablePath);
            File.WriteAllText(Path.Combine(currentDirectory, "maieutics.json"), "{}");
            var userFile = MaieuticsConfigurationFile.Resolve(
                [],
                _ => null,
                applicationBase,
                currentDirectory,
                applicationData);
            userFile.Path.Should().Be(Path.Combine(applicationData, "Maieutics", "maieutics.json"));
            userFile.Required.Should().BeFalse();
            userFile.Source.Should().Be("user");

            var noFile = MaieuticsConfigurationFile.Resolve(
                [],
                _ => null,
                applicationBase,
                currentDirectory,
                string.Empty);
            noFile.Path.Should().BeNull();
            noFile.Required.Should().BeFalse();
            noFile.Source.Should().Be("none");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HostOwnsAndDisposesConfigurationFileProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-config-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        File.WriteAllText(configurationFile, "{}");

        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            var host = builder.Build();
            var provider = host.Services.GetRequiredService<MaieuticsConfigurationFileProvider>();

            provider.IsDisposed.Should().BeFalse();
            host.Dispose();
            provider.IsDisposed.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task RuntimeInitializationReconcilesConfigurationChangesDuringProviderCreation()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-config-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration(connectionFile, "Fake", "one"),
            deadline.Token);

        var factory = new BlockingChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();

        try
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            var resolution = Task.Run(
                () => host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>(),
                deadline.Token);
            await factory.CreateStarted.Task.WaitAsync(deadline.Token);

            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = configuration.GetReloadToken().RegisterChangeCallback(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                changed);
            await File.WriteAllTextAsync(
                configurationFile,
                CreateConfiguration(connectionFile, "Fake", "two"),
                deadline.Token);
            await changed.Task.WaitAsync(deadline.Token);

            factory.ReleaseCreation.TrySetResult();
            var runtime = await resolution.WaitAsync(deadline.Token);
            while (runtime.CompletedReloadAttempt == 0)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            (await AcquireClientAsync(runtime)).Model.Should().Be("two");
            runtime.Version.Should().Be(2);
        }
        finally
        {
            factory.ReleaseCreation.TrySetResult();
            await ((IAsyncDisposable)host).DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConfigurationSourcesUseJsonAliasStandardEnvironmentThenCommandLinePrecedence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-config-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        File.WriteAllText(configurationFile, CreateConfiguration(
            connectionFile: Path.Combine(root, "connection.json"),
            provider: "OpenAI",
            model: "json-model",
            apiFlavor: OpenAiApiFlavor.Responses));

        using var environment = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["MAIEUTICS_CONFIG"] = null,
            ["MAIEUTICS_MODEL"] = "alias-model",
            ["MAIEUTICS_OPENAI_API"] = nameof(OpenAiApiFlavor.ChatCompletions),
            ["Maieutics__Model__Name"] = "standard-model",
            ["Maieutics__Providers__OpenAI__ApiFlavor"] = null,
            ["OPENAI_API_KEY"] = null,
            ["OPENAI_BASE_URL"] = null
        });

        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(
                ["--config", configurationFile, "--model", "command-model"]);
            using var host = builder.Build();

            builder.Configuration["Maieutics:Model:Name"].Should().Be("command-model");
            builder.Configuration["Maieutics:Providers:OpenAI:ApiFlavor"].Should()
                .Be(nameof(OpenAiApiFlavor.ChatCompletions));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task RuntimeConfigurationReloadsValidSnapshotsAndKeepsLastKnownGood()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        var replacementConnectionFile = Path.Combine(root, "connection-2.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(replacementConnectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration(connectionFile, "Fake", "one", systemPrompt: "first", maxInputCharacters: 100),
            deadline.Token);

        var factory = new TrackingChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();

        try
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();
            var firstLease = runtime.Acquire();
            var firstClient = firstLease.Profile.ChatClient.Should().BeOfType<TrackingChatClient>().Subject;
            firstClient.Model.Should().Be("one");
            firstLease.Profile.Options.SystemPrompt.Should().Be("first");

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration(connectionFile, "Fake", "two", systemPrompt: "second", maxInputCharacters: 200,
                    flushCharacters: 2048),
                deadline.Token);
            runtime.Version.Should().Be(2);
            await using (var secondLease = runtime.Acquire())
            {
                var secondClient = secondLease.Profile.ChatClient.Should().BeOfType<TrackingChatClient>().Subject;
                secondClient.Model.Should().Be("two");
                secondLease.Profile.Options.SystemPrompt.Should().Be("second");
                secondLease.Profile.Options.MaxInputCharacters.Should().Be(200);
                runtime.GetKernelOptions().FlushCharacters.Should().Be(2048);
                firstClient.Disposed.Should().BeFalse();
            }

            await firstLease.DisposeAsync();
            firstClient.Disposed.Should().BeTrue();

            var acceptedVersion = runtime.Version;
            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration(connectionFile, "Fake", "invalid", maxInputCharacters: 0),
                deadline.Token);
            runtime.Version.Should().Be(acceptedVersion);
            (await AcquireClientAsync(runtime)).Model.Should().Be("two");

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                "{",
                deadline.Token);
            runtime.Version.Should().Be(acceptedVersion);

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration(connectionFile, "Fake", "three", systemPrompt: "repaired"),
                deadline.Token);
            runtime.Version.Should().Be(acceptedVersion + 1);
            (await AcquireClientAsync(runtime)).Model.Should().Be("three");

            acceptedVersion = runtime.Version;
            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration(connectionFile, "Fake", "fail"),
                deadline.Token);
            runtime.Version.Should().Be(acceptedVersion);
            (await AcquireClientAsync(runtime)).Model.Should().Be("three");

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration(replacementConnectionFile, "Fake", "three", flushCharacters: 4096),
                deadline.Token);
            runtime.Version.Should().Be(acceptedVersion + 1);
            runtime.ConnectionFile.Should().Be(Path.GetFullPath(connectionFile));
            runtime.GetKernelOptions().FlushCharacters.Should().Be(4096);
        }
        finally
        {
            await ((IAsyncDisposable)host).DisposeAsync();
            factory.Clients.Should().OnlyContain(static client => client.Disposed);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<TrackingChatClient> AcquireClientAsync(MaieuticsRuntimeConfiguration runtime)
    {
        await using var lease = runtime.Acquire();
        return lease.Profile.ChatClient.Should().BeOfType<TrackingChatClient>().Subject;
    }

    private static async Task WriteAndWaitForReloadAsync(
        IConfiguration configuration,
        MaieuticsRuntimeConfiguration runtime,
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        var previousAttempt = runtime.ReloadAttempt;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = configuration.GetReloadToken().RegisterChangeCallback(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            changed);
        await File.WriteAllTextAsync(path, contents, cancellationToken);
        await changed.Task.WaitAsync(cancellationToken);
        while (runtime.CompletedReloadAttempt <= previousAttempt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static string CreateConfiguration(
        string connectionFile,
        string provider,
        string model,
        OpenAiApiFlavor apiFlavor = OpenAiApiFlavor.Responses,
        string? systemPrompt = null,
        int maxInputCharacters = 32_000,
        int flushCharacters = 1024)
    {
        var root = new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["SystemPrompt"] = systemPrompt,
                ["Model"] = new JsonObject
                {
                    ["Provider"] = provider,
                    ["Name"] = model
                },
                ["Providers"] = new JsonObject
                {
                    ["OpenAI"] = new JsonObject
                    {
                        ["ApiFlavor"] = apiFlavor.ToString(),
                        ["ApiKey"] = "test-key"
                    }
                },
                ["Agent"] = new JsonObject
                {
                    ["MaxInputCharacters"] = maxInputCharacters
                },
                ["Jupyter"] = new JsonObject
                {
                    ["ConnectionFile"] = connectionFile,
                    ["FlushCharacters"] = flushCharacters
                }
            }
        };
        return root.ToJsonString();
    }

    private static Dictionary<string, string?> ClearedProviderEnvironment() => new()
    {
        ["MAIEUTICS_CONFIG"] = null,
        ["MAIEUTICS_PROVIDER"] = null,
        ["MAIEUTICS_MODEL"] = null,
        ["MAIEUTICS_OPENAI_API"] = null,
        ["OPENAI_API_KEY"] = null,
        ["OPENAI_BASE_URL"] = null,
        ["Maieutics__Model__Provider"] = null,
        ["Maieutics__Model__Name"] = null,
        ["Maieutics__Providers__OpenAI__ApiFlavor"] = null,
        ["Maieutics__Providers__OpenAI__ApiKey"] = null,
        ["Maieutics__Providers__OpenAI__Endpoint"] = null
    };

    private sealed class TrackingChatClientFactory : IConfiguredChatClientFactory
    {
        public string ProviderName => "Fake";

        public List<TrackingChatClient> Clients { get; } = [];

        public object GetConfigurationKey(MaieuticsOptions options) => options.Model.Name;

        public IChatClient Create(MaieuticsOptions options)
        {
            if (options.Model.Name == "fail")
            {
                throw new InvalidOperationException("Configured provider creation failure.");
            }

            var client = new TrackingChatClient(options.Model.Name);
            Clients.Add(client);
            return client;
        }
    }

    private sealed class BlockingChatClientFactory : IConfiguredChatClientFactory
    {
        public string ProviderName => "Fake";

        public TaskCompletionSource CreateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCreation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public object GetConfigurationKey(MaieuticsOptions options) => options.Model.Name;

        public IChatClient Create(MaieuticsOptions options)
        {
            if (options.Model.Name == "one")
            {
                CreateStarted.TrySetResult();
                ReleaseCreation.Task.GetAwaiter().GetResult();
            }

            return new TrackingChatClient(options.Model.Name);
        }
    }

    private sealed class TrackingChatClient(string model) : IChatClient
    {
        public string Model { get; } = model;

        public bool Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(new NotSupportedException());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposed = true;
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> original = new(StringComparer.Ordinal);

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (name, value) in values)
            {
                original.Add(name, Environment.GetEnvironmentVariable(name));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}