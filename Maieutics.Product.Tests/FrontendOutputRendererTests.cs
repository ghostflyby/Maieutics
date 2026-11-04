using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maieutics.Configuration;
using Maieutics.DenoRepl;
using Maieutics.Frontend;
using Microsoft.Extensions.DependencyInjection;
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
        Process? child = null;
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
            Assert.NotNull(child);
            Assert.True(child.HasExited, "Host disposal must stop its renderer child before returning.");
            Assert.False(Directory.Exists(directory), "Host disposal must remove its materialized rendering bundle.");
        }
        finally
        {
            child?.Dispose();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task RenderingRejectsUnauthenticatedInvalidAndForgedReadinessRequests()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        await using var harness = await FrontendHarness.StartAsync(budget.Token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "unused"), false);
        using var anonymous = new HttpClient { BaseAddress = new Uri(harness.Url) };
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
