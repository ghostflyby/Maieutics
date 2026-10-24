using System.Text.Json;
using Maieutics.Control;
using Maieutics.Plugins;

namespace Maieutics.Product.Tests;

/// <summary>
///     The model-callable plugin tool contract: declaration validation, the describe/call
///     phases, result-envelope mapping, and the cancellation identity a call carries. The
///     host and worker round trip lives in the interop suites; these tests pin the kernel-side
///     interpretation so a malformed declaration can never reach the Agent tool registry.
/// </summary>
public sealed class PluginToolProtocolTests
{
    [Fact]
    public void DescribeRequestSerializesThePhase()
    {
        var request = PluginToolProtocol.CreateDescribeRequest();

        Assert.Equal("describe", request.GetProperty("phase").GetString());
        Assert.False(request.TryGetProperty("callId", out _));
    }

    [Fact]
    public void CallRequestCarriesIdentityAndArguments()
    {
        using var document = JsonDocument.Parse("""{"text":"hello"}""");
        var request = PluginToolProtocol.CreateCallRequest(
            "call-1",
            "session-1",
            document.RootElement.Clone());

        Assert.Equal("call", request.GetProperty("phase").GetString());
        Assert.Equal("call-1", request.GetProperty("callId").GetString());
        Assert.Equal("session-1", request.GetProperty("sessionId").GetString());
        Assert.Equal("hello", request.GetProperty("arguments").GetProperty("text").GetString());
    }

