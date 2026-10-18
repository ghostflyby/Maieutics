using System.Text;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Frontend;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>
///     Pins the provider-neutral transcript → frontend wire mapping: content kinds,
///     role mapping fallbacks, the reasoning/usage privacy drops, JSON data parsing
///     with its string degrade path, and the absent-value degradation for argument
///     shapes the source-generated object serializer cannot express (NativeAOT).
/// </summary>
public sealed class FrontendTranscriptMapperTests
{
    [Fact]
    public void TextContentMapsToATextPart()
    {
        var message = new ChatMessage(ChatRole.Assistant, [new TextContent("hello")]);

        var part = Assert.Single(FrontendTranscriptMapper.ToMessage(message).Parts);

        Assert.Equal("text", part.Kind);
        Assert.Equal("hello", part.Text);
        Assert.Null(part.Value);
    }

    [Fact]
    public void KnownRolesMapToTheirWireNames()
    {
        (ChatRole Role, string Expected)[] cases =
        [
            (ChatRole.User, "user"),
            (ChatRole.Assistant, "assistant"),
            (ChatRole.Tool, "tool"),
            (ChatRole.System, "system")
        ];

        foreach (var (role, expected) in cases)
        {
            var message = new ChatMessage(role, [new TextContent("hi")]);
            Assert.Equal(expected, FrontendTranscriptMapper.ToMessage(message).Role);
        }
    }

    [Fact]
    public void UnknownRolesFallBackToTheirRawValue()
    {
        var message = new ChatMessage(new ChatRole("developer"), [new TextContent("hi")]);

        Assert.Equal("developer", FrontendTranscriptMapper.ToMessage(message).Role);
    }

    [Fact]
    public void ReasoningAndUsageContentStayOutOfTheWire()
    {
        var message = new ChatMessage(
            ChatRole.Assistant,
            [
                new TextReasoningContent("chain of thought"),
                new TextContent("answer"),
                new UsageContent(new UsageDetails { InputTokenCount = 3 })
            ]);

        var parts = FrontendTranscriptMapper.ToMessage(message).Parts;

        // Never emit private chain-of-thought; usage rides the terminal run frame instead.
        Assert.Equal(["text"], parts.Select(part => part.Kind));
    }

    [Fact]
    public void UnrecognizedContentMapsToAnUnknownPartWithoutAValue()
    {
        var message = new ChatMessage(ChatRole.Assistant, [new HostedFileContent("file-id")]);

        var part = Assert.Single(FrontendTranscriptMapper.ToMessage(message).Parts);

        Assert.Equal("unknown", part.Kind);
        Assert.Null(part.Text);
        Assert.Null(part.Value);
    }

    [Fact]
    public void JsonDataContentParsesIntoAStructuredValue()
    {
        var message = new ChatMessage(
            ChatRole.User,
            [new DataContent(Encoding.UTF8.GetBytes("{\"answer\":42}"), "application/json")]);

        var part = Assert.Single(FrontendTranscriptMapper.ToMessage(message).Parts);

        Assert.Equal("data", part.Kind);
        Assert.NotNull(part.Value);
        Assert.Equal(42, part.Value!.Value.GetProperty("answer").GetInt32());
    }

    [Fact]
    public void NonJsonDataContentDegradesToAStringValue()
    {
        var message = new ChatMessage(
            ChatRole.User,
            [new DataContent("not json"u8.ToArray(), "application/octet-stream")]);

        var part = Assert.Single(FrontendTranscriptMapper.ToMessage(message).Parts);

        Assert.Equal("data", part.Kind);
        Assert.Equal("not json", part.Value!.Value.GetString());
    }

