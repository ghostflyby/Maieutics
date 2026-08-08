using System.IO.Pipelines;
using FluentAssertions;
using Maieutics.Control;
using Maieutics.Mcp;
using Maieutics.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maieutics.Jupyter.Tests;

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
        await using var coordinator = new PluginMcpCoordinator(
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(currentDiscovery);
            },
            generationFactory.CreateAsync,
            NullLogger<PluginHostManager>.Instance);
        coordinator.Start();

        (await coordinator.PublishRegistryAsync([Registration]).WaitAsync(deadline.Token)).Should().BeTrue();
        generationFactory.Generations.Should().ContainSingle();
        var originalGeneration = generationFactory.Generations[0];
        var originalLease = coordinator.AcquireLeases().Should().ContainSingle().Which;

        (await coordinator.PublishRegistryAsync([Registration]).WaitAsync(deadline.Token)).Should().BeTrue();
        generationFactory.Generations.Should().ContainSingle("an unchanged generation key must be reused");

        var replacementDefinition = CreateDefinition("two");
        generationFactory.FailGenerationKey = replacementDefinition.GenerationKey;
        currentDiscovery = PluginMcpDiscoveryResult.Success([replacementDefinition]);
        (await coordinator.PublishRegistryAsync([Registration]).WaitAsync(deadline.Token)).Should().BeFalse();
        generationFactory.Generations.Should().ContainSingle("a failed candidate must not replace the active snapshot");
        var leaseAfterFailure = coordinator.AcquireLeases().Should().ContainSingle().Which;
        await leaseAfterFailure.DisposeAsync();

        generationFactory.FailGenerationKey = null;
        (await coordinator.PublishRegistryAsync([Registration]).WaitAsync(deadline.Token)).Should().BeTrue();
        generationFactory.Generations.Should().HaveCount(2);
        originalGeneration.TryAcquire().Should().BeNull("the replaced generation must be retired immediately");
        var originalRetirement = originalGeneration.Retire();
        originalRetirement.IsCompleted.Should().BeFalse("the original run lease is still active");
        await originalLease.DisposeAsync();
        await originalRetirement.WaitAsync(deadline.Token);

        var replacementGeneration = generationFactory.Generations[1];
        var replacementLease = coordinator.AcquireLeases().Should().ContainSingle().Which;
        currentDiscovery = PluginMcpDiscoveryResult.Failed("temporary_failure");
        (await coordinator.PublishRegistryAsync([Registration]).WaitAsync(deadline.Token)).Should().BeTrue();
        generationFactory.Generations.Should().HaveCount(2);
        var retainedLease = replacementGeneration.TryAcquire()
                            ?? throw new InvalidOperationException("The last-known-good generation was not retained.");
        await retainedLease.DisposeAsync();

        (await coordinator.PublishRegistryAsync([]).WaitAsync(deadline.Token)).Should().BeTrue();
        coordinator.AcquireLeases().Should().BeEmpty();
        var replacementRetirement = replacementGeneration.Retire();
        replacementRetirement.IsCompleted.Should().BeFalse("the replacement run lease is still active");
        await replacementLease.DisposeAsync();
        await replacementRetirement.WaitAsync(deadline.Token);
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

        (await firstRefresh.WaitAsync(deadline.Token)).Should().BeFalse();
        releaseFirst.TrySetResult();
        (await secondRefresh.WaitAsync(deadline.Token)).Should().BeTrue();

        maximumConcurrent.Should().Be(1);
        generationFactory.Definitions.Should().ContainSingle()
            .Which.GenerationKey.Should().Be(CreateDefinition("current").GenerationKey);
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

        secondDisposal.Should().BeSameAs(firstDisposal);
        await firstDisposal.WaitAsync(deadline.Token);
        (await refresh.WaitAsync(deadline.Token)).Should().BeFalse();
        coordinator.AcquireLeases().Should().BeEmpty();
    }

    private static McpServerDefinition CreateDefinition(string command)
    {
        var transport = new StdioMcpTransportDefinition(command, [], null, null);
        return new McpServerDefinition(
            "plugin:plugin::server",
            transport,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero,
            McpServerDefinition.CreateGenerationKey(
                transport,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero));
    }

    private sealed class TestGenerationFactory : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly List<(McpServer Server, Task Completion)> servers = [];

        internal List<McpServerDefinition> Definitions { get; } = [];

        internal string? FailGenerationKey { get; set; }

        internal List<McpServerGeneration> Generations { get; } = [];

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            foreach (var (server, _) in servers) await server.DisposeAsync();

            foreach (var (_, completion) in servers)
                try
                {
                    await completion;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }

            lifetime.Dispose();
        }

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
                CreateTransportAsync);
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
                loggerFactory,
                null);
            servers.Add((server, server.RunAsync(lifetime.Token)));
            IClientTransport transport = new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream(),
                loggerFactory);
            return ValueTask.FromResult(transport);
        }
    }

    private sealed record EchoResult(string Value);
}