    [Fact]
    public void ValidDefinitionBindsEveryField()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "name": "docs_search",
              "description": "Search the index.",
              "inputSchema": { "type": "object", "properties": {} },
              "timeoutMs": 5000
            }
            """);

        var valid = PluginToolProtocol.TryReadDefinition(
            "plugin-1",
            "./search",
            document.RootElement,
            out var definition,
            out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.NotNull(definition);
        Assert.Equal("plugin-1", definition.PluginId);
        Assert.Equal("./search", definition.ExportName);
        Assert.Equal("docs_search", definition.Name);
        Assert.Equal("Search the index.", definition.Description);
        Assert.Equal(TimeSpan.FromSeconds(5), definition.Timeout);
    }

    [Fact]
    public void MissingTimeoutUsesTheDefaultBudget()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "name": "no_timeout",
              "description": "Default budget.",
              "inputSchema": { "type": "object" }
            }
            """);

        var valid = PluginToolProtocol.TryReadDefinition(
            "plugin-1",
            "./main",
            document.RootElement,
            out var definition,
            out _);

        Assert.True(valid);
        Assert.Equal(PluginToolDefinition.DefaultTimeout, definition!.Timeout);
    }

    [Theory]
    [InlineData("""{"name":"has space","description":"d","inputSchema":{"type":"object"}}""")]
    [InlineData("""{"name":"","description":"d","inputSchema":{"type":"object"}}""")]
    [InlineData("""{"name":"ok","description":"   ","inputSchema":{"type":"object"}}""")]
    [InlineData("""{"name":"ok","description":"d","inputSchema":{"type":"string"}}""")]
    [InlineData("""{"name":"ok","description":"d"}""")]
    [InlineData("""{"description":"d","inputSchema":{"type":"object"}}""")]
    public void InvalidDefinitionIsRejected(string declaration)
    {
        using var document = JsonDocument.Parse(declaration);

        var valid = PluginToolProtocol.TryReadDefinition(
            "plugin-1",
            "./main",
            document.RootElement,
            out var definition,
            out var error);

        Assert.False(valid);
        Assert.Null(definition);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ATrailingNewlineInTheNameIsRejectedHereNotAtSessionConstruction()
    {
        // .NET's '$' also matches before a trailing newline, so an unanchored pattern accepts
        // "echo\n" while AgentSession's own validator rejects it. That mismatch would let the
        // definition be cached and then throw out of StartTurnAsync for every later turn —
        // aborting the notebook instead of hiding one tool.
        using var document = JsonDocument.Parse(
            """{"name":"echo\n","description":"d","inputSchema":{"type":"object"}}""");

        var valid = PluginToolProtocol.TryReadDefinition(
            "plugin-1",
            "./main",
            document.RootElement,
            out var definition,
            out var error);

        Assert.False(valid);
        Assert.Null(definition);
        Assert.Contains("name", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    [InlineData(600_001)]
    public void OutOfRangeTimeoutIsRejected(int timeoutMs)
    {
        using var document = JsonDocument.Parse(
            $$"""
              {
                "name": "ok",
                "description": "d",
                "inputSchema": { "type": "object" },
                "timeoutMs": {{timeoutMs}}
              }
              """);

        var valid = PluginToolProtocol.TryReadDefinition(
            "plugin-1",
            "./main",
            document.RootElement,
            out _,
            out var error);

        Assert.False(valid);
        Assert.Contains("timeoutMs", error);
    }

    [Fact]
    public void AnOversizedSchemaIsRejected()
    {
        var padding = new string('x', PluginToolProtocol.MaximumSchemaBytes + 1);
        using var document = JsonDocument.Parse(
            $$"""
              {
                "name": "ok",
                "description": "d",
                "inputSchema": { "type": "object", "description": "{{padding}}" }
              }
              """);

        var valid = PluginToolProtocol.TryReadDefinition(
            "plugin-1",
            "./main",
            document.RootElement,
            out _,
            out var error);

        Assert.False(valid);
        Assert.Contains("bytes", error);
    }

    [Fact]
    public void AnAbsentAnnotationBlockTakesThePessimisticDefaults()
    {
        using var document = JsonDocument.Parse(
            """{"name":"ok","description":"d","inputSchema":{"type":"object"}}""");

        var valid = PluginToolProtocol.TryReadDefinition(
            "plugin-1",
            "./main",
            document.RootElement,
            out var definition,
            out _);

        Assert.True(valid);
        var annotations = definition!.Annotations;
        Assert.Null(annotations.Title);
        Assert.False(annotations.ReadOnly);
        // MCP's direction: assume a tool may destroy, may repeat with effect, may reach out.
        Assert.True(annotations.Destructive);
        Assert.False(annotations.Idempotent);
        Assert.True(annotations.OpenWorld);
    }

    [Fact]
    public void ADeclaredAnnotationBlockIsReadMemberByMember()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "name": "ok",
              "description": "d",
              "inputSchema": { "type": "object" },
              "annotations": {
                "title": "  Echo Text  ",
                "readOnly": true,
                "destructive": false,
                "idempotent": true,
                "openWorld": false
              }
            }
            """);

        var valid = PluginToolProtocol.TryReadDefinition(
            "plugin-1",
            "./main",
            document.RootElement,
            out var definition,
            out _);

        Assert.True(valid);
        var annotations = definition!.Annotations;
        Assert.Equal("Echo Text", annotations.Title);
        Assert.True(annotations.ReadOnly);
        Assert.False(annotations.Destructive);
        Assert.True(annotations.Idempotent);
        Assert.False(annotations.OpenWorld);
    }

    [Fact]
    public void APartialAnnotationBlockKeepsTheDefaultsForAbsentMembers()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "name": "ok",
              "description": "d",
              "inputSchema": { "type": "object" },
              "annotations": { "readOnly": true }
            }
            """);

        var valid = PluginToolProtocol.TryReadDefinition(
            "plugin-1",
            "./main",
            document.RootElement,
            out var definition,
            out _);

        Assert.True(valid);
        var annotations = definition!.Annotations;
        Assert.True(annotations.ReadOnly);
        Assert.True(annotations.Destructive);
        Assert.True(annotations.OpenWorld);
    }

    [Theory]
    [InlineData("""{"readOnly":"yes"}""")]
    [InlineData("""{"destructive":1}""")]
    [InlineData("""{"title":""}""")]
    [InlineData("""{"title":123}""")]
    [InlineData("""[]""")]
    public void AMalformedAnnotationBlockIsRejected(string annotations)
    {
        using var document = JsonDocument.Parse(
            $$"""
              {
                "name": "ok",
                "description": "d",
                "inputSchema": { "type": "object" },
                "annotations": {{annotations}}
              }
              """);

        var valid = PluginToolProtocol.TryReadDefinition(
            "plugin-1",
            "./main",
            document.RootElement,
            out var definition,
            out var error);

        Assert.False(valid);
        Assert.Null(definition);
        Assert.Contains("annotation", error);
    }

    [Fact]
    public void AnOversizedTitleIsRejected()
    {
        var title = new string('t', PluginToolProtocol.MaximumTitleLength + 1);
        using var document = JsonDocument.Parse(
            $$"""
              {
                "name": "ok",
                "description": "d",
                "inputSchema": { "type": "object" },
                "annotations": { "title": "{{title}}" }
              }
              """);

        var valid = PluginToolProtocol.TryReadDefinition(
            "plugin-1",
            "./main",
            document.RootElement,
            out _,
            out var error);

        Assert.False(valid);
        Assert.Contains("title", error);
    }

    [Fact]
    public void CallResponseMapsSuccessToAPlainValue()
    {
        using var document = JsonDocument.Parse("""{"outcome":"ok","value":{"matches":[1,2]}}""");

        var succeeded = PluginToolProtocol.TryReadCallResponse(
            document.RootElement,
            out var result,
            out _,
            out _);

        Assert.True(succeeded);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public void CallResponseMapsRecoverableErrorToATypedFailure()
    {
        using var document = JsonDocument.Parse(
            """{"outcome":"error","code":"no_match","message":"Nothing matched."}""");

        var succeeded = PluginToolProtocol.TryReadCallResponse(
            document.RootElement,
            out var result,
            out var code,
            out var message);

        Assert.False(succeeded);
        Assert.Null(result);
        Assert.Equal("no_match", code);
        Assert.Equal("Nothing matched.", message);
    }

    [Fact]
    public void AMalformedCallResponseBecomesAReoverableFailure()
    {
        using var document = JsonDocument.Parse("""{"unexpected":true}""");

        var succeeded = PluginToolProtocol.TryReadCallResponse(
            document.RootElement,
            out _,
            out var code,
            out var message);

        Assert.False(succeeded);
        Assert.Equal("tool_failed", code);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void AnOkResponseWithoutAValueIsANullValue()
    {
        using var document = JsonDocument.Parse("""{"outcome":"ok"}""");

        var succeeded = PluginToolProtocol.TryReadCallResponse(
            document.RootElement,
            out var result,
            out _,
            out _);

        Assert.True(succeeded);
        Assert.Null(result);
    }

    [Fact]
    public void TheToolExtensionPointNameIsCanonical()
    {
        Assert.Equal("Tool", ReplExtensionPointName.Tool);
        Assert.True(ReplExtensionPointName.IsKnown("tool"));
        Assert.Equal(ReplExtensionPointName.Tool, ReplExtensionPointName.Canonicalize("TOOL"));
    }

    [Fact]
    public void TheCancelFrameSerializesTheCallIdentity()
    {
        var payload = new HostInvokeCancelPayload("plugin-1", "./main", "call-1");

        var json = JsonSerializer.Serialize(
            new ReplEnvelope(
                1,
                ReplMessageType.HostInvokeCancel,
                "c-1",
                JsonSerializer.SerializeToElement(
                    payload,
                    ReplControlJsonContext.Default.HostInvokeCancelPayload)),
            ReplControlJsonContext.Default.ReplEnvelope);

        Assert.Contains("\"host.invokeCancel\"", json);
        Assert.Contains("\"pluginId\":\"plugin-1\"", json);
        Assert.Contains("\"exportName\":\"./main\"", json);
        Assert.Contains("\"callId\":\"call-1\"", json);
    }

    [Fact]
    public void TheToolBridgeExposesTheDeclaredFunctionShape()
    {
        using var document = JsonDocument.Parse(
            """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""");
        var definition = new PluginToolDefinition(
            "plugin-1",
            "./main",
            "echo",
            "Echo the text.",
            document.RootElement.Clone(),
            TimeSpan.FromSeconds(30),
            PluginToolAnnotations.Default);
        var function = new PluginToolAIFunction(
            definition,
            (_, _, _, _, _, _, _) => Task.FromResult(ExtensionCallOutcome.Result(null)));

        Assert.Equal("echo", function.Name);
        Assert.Equal("Echo the text.", function.Description);
        Assert.Equal(JsonValueKind.Object, function.JsonSchema.ValueKind);
        Assert.Equal("object", function.JsonSchema.GetProperty("type").GetString());
    }

    [Fact]
    public void TheToolBridgeCarriesTheAnnotationsForApprovalSurfaces()
    {
        using var document = JsonDocument.Parse("""{"type":"object"}""");
        var annotations = new PluginToolAnnotations("Echo Text", ReadOnly: true, Destructive: false, Idempotent: true, OpenWorld: false);
        var definition = new PluginToolDefinition(
            "plugin-1",
            "./echo",
            "echo",
            "Echo the text.",
            document.RootElement.Clone(),
            TimeSpan.FromSeconds(30),
            annotations);
        var function = new PluginToolAIFunction(
            definition,
            (_, _, _, _, _, _, _) => Task.FromResult(ExtensionCallOutcome.Result(null)));

        // The hints ride AdditionalProperties, which provider adapters never render: the
        // classification must not appear in the tool definition sent to the model.
        Assert.Same(
            annotations,
            function.AdditionalProperties[PluginToolAIFunction.AnnotationsKey]);
        Assert.Equal("plugin-1", function.AdditionalProperties["pluginId"]);
        Assert.Equal("./echo", function.AdditionalProperties["exportName"]);
        Assert.Equal(JsonValueKind.Object, function.JsonSchema.ValueKind);
    }
}
