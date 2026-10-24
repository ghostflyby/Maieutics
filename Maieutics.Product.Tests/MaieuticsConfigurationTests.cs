using System.Text.Json.Nodes;
using Maieutics.Agent;
using Maieutics.Configuration;
using Maieutics.Plugins;
using Maieutics.Providers;
using Maieutics.Providers.Anthropic;
using Maieutics.Providers.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
public sealed class MaieuticsConfigurationTests
{
    /// <summary>Deletes a temporary directory, retrying with an asynchronous backoff: on
    /// Windows a config-file watcher or an exiting MCP stdio child can hold a file for a
    /// short while after the test body completes. The lock holder has no completion
    /// signal, so the retry waits by delay — asynchronously, never blocking a pool
    /// thread. Delegates to the shared cleanup policy (same budget and retry shape).</summary>
    internal static Task DeleteDirectoryWithRetryAsync(string path, CancellationToken cancellationToken) =>
        TempArea.DeleteAsync(path, cancellationToken);

    [Fact(Timeout = 30_000)]
    public async Task ConfigurationPathResolutionUsesExplicitEnvironmentPortableThenUserPath()
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
            Assert.Equal(Path.Combine(currentDirectory, "explicit.json"), explicitFile.Path);
            Assert.True(explicitFile.Required);
            Assert.Equal("command line", explicitFile.Source);

            var environmentFile = MaieuticsConfigurationFile.Resolve(
                [],
                name => name == "MAIEUTICS_CONFIG" ? "environment.json" : null,
                applicationBase,
                currentDirectory,
                applicationData);
            Assert.Equal(Path.Combine(currentDirectory, "environment.json"), environmentFile.Path);
            Assert.True(environmentFile.Required);

            var portablePath = Path.Combine(applicationBase, "maieutics.json");
            File.WriteAllText(portablePath, "{}");
            var portableFile = MaieuticsConfigurationFile.Resolve(
                [],
                _ => null,
                applicationBase,
                currentDirectory,
                applicationData);
            Assert.Equal(portablePath, portableFile.Path);
            Assert.Equal("portable", portableFile.Source);

            File.Delete(portablePath);
            File.WriteAllText(Path.Combine(currentDirectory, "maieutics.json"), "{}");
            var userFile = MaieuticsConfigurationFile.Resolve(
                [],
                _ => null,
                applicationBase,
                currentDirectory,
                applicationData);
            Assert.Equal(Path.Combine(applicationData, "Maieutics", "maieutics.json"), userFile.Path);
            Assert.False(userFile.Required);
            Assert.Equal("user", userFile.Source);

            var noFile = MaieuticsConfigurationFile.Resolve(
                [],
                _ => null,
                applicationBase,
                currentDirectory,
                string.Empty);
            Assert.Null(noFile.Path);
            Assert.False(noFile.Required);
            Assert.Equal("none", noFile.Source);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task HostOwnsAndDisposesConfigurationFileProvider()
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

