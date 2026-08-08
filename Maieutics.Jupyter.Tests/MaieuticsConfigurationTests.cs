using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.Jupyter.Shared;
using Maieutics.Providers;
using Maieutics.Providers.Anthropic;
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
            Directory.Delete(root, true);
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
            ((IDisposable)host).Dispose();
            provider.IsDisposed.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RuntimeInitializationAllowsNoModelOrProviderConfiguration()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-empty-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            new JsonObject
            {
                ["Maieutics"] = new JsonObject
                {
                    ["Jupyter"] = new JsonObject
                    {
                        ["ConnectionFile"] = connectionFile
                    }
                }
            }.ToJsonString(),
            deadline.Token);

        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            await using var host = builder.Build();
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();

            runtime.GetModelProfileSelection().Profiles.Should().BeEmpty();
            (await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token)).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RuntimeInitializationRetainsSourcesWithoutProfilesForDiscovery()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-source-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(connectionFile, new NamedSource("vendor", "one")),
            deadline.Token);

        var factory = new DiscoveryChatClientFactory();
        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            builder.Services.RemoveAll<IConfiguredChatClientFactory>();
            builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
            await using var host = builder.Build();
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();

            runtime.GetModelProfileSelection().Profiles.Should().BeEmpty();
            runtime.GetModelSourceIds().Should().Equal("vendor");
            var groups = await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);
            groups.Should().ContainSingle().Which.Models.Should().HaveCount(2);

            runtime.GetCachedAutomaticModelProfiles().Select(static profile => profile.Id).Should().Equal(
                "@vendor/model-alpha",
                "@vendor/model-beta");
            runtime.SelectModelProfile("@vendor/model-alpha");
            var selection = runtime.GetModelProfileSelection();
            selection.SelectedProfileId.Should().Be("@vendor/model-alpha");
            selection.HasSessionOverride.Should().BeTrue();
            selection.Profiles.Should().ContainSingle().Which.IsAutomatic.Should().BeTrue();

            var lease = runtime.Acquire();
            var client = lease.Profile.ChatClient.Should().BeOfType<TrackingChatClient>().Subject;
            client.Model.Should().Be("model-alpha");
            lease.Profile.ModelIdentity.Should().NotBeNull();
            lease.Profile.ModelIdentity.Provider.Should().Be("Fake");
            lease.Profile.ModelIdentity.Model.Should().Be("model-alpha");

            runtime.SelectModelProfile("@vendor/model-alpha");
            factory.Clients.Should().ContainSingle();
            runtime.ResetModelProfile();
            runtime.GetModelProfileSelection().Profiles.Should().BeEmpty();
            client.Disposed.Should().BeFalse();
            await lease.DisposeAsync();
            client.Disposed.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AutomaticProfileRetiresAfterItsSourceGenerationChanges()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-auto-profile-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(connectionFile, new NamedSource("vendor", "one")),
            deadline.Token);

        var factory = new DiscoveryChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();

        try
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();
            await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);
            runtime.SelectModelProfile("@vendor/model-alpha");
            var lease = runtime.Acquire();
            var client = lease.Profile.ChatClient.Should().BeOfType<TrackingChatClient>().Subject;

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateSourceOnlyConfiguration(connectionFile, new NamedSource("vendor", "two")),
                deadline.Token);

            runtime.GetModelProfileSelection().Profiles.Should().BeEmpty();
            client.Disposed.Should().BeFalse();
            await lease.DisposeAsync();
            client.Disposed.Should().BeTrue();
        }
        finally
        {
            await host.DisposeAsync();
            factory.Clients.Should().OnlyContain(static client => client.Disposed);
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task StaleDiscoveryCannotReplaceCacheAfterSourceReload()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-stale-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(connectionFile, new NamedSource("vendor", "one")),
            deadline.Token);

        var factory = new CoordinatedDiscoveryChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();

        try
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();
            var staleDiscovery = runtime.GetDiscoveredModelsAsync(
                refresh: true,
                cancellationToken: deadline.Token).AsTask();
            await factory.FirstDiscoveryStarted.Task.WaitAsync(deadline.Token);

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateSourceOnlyConfiguration(connectionFile, new NamedSource("vendor", "two")),
                deadline.Token);

            var current = await runtime.GetDiscoveredModelsAsync(
                refresh: true,
                cancellationToken: deadline.Token);
            current.Should().ContainSingle().Which.Models.Should().ContainSingle()
                .Which.Id.Should().Be("model-two");

            factory.ReleaseFirstDiscovery.TrySetResult();
            var stale = await staleDiscovery.WaitAsync(deadline.Token);
            stale.Should().ContainSingle().Which.Models.Should().ContainSingle()
                .Which.Id.Should().Be("model-one");
            runtime.GetCachedAutomaticModelProfiles().Should().ContainSingle()
                .Which.Id.Should().Be("@vendor/model-two");
        }
        finally
        {
            factory.ReleaseFirstDiscovery.TrySetResult();
            await host.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task CallerCancellationIsNotConvertedIntoOrCachedAsDiscoveryFailure()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-cancelled-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(connectionFile, new NamedSource("vendor", "one")),
            deadline.Token);

        var factory = new CoordinatedDiscoveryChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();

        try
        {
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var cancelledDiscovery = runtime.GetDiscoveredModelsAsync(
                refresh: true,
                cancellationToken: cancellation.Token).AsTask();
            await factory.FirstDiscoveryStarted.Task.WaitAsync(deadline.Token);
            await cancellation.CancelAsync();

            await runtime.Awaiting(_ => cancelledDiscovery).Should()
                .ThrowAsync<OperationCanceledException>();
            factory.DiscoveryCount.Should().Be(1);

            var recovered = await runtime.GetDiscoveredModelsAsync(
                cancellationToken: deadline.Token);
            recovered.Should().ContainSingle().Which.Models.Should().ContainSingle()
                .Which.Id.Should().Be("model-one");
            factory.DiscoveryCount.Should().Be(2, "the canceled attempt must not populate the discovery cache");
        }
        finally
        {
            factory.ReleaseFirstDiscovery.TrySetResult();
            await host.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AutomaticProfileRequiresQualifiedSelectorForDuplicateModelIds()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-auto-profile-ambiguity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(
                connectionFile,
                new NamedSource("first", "one"),
                new NamedSource("second", "one")),
            deadline.Token);

        var factory = new DiscoveryChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();

        try
        {
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();
            await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);

            runtime.Invoking(r => r.SelectModelProfile("model-alpha"))
                .Should().Throw<ArgumentException>()
                .WithMessage("*'@first/model-alpha'*'@second/model-alpha'*");

            runtime.SelectModelProfile("@second/model-alpha");
            await using var lease = runtime.Acquire();
            lease.Profile.ModelIdentity?.Model.Should().Be("model-alpha");
        }
        finally
        {
            await host.DisposeAsync();
            factory.Clients.Should().OnlyContain(static client => client.Disposed);
            Directory.Delete(root, true);
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
                static state => ((TaskCompletionSource?)state)?.TrySetResult(),
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
            await host.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ConfigurationSourcesUseJsonAliasStandardEnvironmentThenCommandLinePrecedence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-config-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        File.WriteAllText(configurationFile, CreateConfiguration(
            Path.Combine(root, "connection.json"),
            "OpenAI",
            "json-model",
            OpenAiApiFlavor.Responses));

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
            using var host = (IDisposable)builder.Build();

            builder.Configuration["Maieutics:Model:Name"].Should().Be("command-model");
            builder.Configuration["Maieutics:Sources:openai:ApiFlavor"].Should()
                .Be(nameof(OpenAiApiFlavor.ChatCompletions));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NamedConfigurationAliasesUseStandardEnvironmentAndCommandLinePrecedence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-named-config-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        File.WriteAllText(configurationFile, new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["DefaultProfile"] = "json-profile",
                ["Sources"] = new JsonObject
                {
                    ["anthropic"] = new JsonObject
                    {
                        ["Provider"] = "Anthropic",
                        ["ApiKey"] = "json-key"
                    }
                }
            }
        }.ToJsonString());

        using var environment = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["MAIEUTICS_CONFIG"] = null,
            ["MAIEUTICS_PROFILE"] = "alias-profile",
            ["ANTHROPIC_API_KEY"] = "alias-key",
            ["ANTHROPIC_BASE_URL"] = "https://alias.example",
            ["Maieutics__DefaultProfile"] = "standard-profile",
            ["Maieutics__Sources__anthropic__ApiKey"] = "standard-key",
            ["Maieutics__Sources__anthropic__Endpoint"] = "https://standard.example"
        });

        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(
                ["--config", configurationFile, "--profile", "command-profile"]);
            using var host = (IDisposable)builder.Build();

            builder.Configuration["Maieutics:DefaultProfile"].Should().Be("command-profile");
            builder.Configuration["Maieutics:Sources:anthropic:ApiKey"].Should().Be("standard-key");
            builder.Configuration["Maieutics:Sources:anthropic:Endpoint"].Should()
                .Be("https://standard.example");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("Anthropic")]
    public void ProviderClientGenerationKeysDetectCredentialRotationWithoutRevealingCredentials(
        string providerName)
    {
        const string firstApiKey = "first-secret-api-key";
        const string rotatedApiKey = "rotated-secret-api-key";
        IConfiguredChatClientFactory factory = providerName switch
        {
            "OpenAI" => new OpenAiChatClientFactory(),
            "Anthropic" => new AnthropicChatClientFactory(),
            _ => throw new InvalidOperationException($"Unsupported test provider '{providerName}'.")
        };

        var first = factory.BindSource(
            "source",
            CreateProviderSourceConfiguration(providerName, firstApiKey));
        var unchanged = factory.BindSource(
            "source",
            CreateProviderSourceConfiguration(providerName, firstApiKey));
        var rotated = factory.BindSource(
            "source",
            CreateProviderSourceConfiguration(providerName, rotatedApiKey));

        first.ClientGenerationKey.Should().Be(unchanged.ClientGenerationKey);
        first.ClientGenerationKey.Should().NotBe(rotated.ClientGenerationKey);
        first.ClientGenerationKey.ToString().Should()
            .Contain("<redacted>")
            .And.NotContain(firstApiKey);
        rotated.ClientGenerationKey.ToString().Should().NotContain(rotatedApiKey);
    }

    [Theory]
    [InlineData("OpenAI", "ApiFlavor", "Responses", "UnexpectedAnthropicField")]
    [InlineData("Anthropic", "ApiKey", "test-key", "UnexpectedOpenAiField")]
    public async Task ProviderSourcesRejectUnknownProviderSpecificFields(
        string provider,
        string requiredField,
        string requiredValue,
        string unknownField)
    {
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-provider-fields-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(
            connectionFile,
            TestContext.Current.CancellationToken);
        var configurationFile = Path.Combine(root, "maieutics.json");
        var source = new JsonObject
        {
            ["Provider"] = provider,
            ["ApiKey"] = "test-key",
            [requiredField] = requiredValue,
            [unknownField] = true
        };
        await File.WriteAllTextAsync(configurationFile, new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["DefaultProfile"] = "test",
                ["Sources"] = new JsonObject { ["source"] = source },
                ["Profiles"] = new JsonObject
                {
                    ["test"] = new JsonObject
                    {
                        ["Source"] = "source",
                        ["Model"] = "test-model"
                    }
                },
                ["Jupyter"] = new JsonObject { ["ConnectionFile"] = connectionFile }
            }
        }.ToJsonString(), TestContext.Current.CancellationToken);

        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            await using var host = builder.Build();

            host.Services.Invoking(services => services.GetRequiredService<MaieuticsRuntimeConfiguration>())
                .Should().Throw<InvalidOperationException>()
                .WithMessage($"*{unknownField}*");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LegacyMaxHistoryCharactersConvertsToMaxHistoryBytes()
    {
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-history-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(
            connectionFile,
            TestContext.Current.CancellationToken);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration(
                connectionFile,
                "Fake",
                "test-model",
                maxHistoryCharacters: 123_456),
            TestContext.Current.CancellationToken);

        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory, TrackingChatClientFactory>();
        var host = builder.Build();

        try
        {
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();
            await using var lease = runtime.Acquire();
            lease.Profile.Options.MaxHistoryBytes.Should().Be(246_912);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MaxHistoryBytesCannotBeCombinedWithLegacyMaxHistoryCharacters()
    {
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-history-conflict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(
            connectionFile,
            TestContext.Current.CancellationToken);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration(
                connectionFile,
                "Fake",
                "test-model",
                maxHistoryBytes: 400_000,
                maxHistoryCharacters: 200_000),
            TestContext.Current.CancellationToken);

        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            await using var host = builder.Build();

            host.Services.Invoking(services => services.GetRequiredService<MaieuticsRuntimeConfiguration>())
                .Should().Throw<InvalidOperationException>()
                .WithMessage("*MaxHistoryBytes*MaxHistoryCharacters*");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AgentTurnDurationBindsToLeaseAndNegativeValuesAreRejected()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-turn-duration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var connectionFile = Path.Combine(root, "connection.json");
            await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
            var configurationFile = Path.Combine(root, "maieutics.json");
            await File.WriteAllTextAsync(
                configurationFile,
                CreateConfiguration(connectionFile, "Fake", "one", maxTurnDuration: "00:00:45"),
                deadline.Token);

            var factory = new TrackingChatClientFactory();
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            builder.Services.RemoveAll<IConfiguredChatClientFactory>();
            builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
            var host = builder.Build();
            try
            {
                var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();
                await using var lease = runtime.Acquire();
                lease.Profile.Options.MaxTurnDuration.Should().Be(TimeSpan.FromSeconds(45));
            }
            finally
            {
                await host.DisposeAsync();
            }

            FluentActions.Invoking(() =>
                    new MaieuticsAgentOptions { MaxTurnDuration = TimeSpan.FromSeconds(-1) }.Validate())
                .Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new MaieuticsAgentOptions { MaxTurnDuration = TimeSpan.Zero }.Validate())
                .Should().NotThrow();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task HistoryLimitCompatibilityParticipatesInHotReloadValidation()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-history-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration(connectionFile, "Fake", "test-model", maxHistoryBytes: 1_000),
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
            await using (var lease = runtime.Acquire())
            {
                lease.Profile.Options.MaxHistoryBytes.Should().Be(1_000);
            }

            while (runtime.CompletedReloadAttempt == 0)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration(connectionFile, "Fake", "test-model", maxHistoryCharacters: 600),
                deadline.Token);
            runtime.Version.Should().Be(2);
            await using (var lease = runtime.Acquire())
            {
                lease.Profile.Options.MaxHistoryBytes.Should().Be(1_200);
            }

            var acceptedVersion = runtime.Version;
            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration(
                    connectionFile,
                    "Fake",
                    "test-model",
                    maxHistoryBytes: 1_400,
                    maxHistoryCharacters: 700),
                deadline.Token);
            runtime.Version.Should().Be(acceptedVersion);

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration(connectionFile, "Fake", "test-model", maxHistoryCharacters: int.MaxValue),
                deadline.Token);
            runtime.Version.Should().Be(acceptedVersion);

            await using (var lease = runtime.Acquire())
            {
                lease.Profile.Options.MaxHistoryBytes.Should().Be(1_200);
            }

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration(connectionFile, "Fake", "test-model", maxHistoryBytes: 1_400),
                deadline.Token);
            runtime.Version.Should().Be(acceptedVersion + 1);
            await using (var lease = runtime.Acquire())
            {
                lease.Profile.Options.MaxHistoryBytes.Should().Be(1_400);
            }
        }
        finally
        {
            await host.DisposeAsync();
            factory.Clients.Should().OnlyContain(static client => client.Disposed);
            Directory.Delete(root, true);
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
            await host.DisposeAsync();
            factory.Clients.Should().OnlyContain(static client => client.Disposed);
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task NamedProfileCatalogSwitchesReusesAndAtomicallyReplacesGenerations()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-profile-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateNamedConfiguration(
                connectionFile,
                "one",
                new NamedProfile("one", "primary", "model-one", "primary-v1"),
                new NamedProfile("two", "secondary", "model-two", "secondary-v1")),
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
            var initialSelection = runtime.GetModelProfileSelection();
            initialSelection.DefaultProfileId.Should().Be("one");
            initialSelection.SelectedProfileId.Should().Be("one");
            initialSelection.HasSessionOverride.Should().BeFalse();

            var firstLease = runtime.Acquire();
            var firstClient = firstLease.Profile.ChatClient.Should().BeOfType<TrackingChatClient>().Subject;
            firstLease.Profile.ModelIdentity.Should().Be(new AgentModelIdentity(
                new AgentModelProfileId("one"), "Fake", "model-one"));

            runtime.SelectModelProfile("TWO");
            var secondLease = runtime.Acquire();
            var secondClient = secondLease.Profile.ChatClient.Should().BeOfType<TrackingChatClient>().Subject;
            runtime.GetModelProfileSelection().HasSessionOverride.Should().BeTrue();

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateNamedConfiguration(
                    connectionFile,
                    "one",
                    new NamedProfile("one", "primary", "model-one-v2", "primary-v2"),
                    new NamedProfile("two", "secondary", "model-two", "secondary-v1")),
                deadline.Token);

            await using (var reusedLease = runtime.Acquire())
            {
                reusedLease.Profile.ChatClient.Should().BeSameAs(secondClient);
                reusedLease.Profile.ModelIdentity?.ProfileId.Value.Should().Be("two");
            }

            firstClient.Disposed.Should().BeFalse();
            await firstLease.DisposeAsync();
            firstClient.Disposed.Should().BeTrue();

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateNamedConfiguration(
                    connectionFile,
                    "one",
                    new NamedProfile("one", "primary", "model-one-v2", "primary-v2")),
                deadline.Token);
            var fallback = runtime.GetModelProfileSelection();
            fallback.SelectedProfileId.Should().Be("one");
            fallback.HasSessionOverride.Should().BeFalse();
            secondClient.Disposed.Should().BeFalse();
            await secondLease.DisposeAsync();
            secondClient.Disposed.Should().BeTrue();

            var acceptedVersion = runtime.Version;
            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateNamedConfiguration(
                    connectionFile,
                    "a-good",
                    new NamedProfile("a-good", "primary", "replacement", "primary-v3"),
                    new NamedProfile("z-bad", "broken", "fail", "broken-v1")),
                deadline.Token);
            runtime.Version.Should().Be(acceptedVersion);
            (await AcquireClientAsync(runtime)).Model.Should().Be("model-one-v2");
            factory.Clients.Single(client => client.Model == "replacement").Disposed.Should().BeTrue();
        }
        finally
        {
            await host.DisposeAsync();
            factory.Clients.Should().OnlyContain(static client => client.Disposed);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NamedAndLegacyModelConfigurationCannotBeCombined()
    {
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-profile-conflict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(
            connectionFile,
            TestContext.Current.CancellationToken);
        var configurationFile = Path.Combine(root, "maieutics.json");
        var configuration = JsonNode.Parse(CreateNamedConfiguration(
            connectionFile,
            "one",
            new NamedProfile("one", "primary", "model-one", "primary-v1")))?.AsObject();
        configuration?["Maieutics"]?["Model"] = new JsonObject
        {
            ["Provider"] = "Fake",
            ["Name"] = "legacy"
        };
        await File.WriteAllTextAsync(
            configurationFile,
            configuration?.ToJsonString(),
            TestContext.Current.CancellationToken);

        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            builder.Services.RemoveAll<IConfiguredChatClientFactory>();
            builder.Services.AddSingleton<IConfiguredChatClientFactory>(new TrackingChatClientFactory());
            await using var host = builder.Build();

            host.Services.Invoking(services =>
                    services.GetRequiredService<MaieuticsRuntimeConfiguration>()).Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*cannot be combined*");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task McpFileConfigurationValidatesTransportsHttpsSseKeysAndDefaultEnablement()
    {
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-mcp-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(
            connectionFile,
            TestContext.Current.CancellationToken);
        var configurationFile = Path.Combine(root, "maieutics.json");
        var mcpFile = Path.Combine(root, "mcp.json");
        await File.WriteAllTextAsync(configurationFile, CreateMcpHostConfigurationBase(connectionFile),
            TestContext.Current.CancellationToken);

        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            await using (var host = builder.Build())
            {
                host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>().Should().NotBeNull();
            }

            AssertRejected(
                new JsonObject { ["remote"] = HttpMcpServer("http://example.test/mcp") },
                "*must use HTTPS*");
            AssertRejected(
                new JsonObject { ["remote"] = SseMcpServer("https://example.test/mcp") },
                "*unsupported 'sse'*");

            var invalidStdio = StdioMcpServer();
            invalidStdio["Nope"] = true;
            AssertRejected(new JsonObject { ["stdio"] = invalidStdio }, "*not valid for MCP server*");

            await File.WriteAllTextAsync(mcpFile, new JsonObject
            {
                ["mcpServers"] = new JsonObject { ["one"] = StdioMcpServer() },
                ["servers"] = new JsonObject { ["two"] = StdioMcpServer() }
            }.ToJsonString(), TestContext.Current.CancellationToken);
            var conflicting = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            await using var conflictingHost = conflicting.Build();
            conflictingHost.Services.Invoking(static services =>
                    services.GetRequiredService<MaieuticsRuntimeConfiguration>())
                .Should().Throw<Exception>().WithMessage("*must not combine*");

            await File.WriteAllTextAsync(mcpFile, "{", TestContext.Current.CancellationToken);
            FluentActions.Invoking(() => MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]))
                .Should().Throw<JsonException>();

            // A server disabled with the VS Code convention is skipped before transport validation.
            await File.WriteAllTextAsync(mcpFile, CreateMcpFile(new JsonObject
            {
                ["disabled"] = new JsonObject
                {
                    ["enabled"] = false,
                    ["type"] = "http",
                    ["url"] = "not-a-url"
                }
            }), TestContext.Current.CancellationToken);
            var disabledBuilder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            await using var disabledHost = disabledBuilder.Build();
            disabledHost.Services.GetRequiredService<MaieuticsRuntimeConfiguration>().Should().NotBeNull();

            await File.WriteAllTextAsync(mcpFile, CreateMcpFile(new JsonObject
            {
                ["stdio"] = StdioMcpServer(),
                ["http"] = HttpMcpServer("http://127.0.0.1:65535/mcp")
            }), TestContext.Current.CancellationToken);
            var valid = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            await using var validHost = valid.Build();
            validHost.Services.GetRequiredService<MaieuticsRuntimeConfiguration>().Should().NotBeNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }

        void AssertRejected(JsonObject servers, string expectedMessage)
        {
            File.WriteAllText(mcpFile, CreateMcpFile(servers));
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            var host = builder.Build();
            using (IDisposable _ = host)
            {
                host.Services.Invoking(static services =>
                        services.GetRequiredService<MaieuticsRuntimeConfiguration>())
                    .Should().Throw<Exception>().WithMessage(expectedMessage);
            }
        }
    }

    [Fact]
    public async Task McpFileChangesTriggerReloadAndInvalidUpdatesRetainLastKnownGood()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-mcp-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        var mcpFile = Path.Combine(root, "mcp.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateMcpHostConfigurationBase(connectionFile),
            deadline.Token);

        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        var host = builder.Build();
        try
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();
            runtime.GetMcpServers().Should().BeEmpty();

            var acceptedVersion = runtime.Version;
            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                mcpFile,
                CreateMcpFile(new JsonObject { ["remote"] = HttpMcpServer("http://example.test/mcp") }),
                deadline.Token);
            runtime.Version.Should().Be(acceptedVersion);
            runtime.GetMcpServers().Should().BeEmpty();

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                mcpFile,
                CreateMcpFile(new JsonObject { ["stdio"] = StdioMcpServer() }),
                deadline.Token);
            runtime.Version.Should().Be(acceptedVersion);
            runtime.GetMcpServers().Should().BeEmpty();
        }
        finally
        {
            await host.StopAsync(deadline.Token);
            await host.DisposeAsync();
            Directory.Delete(root, true);
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
            static state => (state as TaskCompletionSource)?.TrySetResult(),
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
        int flushCharacters = 1024,
        int? maxHistoryBytes = null,
        int? maxHistoryCharacters = null,
        string? maxTurnDuration = null)
    {
        var agent = new JsonObject
        {
            ["MaxInputCharacters"] = maxInputCharacters
        };
        if (maxTurnDuration is not null) agent["MaxTurnDuration"] = maxTurnDuration;

        if (maxHistoryBytes.HasValue) agent["MaxHistoryBytes"] = maxHistoryBytes.Value;

        if (maxHistoryCharacters.HasValue) agent["MaxHistoryCharacters"] = maxHistoryCharacters.Value;

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
                ["Agent"] = agent,
                ["Jupyter"] = new JsonObject
                {
                    ["ConnectionFile"] = connectionFile,
                    ["FlushCharacters"] = flushCharacters
                }
            }
        };
        return root.ToJsonString();
    }

    private static string CreateMcpFile(JsonObject servers)
    {
        return new JsonObject
        {
            ["mcpServers"] = servers
        }.ToJsonString();
    }

    private static string CreateMcpHostConfigurationBase(string connectionFile)
    {
        return new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["Jupyter"] = new JsonObject { ["ConnectionFile"] = connectionFile }
            }
        }.ToJsonString();
    }

    private static JsonObject StdioMcpServer()
    {
        return new JsonObject
        {
            ["command"] = "/usr/bin/false",
            ["args"] = new JsonArray(),
            ["env"] = new JsonObject()
        };
    }

    private static JsonObject HttpMcpServer(string url)
    {
        return new JsonObject
        {
            ["type"] = "http",
            ["url"] = url,
            ["headers"] = new JsonObject()
        };
    }

    private static JsonObject SseMcpServer(string url)
    {
        return new JsonObject
        {
            ["type"] = "sse",
            ["url"] = url
        };
    }

    private static string CreateNamedConfiguration(
        string connectionFile,
        string defaultProfile,
        params NamedProfile[] profiles)
    {
        var sources = new JsonObject();
        var profileNodes = new JsonObject();
        foreach (var profile in profiles)
        {
            sources[profile.SourceId] = new JsonObject
            {
                ["Provider"] = "Fake",
                ["Revision"] = profile.SourceRevision
            };
            profileNodes[profile.Id] = new JsonObject
            {
                ["Source"] = profile.SourceId,
                ["Model"] = profile.Model
            };
        }

        return new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["DefaultProfile"] = defaultProfile,
                ["Sources"] = sources,
                ["Profiles"] = profileNodes,
                ["Jupyter"] = new JsonObject
                {
                    ["ConnectionFile"] = connectionFile
                }
            }
        }.ToJsonString();
    }

    private static string CreateSourceOnlyConfiguration(
        string connectionFile,
        params NamedSource[] sources)
    {
        var sourceNodes = new JsonObject();
        foreach (var source in sources)
            sourceNodes[source.Id] = new JsonObject
            {
                ["Provider"] = "Fake",
                ["Revision"] = source.Revision
            };

        return new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["Sources"] = sourceNodes,
                ["Jupyter"] = new JsonObject
                {
                    ["ConnectionFile"] = connectionFile
                }
            }
        }.ToJsonString();
    }

    private static IConfigurationSection CreateProviderSourceConfiguration(string providerName, string apiKey)
    {
        var values = new Dictionary<string, string?>
        {
            ["Source:Provider"] = providerName,
            ["Source:ApiKey"] = apiKey
        };
        if (string.Equals(providerName, "OpenAI", StringComparison.Ordinal))
            values["Source:ApiFlavor"] = nameof(OpenAiApiFlavor.Responses);

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build()
            .GetSection("Source");
    }

    private static Dictionary<string, string?> ClearedProviderEnvironment()
    {
        return new Dictionary<string, string?>
        {
            ["MAIEUTICS_CONFIG"] = null,
            ["MAIEUTICS_PROFILE"] = null,
            ["MAIEUTICS_PROVIDER"] = null,
            ["MAIEUTICS_MODEL"] = null,
            ["MAIEUTICS_OPENAI_API"] = null,
            ["MAIEUTICS_WORKSPACE"] = null,
            ["OPENAI_API_KEY"] = null,
            ["OPENAI_BASE_URL"] = null,
            ["ANTHROPIC_API_KEY"] = null,
            ["ANTHROPIC_BASE_URL"] = null,
            ["Maieutics__DefaultProfile"] = null,
            ["Maieutics__Sources__openai__ApiFlavor"] = null,
            ["Maieutics__Sources__openai__ApiKey"] = null,
            ["Maieutics__Sources__openai__Endpoint"] = null,
            ["Maieutics__Sources__anthropic__ApiKey"] = null,
            ["Maieutics__Sources__anthropic__Endpoint"] = null,
            ["Maieutics__Model__Provider"] = null,
            ["Maieutics__Model__Name"] = null,
            ["Maieutics__Workspace__Root"] = null,
            ["Maieutics__Agent__MaxHistoryBytes"] = null,
            ["Maieutics__Agent__MaxHistoryCharacters"] = null,
            ["Maieutics__Providers__OpenAI__ApiFlavor"] = null,
            ["Maieutics__Providers__OpenAI__ApiKey"] = null,
            ["Maieutics__Providers__OpenAI__Endpoint"] = null
        };
    }

    [Fact]
    public async Task GetDiscoveredModelsReturnsModelsFromDiscoveryEnabledSources()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateNamedConfiguration(
                connectionFile,
                "one",
                new NamedProfile("one", "discovery-source", "test-model", "v1")),
            deadline.Token);

        var factory = new DiscoveryChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();

        try
        {
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();
            var groups = await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);

            var group = groups.Should().ContainSingle().Subject;
            group.SourceId.Should().Be("discovery-source");
            group.Provider.Should().Be("Fake");
            group.Failure.Should().BeNull();
            group.Models.Should().HaveCount(2);
            group.Models[0].Id.Should().Be("model-alpha");
            group.Models[0].Provider.Should().Be("Fake");
            group.Models[0].OwnedBy.Should().Be("test-org");
            group.Models[1].Id.Should().Be("model-beta");
            group.Models[1].Provider.Should().Be("Fake");
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task GetDiscoveredModelsCacheRespectsRefreshFlag()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-discovery-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateNamedConfiguration(
                connectionFile,
                "one",
                new NamedProfile("one", "discovery-source", "test-model", "v1")),
            deadline.Token);

        var factory = new DiscoveryChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();

        try
        {
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();

            // First call populates cache
            await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);
            factory.DiscoveryCount.Should().Be(1);

            // Second call within TTL uses cache
            await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);
            factory.DiscoveryCount.Should().Be(1);

            // Third call with refresh=true bypasses cache
            await runtime.GetDiscoveredModelsAsync(refresh: true, cancellationToken: deadline.Token);
            factory.DiscoveryCount.Should().Be(2);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task GetDiscoveredModelsSkipsSourcesWithoutDiscoverySupport()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-discovery-skip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateNamedConfiguration(
                connectionFile,
                "one",
                new NamedProfile("one", "no-discovery", "test-model", "v1")),
            deadline.Token);

        var factory = new TrackingChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();

        try
        {
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();
            var groups = await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);
            groups.Should().BeEmpty();
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task GetDiscoveredModelsCapturesErrorsFromFailingSources()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-discovery-error-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateNamedConfiguration(
                connectionFile,
                "one",
                new NamedProfile("one", "failing-source", "test-model", "v1")),
            deadline.Token);

        var factory = new FailingDiscoveryChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();

        try
        {
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();
            var groups = await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);

            var group = groups.Should().ContainSingle().Subject;
            group.Failure.Should().Be(ModelDiscoveryFailureKind.ProviderError);
            group.Models.Should().BeEmpty();
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    private sealed class TrackingChatClientFactory : IConfiguredChatClientFactory
    {
        public List<TrackingChatClient> Clients { get; } = [];
        public string ProviderName => "Fake";

        public IConfiguredChatClientSource BindSource(string sourceId, IConfigurationSection configuration)
        {
            return new TrackingSource(this, sourceId, configuration["Revision"] ?? sourceId);
        }

        private IChatClient Create(string model)
        {
            if (model == "fail") throw new InvalidOperationException("Configured provider creation failure.");

            var client = new TrackingChatClient(model);
            Clients.Add(client);
            return client;
        }

        private sealed class TrackingSource(
            TrackingChatClientFactory factory,
            string sourceId,
            string revision) : IConfiguredChatClientSource
        {
            public string ProviderName => "Fake";

            public object ClientGenerationKey => (sourceId, revision);

            public AgentModelCapabilities Capabilities =>
                AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

            public IChatClient Create(string model)
            {
                return factory.Create(model);
            }
        }
    }

    private sealed class BlockingChatClientFactory : IConfiguredChatClientFactory
    {
        public TaskCompletionSource CreateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCreation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderName => "Fake";

        public IConfiguredChatClientSource BindSource(string sourceId, IConfigurationSection configuration)
        {
            return new BlockingSource(this, sourceId);
        }

        private IChatClient Create(string model)
        {
            if (model == "one")
            {
                CreateStarted.TrySetResult();
                ReleaseCreation.Task.GetAwaiter().GetResult();
            }

            return new TrackingChatClient(model);
        }

        private sealed class BlockingSource(
            BlockingChatClientFactory factory,
            string sourceId) : IConfiguredChatClientSource
        {
            public string ProviderName => "Fake";

            public object ClientGenerationKey => sourceId;

            public AgentModelCapabilities Capabilities =>
                AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

            public IChatClient Create(string model)
            {
                return factory.Create(model);
            }
        }
    }

    private sealed class TrackingChatClient(string model) : IChatClient
    {
        public string Model { get; } = model;

        public bool Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<ChatResponse>(new NotSupportedException());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed record NamedProfile(string Id, string SourceId, string Model, string SourceRevision);

    private sealed record NamedSource(string Id, string Revision);

    private sealed class DiscoveryChatClientFactory : IConfiguredChatClientFactory
    {
        public int DiscoveryCount { get; private set; }

        public List<TrackingChatClient> Clients { get; } = [];
        public string ProviderName => "Fake";

        public IConfiguredChatClientSource BindSource(string sourceId, IConfigurationSection configuration)
        {
            return new DiscoverySource(this, sourceId, configuration["Revision"] ?? sourceId);
        }

        private sealed class DiscoverySource(
            DiscoveryChatClientFactory factory,
            string sourceId,
            string revision) : IConfiguredChatClientSource, IModelDiscoverySource
        {
            public string ProviderName => "Fake";

            public object ClientGenerationKey => (sourceId, revision);

            public AgentModelCapabilities Capabilities =>
                AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

            public IChatClient Create(string model)
            {
                var client = new TrackingChatClient(model);
                factory.Clients.Add(client);
                return client;
            }

            public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
                CancellationToken cancellationToken = default)
            {
                factory.DiscoveryCount++;
                return new ValueTask<IReadOnlyList<AgentModelDescriptor>>([
                    new AgentModelDescriptor("model-alpha", "Fake", "test-org"),
                    new AgentModelDescriptor("model-beta", "Fake")
                ]);
            }
        }
    }

    private sealed class FailingDiscoveryChatClientFactory : IConfiguredChatClientFactory
    {
        public string ProviderName => "Fake";

        public IConfiguredChatClientSource BindSource(string sourceId, IConfigurationSection configuration)
        {
            return new FailingSource(sourceId);
        }

        private sealed class FailingSource(
            string sourceId) : IConfiguredChatClientSource, IModelDiscoverySource
        {
            public string ProviderName => "Fake";

            public object ClientGenerationKey => sourceId;

            public AgentModelCapabilities Capabilities =>
                AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

            public IChatClient Create(string model)
            {
                return new TrackingChatClient(model);
            }

            public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("simulated discovery failure");
            }
        }
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
            foreach (var (name, value) in original) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed class CoordinatedDiscoveryChatClientFactory : IConfiguredChatClientFactory
    {
        private int discoveryCount;

        public int DiscoveryCount => Volatile.Read(ref discoveryCount);

        public TaskCompletionSource FirstDiscoveryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstDiscovery { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderName => "Fake";

        public IConfiguredChatClientSource BindSource(string sourceId, IConfigurationSection configuration)
        {
            return new CoordinatedDiscoverySource(
                this,
                sourceId,
                configuration["Revision"] ?? throw new InvalidOperationException("A revision is required."));
        }

        private async ValueTask<IReadOnlyList<AgentModelDescriptor>> DiscoverAsync(
            string revision,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref discoveryCount) == 1)
            {
                FirstDiscoveryStarted.TrySetResult();
                await ReleaseFirstDiscovery.Task.WaitAsync(cancellationToken);
            }

            return [new AgentModelDescriptor($"model-{revision}", "Fake")];
        }

        private sealed class CoordinatedDiscoverySource(
            CoordinatedDiscoveryChatClientFactory factory,
            string sourceId,
            string revision) : IConfiguredChatClientSource, IModelDiscoverySource
        {
            public string ProviderName => "Fake";

            public object ClientGenerationKey => (sourceId, revision);

            public AgentModelCapabilities Capabilities =>
                AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

            public IChatClient Create(string model)
            {
                return new TrackingChatClient(model);
            }

            public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
                CancellationToken cancellationToken = default)
            {
                return factory.DiscoverAsync(revision, cancellationToken);
            }
        }
    }
}
