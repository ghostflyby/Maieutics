using System.Text.Json;
using FluentAssertions;
using Maieutics.Mcp;
using Maieutics.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

/// <summary>
///     The MCP adjustment responsibility chain (ADR 0034): composition folds run
///     in dependency-topological order with authorization filtering (only declared
///     dependencies' servers are in scope), drops are sticky per adjuster, and the
///     tool-listing chain threads listings with sticky fail-closed fallback.
/// </summary>
public sealed class McpAdjustmentChainTests
{
    private static readonly StdioMcpTransportDefinition Stdio = new("deno");

    private static McpAdjustmentChain CreateChain(
        Func<string, string, JsonElement?, CancellationToken, Task<JsonElement?>> handler,
        McpAdjustmentSnapshot? snapshot = null)
    {
        var chain = new McpAdjustmentChain(
            (pluginId, exportName, _, request, ct) => handler(pluginId, exportName, request, ct).ContinueWith(
                task => task.Result is null
                    ? ExtensionCallOutcome.Error("adjuster_failed", "no result")
                    : ExtensionCallOutcome.Result(task.Result),
                ct,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default),
            NullLogger.Instance);
        if (snapshot is not null) chain.UpdateSnapshot(snapshot);
        return chain;
    }

    private static McpAdjustmentSnapshot Snapshot(params (string PluginId, string[] Dependencies)[] plugins)
    {
        var order = plugins.Select(static plugin => plugin.PluginId).ToArray();
        var dependencies = plugins.ToDictionary(
            static plugin => plugin.PluginId,
            static plugin => (IReadOnlyList<string>)plugin.Dependencies);
        var adjusters = plugins
            .Select(static plugin => new McpAdjusterRegistration(plugin.PluginId, "adjust"))
            .ToList();
        return new McpAdjustmentSnapshot(order, dependencies, adjusters);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task CompositionFoldRunsInTopologicalOrderAndDropsServers()
    {
        // "provider" defines the server; "middle" depends on provider; "consumer"
        // depends on both. The adjuster list is deliberately out of order to prove
        // the snapshot order governs the fold.
        var snapshot = Snapshot(
            ("consumer", ["provider", "middle"]),
            ("provider", []),
            ("middle", ["provider"]));
        snapshot = snapshot with
        {
            OrderedPluginIds = ["provider", "middle", "consumer"],
            Adjusters =
            [
                new McpAdjusterRegistration("consumer", "adjust"),
                new McpAdjusterRegistration("middle", "adjust"),
                new McpAdjusterRegistration("provider", "adjust"),
            ],
        };

        var calls = new List<(string PluginId, string ServerIds)>();
        var chain = CreateChain(
            (pluginId, _, request, _) =>
            {
                var body = request ?? default;
                var ids = body.ValueKind == JsonValueKind.Object &&
                    body.TryGetProperty("servers", out var servers)
                    ? string.Join(",", servers.EnumerateArray()
                        .Select(server => server.GetProperty("id").GetString()))
                    : string.Empty;
                calls.Add((pluginId, ids));
                // "middle" drops the provider server for everyone downstream.
                JsonElement? result = pluginId == "middle"
                    ? Json("""{"servers":[{"id":"plugin:provider::main","drop":true}]}""")
                    : Json("""{"servers":[]}""");
                return Task.FromResult(result);
            },
            snapshot);

        var view = new List<(string, string, McpTransportDefinition)>
        {
            ("provider", "plugin:provider::main", Stdio),
            ("middle", "plugin:middle::aux", Stdio),
        };
        var dropped = await chain.FoldCompositionAsync(view, TestContext.Current.CancellationToken);

        dropped.Should().Contain("plugin:provider::main");
        // The provider adjuster's scope is empty (it has no dependencies) and is
        // skipped; "consumer" folds after "middle", whose drop removed the provider.
        calls.Select(call => call.PluginId).Should().Equal("middle", "consumer");
        calls.Where(call => call.PluginId == "middle").Select(call => call.ServerIds)
            .Single().Should().Contain("plugin:provider::main");
        calls.Where(call => call.PluginId == "consumer").Select(call => call.ServerIds)
            .Single().Should().NotContain("plugin:provider::main");
    }

    [Fact]
    public async Task CompositionFoldIsStickyPerAdjusterOnFailure()
    {
        var snapshot = Snapshot(("adjuster", ["provider"]));
        var failing = true;
        var chain = CreateChain(
            (_, _, _, _) =>
            {
                JsonElement? result = failing
                    ? null
                    : Json("""{"servers":[{"id":"plugin:provider::main","drop":true}]}""");
                return Task.FromResult(result);
            },
            snapshot);

        var view = new List<(string, string, McpTransportDefinition)>
        {
            ("provider", "plugin:provider::main", Stdio),
        };

        // First fold fails with no previous result: the scope passes through.
        var first = await chain.FoldCompositionAsync(view, TestContext.Current.CancellationToken);
        first.Should().BeEmpty();

        // After one success, a later failure keeps the last-good drops active: the
        // removed server never resurrects because the adjuster hiccups.
        failing = false;
        await chain.FoldCompositionAsync(view, TestContext.Current.CancellationToken);
        failing = true;
        var third = await chain.FoldCompositionAsync(view, TestContext.Current.CancellationToken);
        third.Should().Contain("plugin:provider::main");
    }

    [Fact]
    public async Task CompositionFoldFiltersOutOfScopeServersFromThePayload()
    {
        // The adjuster depends on "provider" only: "other" must not appear in its
        // payload — the kernel filters scope before the handler ever runs.
        var snapshot = Snapshot(("adjuster", ["provider"]));
        string? seenServers = null;
        var chain = CreateChain(
            (_, _, request, _) =>
            {
                var body = request ?? default;
                seenServers = body.ValueKind == JsonValueKind.Object &&
                    body.TryGetProperty("servers", out var servers)
                    ? servers.GetRawText()
                    : string.Empty;
                return Task.FromResult<JsonElement?>(Json("""{"servers":[]}"""));
            },
            snapshot);

        var view = new List<(string, string, McpTransportDefinition)>
        {
            ("provider", "plugin:provider::main", Stdio),
            ("other", "plugin:other::secret", Stdio),
        };
        await chain.FoldCompositionAsync(view, TestContext.Current.CancellationToken);

        seenServers.Should().NotBeNull().And.Contain("plugin:provider::main").And.NotContain("plugin:other::secret");
    }

    [Fact]
    public async Task ToolChainThreadsListings()
    {
        var snapshot = Snapshot(("adjuster", ["provider"]));
        var chain = CreateChain(
            (_, _, request, _) =>
            {
                var body = request ?? default;
                var listing = body.TryGetProperty("tools", out var tools) ? tools : default;
                // Rename echo -> echo_safe and drop the rest by omission.
                var adjusted = new List<string>();
                foreach (var tool in listing.EnumerateArray())
                {
                    var name = tool.GetProperty("name").GetString();
                    if (name != "echo") continue;
                    adjusted.Add(JsonSerializer.Serialize(new
                    {
                        aliasOf = "echo",
                        name = "echo_safe",
                        description = "Safe echo",
                    }));
                }

                return Task.FromResult<JsonElement?>(Json("[" + string.Join(",", adjusted) + "]"));
            },
            snapshot);

        var listing = Json("""
            [
              {"name":"echo","description":"echoes","inputSchema":{"type":"object"}},
              {"name":"secret","description":"hidden","inputSchema":{"type":"object"}}
            ]
            """);
        var adjusted = await chain.AdjustToolsAsync("plugin:provider::main", listing, TestContext.Current.CancellationToken);
        adjusted.Should().NotBeNull();
        adjusted.Value.EnumerateArray().Should().ContainSingle();
        var tool = adjusted.Value[0];
        tool.GetProperty("aliasOf").GetString().Should().Be("echo");
        tool.GetProperty("name").GetString().Should().Be("echo_safe");
        tool.GetProperty("description").GetString().Should().Be("Safe echo");
    }

    [Fact]
    public async Task ToolChainFailsClosedWhenNoListingEverSucceeded()
    {
        var snapshot = Snapshot(("adjuster", ["provider"]));
        var chain = CreateChain((_, _, _, _) => Task.FromResult<JsonElement?>(null), snapshot);

        var listing = Json("""[{"name":"echo","inputSchema":{"type":"object"}}]""");
        var adjusted = await chain.AdjustToolsAsync("plugin:provider::main", listing, TestContext.Current.CancellationToken);
        adjusted.Should().BeNull("a failed adjustment with no previous listing exposes nothing (fail-closed)");
    }

    [Fact]
    public async Task EmptyChainPassesTheListingThrough()
    {
        var chain = CreateChain((_, _, _, _) => throw new InvalidOperationException("must not be invoked"));
        var listing = Json("""[{"name":"echo","inputSchema":{"type":"object"}}]""");
        var adjusted = await chain.AdjustToolsAsync("plugin:provider::main", listing, TestContext.Current.CancellationToken);
        adjusted.Should().NotBeNull();
        adjusted.Value.GetRawText().Should().Be(listing.GetRawText());
    }

    [Fact]
    public async Task UnknownAliasEntriesAreDroppedWithTheRestApplied()
    {
        var snapshot = Snapshot(("adjuster", ["provider"]));
        var chain = CreateChain(
            (_, _, _, _) => Task.FromResult<JsonElement?>(Json("""
                [
                  {"aliasOf":"echo","name":"echo_renamed"},
                  {"aliasOf":"fabricated","name":"ghost"}
                ]
                """)),
            snapshot);

        var listing = Json("""[{"name":"echo","inputSchema":{"type":"object"}}]""");
        var adjusted = await chain.AdjustToolsAsync("plugin:provider::main", listing, TestContext.Current.CancellationToken);
        adjusted.Should().NotBeNull();
        adjusted.Value.EnumerateArray().Should().ContainSingle().Which
            .GetProperty("name").GetString().Should().Be("echo_renamed");
    }
}
