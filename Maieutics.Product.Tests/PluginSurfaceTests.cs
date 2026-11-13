using System.Net.Http.Json;
using System.Text.Json;
using Maieutics.Control;

namespace Maieutics.Product.Tests;

/// <summary>
///     The plugins surface (ADR 0038 stage 4): the Frontend seam adapter over the
///     plugin host manager (approval-gated page URLs, form template translation) and
///     the REST endpoints — GET /v1/plugins and the on-demand form publication.
/// </summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class PluginSurfaceTests
{
    [Fact(Timeout = 60_000)]
    public async Task PluginPageUrlsUseOnlyLiveMountScopedCredentials()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket Kestrel harness.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        var root = Path.Combine(Path.GetTempPath(), "mc-http-surface-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "deno.json"),
            """{"name":"@maieutics/http-probe","exports":{"./main":"./main.ts"}}""");
        File.WriteAllText(Path.Combine(root, "maieutics.json"),
            """{"entrypoints":{"worker":{"main":["./main.ts"]}}}""");
        File.WriteAllText(Path.Combine(root, "main.ts"), "export const marker = 1;");
        var approvals = PluginApprovalSeeds.SeedLocalPlugins(root);
        try
        {
            await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(root, deadline.Token, approvals);
            var id = Assert.Single(harness.Manager.ListPluginApprovals()).PluginId;
            var legacy = JsonSerializer.Deserialize(
                $$"""{"hostname":"127.0.0.1","port":1234,"token":"host-wide","mounts":[{"pluginId":"{{id}}","specifier":"probe/main","live":true}]}""",
                ReplControlJsonContext.Default.PluginHttpGatewayPayload);
            Assert.NotNull(legacy);
            Assert.Null(Assert.Single(harness.Manager.GetPluginSurfaces(legacy)).PageUrl);
            var scoped = legacy with { Mounts = [new PluginHttpMountPayload(id, "probe/main", true, "scoped-token")] };
            var frame = new ReplEnvelope(1, ReplMessageType.ExtensionRegistry, null,
                JsonSerializer.SerializeToElement(new ExtensionRegistryPayload([], HttpGateway: scoped),
                    ReplControlJsonContext.Default.ExtensionRegistryPayload));
            harness.Manager.HandleHostMessage(JsonSerializer.Serialize(frame, ReplControlJsonContext.Default.ReplEnvelope));
            Assert.Equal("scoped-token", Assert.Single(Assert.IsType<PluginHttpGatewayPayload>(harness.Manager.GetHttpGateway()).Mounts).AccessToken);
            Assert.Equal($"http://127.0.0.1:1234/scoped-token/plugins/{id}/",
                Assert.Single(new FrontendPluginSurfaceAdapter(harness.Manager).GetPluginSurfaces()).PageUrl);
            Assert.Equal($"http://127.0.0.1:1234/scoped-token/plugins/{id}/",
                Assert.Single(harness.Manager.GetPluginSurfaces(scoped)).PageUrl);
            Assert.Null(Assert.Single(harness.Manager.GetPluginSurfaces(scoped with
            {
                Mounts = [new PluginHttpMountPayload(id, "probe/main", false, "scoped-token")],
            })).PageUrl);
            Assert.Null(Assert.Single(harness.Manager.GetPluginSurfaces(null)).PageUrl);
        }
        finally
        {
            TempArea.DeleteTree(root);
            File.Delete(approvals);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task PluginsEndpointServesTheSurfaceAndPublishesForms()
    {
        var token = TestContext.Current.CancellationToken;
        await using var harness = await FrontendApiIntegrationTests.FrontendHarness.StartAsync(
            token,
            new GatedModelProvider(ModelApiFlavor.ChatCompletions, ProviderGateMode.Scripted, answer: "ok"),
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
