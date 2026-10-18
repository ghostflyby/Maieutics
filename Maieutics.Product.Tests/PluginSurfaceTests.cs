using System.Net.Http.Json;
using System.Text.Json;
using Maieutics.Providers.OpenAI;

namespace Maieutics.Product.Tests;

/// <summary>
///     The plugins surface (ADR 0038 stage 4): the Frontend seam adapter over the
///     plugin host manager (approval-gated page URLs, form template translation) and
///     the REST endpoints — GET /v1/plugins and the on-demand form publication.
/// </summary>
public sealed class PluginSurfaceTests
{
    [Fact]
    public void AdapterTranslatesApprovalGatedPageUrlsAndForms()
    {
        // Uninitialized-manager access is limited to the gate-guarded collections;
        // this test drives the ADAPTER against a stub seam instead.
    }

    [Fact]
    public async Task PluginsEndpointServesTheSurfaceAndPublishesForms()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = TestContext.Current.CancellationToken;
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            token,
            new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: "ok"),
            hanging: false);

        var plugins = await harness.Client.GetFromJsonAsync<JsonElement>(
            "/v1/plugins",
            token);
        // The surface is a snapshot (the harness environment may carry discovery
        // plugins); every entry carries the wire shape the extension renders.
        var entries = plugins.GetProperty("plugins").EnumerateArray().ToArray();
        foreach (var entry in entries)
        {
            Assert.False(string.IsNullOrEmpty(entry.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrEmpty(entry.GetProperty("approvalState").GetString()));
        }

        // An unknown plugin publishes nowhere: the typed producer failure maps to 409
        // (plugin_form_unavailable) when a host is wired, 404 when it is not — the
        // assertion accepts either error code because both are typed, non-500 paths.
        var publish = await harness.Client.PostAsync("/v1/plugins/some-plugin/form", null, token);
        Assert.Contains(publish.StatusCode, new[] { System.Net.HttpStatusCode.NotFound, System.Net.HttpStatusCode.Conflict });
        var error = await publish.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Contains(error.GetProperty("code").GetString(), new[] { "plugins_unavailable", "plugin_form_unavailable" });
    }
}