    [Fact]
    public void FunctionCallsCarryCallIdentityAndJsonElementArguments()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["path"] = JsonSerializer.SerializeToElement("README.md"),
            ["depth"] = JsonSerializer.SerializeToElement(2)
        };
        var message = new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call-1", "workspace_read", arguments)]);

        var part = Assert.Single(FrontendTranscriptMapper.ToMessage(message).Parts);

        Assert.Equal("tool_call", part.Kind);
        Assert.Equal("call-1", part.CallId);
        Assert.Equal("workspace_read", part.Name);
        Assert.Equal("README.md", part.Value!.Value.GetProperty("path").GetString());
        Assert.Equal(2, part.Value!.Value.GetProperty("depth").GetInt32());
    }

    [Fact]
    public void FunctionCallsWithoutArgumentsRenderAnEmptyObject()
    {
        var empty = new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call-2", "workspace_list", new Dictionary<string, object?>())]);
        var absent = new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call-3", "workspace_list", null)]);

        foreach (var message in new[] { empty, absent })
        {
            var part = Assert.Single(FrontendTranscriptMapper.ToMessage(message).Parts);
            Assert.Equal(JsonValueKind.Object, part.Value!.Value.ValueKind);
            Assert.Empty(part.Value!.Value.EnumerateObject());
        }
    }

    [Fact]
    public void UnsupportedCallArgumentsDegradeToAnEmptyObject()
    {
        // Unlike the result path, whose degradation drops the value entirely,
        // an unserializable call argument set degrades to an empty object.
        var message = new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(
                "call-4",
                "workspace_read",
                new Dictionary<string, object?> { ["callback"] = (Func<int>)(() => 42) })]);

        var part = Assert.Single(FrontendTranscriptMapper.ToMessage(message).Parts);

        Assert.Equal(JsonValueKind.Object, part.Value!.Value.ValueKind);
        Assert.Empty(part.Value!.Value.EnumerateObject());
    }

    [Fact]
    public void FunctionResultsCarryTheirValueAndNullResultsStayAbsent()
    {
        var result = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-1", JsonSerializer.SerializeToElement((object?)new { status = "ok" }))]);
        var empty = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-2", null)]);

        var resultPart = Assert.Single(FrontendTranscriptMapper.ToMessage(result).Parts);
        Assert.Equal("tool_result", resultPart.Kind);
        Assert.Equal("call-1", resultPart.CallId);
        Assert.Equal("ok", resultPart.Value!.Value.GetProperty("status").GetString());

        var emptyPart = Assert.Single(FrontendTranscriptMapper.ToMessage(empty).Parts);
        Assert.Null(emptyPart.Value);
    }

    [Fact]
    public void UnsupportedResultValuesDegradeToAnAbsentValue()
    {
        // Delegates have no JSON representation on the source-generated object path;
        // the render must degrade instead of failing the transcript.
        var message = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-3", (Func<int>)(() => 42))]);

        var part = Assert.Single(FrontendTranscriptMapper.ToMessage(message).Parts);

        Assert.Equal("tool_result", part.Kind);
        Assert.Null(part.Value);
    }

    [Fact]
    public void ProgressTextContentCarriesItsText()
    {
        var content = FrontendTranscriptMapper.ToProgressContent(new TextContent("working"));

        Assert.Equal("text", content.Kind);
        Assert.Equal("working", content.Text);
        Assert.Null(content.Value);
    }

    [Fact]
    public void ProgressDataContentCarriesStructuredValuesAndDegradesToStrings()
    {
        var structured = FrontendTranscriptMapper.ToProgressContent(
            new DataContent(Encoding.UTF8.GetBytes("[1,2]"), "application/json"));
        var degraded = FrontendTranscriptMapper.ToProgressContent(
            new DataContent("nan"u8.ToArray(), "application/json"));
        var unknown = FrontendTranscriptMapper.ToProgressContent(
            new TextReasoningContent("private"));

        Assert.Equal("json", structured.Kind);
        Assert.Equal(2, structured.Value!.Value.GetArrayLength());
        Assert.Equal("json", degraded.Kind);
        Assert.Equal("nan", degraded.Value!.Value.GetString());
        Assert.Equal("unknown", unknown.Kind);
        Assert.Null(unknown.Text);
        Assert.Null(unknown.Value);
    }

    [Fact]
    public void TranscriptTurnsMapOntoTheWireWithIdsAndModelIdentity()
    {
        var sessionId = AgentSessionId.Create();
        var runId = AgentRunId.Create();
        var transcript = new AgentTranscript(
            sessionId,
            version: 7,
            [
                new AgentTranscriptTurn(
                    runId,
                    [
                        new ChatMessage(ChatRole.User, [new TextContent("hi")]),
                        new ChatMessage(ChatRole.Assistant, [new TextContent("hello")])
                    ],
                    new AgentModelIdentity(new AgentModelProfileId("fast"), "OpenAI", "test-model"),
                    truncated: true),
                new AgentTranscriptTurn(
                    AgentRunId.Create(),
                    [
                        new ChatMessage(ChatRole.User, [new TextContent("again")]),
                        new ChatMessage(ChatRole.Assistant, [new TextContent("sure")])
                    ])
            ]);

        var wire = FrontendTranscriptMapper.ToTranscript(transcript);

        Assert.Equal(sessionId.Value.ToString("N"), wire.SessionId);
        Assert.Equal(7, wire.Version);
        Assert.Equal(2, wire.Turns.Count());
        Assert.Equal(runId.Value.ToString("N"), wire.Turns[0].RunId);
        Assert.True(wire.Turns[0].Truncated);
        Assert.NotNull(wire.Turns[0].Model);
        Assert.Equal("fast", wire.Turns[0].Model!.ProfileId);
        Assert.Equal("OpenAI", wire.Turns[0].Model!.Provider);
        Assert.Equal("test-model", wire.Turns[0].Model!.Model);
        Assert.Equal(["user", "assistant"], wire.Turns[0].Messages.Select(message => message.Role));
        // A turn without an identity (legacy row) carries no model block.
        Assert.Null(wire.Turns[1].Model);
        Assert.False(wire.Turns[1].Truncated);
    }
}
