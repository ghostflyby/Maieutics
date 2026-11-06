using System.Text.Json;
using Maieutics.Control;
using Maieutics.Plugins;
using Maieutics.Plugins.Contributions;
using Microsoft.Extensions.Time.Testing;

namespace Maieutics.Product.Tests;

public sealed class PluginSystemPromptTests
{
    [Fact(Timeout = 60_000)]
    public async Task StaticInstructionsRequireApprovalAndDeclarationChangesRevokeTheirEffect()
    {
        RequireSimulatedHost();
        using var budget = Budget(TestContext.Current.CancellationToken);
        await using var root = TempArea.Create("system-prompt-static");
        await using var approvals = TempArea.Create("system-prompt-approvals");
        await WritePluginAsync(root.Path, """{"extensions":{"SystemPrompt":[{"name":"style","content":"first"}]}}""", budget.Token);
        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(root.Path, budget.Token,
            Path.Combine(approvals.Path, "approvals.json"));
        var host = harness.Manager;
        var id = Path.GetFileName(root.Path);
        Assert.Empty(await host.GetSystemPromptsAsync(budget.Token));
        await host.ApproveAsync(id, budget.Token);
        var captured = await host.GetSystemPromptsAsync(budget.Token);
        Assert.Equal("first", Assert.Single(captured).Content);

        var reloaded = host.ReloadApplied;
        await File.WriteAllTextAsync(Path.Combine(root.Path, "maieutics.json"),
            """{"extensions":{"SystemPrompt":[{"name":"style","content":"second"}]}}""", budget.Token);
        await reloaded.WaitAsync(budget.Token);
        Assert.Empty(await host.GetSystemPromptsAsync(budget.Token));
        Assert.Equal("first", Assert.Single(captured).Content);
        await host.ApproveAsync(id, budget.Token);
        Assert.Equal("second", Assert.Single(await host.GetSystemPromptsAsync(budget.Token)).Content);
        await host.RevokeAsync(id, budget.Token);
        Assert.Empty(await host.GetSystemPromptsAsync(budget.Token));
        await host.StopAsync(budget.Token);
        Assert.Empty(await host.GetSystemPromptsAsync(budget.Token));
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerHooksRequireAnApprovedProviderDeclarationAndReturnFreshInstructions(bool declared)
    {
        RequireSimulatedHost();
        using var budget = Budget(TestContext.Current.CancellationToken);
        await using var root = TempArea.Create("system-prompt-worker");
        await using var approvals = TempArea.Create("system-prompt-approvals");
        await WritePluginAsync(root.Path, declared
            ? """{"entrypoints":{"worker":{"main":["./mod.ts"]}},"extensions":{"SystemPrompt":[{"provider":"main"}]}}"""
            : """{"entrypoints":{"worker":{"main":["./mod.ts"]}}}""", budget.Token);
        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(root.Path, budget.Token,
            SeedApprovals(root.Path, approvals.Path));
        // An explicit provider must also work before its cold worker's first registry frame.
        if (!declared) Register(harness.Manager, Path.GetFileName(root.Path));
        var acquiring = harness.Manager.GetSystemPromptsAsync(budget.Token);
        if (!declared)
        {
            Assert.Empty(await acquiring.WaitAsync(budget.Token));
            return;
        }
        var invoke = await ReadInvokeAsync(harness, budget.Token);
        var request = invoke.Payload?.GetProperty("request") ?? throw new InvalidOperationException("Missing hook context.");
        Assert.Equal("workspaceRoot", Assert.Single(request.EnumerateObject()).Name);
        Reply(harness.Manager, invoke, """{"value":[{"name":"style","content":"generated"}]}""");
        var captured = await acquiring.WaitAsync(budget.Token);
        Assert.Equal("generated", Assert.Single(captured).Content);

        var next = harness.Manager.GetSystemPromptsAsync(budget.Token);
        invoke = await ReadInvokeAsync(harness, budget.Token);
        Reply(harness.Manager, invoke, """{"code":"expected_failure","message":"failed"}""", error: true);
        Assert.Empty(await next.WaitAsync(budget.Token));
        Assert.Equal("generated", Assert.Single(captured).Content);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHookRacingRevocationOrCancellationCannotPublishItsResult(bool cancel)
    {
        RequireSimulatedHost();
        using var budget = Budget(TestContext.Current.CancellationToken);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        await using var root = TempArea.Create("system-prompt-race");
        await using var approvals = TempArea.Create("system-prompt-approvals");
        await WritePluginAsync(root.Path,
            """{"entrypoints":{"worker":{"main":["./mod.ts"]}},"extensions":{"SystemPrompt":[{"provider":"main"}]}}""", budget.Token);
        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(root.Path, budget.Token,
            SeedApprovals(root.Path, approvals.Path));
        var id = Path.GetFileName(root.Path);
        Register(harness.Manager, id);
        var acquiring = harness.Manager.GetSystemPromptsAsync(caller.Token);
        var invoke = await ReadInvokeAsync(harness, budget.Token);
        if (cancel)
        {
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquiring);
        }
        else
        {
            await harness.Manager.RevokeAsync(id, budget.Token);
            Reply(harness.Manager, invoke, """{"value":[{"name":"stale","content":"must not publish"}]}""");
            Assert.Empty(await acquiring.WaitAsync(budget.Token));
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task DisposedWaitResourcesAreUnavailableWithoutLatchingFailureOrHidingCancellation()
    {
        RequireSimulatedHost();
        using var budget = Budget(TestContext.Current.CancellationToken);
        await using var root = TempArea.Create("system-prompt-disposed");
        await using var approvals = TempArea.Create("system-prompt-approvals");
        await WritePluginAsync(root.Path,
            """{"extensions":{"SystemPrompt":[{"name":"one","content":"active"}]}}""", budget.Token);
        var clock = new DisposedTimerTimeProvider();
        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(root.Path, budget.Token,
            SeedApprovals(root.Path, approvals.Path), clock);
        clock.ThrowOnTimerCreation = true;
        try
        {
            Assert.Empty(await harness.Manager.GetSystemPromptsAsync(budget.Token));
        }
        finally { clock.ThrowOnTimerCreation = false; }
        Assert.Equal("active", Assert.Single(await harness.Manager.GetSystemPromptsAsync(budget.Token)).Content);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Manager.GetSystemPromptsAsync(canceled.Token));
    }

    [Fact(Timeout = 60_000)]
    public async Task AllWorkerProvidersShareOneCollectionBudget()
    {
        RequireSimulatedHost();
        using var budget = Budget(TestContext.Current.CancellationToken);
        await using var root = TempArea.Create("system-prompt-budget");
        await using var approvals = TempArea.Create("system-prompt-approvals");
        await WritePluginAsync(root.Path,
            """{"entrypoints":{"worker":{"main":["./mod.ts"],"second":["./mod.ts"]}},"extensions":{"SystemPrompt":[{"provider":"main"},{"provider":"second"}]}}""", budget.Token);
        var clock = new FakeTimeProvider();
        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(root.Path, budget.Token,
            SeedApprovals(root.Path, approvals.Path), clock);
        Register(harness.Manager, Path.GetFileName(root.Path), "main", "second");
        var acquiring = harness.Manager.GetSystemPromptsAsync(budget.Token);
        var first = await ReadInvokeAsync(harness, budget.Token);
        clock.Advance(TimeSpan.FromSeconds(3));
        Reply(harness.Manager, first, """{"value":[{"name":"first","content":"completed"}]}""");
        _ = await ReadInvokeAsync(harness, budget.Token);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("completed", Assert.Single(await acquiring.WaitAsync(budget.Token)).Content);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[{}]")]
    [InlineData("[{\"name\":\"bad name\",\"content\":\"text\"}]")]
    [InlineData("[{\"name\":\"a\",\"content\":\"x\"},{\"name\":\"a\",\"content\":\"y\"}]")]
    public void MalformedInstructionSetsAreRejectedAtomically(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(SystemPromptContributionKind.TryRead(document.RootElement, "p", out var entries, out var error));
        Assert.Empty(entries);
        Assert.NotNull(error);
    }

    [Fact]
    public void AdditiveWireFieldsAreToleratedAndInstructionTextRemainsLiteral()
    {
        var context = JsonSerializer.Deserialize("""{"workspaceRoot":"/work","future":true}""",
            ReplControlJsonContext.Default.SystemPromptInvokePayload);
        Assert.Equal("/work", context?.WorkspaceRoot);
        using var document = JsonDocument.Parse("""[{"name":"style","content":"${env.SECRET}","future":true}]""");
        Assert.True(SystemPromptContributionKind.TryRead(document.RootElement, "p", out var entries, out var error), error);
        Assert.Equal("${env.SECRET}", Assert.Single(entries).Content);
    }

    [Fact]
    public void OversizedInstructionsAreRejectedAndPromptChangesAlterTheApprovalFingerprint()
    {
        var payload = JsonSerializer.SerializeToElement(new[] { new { name = "large", content = new string('x', 16_385) } });
        Assert.False(SystemPromptContributionKind.TryRead(payload, "p", out var entries, out _));
        Assert.Empty(entries);
        using var root = TempArea.Create("system-prompt-fingerprint");
        File.WriteAllText(Path.Combine(root.Path, "deno.json"), """{"name":"@test/prompts","version":"0.1.0"}""");
        var manifest = Path.Combine(root.Path, "maieutics.json");
        File.WriteAllText(manifest, """{"extensions":{"systemprompt":[{"name":"style","content":"first"}]}}""");
        Assert.True(PluginManifest.TryLoad(root.Path, out var first, out var failure), failure);
        File.WriteAllText(manifest, """{"extensions":{"SystemPrompt":[{"name":"style","content":"second"}]}}""");
        Assert.True(PluginManifest.TryLoad(root.Path, out var second, out failure), failure);
        Assert.NotEqual(PluginDeclarationFingerprint.Compute(first), PluginDeclarationFingerprint.Compute(second));
        Assert.False(PluginDeclarationFingerprint.IsApprovalExempt(first));
    }

    private static CancellationTokenSource Budget(CancellationToken token)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        return budget;
    }

    private static string SeedApprovals(string root, string area)
    {
        var path = Path.Combine(area, "approvals.json");
        File.Move(PluginApprovalSeeds.SeedLocalPlugins(root), path);
        return path;
    }

    private static void RequireSimulatedHost()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("The simulated host uses the Unix fake-Deno harness.");
    }

    private sealed class DisposedTimerTimeProvider : TimeProvider
    {
        internal bool ThrowOnTimerCreation { get; set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (ThrowOnTimerCreation) throw new ObjectDisposedException("generation wait source");
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    private static async Task WritePluginAsync(string root, string manifest, CancellationToken token)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "deno.json"), """{"name":"@test/prompts","version":"0.1.0"}""", token);
        await File.WriteAllTextAsync(Path.Combine(root, "maieutics.json"), manifest, token);
        await File.WriteAllTextAsync(Path.Combine(root, "mod.ts"), "export const value = 1;", token);
    }

