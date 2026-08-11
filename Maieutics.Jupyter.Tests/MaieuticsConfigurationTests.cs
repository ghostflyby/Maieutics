using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.Jupyter.Shared;
using Maieutics.Plugins;
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

    [Fact(Timeout = 40_000)]
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);

            runtime.GetModelProfileSelection().Profiles.Should().BeEmpty();
            (await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token)).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 40_000)]
    public async Task RuntimeInitializationCancellationRetiresCreatedProfileExactlyOnce()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-init-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration(connectionFile, "Fake", "test-model"),
            deadline.Token);

        var factory = new TrackingChatClientFactory
        {
            ClientCreated = _ => cancellation.Cancel()
        };
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();
        try
        {
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();
            var pluginHosts = host.Services.GetRequiredService<PluginHostManager>();

            await runtime.Awaiting(r => r.InitializeAsync(pluginHosts, cancellation.Token))
                .Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            await host.DisposeAsync();
            factory.Clients.Should().ContainSingle().Which.DisposalCount.Should().Be(1);
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 40_000)]
    public async Task RuntimeInitializationFailureRetiresEarlierProfilesExactlyOnce()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-init-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateNamedConfiguration(
                connectionFile,
                "one",
                new NamedProfile("one", "first", "model-one", "v1"),
                new NamedProfile("two", "second", "fail", "v1")),
            deadline.Token);

        var factory = new TrackingChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();
        try
        {
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();
            var pluginHosts = host.Services.GetRequiredService<PluginHostManager>();

            await runtime.Awaiting(r => r.InitializeAsync(pluginHosts, deadline.Token))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*provider creation failure*");
        }
        finally
        {
            await host.DisposeAsync();
            factory.Clients.Should().ContainSingle().Which.DisposalCount.Should().Be(1);
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 40_000)]
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);

            runtime.GetModelProfileSelection().Profiles.Should().BeEmpty();
            runtime.GetModelSourceIds().Should().Equal("vendor");
            var groups = await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);
            groups.Should().ContainSingle().Which.Models.Should().HaveCount(2);

            runtime.GetCachedAutomaticModelProfiles().Select(static profile => profile.Id).Should().Equal(
                "@vendor/model-alpha",
                "@vendor/model-beta");
            await runtime.SelectModelProfileAsync("@vendor/model-alpha", deadline.Token);
            var selection = runtime.GetModelProfileSelection();
            selection.SelectedProfileId.Should().Be("@vendor/model-alpha");
            selection.HasSessionOverride.Should().BeTrue();
            selection.Profiles.Should().ContainSingle().Which.IsAutomatic.Should().BeTrue();

            var lease = await runtime.AcquireAsync(deadline.Token);
            var client = lease.Profile.ChatClient.Should().BeOfType<TrackingChatClient>().Subject;
            client.Model.Should().Be("model-alpha");
            lease.Profile.ModelIdentity.Should().NotBeNull();
            lease.Profile.ModelIdentity.Provider.Should().Be("Fake");
            lease.Profile.ModelIdentity.Model.Should().Be("model-alpha");

            await runtime.SelectModelProfileAsync("@vendor/model-alpha", deadline.Token);
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

    [Fact(Timeout = 40_000)]
    public async Task AutomaticProfileSelectionCancellationRetiresCreatedClientExactlyOnce()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-auto-profile-cancel-{Guid.NewGuid():N}");
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
            await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);
            factory.ClientCreated = model =>
            {
                if (model == "model-alpha") cancellation.Cancel();
            };

            await runtime.Awaiting(r => r
                    .SelectModelProfileAsync("@vendor/model-alpha", cancellation.Token)
                    .AsTask())
                .Should().ThrowAsync<OperationCanceledException>();

            runtime.GetModelProfileSelection().HasSessionOverride.Should().BeFalse();
        }
        finally
        {
            await host.DisposeAsync();
            factory.Clients.Should().ContainSingle().Which.DisposalCount.Should().Be(1);
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
            await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);
            await runtime.SelectModelProfileAsync("@vendor/model-alpha", deadline.Token);
            var lease = await runtime.AcquireAsync(deadline.Token);
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
    public async Task AutomaticProfileRetiresWhenHostedCapabilitiesChangeOnReload()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-auto-profile-endpoints-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        var webSearch = new JsonArray(new JsonObject
        {
            ["Url"] = "https://api.example.com/v1",
            ["Capabilities"] = new JsonArray("WebSearch")
        });
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(
                connectionFile,
                webSearch,
                new NamedSource("vendor", "one", "https://api.example.com/v1")),
            deadline.Token);

        var factory = new DiscoveryChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();

        try
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
            await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);
            await runtime.SelectModelProfileAsync("@vendor/model-alpha", deadline.Token);
            var lease = await runtime.AcquireAsync(deadline.Token);
            var client = lease.Profile.ChatClient.Should().BeOfType<TrackingChatClient>().Subject;
            lease.Profile.HostedCapabilities.Should().Equal(["WebSearch"]);

            var shell = new JsonArray(new JsonObject
            {
                ["Url"] = "https://api.example.com/v1",
                ["Capabilities"] = new JsonArray("Shell")
            });
            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateSourceOnlyConfiguration(
                    connectionFile,
                    shell,
                    new NamedSource("vendor", "one", "https://api.example.com/v1")),
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
    public async Task AutomaticProfileSelectionFailureRetiresCreatedClientExactlyOnce()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-auto-profile-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(connectionFile, new NamedSource("vendor", "one")),
            deadline.Token);

        var factory = new CoordinatedAutomaticProfileChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();
        try
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
            await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);

            var selection = Task.Run(
                () => runtime.SelectModelProfileAsync("@vendor/model-alpha", deadline.Token).AsTask(),
                deadline.Token);
            await factory.AutomaticCreateStarted.Task.WaitAsync(deadline.Token);
            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateSourceOnlyConfiguration(connectionFile, new NamedSource("vendor", "two")),
                deadline.Token);
            factory.ReleaseAutomaticCreate.TrySetResult();

            await FluentActions.Awaiting(() => selection)
                .Should().ThrowAsync<ArgumentException>()
                .WithMessage("*changed while the automatic profile was selected*");
            runtime.GetModelProfileSelection().HasSessionOverride.Should().BeFalse();
        }
        finally
        {
            factory.ReleaseAutomaticCreate.TrySetResult();
            await host.DisposeAsync();
            factory.Clients.Should().ContainSingle().Which.DisposalCount.Should().Be(1);
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task CanceledPluginReadinessWaitRollsBackTheProfileGenerationLease()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-profile-readiness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration(connectionFile, "Fake", "test-model"),
            deadline.Token);

        var factory = new TrackingChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();
        try
        {
            var runtime = host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>();
            var pluginHosts = host.Services.GetRequiredService<PluginHostManager>();
            await runtime.InitializeAsync(pluginHosts, deadline.Token);

            var acquisition = runtime.AcquireAsync(cancellation.Token);
            acquisition.IsCompleted.Should().BeFalse();
            cancellation.Cancel();

            await acquisition
                .Invoking(static task => task)
                .Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            await host.DisposeAsync();
            factory.Clients.Should().ContainSingle().Which.Disposed.Should().BeTrue();
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
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

    [Fact(Timeout = 40_000)]
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
            await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);

            await runtime.Awaiting(r => r.SelectModelProfileAsync("model-alpha", deadline.Token).AsTask())
                .Should().ThrowAsync<ArgumentException>()
                .WithMessage("*'@first/model-alpha'*'@second/model-alpha'*");

            await runtime.SelectModelProfileAsync("@second/model-alpha", deadline.Token);
            await using var lease = await runtime.AcquireAsync(deadline.Token);
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
            var initialization = Task.Run(
                () => InitializeRuntimeAsync(host.Services, deadline.Token),
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
            var runtime = await initialization.WaitAsync(deadline.Token);
            await runtime.WaitForReloadCompletionAsync(0, deadline.Token);

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

    [Theory(Timeout = 30_000)]
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

    [Fact(Timeout = 30_000)]
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
            var runtime = await InitializeRuntimeAsync(
                host.Services,
                TestContext.Current.CancellationToken);
            await using var lease = await runtime.AcquireAsync(TestContext.Current.CancellationToken);
            lease.Profile.Options.MaxHistoryBytes.Should().Be(246_912);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 30_000)]
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
                var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
                await using var lease = await runtime.AcquireAsync(deadline.Token);
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
            await using (var lease = await runtime.AcquireAsync(deadline.Token))
            {
                lease.Profile.Options.MaxHistoryBytes.Should().Be(1_000);
            }

            await runtime.WaitForReloadCompletionAsync(0, deadline.Token);

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration(connectionFile, "Fake", "test-model", maxHistoryCharacters: 600),
                deadline.Token);
            runtime.Version.Should().Be(2);
            runtime.GetStatus().LastReload.Should().Match<MaieuticsConfigurationReloadInfo>(reload =>
                reload.Attempt == runtime.CompletedReloadAttempt &&
                reload.Outcome == MaieuticsConfigurationReloadOutcome.Applied &&
                reload.ActiveVersion == 2);
            await using (var lease = await runtime.AcquireAsync(deadline.Token))
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

            await using (var lease = await runtime.AcquireAsync(deadline.Token))
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
            await using (var lease = await runtime.AcquireAsync(deadline.Token))
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
            var firstLease = await runtime.AcquireAsync(deadline.Token);
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
            await using (var secondLease = await runtime.AcquireAsync(deadline.Token))
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
            runtime.GetStatus().LastReload.Outcome.Should().Be(MaieuticsConfigurationReloadOutcome.Rejected);
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
            runtime.GetStatus().LastReload.Outcome.Should().Be(MaieuticsConfigurationReloadOutcome.Applied);
            (await AcquireClientAsync(runtime)).Model.Should().Be("three");

            acceptedVersion = runtime.Version;
            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration(connectionFile, "Fake", "fail"),
                deadline.Token);
            runtime.Version.Should().Be(acceptedVersion);
            runtime.GetStatus().LastReload.Outcome.Should().Be(MaieuticsConfigurationReloadOutcome.Rejected);
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
            var initialSelection = runtime.GetModelProfileSelection();
            initialSelection.DefaultProfileId.Should().Be("one");
            initialSelection.SelectedProfileId.Should().Be("one");
            initialSelection.HasSessionOverride.Should().BeFalse();

            var firstLease = await runtime.AcquireAsync(deadline.Token);
            var firstClient = firstLease.Profile.ChatClient.Should().BeOfType<TrackingChatClient>().Subject;
            firstLease.Profile.ModelIdentity.Should().Be(new AgentModelIdentity(
                new AgentModelProfileId("one"), "Fake", "model-one"));

            await runtime.SelectModelProfileAsync("TWO", deadline.Token);
            var secondLease = await runtime.AcquireAsync(deadline.Token);
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

            await using (var reusedLease = await runtime.AcquireAsync(deadline.Token))
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

    [Fact(Timeout = 30_000)]
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

    [Fact(Timeout = 30_000)]
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

    [Fact(Timeout = 60_000)]
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
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
        await using var lease = await runtime.AcquireAsync(TestContext.Current.CancellationToken);
        return lease.Profile.ChatClient.Should().BeOfType<TrackingChatClient>().Subject;
    }

    private static async Task<MaieuticsRuntimeConfiguration> InitializeRuntimeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var runtime = services.GetRequiredService<MaieuticsRuntimeConfiguration>();
        var pluginHosts = services.GetRequiredService<PluginHostManager>();
        await pluginHosts.StartAsync(cancellationToken);
        await runtime.InitializeAsync(pluginHosts, cancellationToken);
        return runtime;
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
        await runtime.WaitForReloadCompletionAsync(previousAttempt, cancellationToken);
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

    [Fact(Timeout = 40_000)]
    public async Task AcquiredProfileMergesConfiguredEndpointCapabilities()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-endpoint-capabilities-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        var document = new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["DefaultProfile"] = "gpt",
                ["Sources"] = new JsonObject
                {
                    ["fake"] = new JsonObject
                    {
                        ["Provider"] = "Fake",
                        ["Endpoint"] = "https://api.example.com/v1"
                    }
                },
                ["Profiles"] = new JsonObject
                {
                    ["gpt"] = new JsonObject
                    {
                        ["Source"] = "fake",
                        ["Model"] = "test-model"
                    }
                },
                ["Endpoints"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Url"] = "https://api.example.com/v1/",
                        ["Capabilities"] = new JsonArray("WebSearch", "Responses.FileSearch")
                    }
                },
                ["Jupyter"] = new JsonObject
                {
                    ["ConnectionFile"] = connectionFile
                }
            }
        };
        await File.WriteAllTextAsync(configurationFile, document.ToJsonString(), deadline.Token);

        var factory = new EndpointAwareChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        await using var host = builder.Build();
        var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);

        var status = runtime.GetStatus();
        var capabilityInfo = status.CapabilityProfiles.Should().ContainSingle().Subject;
        capabilityInfo.SourceId.Should().Be("fake");
        capabilityInfo.ModelId.Should().Be("test-model");
        capabilityInfo.Matched.Should().BeTrue();
        capabilityInfo.KnownVendor.Should().BeFalse();
        capabilityInfo.HostedCapabilities.Should().Equal(["FileSearch", "WebSearch"]);
        capabilityInfo.Capabilities.Should().HaveFlag(AgentModelCapabilities.StreamingText);
        capabilityInfo.Capabilities.Should().HaveFlag(AgentModelCapabilities.FunctionCalling);

        await using var lease = await runtime.AcquireAsync(deadline.Token);
        lease.Profile.Capabilities.Should().HaveFlag(AgentModelCapabilities.StreamingText);
        lease.Profile.Capabilities.Should().HaveFlag(AgentModelCapabilities.FunctionCalling);
        lease.Profile.HostedCapabilities.Should().Equal(["FileSearch", "WebSearch"]);
    }

    [Fact(Timeout = 40_000)]
    public async Task UnmatchedEndpointKeepsOnlyDeclaredSourceCapabilities()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-endpoint-baseline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        var document = new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["DefaultProfile"] = "gpt",
                ["Sources"] = new JsonObject
                {
                    ["fake"] = new JsonObject
                    {
                        ["Provider"] = "Fake",
                        ["Endpoint"] = "https://api.example.com/v1?tenant=one"
                    }
                },
                ["Profiles"] = new JsonObject
                {
                    ["gpt"] = new JsonObject
                    {
                        ["Source"] = "fake",
                        ["Model"] = "test-model"
                    }
                },
                ["Endpoints"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Url"] = "https://api.example.com/v1",
                        ["Capabilities"] = new JsonArray("WebSearch")
                    }
                },
                ["Jupyter"] = new JsonObject
                {
                    ["ConnectionFile"] = connectionFile
                }
            }
        };
        await File.WriteAllTextAsync(configurationFile, document.ToJsonString(), deadline.Token);

        var factory = new EndpointAwareChatClientFactory();
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        await using var host = builder.Build();
        var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);

        var status = runtime.GetStatus();
        var capabilityInfo = status.CapabilityProfiles.Should().ContainSingle().Subject;
        capabilityInfo.SourceId.Should().Be("fake");
        capabilityInfo.ModelId.Should().Be("test-model");
        capabilityInfo.Matched.Should().BeFalse();
        capabilityInfo.KnownVendor.Should().BeFalse();
        capabilityInfo.HostedCapabilities.Should().BeEmpty();
        capabilityInfo.Capabilities.Should().HaveFlag(AgentModelCapabilities.StreamingText);
        capabilityInfo.Capabilities.Should().HaveFlag(AgentModelCapabilities.FunctionCalling);

        await using var lease = await runtime.AcquireAsync(deadline.Token);
        lease.Profile.Capabilities.Should().HaveFlag(AgentModelCapabilities.StreamingText);
        lease.Profile.Capabilities.Should().HaveFlag(AgentModelCapabilities.FunctionCalling);
        lease.Profile.HostedCapabilities.Should().BeEmpty();
    }

    [Fact(Timeout = 40_000)]
    public async Task KnownVendorEndpointGrantsAutomaticHostedCapabilities()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-known-vendor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        var document = new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["DefaultProfile"] = "gpt",
                ["Sources"] = new JsonObject
                {
                    ["fake"] = new JsonObject
                    {
                        ["Provider"] = "Fake",
                        ["Endpoint"] = "https://api.openai.com/v1"
                    }
                },
                ["Profiles"] = new JsonObject
                {
                    ["gpt"] = new JsonObject
                    {
                        ["Source"] = "fake",
                        ["Model"] = "test-model"
                    }
                },
                ["Jupyter"] = new JsonObject
                {
                    ["ConnectionFile"] = connectionFile
                }
            }
        };
        await File.WriteAllTextAsync(configurationFile, document.ToJsonString(), deadline.Token);

        var factory = new EndpointAwareChatClientFactory { FormatCapabilities = ResponsesFormatCapabilities };
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        await using var host = builder.Build();
        var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);

        var status = runtime.GetStatus();
        var capabilityInfo = status.CapabilityProfiles.Should().ContainSingle().Subject;
        capabilityInfo.KnownVendor.Should().BeTrue();
        capabilityInfo.Matched.Should().BeFalse();
        capabilityInfo.HostedCapabilities.Should().Equal(ResponsesFormatCapabilities);

        await using var lease = await runtime.AcquireAsync(deadline.Token);
        lease.Profile.HostedCapabilities.Should().Equal(ResponsesFormatCapabilities);
    }

    [Fact(Timeout = 40_000)]
    public async Task VendorsCatalogNarrowsCapabilitiesByModel()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-vendor-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        var document = new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["DefaultProfile"] = "gpt",
                ["Sources"] = new JsonObject
                {
                    ["fake"] = new JsonObject
                    {
                        ["Provider"] = "Fake",
                        ["Endpoint"] = "https://opencode.ai/v1",
                        ["Vendor"] = "opencode"
                    }
                },
                ["Profiles"] = new JsonObject
                {
                    ["gpt"] = new JsonObject
                    {
                        ["Source"] = "fake",
                        ["Model"] = "gpt-5"
                    }
                },
                ["Vendors"] = new JsonObject
                {
                    ["opencode"] = new JsonObject
                    {
                        ["Endpoints"] = new JsonArray("https://opencode.ai/v1"),
                        ["Capabilities"] = new JsonArray("WebSearch", "Shell"),
                        ["Models"] = new JsonObject
                        {
                            ["gpt-5"] = new JsonObject
                            {
                                ["Capabilities"] = new JsonArray("WebSearch")
                            }
                        }
                    }
                },
                ["Jupyter"] = new JsonObject
                {
                    ["ConnectionFile"] = connectionFile
                }
            }
        };
        await File.WriteAllTextAsync(configurationFile, document.ToJsonString(), deadline.Token);

        var factory = new EndpointAwareChatClientFactory
        {
            FormatCapabilities = ["WebSearch", "Shell"]
        };
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        await using var host = builder.Build();
        var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);

        await using var lease = await runtime.AcquireAsync(deadline.Token);
        lease.Profile.HostedCapabilities.Should().Equal(["WebSearch"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task AutomaticProfileRetiresWhenVendorCatalogChangesOnReload()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-auto-profile-vendor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateVendorCatalogConfiguration(connectionFile, "WebSearch"),
            deadline.Token);

        var factory = new DiscoveryChatClientFactory { FormatCapabilities = ResponsesFormatCapabilities };
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();

        try
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
            await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);
            await runtime.SelectModelProfileAsync("@vendor/model-alpha", deadline.Token);
            var lease = await runtime.AcquireAsync(deadline.Token);
            var client = lease.Profile.ChatClient.Should().BeOfType<TrackingChatClient>().Subject;
            lease.Profile.HostedCapabilities.Should().Equal(["WebSearch"]);

            // The active automatic override is reported alongside configured profiles.
            runtime.GetStatus().CapabilityProfiles.Should().ContainSingle(profile =>
                profile.SourceId == "vendor" &&
                profile.ModelId == "model-alpha" &&
                profile.KnownVendor &&
                profile.HostedCapabilities.SequenceEqual(new[] { "WebSearch" }));

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateVendorCatalogConfiguration(connectionFile, "Shell"),
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
    public async Task AutomaticProfileRetiresWhenVendorModelCatalogChangesOnReload()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-auto-profile-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionFile = Path.Combine(root, "connection.json");
        await JupyterConnectionInfo.CreateLocalTcp().WriteFileAsync(connectionFile, deadline.Token);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateVendorModelCatalogConfiguration(connectionFile, "WebSearch"),
            deadline.Token);

        var factory = new DiscoveryChatClientFactory { FormatCapabilities = ResponsesFormatCapabilities };
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();

        try
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
            await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);
            await runtime.SelectModelProfileAsync("@vendor/model-alpha", deadline.Token);
            var lease = await runtime.AcquireAsync(deadline.Token);
            var client = lease.Profile.ChatClient.Should().BeOfType<TrackingChatClient>().Subject;
            lease.Profile.HostedCapabilities.Should().Equal(["WebSearch"]);

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateVendorModelCatalogConfiguration(connectionFile, "Shell"),
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

    private static string CreateVendorModelCatalogConfiguration(string connectionFile, string modelCapability)
    {
        return new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["Sources"] = new JsonObject
                {
                    ["vendor"] = new JsonObject
                    {
                        ["Provider"] = "Fake",
                        ["Revision"] = "one",
                        ["Endpoint"] = "https://opencode.ai/v1",
                        ["Vendor"] = "opencode"
                    }
                },
                ["Vendors"] = new JsonObject
                {
                    ["opencode"] = new JsonObject
                    {
                        ["Endpoints"] = new JsonArray("https://opencode.ai/v1"),
                        ["Models"] = new JsonObject
                        {
                            ["model-alpha"] = new JsonObject
                            {
                                ["Capabilities"] = new JsonArray(modelCapability)
                            }
                        }
                    }
                },
                ["Jupyter"] = new JsonObject
                {
                    ["ConnectionFile"] = connectionFile
                }
            }
        }.ToJsonString();
    }

    private static string CreateVendorCatalogConfiguration(string connectionFile, string vendorCapability)
    {
        return new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["Sources"] = new JsonObject
                {
                    ["vendor"] = new JsonObject
                    {
                        ["Provider"] = "Fake",
                        ["Revision"] = "one",
                        ["Endpoint"] = "https://opencode.ai/v1",
                        ["Vendor"] = "opencode"
                    }
                },
                ["Vendors"] = new JsonObject
                {
                    ["opencode"] = new JsonObject
                    {
                        ["Endpoints"] = new JsonArray("https://opencode.ai/v1"),
                        ["Capabilities"] = new JsonArray(vendorCapability)
                    }
                },
                ["Jupyter"] = new JsonObject
                {
                    ["ConnectionFile"] = connectionFile
                }
            }
        }.ToJsonString();
    }

    private static readonly string[] ResponsesFormatCapabilities =
    [
        "ApplyPatch",
        "CodeInterpreter",
        "ComputerUse",
        "FileSearch",
        "ImageGeneration",
        "Mcp",
        "WebSearch"
    ];

    private static string CreateSourceOnlyConfiguration(
        string connectionFile,
        JsonArray endpoints,
        params NamedSource[] sources)
    {
        return CreateSourceOnlyConfigurationCore(connectionFile, endpoints, sources);
    }

    private static string CreateSourceOnlyConfiguration(
        string connectionFile,
        params NamedSource[] sources)
    {
        return CreateSourceOnlyConfigurationCore(connectionFile, null, sources);
    }

    private static string CreateSourceOnlyConfigurationCore(
        string connectionFile,
        JsonArray? endpoints,
        NamedSource[] sources)
    {
        var sourceNodes = new JsonObject();
        foreach (var source in sources)
        {
            var node = new JsonObject
            {
                ["Provider"] = "Fake",
                ["Revision"] = source.Revision
            };
            if (source.Endpoint is { } endpoint) node["Endpoint"] = endpoint;
            sourceNodes[source.Id] = node;
        }

        var maieutics = new JsonObject
        {
            ["Sources"] = sourceNodes,
            ["Jupyter"] = new JsonObject
            {
                ["ConnectionFile"] = connectionFile
            }
        };
        if (endpoints is not null) maieutics["Endpoints"] = endpoints;

        return new JsonObject { ["Maieutics"] = maieutics }.ToJsonString();
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

    [Fact(Timeout = 40_000)]
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
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

    [Fact(Timeout = 40_000)]
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);

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

    [Fact(Timeout = 40_000)]
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
            var groups = await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);
            groups.Should().BeEmpty();
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 40_000)]
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
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
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
        public Action<string>? ClientCreated { get; init; }

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
            ClientCreated?.Invoke(model);
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

            public Uri? EndpointUri => null;

            public string? Vendor => null;

            public IReadOnlyList<string> FormatCapabilities => [];

            public IChatClient Create(string model)
            {
                return factory.Create(model);
            }
        }
    }

    private sealed class EndpointAwareChatClientFactory : IConfiguredChatClientFactory
    {
        public string ProviderName => "Fake";

        public IReadOnlyList<string> FormatCapabilities { get; set; } = [];

        public IConfiguredChatClientSource BindSource(string sourceId, IConfigurationSection configuration)
        {
            var endpoint = configuration["Endpoint"] is { } raw ? new Uri(raw) : null;
            return new EndpointAwareSource(endpoint, FormatCapabilities);
        }

        private sealed class EndpointAwareSource(
            Uri? endpoint,
            IReadOnlyList<string> formatCapabilities) : IConfiguredChatClientSource
        {
            public string ProviderName => "Fake";

            public object ClientGenerationKey => "endpoint-aware";

            public AgentModelCapabilities Capabilities =>
                AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

            public Uri? EndpointUri => endpoint;

            public string? Vendor => null;

            public IReadOnlyList<string> FormatCapabilities => formatCapabilities;

            public IChatClient Create(string model)
            {
                return new TrackingChatClient(model);
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

            public Uri? EndpointUri => null;

            public string? Vendor => null;

            public IReadOnlyList<string> FormatCapabilities => [];

            public IChatClient Create(string model)
            {
                return factory.Create(model);
            }
        }
    }

    private sealed class TrackingChatClient(string model) : IChatClient
    {
        private int disposalCount;

        public string Model { get; } = model;

        public bool Disposed => DisposalCount != 0;

        public int DisposalCount => Volatile.Read(ref disposalCount);

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
            Interlocked.Increment(ref disposalCount);
        }
    }

    private sealed record NamedProfile(string Id, string SourceId, string Model, string SourceRevision);

    private sealed record NamedSource(string Id, string Revision, string? Endpoint = null);

    private sealed class DiscoveryChatClientFactory : IConfiguredChatClientFactory
    {
        public Action<string>? ClientCreated { get; set; }

        public int DiscoveryCount { get; private set; }

        public List<TrackingChatClient> Clients { get; } = [];

        public IReadOnlyList<string> FormatCapabilities { get; set; } = [];

        public string ProviderName => "Fake";

        public IConfiguredChatClientSource BindSource(string sourceId, IConfigurationSection configuration)
        {
            var endpoint = configuration["Endpoint"] is { } raw ? new Uri(raw) : null;
            return new DiscoverySource(
                this,
                sourceId,
                configuration["Revision"] ?? sourceId,
                endpoint,
                configuration["Vendor"],
                FormatCapabilities);
        }

        private sealed class DiscoverySource(
            DiscoveryChatClientFactory factory,
            string sourceId,
            string revision,
            Uri? endpoint,
            string? vendor,
            IReadOnlyList<string> formatCapabilities) : IConfiguredChatClientSource, IModelDiscoverySource
        {
            public string ProviderName => "Fake";

            public object ClientGenerationKey => (sourceId, revision, endpoint);

            public AgentModelCapabilities Capabilities =>
                AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

            public Uri? EndpointUri => endpoint;

            public string? Vendor => vendor;

            public IReadOnlyList<string> FormatCapabilities => formatCapabilities;

            public IChatClient Create(string model)
            {
                var client = new TrackingChatClient(model);
                factory.Clients.Add(client);
                factory.ClientCreated?.Invoke(model);
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

    private sealed class CoordinatedAutomaticProfileChatClientFactory : IConfiguredChatClientFactory
    {
        public TaskCompletionSource AutomaticCreateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<TrackingChatClient> Clients { get; } = [];

        public TaskCompletionSource ReleaseAutomaticCreate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderName => "Fake";

        public IConfiguredChatClientSource BindSource(string sourceId, IConfigurationSection configuration)
        {
            return new CoordinatedAutomaticProfileSource(
                this,
                sourceId,
                configuration["Revision"] ?? sourceId);
        }

        private IChatClient Create(string model)
        {
            if (model == "model-alpha")
            {
                AutomaticCreateStarted.TrySetResult();
                ReleaseAutomaticCreate.Task.GetAwaiter().GetResult();
            }

            var client = new TrackingChatClient(model);
            Clients.Add(client);
            return client;
        }

        private sealed class CoordinatedAutomaticProfileSource(
            CoordinatedAutomaticProfileChatClientFactory factory,
            string sourceId,
            string revision) : IConfiguredChatClientSource, IModelDiscoverySource
        {
            public string ProviderName => "Fake";

            public object ClientGenerationKey => (sourceId, revision);

            public AgentModelCapabilities Capabilities =>
                AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

            public Uri? EndpointUri => null;

            public string? Vendor => null;

            public IReadOnlyList<string> FormatCapabilities => [];

            public IChatClient Create(string model)
            {
                return factory.Create(model);
            }

            public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<IReadOnlyList<AgentModelDescriptor>>([
                    new AgentModelDescriptor("model-alpha", "Fake")
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

            public Uri? EndpointUri => null;

            public string? Vendor => null;

            public IReadOnlyList<string> FormatCapabilities => [];

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

            public Uri? EndpointUri => null;

            public string? Vendor => null;

            public IReadOnlyList<string> FormatCapabilities => [];

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
