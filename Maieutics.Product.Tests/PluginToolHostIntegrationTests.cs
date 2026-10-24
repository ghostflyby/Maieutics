using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Control;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>
///     The end-to-end kernel path for a plugin tool through the simulated host socket: a registry
///     frame carrying a <c>Tool</c> export drives a describe request, the answer becomes a
///     provider-visible function in the run-profile snapshot, and a call routes back to the same
///     export. This exercises the real manager wiring (gate, approval, describe coordinator,
///     snapshot) without spawning Deno.
/// </summary>
public sealed class PluginToolHostIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact(Timeout = 60_000)]
    public async Task ARegisteredToolExportBecomesASnapshotFunction()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket Kestrel harness.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(deadline.Token);

        harness.Manager.HandleHostMessage(ToolRegistryFrame("plugin-1", "./echo"));
        // Capture after the frame: the coordinator starts its single-flight drain inside
        // the registry update, so "idle" now refers to that pass instead of the already
        // completed empty state.
        var idle = harness.Manager.PluginToolsIdle;
        await ReplyToDescribeAsync(harness, deadline.Token);
        await idle.WaitAsync(Deadline, deadline.Token);

        var function = Assert.Single(harness.Manager.SnapshotPluginTools());
        Assert.Equal("echo", function.Name);
        Assert.Equal("Echo the text.", function.Description);
        Assert.Equal("object", function.JsonSchema.GetProperty("type").GetString());
    }

    [Fact(Timeout = 60_000)]
    public async Task RemovingTheExportDropsTheFunction()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket Kestrel harness.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(deadline.Token);

        harness.Manager.HandleHostMessage(ToolRegistryFrame("plugin-1", "./echo"));
        var idle = harness.Manager.PluginToolsIdle;
        await ReplyToDescribeAsync(harness, deadline.Token);
        await idle.WaitAsync(Deadline, deadline.Token);
        Assert.Single(harness.Manager.SnapshotPluginTools());

        harness.Manager.HandleHostMessage(EmptyRegistryFrame());
        Assert.Empty(harness.Manager.SnapshotPluginTools());
    }

    [Fact(Timeout = 60_000)]
    public async Task ACallSendsThePhaseTaggedRequestAndReturnsTheValue()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The simulated host attaches over a Unix-socket Kestrel harness.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        await using var harness = await PluginHostInvokeTests.CreateHarnessAsync(deadline.Token);

        harness.Manager.HandleHostMessage(ToolRegistryFrame("plugin-1", "./echo"));
        var idle = harness.Manager.PluginToolsIdle;
        await ReplyToDescribeAsync(harness, deadline.Token);
        await idle.WaitAsync(Deadline, deadline.Token);

        var function = Assert.Single(harness.Manager.SnapshotPluginTools());
        var invocation = new AIFunctionArguments(new Dictionary<string, object?> { ["text"] = "hello" })
        {
            Context = new Dictionary<object, object?>
            {
                [typeof(AgentToolContext)] = new AgentToolContext(
                    AgentSessionId.Create(),
                    AgentRunId.Create(),
                    AgentToolCallId.Create(),
                    static (_, _) => ValueTask.CompletedTask),
            },
        };

        var call = function.InvokeAsync(invocation, deadline.Token).AsTask();
        var envelope = EnvelopeOf(await harness.Host.ReadSentAsync(deadline.Token));
        Assert.Equal(ReplMessageType.HostInvoke, envelope.Type);
        var payload = JsonSerializer.Deserialize(
            envelope.Payload?.GetRawText() ?? string.Empty,
            ReplControlJsonContext.Default.HostInvokePayload)!;
        Assert.Equal(ReplExtensionPointName.Tool, payload.ExtensionPoint);
        Assert.Equal("call", payload.Request?.GetProperty("phase").GetString());
        Assert.Equal("hello", payload.Request?.GetProperty("arguments").GetProperty("text").GetString());

        harness.Manager.HandleHostMessage(
            "{\"version\":1,\"type\":\"host.invokeResult\",\"correlationId\":\"" + envelope.CorrelationId +
            "\",\"payload\":{\"value\":{\"outcome\":\"ok\",\"value\":{\"text\":\"hello\"}}}}");

        var result = Assert.IsType<JsonElement>(await call);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        // The bridge returns the raw `value`; AgentSession owns the envelope. Asserting the
        // unwrapped shape pins that contract (a pre-built envelope would nest a status here).
        Assert.False(result.TryGetProperty("status", out _));
        Assert.Equal("hello", result.GetProperty("text").GetString());
    }

    /// <summary>Answers the pending describe invoke the registry frame scheduled. The manager's
    /// describe pass runs on its own drain, so the request is read from the fake socket.</summary>
    private static async Task ReplyToDescribeAsync(
        PluginHostInvokeTests.HostHarness harness,
        CancellationToken cancellationToken)
    {
        var envelope = EnvelopeOf(await harness.Host.ReadSentAsync(cancellationToken));
        Assert.Equal(ReplMessageType.HostInvoke, envelope.Type);
        var payload = JsonSerializer.Deserialize(
            envelope.Payload?.GetRawText() ?? string.Empty,
            ReplControlJsonContext.Default.HostInvokePayload)!;
        Assert.Equal(ReplExtensionPointName.Tool, payload.ExtensionPoint);
        Assert.Equal("describe", payload.Request?.GetProperty("phase").GetString());

        harness.Manager.HandleHostMessage(
            "{\"version\":1,\"type\":\"host.invokeResult\",\"correlationId\":\"" + envelope.CorrelationId +
            "\",\"payload\":{\"value\":{\"name\":\"echo\",\"description\":\"Echo the text.\"," +
            "\"inputSchema\":{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}," +
            "\"required\":[\"text\"]}}}}");
    }

    private static string ToolRegistryFrame(string pluginId, string exportName)
    {
        return RegistryFrameWithExtensionPoints(pluginId, exportName, ReplExtensionPointName.Tool);
    }

    private static string EmptyRegistryFrame()
    {
        var payload = JsonSerializer.SerializeToElement(new { plugins = Array.Empty<object>() });
        return JsonSerializer.Serialize(
            new ReplEnvelope(
                1,
                ReplMessageType.ExtensionRegistry,
                Guid.NewGuid().ToString("N"),
                payload),
            ReplControlJsonContext.Default.ReplEnvelope);
    }

    private static string RegistryFrameWithExtensionPoints(
        string pluginId,
        string exportName,
        params string[] extensionPoints)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            plugins = new[]
            {
                new
                {
                    pluginId,
                    exportName,
                    extensionPoints,
                },
            },
        });
        return JsonSerializer.Serialize(
            new ReplEnvelope(
                1,
                ReplMessageType.ExtensionRegistry,
                Guid.NewGuid().ToString("N"),
                payload),
            ReplControlJsonContext.Default.ReplEnvelope);
    }

    private static ReplEnvelope EnvelopeOf(string json)
    {
        return JsonSerializer.Deserialize(json, ReplControlJsonContext.Default.ReplEnvelope)!;
    }
}