    private static void Register(PluginHostManager manager, string id, params string[] exports) => manager.HandleHostMessage(
        JsonSerializer.Serialize(new ReplEnvelope(1, ReplMessageType.ExtensionRegistry, null,
            JsonSerializer.SerializeToElement(new { plugins = (exports.Length == 0 ? ["main"] : exports)
                .Select(export => new { pluginId = id, exportName = export, extensionPoints = new[] { "SystemPrompt" } }).ToArray() })),
            ReplControlJsonContext.Default.ReplEnvelope));

    private static async Task<ReplEnvelope> ReadInvokeAsync(PluginHostInvokeTests.HostHarness harness, CancellationToken token)
    {
        var json = await harness.Host.ReadSentAsync(token);
        var envelope = JsonSerializer.Deserialize(json, ReplControlJsonContext.Default.ReplEnvelope) ??
            throw new InvalidOperationException("Missing host invoke envelope.");
        Assert.Equal(ReplMessageType.HostInvoke, envelope.Type);
        Assert.Equal("SystemPrompt", envelope.Payload?.GetProperty("extensionPoint").GetString());
        return envelope;
    }

    private static void Reply(PluginHostManager manager, ReplEnvelope invoke, string json, bool error = false)
    {
        using var payload = JsonDocument.Parse(json);
        manager.HandleHostMessage(JsonSerializer.Serialize(new ReplEnvelope(1,
            error ? ReplMessageType.HostInvokeError : ReplMessageType.HostInvokeResult, invoke.CorrelationId, payload.RootElement),
            ReplControlJsonContext.Default.ReplEnvelope));
    }
}
