using System.IO.Pipelines;
using System.Text.Json;
using Maieutics.Control;
using Maieutics.Mcp;
using Maieutics.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maieutics.Product.Tests;

public sealed class PluginMcpCoordinatorTests
{
    private static readonly PluginRegistration Registration =
        new("plugin", "main", ReplExtensionPointName.McpDiscover);

    [Fact(Timeout = 30_000)]
    public async Task ReusesReplacesAndRemovesGenerationsWithLastKnownGoodDiscovery()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var generationFactory = new TestGenerationFactory();
        var currentDiscovery = PluginMcpDiscoveryResult.Success([CreateDefinition("one")]);
        // The test reassigns the scripted discovery between phases and the coordinator reads
        // it live by design; phase boundaries quiesce the writes.
        // ReSharper disable once AccessToModifiedClosure
        await using var coordinator = new PluginMcpCoordinator(
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(currentDiscovery);
            },
            generationFactory.CreateAsync,
            NullLogger<PluginHostManager>.Instance);
        coordinator.Start();
        // The differential coordinator reuses an unchanged registration's contribution,
        // so every step below that simulates CHANGED discovery output re-runs through
        // the forced publish (the trigger form); plain publishes assert the diff itself.
        var forced = new HashSet<string>(["plugin"], StringComparer.Ordinal);

        Assert.True(await coordinator.PublishRegistryAsync([Registration]).WaitAsync(deadline.Token));
        var item = Assert.Single(generationFactory.Generations);
        var originalGeneration = item;
        var originalLease = Assert.Single(coordinator.AcquireLeases());

        Assert.True(await coordinator.PublishRegistryAsync([Registration]).WaitAsync(deadline.Token));
        Assert.Single(generationFactory.Generations);

        var replacementDefinition = CreateDefinition("two");
        generationFactory.FailGenerationKey = replacementDefinition.GenerationKey;
        currentDiscovery = PluginMcpDiscoveryResult.Success([replacementDefinition]);
        Assert.False(await coordinator.PublishRegistryAsync([Registration], forced).WaitAsync(deadline.Token));
        Assert.Single(generationFactory.Generations);
        var leaseAfterFailure = Assert.Single(coordinator.AcquireLeases());
        await leaseAfterFailure.DisposeAsync();

        generationFactory.FailGenerationKey = null;
        Assert.True(await coordinator.PublishRegistryAsync([Registration], forced).WaitAsync(deadline.Token));
        Assert.Equal(2, generationFactory.Generations.Count());
        Assert.Null(originalGeneration.TryAcquire());
        var originalRetirement = originalGeneration.Retire();
        Assert.False(originalRetirement.IsCompleted, "the original run lease is still active");
        await originalLease.DisposeAsync();
        await originalRetirement.WaitAsync(deadline.Token);

        var replacementGeneration = generationFactory.Generations[1];
        var replacementLease = Assert.Single(coordinator.AcquireLeases());
        currentDiscovery = PluginMcpDiscoveryResult.Failed("temporary_failure");
        Assert.True(await coordinator.PublishRegistryAsync([Registration], forced).WaitAsync(deadline.Token));
        Assert.Equal(2, generationFactory.Generations.Count());
        var retainedLease = replacementGeneration.TryAcquire()
                            ?? throw new InvalidOperationException("The last-known-good generation was not retained.");
        await retainedLease.DisposeAsync();

        Assert.True(await coordinator.PublishRegistryAsync([]).WaitAsync(deadline.Token));
        Assert.Empty(coordinator.AcquireLeases());
        var replacementRetirement = replacementGeneration.Retire();
        Assert.False(replacementRetirement.IsCompleted, "the replacement run lease is still active");
        await replacementLease.DisposeAsync();
        await replacementRetirement.WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 30_000)]
    public async Task UnchangedRegistrationSetRunsNoDiscovery()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var generationFactory = new TestGenerationFactory();
        var discoveryCalls = 0;
        await using var coordinator = new PluginMcpCoordinator(
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref discoveryCalls);
                return Task.FromResult(PluginMcpDiscoveryResult.Success([CreateDefinition("one")]));
            },
            generationFactory.CreateAsync,
            NullLogger<PluginHostManager>.Instance);
        coordinator.Start();

        Assert.True(await coordinator.PublishRegistryAsync([Registration]).WaitAsync(deadline.Token));
        Assert.Equal(1, discoveryCalls);
        var lease = Assert.Single(coordinator.AcquireLeases());

        // Republishing the identical registration set is the common frame (host
        // heartbeats, approval snapshots): the diff keeps the contribution and runs
        // no discovery at all.
        Assert.True(await coordinator.PublishRegistryAsync([Registration]).WaitAsync(deadline.Token));
        Assert.Equal(1, discoveryCalls);
        var retained = coordinator.AcquireLeases();
        Assert.Single(retained);
        foreach (var acquired in retained) await acquired.DisposeAsync();
        await lease.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task RegistrationSetDiffRunsDiscoveryOnlyForNewRegistrations()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var generationFactory = new TestGenerationFactory();
        var alpha = new PluginRegistration("alpha", "main", ReplExtensionPointName.McpDiscover);
        var beta = new PluginRegistration("beta", "main", ReplExtensionPointName.McpDiscover);
        var discovered = new List<PluginRegistration>();
        await using var coordinator = new PluginMcpCoordinator(
            (registration, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                discovered.Add(registration);
                return Task.FromResult(
                    PluginMcpDiscoveryResult.Success(
                        [CreateDefinition(registration.PluginId, registration.PluginId)]));
            },
            generationFactory.CreateAsync,
            NullLogger<PluginHostManager>.Instance);
        coordinator.Start();

        Assert.True(await coordinator.PublishRegistryAsync([alpha]).WaitAsync(deadline.Token));
        Assert.Equal("alpha", Assert.Single(discovered).PluginId);
        var alphaLease = Assert.Single(coordinator.AcquireLeases());

        // Adding a registration discovers only the addition; alpha keeps its
        // contribution (and its generation) untouched.
        discovered.Clear();
        Assert.True(await coordinator.PublishRegistryAsync([alpha, beta]).WaitAsync(deadline.Token));
        Assert.Equal("beta", Assert.Single(discovered).PluginId);
        var both = coordinator.AcquireLeases();
        Assert.Equal(2, both.Count());
        foreach (var acquired in both) await acquired.DisposeAsync();

        // Removing a registration drops its contribution without re-running anyone's
        // discovery; alpha's generation retires and beta's survives.
        discovered.Clear();
        Assert.True(await coordinator.PublishRegistryAsync([beta]).WaitAsync(deadline.Token));
        Assert.Empty(discovered);
        var remaining = coordinator.AcquireLeases();
        Assert.Equal("plugin:beta::server", Assert.Single(remaining).ServerId);
        foreach (var lease in remaining) await lease.DisposeAsync();

        await alphaLease.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task SupersededRevisionCannotPublishAndDiscoveryNeverOverlaps()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var generationFactory = new TestGenerationFactory();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var concurrent = 0;
        var maximumConcurrent = 0;
        await using var coordinator = new PluginMcpCoordinator(
            async (_, cancellationToken) =>
            {
                var active = Interlocked.Increment(ref concurrent);
                maximumConcurrent = Math.Max(maximumConcurrent, active);
                try
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        firstStarted.TrySetResult();
                        await releaseFirst.Task.WaitAsync(cancellationToken);
                        return PluginMcpDiscoveryResult.Success([CreateDefinition("superseded")]);
                    }

                    return PluginMcpDiscoveryResult.Success([CreateDefinition("current")]);
                }
                finally
                {
                    Interlocked.Decrement(ref concurrent);
                }
            },
            generationFactory.CreateAsync,
            NullLogger<PluginHostManager>.Instance);
        coordinator.Start();

        var firstRefresh = coordinator.PublishRegistryAsync([Registration]);
        await firstStarted.Task.WaitAsync(deadline.Token);
        var secondRefresh = coordinator.PublishRegistryAsync([Registration]);

        Assert.False(await firstRefresh.WaitAsync(deadline.Token));
        releaseFirst.TrySetResult();
        Assert.True(await secondRefresh.WaitAsync(deadline.Token));

        Assert.Equal(1, maximumConcurrent);
        Assert.Equal(CreateDefinition("current").GenerationKey, Assert.Single(generationFactory.Definitions).GenerationKey);
    }

    [Fact(Timeout = 30_000)]
    public async Task APlainPublishSupersedingAForcedRevisionInheritsItsForcedSet()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var generationFactory = new TestGenerationFactory();
        var currentDiscovery = PluginMcpDiscoveryResult.Success([CreateDefinition("one")]);
        var discoveryCalls = 0;
        var blockNext = false;
        var discoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDiscovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The test reassigns the scripted discovery and `blockNext` toggle between phases and
        // the coordinator reads them live by design; phase boundaries quiesce the writes.
        // ReSharper disable AccessToModifiedClosure
        await using var coordinator = new PluginMcpCoordinator(
            async (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref discoveryCalls);
                if (blockNext)
                {
                    blockNext = false;
                    discoveryStarted.TrySetResult();
                    await releaseDiscovery.Task.WaitAsync(cancellationToken);
                }

                return currentDiscovery;
            },
            generationFactory.CreateAsync,
            NullLogger<PluginHostManager>.Instance);
        // ReSharper restore AccessToModifiedClosure
        coordinator.Start();
        var forced = new HashSet<string>(["plugin"], StringComparer.Ordinal);

        // The contribution publishes once, so the plain successor has a contribution it
        // could differential-skip back to.
        Assert.True(await coordinator.PublishRegistryAsync([Registration]).WaitAsync(deadline.Token));
        Assert.Equal(1, discoveryCalls);

        // A forced revision (the trigger/reload form) starts its discovery and is
        // superseded by a plain publish before it can apply. The successor must inherit
        // the forced set: skipping would silently drop the "re-run this plugin"
        // semantics and keep the pre-trigger contribution forever.
        blockNext = true;
        var forcedRefresh = coordinator.PublishRegistryAsync([Registration], forced);
        await discoveryStarted.Task.WaitAsync(deadline.Token);
        var plainRefresh = coordinator.PublishRegistryAsync([Registration]);

        currentDiscovery = PluginMcpDiscoveryResult.Success([CreateDefinition("two")]);
        releaseDiscovery.TrySetResult();
        Assert.False(await forcedRefresh.WaitAsync(deadline.Token), "the superseded revision cannot publish");
        Assert.True(await plainRefresh.WaitAsync(deadline.Token));

        // The inherited force re-ran discovery (the aborted attempt plus the successor's
        // fresh run) and the new result is live instead of the reused stale contribution.
        Assert.Equal(3, discoveryCalls);
        Assert.Equal(2, generationFactory.Generations.Count());
        Assert.Equal(CreateDefinition("two").GenerationKey, generationFactory.Definitions[^1].GenerationKey);
    }

    [Fact(Timeout = 30_000)]
    public async Task ConcurrentDisposalCancelsAndObservesActiveDiscovery()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var discoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new PluginMcpCoordinator(
            async (_, cancellationToken) =>
            {
                discoveryStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return PluginMcpDiscoveryResult.Success([]);
            },
            (_, _) => throw new InvalidOperationException("No generation should be created."),
            NullLogger<PluginHostManager>.Instance);
        coordinator.Start();

        var refresh = coordinator.PublishRegistryAsync([Registration]);
        await discoveryStarted.Task.WaitAsync(deadline.Token);
        var firstDisposal = coordinator.DisposeAsync().AsTask();
        var secondDisposal = coordinator.DisposeAsync().AsTask();

        Assert.Same(firstDisposal, secondDisposal);
        await firstDisposal.WaitAsync(deadline.Token);
        Assert.False(await refresh.WaitAsync(deadline.Token));
        Assert.Empty(coordinator.AcquireLeases());
    }

    private static McpServerDefinition CreateDefinition(string command)
    {
        return CreateDefinition("plugin", command);
    }

    private static McpServerDefinition CreateDefinition(string pluginId, string command)
    {
        var transport = new StdioMcpTransportDefinition(command, []);
        return new McpServerDefinition(
            $"plugin:{pluginId}::server",
            transport,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero,
            false,
            false,
            McpServerDefinition.CreateGenerationKey(
                transport,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero,
                rootsEnabled: false,
                elicitationEnabled: false));
    }

    [Fact(Timeout = 30_000)]
    public async Task AdjustmentChainDropsServersAndReProjectsWithFingerprintChanges()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var providerRegistration = new PluginRegistration("provider", "maieutics.json", ReplExtensionPointName.McpDiscover);
        var drop = false;
        var compositionCalls = 0;
        var toolCalls = 0;
        // The test flips `drop` between phases and the fake reads it live by design; the
        // chain callbacks run only while the toggle writes are quiesced.
        // ReSharper disable once AccessToModifiedClosure
        var chain = new McpAdjustmentChain(
            (_, _, _, request, _) =>
            {
                var body = request ?? default;
                var reason = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("reason", out var value)
                    ? value.GetString()
                    : null;
                if (reason == "composition")
                {
                    Interlocked.Increment(ref compositionCalls);
                    JsonElement? result = drop
                        ? ParseAdjustmentJson("""{"servers":[{"id":"plugin:provider::server","drop":true}]}""")
                        : ParseAdjustmentJson("""{"servers":[]}""");
                    return Task.FromResult(ExtensionCallOutcome.Result(result));
                }

                if (reason == "tools")
                {
                    Interlocked.Increment(ref toolCalls);
                    return Task.FromResult(ExtensionCallOutcome.Result(
                        ParseAdjustmentJson("""[{"aliasOf":"echo"},{"aliasOf":"echo","name":"echo_alias"}]""")));
                }

                return Task.FromResult(ExtensionCallOutcome.Error("unknown_reason", "unreachable"));
            },
            NullLogger<PluginHostManager>.Instance);
        await using var generationFactory = new TestGenerationFactory { ToolSurfaceAdjuster = chain };
        chain.UpdateSnapshot(new McpAdjustmentSnapshot(
            ["adjuster", "provider"],
            new Dictionary<string, IReadOnlyList<string>> { ["adjuster"] = ["provider"] },
            [new McpAdjusterRegistration("adjuster", "adjust")]));
        // The definition's owner ("provider") must match the adjuster's declared
        // dependency, or the chain scope filters the server out entirely.
        var currentDiscovery = PluginMcpDiscoveryResult.Success(
            [CreateDefinition("one") with { Id = "plugin:provider::server" }]);
        var discoveryRuns = 0;
        // The test reassigns the scripted discovery between phases and the coordinator reads
        // it live by design; phase boundaries quiesce the writes.
        // ReSharper disable once AccessToModifiedClosure
        await using var coordinator = new PluginMcpCoordinator(
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref discoveryRuns);
                return Task.FromResult(currentDiscovery);
            },
            generationFactory.CreateAsync,
            NullLogger<PluginHostManager>.Instance,
            chain);
        coordinator.Start();

        Assert.True(await coordinator.PublishRegistryAsync([providerRegistration]).WaitAsync(deadline.Token));
        Assert.Single(generationFactory.Generations);
        Assert.True(compositionCalls >= 1);
        var firstLease = Assert.Single(coordinator.AcquireLeases());
        await firstLease.DisposeAsync();

        // A chain change (second adjuster joins) keeps the generation but must
        // re-project its tool surface over the live connection.
        chain.UpdateSnapshot(new McpAdjustmentSnapshot(
            ["adjuster", "adjuster2", "provider"],
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["adjuster"] = ["provider"],
                ["adjuster2"] = ["provider"],
            },
            [
                new McpAdjusterRegistration("adjuster", "adjust"),
                new McpAdjusterRegistration("adjuster2", "adjust"),
            ]));
        Assert.True(await coordinator.PublishRegistryAsync([providerRegistration]).WaitAsync(deadline.Token));
        Assert.Single(generationFactory.Generations);
        // The fingerprint change re-projects the tool surface over the LIVE connection.
        // The revision's application has a completion signal: await the registry
        // reconciliation, then assert the re-projected surface synchronously.
        await coordinator.RegistryReconciled.WaitAsync(deadline.Token);
        var projected = coordinator.AcquireLeases();
        var item = Assert.Single(projected);
        Assert.Equal(
            ["echo", "echo_alias"],
            item.Tools.Select(static function => function.Name).Order());
        await projected[0].DisposeAsync();

        // The adjuster drops the server: it leaves the view before the merge, so
        // no generation survives the revision.
        drop = true;
        Assert.True(await coordinator.PublishRegistryAsync([providerRegistration]).WaitAsync(deadline.Token));
        Assert.Empty(coordinator.AcquireLeases());

        // Reverting the drop restores the server on the next (plain, unchanged-set)
        // revision: the persisted contribution stayed raw — drops are a property of
        // the composed view, never of the discovery results — so the fold recomposes
        // the server without re-running discovery.
        var runsAfterDrop = Volatile.Read(ref discoveryRuns);
        drop = false;
        Assert.True(await coordinator.PublishRegistryAsync([providerRegistration]).WaitAsync(deadline.Token));
        Assert.Equal(runsAfterDrop, Volatile.Read(ref discoveryRuns));
        var restored = Assert.Single(coordinator.AcquireLeases());
        await restored.DisposeAsync();
    }

    private static JsonElement ParseAdjustmentJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class TestGenerationFactory : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly List<(McpServer Server, Task Completion)> _servers = [];

        internal List<McpServerDefinition> Definitions { get; } = [];

        internal string? FailGenerationKey { get; set; }

        internal List<McpServerGeneration> Generations { get; } = [];

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            foreach (var (server, _) in _servers) await server.DisposeAsync();

            foreach (var (_, completion) in _servers)
                try
                {
                    await completion;
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                }

            _lifetime.Dispose();
        }

        internal IMcpToolSurfaceAdjuster? ToolSurfaceAdjuster { get; init; }

        internal async Task<McpServerGeneration> CreateAsync(
            McpServerDefinition definition,
            CancellationToken cancellationToken)
        {
            if (string.Equals(FailGenerationKey, definition.GenerationKey, StringComparison.Ordinal))
                throw new InvalidOperationException("simulated generation construction failure");

            var generation = await McpServerGeneration.CreateAsync(
                definition,
                NullLoggerFactory.Instance,
                TimeProvider.System,
                cancellationToken,
                CreateTransportAsync,
                toolSurfaceAdjuster: ToolSurfaceAdjuster);
            Definitions.Add(definition);
            Generations.Add(generation);
            return generation;
        }

        private ValueTask<IClientTransport> CreateTransportAsync(
            McpServerDefinition definition,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var serverTransport = new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream(),
                definition.Id,
                loggerFactory);
            var function = AIFunctionFactory.Create(
                (string value) => new EchoResult(value),
                "echo",
                "Echoes one value.");
            var server = McpServer.Create(
                serverTransport,
                new McpServerOptions
                {
                    ServerInfo = new Implementation { Name = "test", Version = "1.0" },
                    ToolCollection =
                    [
                        McpServerTool.Create(
                            function,
                            new McpServerToolCreateOptions { UseStructuredContent = true })
                    ]
                },
                loggerFactory);
            _servers.Add((server, server.RunAsync(_lifetime.Token)));
            IClientTransport transport = new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream(),
                loggerFactory);
            return ValueTask.FromResult(transport);
        }
    }

    // The value is the tool's structured result; the assertions read the serialized JSON
    // the client receives, so the property is carried for the tool's wire shape.
    // ReSharper disable once NotAccessedPositionalProperty.Local
    private sealed record EchoResult(string Value);
}