            Assert.False(provider.IsDisposed);
            ((IDisposable)host).Dispose();
            Assert.True(provider.IsDisposed);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task PollingChangeTokenFiresWhenContentChangesEvenIfMetadataStampIsRestored()
    {
        // Deterministic regression for the Windows-only flake in
        // RuntimeInitializationReconcilesConfigurationChangesDuringProviderCreation.
        //
        // The poller used to capture only the metadata stamp (exists, length, last-write ticks)
        // when the change token was constructed and fired only when that stamp differed. On
        // Windows a rewrite can keep the metadata stamp identical (NTFS coalesces rapid
        // same-length writes, or a scheduling race lands the write before the token captured its
        // initial stamp), so the token never fired and the test timed out as TaskCanceledException.
        //
        // The fix adds a content hash to the stamp: the token fires when either the metadata or
        // the content changes. This test injects the exact race deterministically -- rewrite the
        // file, then roll the last-write time back to the pre-write value -- and asserts the token
        // still fires. Synchronization uses the change callback (a completion notification), not
        // a sleep-and-poll loop; the bounded WaitAsync only guards against a poller that never
        // runs at all.
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-config-stamp-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        try
        {
            await File.WriteAllTextAsync(configurationFile, "one", TestContext.Current.CancellationToken);
            var initialWriteTime = new FileInfo(configurationFile).LastWriteTimeUtc;

            using var provider = MaieuticsConfigurationFileProvider.Create(configurationFile);
            var token = provider.Provider.Watch("maieutics.json");

            // Inject the race: the write completes and the metadata stamp is restored so a
            // metadata-only poller cannot distinguish "before" from "after". The content changes.
            await File.WriteAllTextAsync(configurationFile, "two", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(configurationFile, initialWriteTime);

            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.RegisterChangeCallback(
                static state => ((TaskCompletionSource?)state)?.TrySetResult(),
                changed);

            // The content hash must make the token fire within several poll intervals (250 ms
            // each). The bounded wait is a completion notification with a fallback timeout, not a
            // polling loop.
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 40_000)]
    public async Task RuntimeInitializationAllowsNoModelOrProviderConfiguration()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-empty-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            new JsonObject
            {
                ["Maieutics"] = new JsonObject
                {
                    // An unknown section must be tolerated rather than rejected; the point of the
                    // test is that a configuration with no model or provider still initializes.
                    ["UnknownSection"] = new JsonObject
                    {
                    }
                }
            }.ToJsonString(),
            deadline.Token);

        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            await using var host = builder.Build();
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);

            Assert.Empty(runtime.GetModelProfileSelection().Profiles);
            Assert.Empty(await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token));
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 40_000)]
    public async Task RuntimeInitializationCancellationRetiresCreatedProfileExactlyOnce()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-init-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration("Fake", "test-model"),
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

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await runtime.InitializeAsync(pluginHosts, cancellation.Token); });
        }
        finally
        {
            await host.DisposeAsync();
            Assert.Equal(1, Assert.Single(factory.Clients).DisposalCount);
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 40_000)]
    public async Task RuntimeInitializationFailureRetiresEarlierProfilesExactlyOnce()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-init-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateNamedConfiguration(
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

            var assertion15 = await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => { await runtime.InitializeAsync(pluginHosts, deadline.Token); });
            Assert.Matches("(?is)^.*provider\\ creation\\ failure.*$", assertion15.Message);
        }
        finally
        {
            await host.DisposeAsync();
            Assert.Equal(1, Assert.Single(factory.Clients).DisposalCount);
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 40_000)]
    public async Task RuntimeInitializationRetainsSourcesWithoutProfilesForDiscovery()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-source-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(new NamedSource("vendor", "one")),
            deadline.Token);

        var factory = new DiscoveryChatClientFactory();
        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            builder.Services.RemoveAll<IConfiguredChatClientFactory>();
            builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
            await using var host = builder.Build();
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);

            Assert.Empty(runtime.GetModelProfileSelection().Profiles);
            Assert.Equal(["vendor"], runtime.GetModelSourceIds());
            var groups = await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);
            Assert.Equal(2, Assert.Single(groups).Models.Count());

            Assert.Equal(["@vendor/model-alpha", "@vendor/model-beta"], runtime.GetCachedAutomaticModelProfiles().Select(static profile => profile.Id));
            await runtime.SelectModelProfileAsync("@vendor/model-alpha", deadline.Token);
            var selection = runtime.GetModelProfileSelection();
            Assert.Equal("@vendor/model-alpha", selection.SelectedProfileId);
            Assert.True(selection.HasSessionOverride);
            Assert.True(Assert.Single(selection.Profiles).IsAutomatic);

            await using var lease = await runtime.AcquireAsync(deadline.Token);
            var client = Assert.IsType<TrackingChatClient>(lease.Profile.ChatClient);
            Assert.Equal("model-alpha", client.Model);
            Assert.NotNull(lease.Profile.ModelIdentity);
            Assert.Equal("Fake", lease.Profile.ModelIdentity.Provider);
            Assert.Equal("model-alpha", lease.Profile.ModelIdentity.Model);

            await runtime.SelectModelProfileAsync("@vendor/model-alpha", deadline.Token);
            Assert.Single(factory.Clients);
            runtime.ResetModelProfile();
            Assert.Empty(runtime.GetModelProfileSelection().Profiles);
            Assert.False(client.Disposed);
            await lease.DisposeAsync();
            Assert.True(client.Disposed);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 40_000)]
    public async Task AutomaticProfileSelectionCancellationRetiresCreatedClientExactlyOnce()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-auto-profile-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(new NamedSource("vendor", "one")),
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

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await runtime
                    .SelectModelProfileAsync("@vendor/model-alpha", cancellation.Token)
                    .AsTask();
            });

            Assert.False(runtime.GetModelProfileSelection().HasSessionOverride);
        }
        finally
        {
            await host.DisposeAsync();
            Assert.Equal(1, Assert.Single(factory.Clients).DisposalCount);
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AutomaticProfileRetiresAfterItsSourceGenerationChanges()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-auto-profile-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(new NamedSource("vendor", "one")),
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
            await using var lease = await runtime.AcquireAsync(deadline.Token);
            var client = Assert.IsType<TrackingChatClient>(lease.Profile.ChatClient);

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateSourceOnlyConfiguration(new NamedSource("vendor", "two")),
                deadline.Token);

            Assert.Empty(runtime.GetModelProfileSelection().Profiles);
            Assert.False(client.Disposed);
            await lease.DisposeAsync();
            Assert.True(client.Disposed);
        }
        finally
        {
            await host.DisposeAsync();
            Assert.All(factory.Clients, static client => Assert.True(client.Disposed));
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AutomaticProfileRetiresWhenHostedCapabilitiesChangeOnReload()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-auto-profile-endpoints-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        var webSearch = new JsonArray(new JsonObject
        {
            ["Url"] = "https://api.example.com/v1",
            ["Capabilities"] = new JsonArray("WebSearch")
        });
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(
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
            await using var lease = await runtime.AcquireAsync(deadline.Token);
            var client = Assert.IsType<TrackingChatClient>(lease.Profile.ChatClient);
            Assert.Equal(["WebSearch"], lease.Profile.HostedCapabilities);

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
                    shell,
                    new NamedSource("vendor", "one", "https://api.example.com/v1")),
                deadline.Token);

            Assert.Empty(runtime.GetModelProfileSelection().Profiles);
            Assert.False(client.Disposed);
            await lease.DisposeAsync();
            Assert.True(client.Disposed);
        }
        finally
        {
            await host.DisposeAsync();
            Assert.All(factory.Clients, static client => Assert.True(client.Disposed));
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AutomaticProfileSelectionFailureRetiresCreatedClientExactlyOnce()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-auto-profile-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(new NamedSource("vendor", "one")),
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
                CreateSourceOnlyConfiguration(new NamedSource("vendor", "two")),
                deadline.Token);
            factory.ReleaseAutomaticCreate.TrySetResult();

            var assertion16 = await Assert.ThrowsAnyAsync<ArgumentException>(async () => { await selection; });
            Assert.Matches("(?is)^.*changed\\ while\\ the\\ automatic\\ profile\\ was\\ selected.*$", assertion16.Message);
            Assert.False(runtime.GetModelProfileSelection().HasSessionOverride);
        }
        finally
        {
            factory.ReleaseAutomaticCreate.TrySetResult();
            await host.DisposeAsync();
            Assert.Equal(1, Assert.Single(factory.Clients).DisposalCount);
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task CanceledPluginReadinessWaitRollsBackTheProfileGenerationLease()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-profile-readiness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration("Fake", "test-model"),
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
            Assert.False(acquisition.IsCompleted);
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await acquisition; });
        }
        finally
        {
            await host.DisposeAsync();
            Assert.True(Assert.Single(factory.Clients).Disposed);
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task StaleDiscoveryCannotReplaceCacheAfterSourceReload()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-stale-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(new NamedSource("vendor", "one")),
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
                CreateSourceOnlyConfiguration(new NamedSource("vendor", "two")),
                deadline.Token);

            var current = await runtime.GetDiscoveredModelsAsync(
                refresh: true,
                cancellationToken: deadline.Token);
            Assert.Equal("model-two", Assert.Single(Assert.Single(current).Models).Id);

            factory.ReleaseFirstDiscovery.TrySetResult();
            var stale = await staleDiscovery.WaitAsync(deadline.Token);
            Assert.Equal("model-one", Assert.Single(Assert.Single(stale).Models).Id);
            Assert.Equal("@vendor/model-two", Assert.Single(runtime.GetCachedAutomaticModelProfiles()).Id);
        }
        finally
        {
            factory.ReleaseFirstDiscovery.TrySetResult();
            await host.DisposeAsync();
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task CallerCancellationIsNotConvertedIntoOrCachedAsDiscoveryFailure()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-cancelled-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(new NamedSource("vendor", "one")),
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

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await cancelledDiscovery; });
            Assert.Equal(1, factory.DiscoveryCount);

            var recovered = await runtime.GetDiscoveredModelsAsync(
                cancellationToken: deadline.Token);
            Assert.Equal("model-one", Assert.Single(Assert.Single(recovered).Models).Id);
            Assert.Equal(2, factory.DiscoveryCount);
        }
        finally
        {
            factory.ReleaseFirstDiscovery.TrySetResult();
            await host.DisposeAsync();
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 40_000)]
    public async Task AutomaticProfileRequiresQualifiedSelectorForDuplicateModelIds()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-auto-profile-ambiguity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateSourceOnlyConfiguration(
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

            var assertion17 = await Assert.ThrowsAnyAsync<ArgumentException>(async () => { await runtime.SelectModelProfileAsync("model-alpha", deadline.Token).AsTask(); });
            Assert.Matches("(?is)^.*'@first/model-alpha'.*'@second/model-alpha'.*$", assertion17.Message);

            await runtime.SelectModelProfileAsync("@second/model-alpha", deadline.Token);
            await using var lease = await runtime.AcquireAsync(deadline.Token);
            Assert.Equal("model-alpha", lease.Profile.ModelIdentity?.Model);
        }
        finally
        {
            await host.DisposeAsync();
            Assert.All(factory.Clients, static client => Assert.True(client.Disposed));
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task RuntimeInitializationReconcilesConfigurationChangesDuringProviderCreation()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-config-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration("Fake", "one"),
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
                CreateConfiguration("Fake", "two"),
                deadline.Token);
            await changed.Task.WaitAsync(deadline.Token);

            factory.ReleaseCreation.TrySetResult();
            var runtime = await initialization.WaitAsync(deadline.Token);
            await runtime.WaitForReloadCompletionAsync(0, deadline.Token);

            Assert.Equal("two", (await AcquireClientAsync(runtime)).Model);
            Assert.Equal(2, runtime.Version);
        }
        finally
        {
            factory.ReleaseCreation.TrySetResult();
            await host.DisposeAsync();
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ReloadPublishedWhilePreviousRequestIsInFlightIsAppliedByLaterAttempt()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-config-reload-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration("Fake", "one"),
            deadline.Token);

        var factory = new BlockingChatClientFactory("two");
        var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
        builder.Services.RemoveAll<IConfiguredChatClientFactory>();
        builder.Services.AddSingleton<IConfiguredChatClientFactory>(factory);
        var host = builder.Build();

        try
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            var runtime = await InitializeRuntimeAsync(host.Services, deadline.Token);
            await runtime.WaitForReloadCompletionAsync(0, deadline.Token);

            await WriteAndWaitForConfigurationSignalAsync(
                configuration,
                configurationFile,
                CreateConfiguration("Fake", "two"),
                deadline.Token);
            await factory.CreateStarted.Task.WaitAsync(deadline.Token);

            var previousRequest = runtime.ReloadRequest;
            await WriteAndWaitForConfigurationSignalAsync(
                configuration,
                configurationFile,
                CreateConfiguration("Fake", "three"),
                deadline.Token);

            factory.ReleaseCreation.TrySetResult();
            await runtime.WaitForReloadCompletionAsync(previousRequest, deadline.Token);

            await using var lease = await runtime.AcquireAsync(deadline.Token);
            Assert.Equal("three", Assert.IsType<TrackingChatClient>(lease.Profile.ChatClient).Model);
            Assert.Equal(3, runtime.Version);
        }
        finally
        {
            factory.ReleaseCreation.TrySetResult();
            await host.DisposeAsync();
            Assert.All(factory.Clients, static client => Assert.True(client.Disposed));
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ConfigurationSourcesUseJsonAliasStandardEnvironmentThenCommandLinePrecedence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-config-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        File.WriteAllText(configurationFile, CreateConfiguration(
            "OpenAI",
            "json-model"));

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

            Assert.Equal("command-model", builder.Configuration["Maieutics:Model:Name"]);
            Assert.Equal(nameof(OpenAiApiFlavor.ChatCompletions), builder.Configuration["Maieutics:Sources:openai:ApiFlavor"]);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task NamedConfigurationAliasesUseStandardEnvironmentAndCommandLinePrecedence()
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

            Assert.Equal("command-profile", builder.Configuration["Maieutics:DefaultProfile"]);
            Assert.Equal("standard-key", builder.Configuration["Maieutics:Sources:anthropic:ApiKey"]);
            Assert.Equal("https://standard.example", builder.Configuration["Maieutics:Sources:anthropic:Endpoint"]);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
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

        Assert.Equal(unchanged.ClientGenerationKey, first.ClientGenerationKey);
        Assert.NotEqual(rotated.ClientGenerationKey, first.ClientGenerationKey);
        Assert.Contains("<redacted>", first.ClientGenerationKey.ToString());
        Assert.DoesNotContain(firstApiKey, first.ClientGenerationKey.ToString());
        Assert.DoesNotContain(rotatedApiKey, rotated.ClientGenerationKey.ToString());
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
            }
        }.ToJsonString(), TestContext.Current.CancellationToken);

        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            await using var host = builder.Build();

            var assertion18 = Assert.ThrowsAny<InvalidOperationException>(() => host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>());
            Assert.Contains(unknownField, assertion18.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task LegacyMaxHistoryCharactersConvertsToMaxHistoryBytes()
    {
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-history-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration(
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
            Assert.Equal(246_912, lease.Profile.Options.MaxHistoryBytes);
        }
        finally
        {
            await host.DisposeAsync();
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task MaxHistoryBytesCannotBeCombinedWithLegacyMaxHistoryCharacters()
    {
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-history-conflict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration(
                "Fake",
                "test-model",
                maxHistoryBytes: 400_000,
                maxHistoryCharacters: 200_000),
            TestContext.Current.CancellationToken);

        try
        {
            var builder = MaieuticsHost.CreateApplicationBuilder(["--config", configurationFile]);
            await using var host = builder.Build();

            var assertion19 = Assert.ThrowsAny<InvalidOperationException>(() => host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>());
            Assert.Matches("(?is)^.*MaxHistoryBytes.*MaxHistoryCharacters.*$", assertion19.Message);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AgentTurnDurationBindsToLeaseAndNegativeValuesAreRejected()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-turn-duration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configurationFile = Path.Combine(root, "maieutics.json");
            await File.WriteAllTextAsync(
                configurationFile,
                CreateConfiguration("Fake", "one", maxTurnDuration: "00:00:45"),
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
                Assert.Equal(TimeSpan.FromSeconds(45), lease.Profile.Options.MaxTurnDuration);
            }
            finally
            {
                await host.DisposeAsync();
            }

            Assert.ThrowsAny<ArgumentOutOfRangeException>(() =>
                new MaieuticsAgentOptions { MaxTurnDuration = TimeSpan.FromSeconds(-1) }.Validate());
            new MaieuticsAgentOptions { MaxTurnDuration = TimeSpan.Zero }.Validate();
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task HistoryLimitCompatibilityParticipatesInHotReloadValidation()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-history-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration("Fake", "test-model", maxHistoryBytes: 1_000),
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
                Assert.Equal(1_000, lease.Profile.Options.MaxHistoryBytes);
            }

            await runtime.WaitForReloadCompletionAsync(0, deadline.Token);

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration("Fake", "test-model", maxHistoryCharacters: 600),
                deadline.Token);
            Assert.Equal(2, runtime.Version);
            Assert.True(((Func<MaieuticsConfigurationReloadInfo, bool>)(reload => reload.Attempt == runtime.CompletedReloadAttempt &&
                                                                                  reload.Outcome == MaieuticsConfigurationReloadOutcome.Applied &&
                                                                                  reload.ActiveVersion == 2))(runtime.GetStatus().LastReload));
            await using (var lease = await runtime.AcquireAsync(deadline.Token))
            {
                Assert.Equal(1_200, lease.Profile.Options.MaxHistoryBytes);
            }

            var acceptedVersion = runtime.Version;
            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration(
                    "Fake",
                    "test-model",
                    maxHistoryBytes: 1_400,
                    maxHistoryCharacters: 700),
                deadline.Token);
            Assert.Equal(acceptedVersion, runtime.Version);

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration("Fake", "test-model", maxHistoryCharacters: int.MaxValue),
                deadline.Token);
            Assert.Equal(acceptedVersion, runtime.Version);

            await using (var lease = await runtime.AcquireAsync(deadline.Token))
            {
                Assert.Equal(1_200, lease.Profile.Options.MaxHistoryBytes);
            }

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration("Fake", "test-model", maxHistoryBytes: 1_400),
                deadline.Token);
            Assert.Equal(acceptedVersion + 1, runtime.Version);
            await using (var lease = await runtime.AcquireAsync(deadline.Token))
            {
                Assert.Equal(1_400, lease.Profile.Options.MaxHistoryBytes);
            }
        }
        finally
        {
            await host.DisposeAsync();
            Assert.All(factory.Clients, static client => Assert.True(client.Disposed));
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task RuntimeConfigurationReloadsValidSnapshotsAndKeepsLastKnownGood()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateConfiguration("Fake", "one", systemPrompt: "first", maxInputCharacters: 100),
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
            await using var firstLease = await runtime.AcquireAsync(deadline.Token);
            var firstClient = Assert.IsType<TrackingChatClient>(firstLease.Profile.ChatClient);
            Assert.Equal("one", firstClient.Model);
            Assert.Equal("first", firstLease.Profile.Options.SystemPrompt);

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration("Fake", "two", systemPrompt: "second", maxInputCharacters: 200),
                deadline.Token);
            Assert.Equal(2, runtime.Version);
            await using (var secondLease = await runtime.AcquireAsync(deadline.Token))
            {
                var secondClient = Assert.IsType<TrackingChatClient>(secondLease.Profile.ChatClient);
                Assert.Equal("two", secondClient.Model);
                Assert.Equal("second", secondLease.Profile.Options.SystemPrompt);
                Assert.Equal(200, secondLease.Profile.Options.MaxInputCharacters);
                Assert.False(firstClient.Disposed);
            }

            await firstLease.DisposeAsync();
            Assert.True(firstClient.Disposed);

            var acceptedVersion = runtime.Version;
            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration("Fake", "invalid", maxInputCharacters: 0),
                deadline.Token);
            Assert.Equal(acceptedVersion, runtime.Version);
            Assert.Equal(MaieuticsConfigurationReloadOutcome.Rejected, runtime.GetStatus().LastReload.Outcome);
            Assert.Equal("two", (await AcquireClientAsync(runtime)).Model);

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                "{",
                deadline.Token);
            Assert.Equal(acceptedVersion, runtime.Version);

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration("Fake", "three", systemPrompt: "repaired"),
                deadline.Token);
            Assert.Equal(acceptedVersion + 1, runtime.Version);
            Assert.Equal(MaieuticsConfigurationReloadOutcome.Applied, runtime.GetStatus().LastReload.Outcome);
            Assert.Equal("three", (await AcquireClientAsync(runtime)).Model);

            acceptedVersion = runtime.Version;
            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateConfiguration("Fake", "fail"),
                deadline.Token);
            Assert.Equal(acceptedVersion, runtime.Version);
            Assert.Equal(MaieuticsConfigurationReloadOutcome.Rejected, runtime.GetStatus().LastReload.Outcome);
            Assert.Equal("three", (await AcquireClientAsync(runtime)).Model);
        }
        finally
        {
            await host.DisposeAsync();
            Assert.All(factory.Clients, static client => Assert.True(client.Disposed));
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task NamedProfileCatalogSwitchesReusesAndAtomicallyReplacesGenerations()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-profile-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateNamedConfiguration(
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
            Assert.Equal("one", initialSelection.DefaultProfileId);
            Assert.Equal("one", initialSelection.SelectedProfileId);
            Assert.False(initialSelection.HasSessionOverride);

            await using var firstLease = await runtime.AcquireAsync(deadline.Token);
            var firstClient = Assert.IsType<TrackingChatClient>(firstLease.Profile.ChatClient);
            Assert.Equal(new AgentModelIdentity(
                new AgentModelProfileId("one"), "Fake", "model-one"), firstLease.Profile.ModelIdentity);

            await runtime.SelectModelProfileAsync("TWO", deadline.Token);
            await using var secondLease = await runtime.AcquireAsync(deadline.Token);
            var secondClient = Assert.IsType<TrackingChatClient>(secondLease.Profile.ChatClient);
            Assert.True(runtime.GetModelProfileSelection().HasSessionOverride);

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateNamedConfiguration(
                    "one",
                    new NamedProfile("one", "primary", "model-one-v2", "primary-v2"),
                    new NamedProfile("two", "secondary", "model-two", "secondary-v1")),
                deadline.Token);

            await using (var reusedLease = await runtime.AcquireAsync(deadline.Token))
            {
                Assert.Same(secondClient, reusedLease.Profile.ChatClient);
                Assert.Equal("two", reusedLease.Profile.ModelIdentity?.ProfileId.Value);
            }

            Assert.False(firstClient.Disposed);
            await firstLease.DisposeAsync();
            Assert.True(firstClient.Disposed);

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateNamedConfiguration(
                    "one",
                    new NamedProfile("one", "primary", "model-one-v2", "primary-v2")),
                deadline.Token);
            var fallback = runtime.GetModelProfileSelection();
            Assert.Equal("one", fallback.SelectedProfileId);
            Assert.False(fallback.HasSessionOverride);
            Assert.False(secondClient.Disposed);
            await secondLease.DisposeAsync();
            Assert.True(secondClient.Disposed);

            var acceptedVersion = runtime.Version;
            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateNamedConfiguration(
                    "a-good",
                    new NamedProfile("a-good", "primary", "replacement", "primary-v3"),
                    new NamedProfile("z-bad", "broken", "fail", "broken-v1")),
                deadline.Token);
            Assert.Equal(acceptedVersion, runtime.Version);
            Assert.Equal("model-one-v2", (await AcquireClientAsync(runtime)).Model);
            Assert.True(factory.Clients.Single(client => client.Model == "replacement").Disposed);
        }
        finally
        {
            await host.DisposeAsync();
            Assert.All(factory.Clients, static client => Assert.True(client.Disposed));
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task NamedAndLegacyModelConfigurationCannotBeCombined()
    {
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-profile-conflict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        var configuration = JsonNode.Parse(CreateNamedConfiguration(
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

            var assertion20 = Assert.ThrowsAny<InvalidOperationException>(() => host.Services.GetRequiredService<MaieuticsRuntimeConfiguration>());
            Assert.Matches("(?is)^.*cannot\\ be\\ combined.*$", assertion20.Message);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<TrackingChatClient> AcquireClientAsync(MaieuticsRuntimeConfiguration runtime)
    {
        await using var lease = await runtime.AcquireAsync(TestContext.Current.CancellationToken);
        return Assert.IsType<TrackingChatClient>(lease.Profile.ChatClient);
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
        var previousRequest = runtime.ReloadRequest;
        await WriteAndWaitForConfigurationSignalAsync(
            configuration,
            path,
            contents,
            cancellationToken);
        await runtime.WaitForReloadCompletionAsync(previousRequest, cancellationToken);
    }

    private static async Task WriteAndWaitForConfigurationSignalAsync(
        IConfiguration configuration,
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        // The reload token's initial file stamp is captured when the token is created. A Windows
        // scheduling race can land the write before that read, making the token never fire (the
        // observed 50s TaskCanceled timeout). After the write, wait briefly for the change; if it
        // does not arrive, touch the file to force a stamp change the poller will observe. This
        // avoids depending on the token's stamp-capture timing while still asserting that the
        // reload pipeline delivers the change. A token that has already fired (a previous reload
        // still mid-flight) is skipped: registering on it would complete immediately and mask the
        // race. Its reload either reads this write directly or re-arms a token whose stamp the
        // write then changes, so the caller's reload-completion wait observes the write either way.
        var token = configuration.GetReloadToken();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable? registration = token.HasChanged
            ? null
            : token.RegisterChangeCallback(
                static state => (state as TaskCompletionSource)?.TrySetResult(),
                changed);
        await File.WriteAllTextAsync(path, contents, cancellationToken);
        if (registration is null) return;
        if (!changed.Task.IsCompleted)
        {
            try
            {
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                // The write did not change the token's captured stamp (stamp read raced ahead of
                // the write). Touch the file so the poller observes a change and fires the token.
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                await changed.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private static string CreateConfiguration(
        string provider,
        string model,
        OpenAiApiFlavor apiFlavor = OpenAiApiFlavor.Responses,
        string? systemPrompt = null,
        int maxInputCharacters = 32_000,
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
                ["Agent"] = agent
            }
        };
        return root.ToJsonString();
    }

    private static string CreateNamedConfiguration(
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
            }
        }.ToJsonString();
    }

    [Fact(Timeout = 40_000)]
    public async Task AcquiredProfileMergesConfiguredEndpointCapabilities()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-endpoint-capabilities-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
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
        var capabilityInfo = Assert.Single(status.CapabilityProfiles);
        Assert.Equal("fake", capabilityInfo.SourceId);
        Assert.Equal("test-model", capabilityInfo.ModelId);
        Assert.True(capabilityInfo.Matched);
        Assert.False(capabilityInfo.KnownVendor);
        Assert.Equal(["FileSearch", "WebSearch"], capabilityInfo.HostedCapabilities);
        Assert.True(capabilityInfo.Capabilities.HasFlag(AgentModelCapabilities.StreamingText));
        Assert.True(capabilityInfo.Capabilities.HasFlag(AgentModelCapabilities.FunctionCalling));

        await using var lease = await runtime.AcquireAsync(deadline.Token);
        Assert.True(lease.Profile.Capabilities.HasFlag(AgentModelCapabilities.StreamingText));
        Assert.True(lease.Profile.Capabilities.HasFlag(AgentModelCapabilities.FunctionCalling));
        Assert.Equal(["FileSearch", "WebSearch"], lease.Profile.HostedCapabilities);
    }

    [Fact(Timeout = 40_000)]
    public async Task UnmatchedEndpointKeepsOnlyDeclaredSourceCapabilities()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-endpoint-baseline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
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
        var capabilityInfo = Assert.Single(status.CapabilityProfiles);
        Assert.Equal("fake", capabilityInfo.SourceId);
        Assert.Equal("test-model", capabilityInfo.ModelId);
        Assert.False(capabilityInfo.Matched);
        Assert.False(capabilityInfo.KnownVendor);
        Assert.Empty(capabilityInfo.HostedCapabilities);
        Assert.True(capabilityInfo.Capabilities.HasFlag(AgentModelCapabilities.StreamingText));
        Assert.True(capabilityInfo.Capabilities.HasFlag(AgentModelCapabilities.FunctionCalling));

        await using var lease = await runtime.AcquireAsync(deadline.Token);
        Assert.True(lease.Profile.Capabilities.HasFlag(AgentModelCapabilities.StreamingText));
        Assert.True(lease.Profile.Capabilities.HasFlag(AgentModelCapabilities.FunctionCalling));
        Assert.Empty(lease.Profile.HostedCapabilities);
    }

    [Fact(Timeout = 40_000)]
    public async Task KnownVendorEndpointGrantsAutomaticHostedCapabilities()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-known-vendor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
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
        var capabilityInfo = Assert.Single(status.CapabilityProfiles);
        Assert.True(capabilityInfo.KnownVendor);
        Assert.False(capabilityInfo.Matched);
        Assert.Equal(ResponsesFormatCapabilities, capabilityInfo.HostedCapabilities);

        await using var lease = await runtime.AcquireAsync(deadline.Token);
        Assert.Equal(ResponsesFormatCapabilities, lease.Profile.HostedCapabilities);
    }

    [Fact(Timeout = 40_000)]
    public async Task VendorsCatalogNarrowsCapabilitiesByModel()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-vendor-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
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
        Assert.Equal(["WebSearch"], lease.Profile.HostedCapabilities);
    }

    [Fact(Timeout = 60_000)]
    public async Task AutomaticProfileRetiresWhenVendorCatalogChangesOnReload()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-auto-profile-vendor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateVendorCatalogConfiguration("WebSearch"),
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
            await using var lease = await runtime.AcquireAsync(deadline.Token);
            var client = Assert.IsType<TrackingChatClient>(lease.Profile.ChatClient);
            Assert.Equal(["WebSearch"], lease.Profile.HostedCapabilities);

            // The active automatic override is reported alongside configured profiles.
            Assert.Single(runtime.GetStatus().CapabilityProfiles, profile =>
                profile.SourceId == "vendor" &&
                profile.ModelId == "model-alpha" &&
                profile.KnownVendor &&
                profile.HostedCapabilities.SequenceEqual(["WebSearch"]));

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateVendorCatalogConfiguration("Shell"),
                deadline.Token);

            Assert.Empty(runtime.GetModelProfileSelection().Profiles);
            Assert.False(client.Disposed);
            await lease.DisposeAsync();
            Assert.True(client.Disposed);
        }
        finally
        {
            await host.DisposeAsync();
            Assert.All(factory.Clients, static client => Assert.True(client.Disposed));
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AutomaticProfileRetiresWhenVendorModelCatalogChangesOnReload()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-auto-profile-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateVendorModelCatalogConfiguration("WebSearch"),
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
            await using var lease = await runtime.AcquireAsync(deadline.Token);
            var client = Assert.IsType<TrackingChatClient>(lease.Profile.ChatClient);
            Assert.Equal(["WebSearch"], lease.Profile.HostedCapabilities);

            await WriteAndWaitForReloadAsync(
                configuration,
                runtime,
                configurationFile,
                CreateVendorModelCatalogConfiguration("Shell"),
                deadline.Token);

            Assert.Empty(runtime.GetModelProfileSelection().Profiles);
            Assert.False(client.Disposed);
            await lease.DisposeAsync();
            Assert.True(client.Disposed);
        }
        finally
        {
            await host.DisposeAsync();
            Assert.All(factory.Clients, static client => Assert.True(client.Disposed));
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    private static string CreateVendorModelCatalogConfiguration(string modelCapability)
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
            }
        }.ToJsonString();
    }

    private static string CreateVendorCatalogConfiguration(string vendorCapability)
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
        JsonArray endpoints,
        params NamedSource[] sources)
    {
        return CreateSourceOnlyConfigurationCore(endpoints, sources);
    }

    private static string CreateSourceOnlyConfiguration(
        params NamedSource[] sources)
    {
        return CreateSourceOnlyConfigurationCore(null, sources);
    }

    private static string CreateSourceOnlyConfigurationCore(
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateNamedConfiguration(
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

            var group = Assert.Single(groups);
            Assert.Equal("discovery-source", group.SourceId);
            Assert.Equal("Fake", group.Provider);
            Assert.Null(group.Failure);
            Assert.Equal(2, group.Models.Count());
            Assert.Equal("model-alpha", group.Models[0].Id);
            Assert.Equal("Fake", group.Models[0].Provider);
            Assert.Equal("test-org", group.Models[0].OwnedBy);
            Assert.Equal("model-beta", group.Models[1].Id);
            Assert.Equal("Fake", group.Models[1].Provider);
        }
        finally
        {
            await host.DisposeAsync();
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 40_000)]
    public async Task GetDiscoveredModelsCacheRespectsRefreshFlag()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-discovery-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateNamedConfiguration(
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
            Assert.Equal(1, factory.DiscoveryCount);

            // Second call within TTL uses cache
            await runtime.GetDiscoveredModelsAsync(cancellationToken: deadline.Token);
            Assert.Equal(1, factory.DiscoveryCount);

            // Third call with refresh=true bypasses cache
            await runtime.GetDiscoveredModelsAsync(refresh: true, cancellationToken: deadline.Token);
            Assert.Equal(2, factory.DiscoveryCount);
        }
        finally
        {
            await host.DisposeAsync();
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 40_000)]
    public async Task GetDiscoveredModelsSkipsSourcesWithoutDiscoverySupport()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-discovery-skip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateNamedConfiguration(
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
            Assert.Empty(groups);
        }
        finally
        {
            await host.DisposeAsync();
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 40_000)]
    public async Task GetDiscoveredModelsCapturesErrorsFromFailingSources()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var environment = new EnvironmentVariableScope(ClearedProviderEnvironment());
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-discovery-error-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationFile = Path.Combine(root, "maieutics.json");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateNamedConfiguration(
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

            var group = Assert.Single(groups);
            Assert.Equal(ModelDiscoveryFailureKind.ProviderError, group.Failure);
            Assert.Empty(group.Models);
        }
        finally
        {
            await host.DisposeAsync();
            await DeleteDirectoryWithRetryAsync(root, TestContext.Current.CancellationToken);
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

    private sealed class BlockingChatClientFactory(string blockedModel = "one") : IConfiguredChatClientFactory
    {
        public TaskCompletionSource CreateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCreation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<TrackingChatClient> Clients { get; } = [];

        public string ProviderName => "Fake";

        public IConfiguredChatClientSource BindSource(string sourceId, IConfigurationSection configuration)
        {
            return new BlockingSource(this, sourceId);
        }

        private IChatClient Create(string model)
        {
            if (model == blockedModel)
            {
                CreateStarted.TrySetResult();
                ReleaseCreation.Task.GetAwaiter().GetResult();
            }

            var client = new TrackingChatClient(model);
            Clients.Add(client);
            return client;
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
