using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maieutics.DenoRepl;
using Maieutics.Frontend;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Maieutics.Product.Tests.FrontendApiIntegrationTests;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendOutputRendererTests
{
    [Fact(Timeout = 60_000)]
    public async Task RealOfflineRendererServesSnapshotsAndStopsWithItsHost()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false);
        Process child;
        var directory = harness.OutputRenderer.ModuleDirectory;
        try
        {
            Assert.Null(harness.OutputRenderer.ProcessId);
            using var capabilities = await harness.Client.GetAsync("/v1/agent/capabilities", budget.Token);
            using var advertised = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync(budget.Token));
            Assert.Equal(1, advertised.RootElement.GetProperty("rendering").GetProperty("version").GetInt32());
            var pending = await RenderAsync(harness, "[Result][r]\n\nLater prose", budget.Token);
            Assert.DoesNotContain("<a ", pending.Html);
            var source = "# Demo\n\n[[toc]]\n\n## Contents {#contents}\n\n> [!NOTE] <img src=x>\n> Alert\n\n::: tip **Advice** [jump](#contents)\n**SSR**\n:::\n\n```ts\nconst answer = 42;\n```\n\n" +
                         "::: code-group\n```ts:line-numbers=7 {2} [report.ts]\n// [!code highlight]\nconst total = 42;\n```\n\n```json [report.json]\n{}\n```\n:::\n\n" +
                         "[Result][r]\n\nLater prose\n\n[r]: https://example.test/result\n\n<script>run()</script>\n\n{{ report.total }}";
            var output = await RenderAsync(harness, source, budget.Token);
            Assert.Contains("id=\"demo-demo\"", output.Html);
            Assert.Contains("custom-block tip", output.Html);
            Assert.Contains("href=\"#demo-contents\">Contents</a>", output.Html);
            Assert.Contains("href=\"#demo-contents\">jump</a>", output.Html);
            Assert.Contains("<strong>Advice</strong>", output.Html);
            Assert.Contains("custom-block note github-alert", output.Html);
            Assert.Contains("&lt;img src=x&gt;", output.Html);
            Assert.DoesNotContain("<img", output.Html);
            Assert.Contains("shiki", output.Html);
            Assert.Contains("vp-code-group", output.Html);
            Assert.Contains("report.ts", output.Html);
            Assert.Contains("data-line=\"7\"", output.Html);
            Assert.Contains("highlighted", output.Html);
            Assert.Contains("href=\"https://example.test/result\"", output.Html);
            Assert.Contains("Later prose", output.Html);
            Assert.Contains("&lt;script&gt;", output.Html);
            Assert.Contains("{{ report.total }}", output.Html);
            Assert.Contains("--vscode", output.Css);
            Assert.Equal("demo", output.DocumentId);
            var math = await RenderAsync(harness, "$x^2$\n\n~~~py\nprint(42)\n~~~\n\n~~~md\nunclosed", budget.Token);
            Assert.Contains("<svg ", math.Html);
            Assert.Contains("code-copy", math.Html);
            Assert.Contains("--shiki-light", math.Html);
            var diagnostic = Assert.Single(Assert.IsType<FrontendRenderDiagnostic[]>(math.Diagnostics));
            Assert.Equal("unclosed_code_fence", diagnostic.Code);
            Assert.Equal(7, diagnostic.Line);
            using var invalid = await harness.Client.PostAsJsonAsync("/v1/render/assistant",
                new FrontendRenderMarkdown(1, "demo", "# Heading\n\n<script setup>const broken = ;</script>\n\n<p>Template</p>"),
                FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var failure = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync(budget.Token));
            Assert.Equal(3, failure.RootElement.GetProperty("diagnostics")[0].GetProperty("line").GetInt32());
            Assert.Equal(0, harness.SessionService.DescribeSession().Turns);
            var pid = Assert.IsType<int>(harness.OutputRenderer.ProcessId);
            child = Process.GetProcessById(pid);
            Assert.False(child.HasExited);
        }
        finally
        {
            await harness.DisposeAsync();
        }
        try
        {
            Assert.True(child.HasExited, "Host disposal must stop its renderer child before returning.");
            Assert.False(Directory.Exists(directory), "Host disposal must remove its materialized rendering bundle.");
        }
        finally
        {
            child.Dispose();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task RenderingRejectsUnauthenticatedInvalidAndForgedReadinessRequests()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        await using var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false);
        using var anonymous = new HttpClient();
        anonymous.BaseAddress = new Uri(harness.Url);
        using var denied = await anonymous.PostAsJsonAsync("/v1/render/markdown", new { }, budget.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var invalid = await harness.Client.PostAsJsonAsync("/v1/render/markdown",
            new FrontendRenderMarkdown(1, "invalid/id", "text"), FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var forged = await harness.Client.PostAsJsonAsync("/v1/render/ready",
            new FrontendRendererReady(1, 1234, "not-the-child"), FrontendJsonContext.Default.FrontendRendererReady, budget.Token);
        Assert.Equal(HttpStatusCode.Conflict, forged.StatusCode);
        Assert.Null(harness.OutputRenderer.ProcessId);
    }

    [Fact(Timeout = 60_000)]
    public async Task TemplateSnapshotsResolveBindingsWithoutExecutingMarkdownOrChangingHistory()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        await using var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false);
        const string source = "Total {{ report.total.toFixed(2) }}\n\nLater prose\n\n{{ empty }}";
        var template = new FrontendTemplateContext(1,
            [new("report", "pending"), new("empty", "ready", Undefined: true)]);
        var request = new FrontendRenderMarkdown(1, "template", source, template);
        using var pendingResponse = await harness.Client.PostAsJsonAsync("/v1/render/template", request,
            FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        Assert.Equal(HttpStatusCode.OK, pendingResponse.StatusCode);
        var pending = await pendingResponse.Content.ReadFromJsonAsync(
            FrontendJsonContext.Default.FrontendRenderedMarkdown, budget.Token);
        Assert.NotNull(pending);
        Assert.Contains("data-template-state=\"pending\"", pending.Html);
        Assert.Contains("Later prose", pending.Html);
        Assert.NotNull(pending.Expressions);
        Assert.Equal(["pending", "ready"], pending.Expressions.Select(static item => item.State));
        using var total = JsonDocument.Parse("{\"total\":42}");
        var readyRequest = request with { Template = template with { Bindings =
            [new("report", "ready", total.RootElement.Clone()), new("empty", "ready", Undefined: true)] } };
        using var readyResponse = await harness.Client.PostAsJsonAsync("/v1/render/template", readyRequest,
            FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        var ready = await readyResponse.Content.ReadFromJsonAsync(
            FrontendJsonContext.Default.FrontendRenderedMarkdown, budget.Token);
        Assert.NotNull(ready);
        Assert.Contains(">42.00</span>", ready.Html);
        Assert.DoesNotContain("data-template-state=\"pending\"", ready.Html);
        using var passiveResponse = await harness.Client.PostAsJsonAsync("/v1/render/markdown", readyRequest,
            FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        var passive = await passiveResponse.Content.ReadFromJsonAsync(
            FrontendJsonContext.Default.FrontendRenderedMarkdown, budget.Token);
        Assert.NotNull(passive);
        Assert.Contains("{{ report.total.toFixed(2) }}", passive.Html);
        Assert.Null(passive.Expressions);
        using var invalidResponse = await harness.Client.PostAsJsonAsync("/v1/render/template",
            request with { Template = new FrontendTemplateContext(1, [new("report", "ready")]) },
            FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Equal(0, harness.SessionService.DescribeSession().Turns);
    }

    [Fact(Timeout = 60_000)]
    public async Task LiveVueComponentUsesIsolatedSsrAndReturnsMatchingScopedClientCode()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        await using var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false);
        const string source = "<script setup lang=\"ts\">import { ref } from 'vue'; const count = ref<number>(0);</script>\n<template><button class=\"counter\" @click=\"count++\">Count</button><span>{{ count }}</span></template>\n<style scoped>.counter { color:red; }</style>";
        var request = new FrontendRenderMarkdown(1, "counter", source, Vue: new FrontendVueRequest(1, "sfc"));
        using var response = await harness.Client.PostAsJsonAsync("/v1/render/vue", request,
            FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync(FrontendJsonContext.Default.FrontendRenderedMarkdown, budget.Token);
        Assert.NotNull(result);
        Assert.Contains(">0</span>", result.Html);
        Assert.Contains("[data-v-counter]", result.Css);
        Assert.NotNull(result.Vue);
        Assert.Equal("data-v-counter", result.Vue.ScopeId);
        Assert.Contains("component.render", result.Vue.Code);
        using var invalid = await harness.Client.PostAsJsonAsync("/v1/render/vue",
            request with { Source = "<script setup>import x from 'node:fs'</script><template/>" },
            FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(0, harness.SessionService.DescribeSession().Turns);
    }

    [Fact(Timeout = 60_000)]
    public async Task SealedAssistantSelectsVueWithoutChangingPassiveMarkdownOrHistory()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        await using var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false);
        using var capabilities = await harness.Client.GetAsync("/v1/agent/capabilities", budget.Token);
        using var advertised = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync(budget.Token));
        Assert.Equal(1, advertised.RootElement.GetProperty("rendering").GetProperty("assistantVersion").GetInt32());
        const string source = "---\ntitle: Counter\n---\n# Counter\n\n<script setup>import { ref, computed } from 'vue'; const count = ref(0); const isZero = computed(() => count.value === 0);</script>\n\n<button @click=\"count++\">+</button><button :disabled=\"isZero\">−</button><span>{{ count }}</span>\n\n<style scoped>button { color:red; }</style>";
        var request = new FrontendRenderMarkdown(1, "assistant", source);
        using var response = await harness.Client.PostAsJsonAsync("/v1/render/assistant", request,
            FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync(FrontendJsonContext.Default.FrontendRenderedMarkdown, budget.Token);
        Assert.NotNull(result);
        Assert.Contains(">0</span>", result.Html);
        Assert.Contains("disabled", result.Html);
        Assert.DoesNotContain("title: Counter", result.Html);
        Assert.NotNull(result.Vue);
        Assert.Contains("--vp-c-brand-1", result.Css);
        using var passive = await harness.Client.PostAsJsonAsync("/v1/render/markdown", request,
            FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        var plain = await passive.Content.ReadFromJsonAsync(FrontendJsonContext.Default.FrontendRenderedMarkdown, budget.Token);
        Assert.NotNull(plain);
        Assert.Null(plain.Vue);
        Assert.Contains("&lt;script setup&gt;", plain.Html);
        Assert.Equal(0, harness.SessionService.DescribeSession().Turns);
    }

    [Fact(Timeout = 60_000)]
    public async Task MissingExecutableCanBeRetriedWithoutRestartingTheHost()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        var options = new DenoReplOptions { Executable = "maieutics-missing-renderer-executable" };
        await using var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false,
            configureBuilder: builder => builder.Services.AddSingleton(services =>
                ActivatorUtilities.CreateInstance<FrontendOutputRenderer>(services, options)));
        using var first = await harness.Client.PostAsJsonAsync("/v1/render/markdown", new FrontendRenderMarkdown(1, "demo", "Before retry"),
            FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Null(harness.OutputRenderer.ProcessId);
        options.Executable = "deno";
        var recovered = await RenderAsync(harness, "After transient startup failure", budget.Token);
        Assert.Contains("After transient startup failure", recovered.Html);
        Assert.NotNull(harness.OutputRenderer.ProcessId);
    }

    [Fact(Timeout = 60_000)]
    public async Task UnexpectedExitInvalidatesThePortAndRestartsWithFreshReadiness()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        var logger = new RendererLogger();
        await using var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false,
            configureBuilder: builder => builder.Services.AddSingleton<ILogger<FrontendOutputRenderer>>(logger));
        await RenderAsync(harness, "Before exit", budget.Token);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var pid = Assert.IsType<int>(harness.OutputRenderer.ProcessId);
            var observation = Assert.IsAssignableFrom<Task>(harness.OutputRenderer.ProcessCompletion);
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            await observation.WaitAsync(budget.Token);
            Assert.Null(harness.OutputRenderer.ProcessId);
            Assert.Contains(logger.Records, entry => entry.Level == LogLevel.Warning && entry.Text.Contains("exited unexpectedly", StringComparison.Ordinal));
            var recovered = await RenderAsync(harness, $"Recovered generation {attempt}", budget.Token);
            Assert.Contains($"Recovered generation {attempt}", recovered.Html);
            Assert.NotNull(harness.OutputRenderer.ProcessId);
            Assert.NotSame(observation, harness.OutputRenderer.ProcessCompletion);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ExitDuringAnActiveRequestFailsThatRequestAndAllowsTheNextOne()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        await using var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false);
        await RenderAsync(harness, "Ready", budget.Token);
        var pid = Assert.IsType<int>(harness.OutputRenderer.ProcessId);
        var observation = Assert.IsAssignableFrom<Task>(harness.OutputRenderer.ProcessCompletion);
        var pending = harness.OutputRenderer.RenderAsync(new FrontendRenderMarkdown(1, "active",
                "<script setup>while(true){}</script><template/>", Vue: new FrontendVueRequest(1, "sfc")),
            budget.Token, vue: true);
        using var process = Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
        await observation.WaitAsync(budget.Token);
        var failure = await Assert.ThrowsAsync<FrontendFailureException>(() => pending.WaitAsync(budget.Token));
        Assert.Equal("render_unavailable", failure.Code);
        Assert.Contains("After active exit", (await RenderAsync(harness, "After active exit", budget.Token)).Html);
    }

    [Fact(Timeout = 60_000)]
    public async Task CallerCancellationDoesNotRetireAHealthyGeneration()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        await using var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false);
        await RenderAsync(harness, "Ready", budget.Token);
        var pid = Assert.IsType<int>(harness.OutputRenderer.ProcessId);
        var pending = harness.OutputRenderer.RenderAsync(new FrontendRenderMarkdown(1, "cancel",
            "<script setup>while(true){}</script><template/>", Vue: new FrontendVueRequest(1, "sfc")), caller.Token, vue: true);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(budget.Token));
        Assert.Contains("After caller cancellation", (await RenderAsync(harness, "After caller cancellation", budget.Token)).Html);
        Assert.Equal(pid, harness.OutputRenderer.ProcessId);
    }

    [Fact(Timeout = 60_000)]
    public async Task ConcurrentDisposalDrainsActiveAndQueuedRequestsAndNeverRestarts()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        await using var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false);
        await RenderAsync(harness, "Ready", budget.Token);
        var pid = Assert.IsType<int>(harness.OutputRenderer.ProcessId);
        using var process = Process.GetProcessById(pid);
        var request = new FrontendRenderMarkdown(1, "shutdown", "<script setup>while(true){}</script><template/>", Vue: new FrontendVueRequest(1, "sfc"));
        var pending = Enumerable.Range(0, 3).Select(_ => harness.OutputRenderer.RenderAsync(request, budget.Token, vue: true)).ToArray();
        var first = harness.OutputRenderer.DisposeAsync().AsTask();
        var second = harness.OutputRenderer.DisposeAsync().AsTask();
        foreach (var operation in pending)
        {
            var failure = await Assert.ThrowsAsync<FrontendFailureException>(() => operation.WaitAsync(budget.Token));
            Assert.Equal("render_unavailable", failure.Code);
        }
        await Task.WhenAll(first, second).WaitAsync(budget.Token);
        harness.OutputRenderer.BeginShutdown();
        Assert.True(process.HasExited, "Renderer disposal must join the child before returning.");
        Assert.False(Directory.Exists(harness.OutputRenderer.ModuleDirectory));
        var late = await Assert.ThrowsAsync<FrontendFailureException>(() => harness.OutputRenderer.RenderAsync(request, budget.Token, vue: true));
        Assert.Equal("render_unavailable", late.Code);
        Assert.Null(harness.OutputRenderer.ProcessId);
    }

    [Theory(Timeout = 60_000)]
    [InlineData("Deno.exit(73)")]
    [InlineData("self.close()")]
    [InlineData("await Function(\"return import('node:process')\")().then(p => p.exit(73))")]
    [InlineData("Deno.exitCode = 73; throw new Error('worker exit code probe')")]
    public async Task WorkerExitStaysWithinItsIsolateAndKeepsTheRendererUsable(string control)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        await using var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false);
        await RenderAsync(harness, "Before worker control", budget.Token);
        var pid = Assert.IsType<int>(harness.OutputRenderer.ProcessId);
        var completion = Assert.IsAssignableFrom<Task>(harness.OutputRenderer.ProcessCompletion);
        using var result = await harness.Client.PostAsJsonAsync("/v1/render/vue",
            new FrontendRenderMarkdown(1, "control", $"<script setup>{control}</script><template><p>unreachable</p></template>",
                Vue: new FrontendVueRequest(1, "sfc")), FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.False(completion.IsCompleted, "Worker process-control attempts must not terminate the renderer generation.");
        Assert.Equal(pid, harness.OutputRenderer.ProcessId);
        Assert.Contains("After worker control", (await RenderAsync(harness, "After worker control", budget.Token)).Html);
        Assert.Equal(pid, harness.OutputRenderer.ProcessId);
    }

    [Fact(Timeout = 60_000)]
    public async Task WorkerSelfSignalCanKillItsRendererButTheNextRequestRestartsIt()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        await using var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false);
        await RenderAsync(harness, "Before worker self signal", budget.Token);
        Assert.IsType<int>(harness.OutputRenderer.ProcessId);
        var completion = Assert.IsAssignableFrom<Task>(harness.OutputRenderer.ProcessCompletion);
        using var result = await harness.Client.PostAsJsonAsync("/v1/render/vue",
            new FrontendRenderMarkdown(1, "control", "<script setup>Deno.kill(Deno.pid, 'SIGKILL')</script><template/>",
                Vue: new FrontendVueRequest(1, "sfc")), FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        await completion.WaitAsync(budget.Token);
        Assert.Null(harness.OutputRenderer.ProcessId);
        Assert.Contains("After worker self signal", (await RenderAsync(harness, "After worker self signal", budget.Token)).Html);
        Assert.NotNull(harness.OutputRenderer.ProcessId);
        Assert.NotSame(completion, harness.OutputRenderer.ProcessCompletion);
    }

    [Fact(Timeout = 60_000)]
    public async Task ProgressiveAssistantUpdatesCompileWithoutRepeatingServerSetup()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        await using var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false);
        using var capabilities = await harness.Client.GetAsync("/v1/agent/capabilities", budget.Token);
        using var advertised = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync(budget.Token));
        Assert.Equal(1, advertised.RootElement.GetProperty("rendering").GetProperty("progressiveVersion").GetInt32());
        const string source = "<script setup lang=\"ts\">\nimport {ref,computed} from 'vue';\nconst count=ref(0);const doubled=computed(()=>count.value*2);\n</script>\n\n<button @click=\"count++\">{{ count }}</button>";
        using var initialResponse = await harness.Client.PostAsJsonAsync("/v1/render/assistant",
            new FrontendRenderMarkdown(1, "progressive", source, Progressive: true),
            FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var initial = await initialResponse.Content.ReadFromJsonAsync(FrontendJsonContext.Default.FrontendRenderedMarkdown, budget.Token)
            ?? throw new InvalidOperationException("Missing progressive initial snapshot.");
        var firstComponent = Assert.IsType<FrontendVueComponent>(initial.Vue);
        Assert.Equal(64, firstComponent.SetupKey?.Length);
        Assert.Contains(">0</button>", initial.Html);
        Assert.NotEmpty(firstComponent.Code ?? "");
        var updatedSource = source + "\n\n<p>{{ doubled }}</p>";
        using var updateResponse = await harness.Client.PostAsJsonAsync("/v1/render/assistant",
            new FrontendRenderMarkdown(1, "progressive", updatedSource, Progressive: true, CompileOnly: true),
            FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var update = await updateResponse.Content.ReadFromJsonAsync(FrontendJsonContext.Default.FrontendRenderedMarkdown, budget.Token)
            ?? throw new InvalidOperationException("Missing progressive update.");
        var nextComponent = Assert.IsType<FrontendVueComponent>(update.Vue);
        Assert.Equal(firstComponent.SetupKey, nextComponent.SetupKey);
        Assert.Equal("", update.Html);
        Assert.Equal("", nextComponent.Code);
        Assert.Contains("doubled", nextComponent.RenderCode);
        Assert.Equal(updatedSource, update.RenderedSource);
        using var invalid = await harness.Client.PostAsJsonAsync("/v1/render/markdown",
            new FrontendRenderMarkdown(1, "progressive", source, Progressive: true),
            FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(0, harness.SessionService.DescribeSession().Turns);
    }

    private sealed class RendererLogger : ILogger<FrontendOutputRenderer>
    {
        internal ConcurrentQueue<(LogLevel Level, string Text)> Records { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Records.Enqueue((logLevel, formatter(state, exception)));
    }

    private static async Task<FrontendRenderedMarkdown> RenderAsync(
        FrontendHarness harness, string source, CancellationToken cancellationToken)
    {
        using var response = await harness.Client.PostAsJsonAsync("/v1/render/markdown",
            new FrontendRenderMarkdown(1, "demo", source), FrontendJsonContext.Default.FrontendRenderMarkdown, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync(
                   FrontendJsonContext.Default.FrontendRenderedMarkdown, cancellationToken)
               ?? throw new InvalidOperationException("The renderer returned no snapshot.");
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupDenialsAndMissingExecutablesKeepTheFrontendUsableAndLeaveNoChild(bool denyEnvironment)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false,
            configureBuilder: builder =>
            {
                if (!denyEnvironment)
                {
                    // Keep the plugin host's real Deno; fail only the presentation child's launch.
                    builder.Services.AddSingleton(services => ActivatorUtilities.CreateInstance<FrontendOutputRenderer>(
                        services, new DenoReplOptions { Executable = "maieutics-missing-renderer-executable" }));
                }
            },
            transformConfiguration: json =>
            {
                var configuration = JsonNode.Parse(json) ?? throw new InvalidOperationException("Missing test configuration.");
                var product = configuration["Maieutics"] ?? throw new InvalidOperationException("Missing product configuration.");
                if (denyEnvironment)
                    product["Permissions"] = JsonNode.Parse("""{"Env":{"deny":["MAIEUTICS_RENDERER_NONCE"]}}""");
                return configuration.ToJsonString();
            });
        var directory = harness.OutputRenderer.ModuleDirectory;
        try
        {
            using var result = await harness.Client.PostAsJsonAsync("/v1/render/markdown",
                new FrontendRenderMarkdown(1, "demo", "# Expected fallback"), FrontendJsonContext.Default.FrontendRenderMarkdown, budget.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
            using var error = JsonDocument.Parse(await result.Content.ReadAsStringAsync(budget.Token));
            Assert.Equal("render_unavailable", error.RootElement.GetProperty("code").GetString());
            Assert.Null(harness.OutputRenderer.ProcessId);
            using var alive = await harness.Client.GetAsync("/v1/agent/capabilities", budget.Token);
            Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        }
        finally
        {
            await harness.DisposeAsync();
        }
        Assert.False(Directory.Exists(directory), "Failed startup must not retain its materialized bundle.");
    }
}
