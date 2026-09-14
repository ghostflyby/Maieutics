using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Maieutics.Providers.OpenAI;

namespace Maieutics.Product.Tests;

public sealed class ApplyPatchWireTests
{
    private const string OperationJson =
        "{\"type\":\"create_file\",\"path\":\"new.txt\",\"diff\":\"+line one\\n+line two\\n\"}";

    private static JsonObject Parse(string json)
    {
        return JsonNode.Parse(json)!.AsObject();
    }

    [Fact]
    public void RequestRewriteSwapsTheFunctionDefinitionForTheBuiltinTool()
    {
        var registry = new ApplyPatchCallRegistry();
        var body = Parse(
            "{\"model\":\"m\",\"tools\":[" +
            "{\"type\":\"function\",\"name\":\"apply_patch\"}," +
            "{\"type\":\"function\",\"name\":\"read_text\"}]," +
            "\"input\":[{\"type\":\"message\",\"role\":\"user\"}]}");

        ApplyPatchWire.RewriteRequest(body, registry);

        var tools = body["tools"]!.AsArray();
        tools[0]!.AsObject()["type"]!.GetValue<string>().Should().Be("apply_patch");
        tools[1]!.AsObject()["name"]!.GetValue<string>().Should().Be("read_text");
    }

    [Fact]
    public void RequestRewriteReplaysRecordedShapesAndOutputs()
    {
        var registry = new ApplyPatchCallRegistry();
        registry.Record("call-builtin", ApplyPatchWireShape.Builtin);
        registry.Record("call-custom", ApplyPatchWireShape.Custom);
        registry.Record("call-unknown", ApplyPatchWireShape.Builtin);
        var v4aA = "*** Begin Patch\n*** Add File: a.txt\n+x\n*** End Patch\n";
        var v4aB = "*** Begin Patch\n*** Add File: b.txt\n+y\n*** End Patch\n";
        JsonObject Call(string callId, string patch)
        {
            return new JsonObject
            {
                ["type"] = "function_call",
                ["call_id"] = callId,
                ["name"] = ApplyPatchWire.ToolName,
                ["arguments"] = JsonSerializer.Serialize(new { patch })
            };
        }

        JsonObject Output(string callId, string output)
        {
            return new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = callId,
                ["output"] = output
            };
        }

        var body = new JsonObject
        {
            ["input"] = new JsonArray
            {
                Call("call-builtin", v4aA),
                Output("call-builtin", JsonSerializer.Serialize(new { status = "ok" })),
                Call("call-custom", v4aB),
                Output("call-custom", JsonSerializer.Serialize(new { status = "error", code = "x", message = "boom" })),
                Call("call-never-seen", v4aA),
                Output("call-other-tool", "ok")
            }
        };

        ApplyPatchWire.RewriteRequest(body, registry);

        var input = body["input"]!.AsArray();
        input[0]!.AsObject()["type"]!.GetValue<string>().Should().Be("apply_patch_call");
        input[1]!.AsObject()["type"]!.GetValue<string>().Should().Be("apply_patch_call_output");
        input[1]!.AsObject()["status"]!.GetValue<string>().Should().Be("completed");
        input[2]!.AsObject()["type"]!.GetValue<string>().Should().Be("custom_tool_call");
        input[3]!.AsObject()["type"]!.GetValue<string>().Should().Be("custom_tool_call_output");
        input[3]!.AsObject()["output"]!.GetValue<string>().Should().Contain("boom");

        // Unrecorded history defaults to the freeform custom shape, which every
        // Responses deployment accepts, and unrelated outputs stay untouched.
        input[4]!.AsObject()["type"]!.GetValue<string>().Should().Be("custom_tool_call");
        input[5]!.AsObject()["type"]!.GetValue<string>().Should().Be("function_call_output");
    }

    [Fact]
    public void ResponseRewriteFoldsApplyPatchCallsIntoFunctionCalls()
    {
        var registry = new ApplyPatchCallRegistry();
        var body = Parse(
            "{\"output\":[" +
            "{\"type\":\"apply_patch_call\",\"id\":\"apc-1\",\"call_id\":\"call-apc\",\"status\":\"completed\",\"operation\":"
            + OperationJson + "}," +
            "{\"type\":\"message\",\"role\":\"assistant\"}]}");

        ApplyPatchWire.RewriteResponse(body, registry);

        var item = body["output"]![0]!.AsObject();
        item["type"]!.GetValue<string>().Should().Be("function_call");
        item["name"]!.GetValue<string>().Should().Be("apply_patch");
        var arguments = item["arguments"]!.GetValue<string>();
        arguments.Should().Contain("new.txt").And.Contain("Begin Patch");
        registry.Resolve("call-apc").Should().Be(ApplyPatchWireShape.Builtin);
    }

    [Fact]
    public void StreamEventsRewriteCallItemsSuppressDeltasAndPassTheRestThrough()
    {
        var registry = new ApplyPatchCallRegistry();

        var delta = Parse("{\"type\":\"response.custom_tool_call_input.delta\",\"delta\":\"more\"}");
        ApplyPatchWire.RewriteStreamEvent(delta, registry).Should().BeEmpty();

        var done = Parse(
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{" +
            "\"type\":\"apply_patch_call\",\"id\":\"apc-1\",\"call_id\":\"call-apc\",\"status\":\"completed\",\"operation\":"
            + OperationJson + "}}");
        // The done event only converts; argument events are synthesized when
        // the item is added, mirroring real function-call streaming.
        var rewritten = ApplyPatchWire.RewriteStreamEvent(done, registry);
        rewritten.Should().HaveCount(1);
        rewritten[0]!.AsObject()["item"]!.AsObject()["type"]!.GetValue<string>().Should().Be("function_call");
        rewritten[0]!.AsObject()["item"]!.AsObject()["arguments"]!.GetValue<string>().Should().Contain("new.txt");

        var added = Parse(
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{" +
            "\"type\":\"apply_patch_call\",\"id\":\"apc-1\",\"call_id\":\"call-apc\",\"status\":\"in_progress\",\"operation\":"
            + OperationJson + "}}");
        var addedEvents = ApplyPatchWire.RewriteStreamEvent(added, registry);
        addedEvents.Should().HaveCount(3);
        addedEvents[1]!.AsObject()["type"]!.GetValue<string>().Should().Be("response.function_call_arguments.delta");
        addedEvents[2]!.AsObject()["type"]!.GetValue<string>().Should().Be("response.function_call_arguments.done");

        var untouched = Parse("{\"type\":\"response.output_text.delta\",\"delta\":\"hi\"}");
        ApplyPatchWire.RewriteStreamEvent(untouched, registry).Should().HaveCount(1);
    }

    [Fact]
    public void RewritesAreIdempotentAcrossRetries()
    {
        var registry = new ApplyPatchCallRegistry();
        var body = Parse("{\"tools\":[{\"type\":\"function\",\"name\":\"apply_patch\"}],\"input\":[]}");
        var first = body.ToJsonString();

        ApplyPatchWire.RewriteRequest(body, registry);
        var once = body.ToJsonString();
        ApplyPatchWire.RewriteRequest(body, registry);

        once.Should().NotBe(first);
        body.ToJsonString().Should().Be(once);
    }
}
