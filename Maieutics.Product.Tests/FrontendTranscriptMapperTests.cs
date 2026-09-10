using System.Text;
using System.Text.Json;
using FluentAssertions;
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

        var part = FrontendTranscriptMapper.ToMessage(message).Parts.Should().ContainSingle().Which;

        part.Kind.Should().Be("text");
        part.Text.Should().Be("hello");
        part.Value.Should().BeNull();
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
            FrontendTranscriptMapper.ToMessage(message).Role.Should().Be(expected);
        }
    }

    [Fact]
    public void UnknownRolesFallBackToTheirRawValue()
    {
        var message = new ChatMessage(new ChatRole("developer"), [new TextContent("hi")]);

        FrontendTranscriptMapper.ToMessage(message).Role.Should().Be("developer");
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
        parts.Select(part => part.Kind).Should().Equal(["text"]);
    }

    [Fact]
    public void UnrecognizedContentMapsToAnUnknownPartWithoutAValue()
    {
        var message = new ChatMessage(ChatRole.Assistant, [new HostedFileContent("file-id")]);

        var part = FrontendTranscriptMapper.ToMessage(message).Parts.Should().ContainSingle().Which;

        part.Kind.Should().Be("unknown");
        part.Text.Should().BeNull();
        part.Value.Should().BeNull();
    }

    [Fact]
    public void JsonDataContentParsesIntoAStructuredValue()
    {
        var message = new ChatMessage(
            ChatRole.User,
            [new DataContent(Encoding.UTF8.GetBytes("{\"answer\":42}"), "application/json")]);

        var part = FrontendTranscriptMapper.ToMessage(message).Parts.Should().ContainSingle().Which;

        part.Kind.Should().Be("data");
        part.Value.Should().NotBeNull();
        part.Value!.Value.GetProperty("answer").GetInt32().Should().Be(42);
    }

    [Fact]
    public void NonJsonDataContentDegradesToAStringValue()
    {
        var message = new ChatMessage(
            ChatRole.User,
            [new DataContent("not json"u8.ToArray(), "application/octet-stream")]);

        var part = FrontendTranscriptMapper.ToMessage(message).Parts.Should().ContainSingle().Which;

        part.Kind.Should().Be("data");
        part.Value!.Value.GetString().Should().Be("not json");
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

        var part = FrontendTranscriptMapper.ToMessage(message).Parts.Should().ContainSingle().Which;

        part.Kind.Should().Be("tool_call");
        part.CallId.Should().Be("call-1");
        part.Name.Should().Be("workspace_read");
        part.Value!.Value.GetProperty("path").GetString().Should().Be("README.md");
        part.Value!.Value.GetProperty("depth").GetInt32().Should().Be(2);
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
            var part = FrontendTranscriptMapper.ToMessage(message).Parts.Should().ContainSingle().Which;
            part.Value!.Value.ValueKind.Should().Be(JsonValueKind.Object);
            part.Value!.Value.EnumerateObject().Should().BeEmpty();
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

        var part = FrontendTranscriptMapper.ToMessage(message).Parts.Should().ContainSingle().Which;

        part.Value!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        part.Value!.Value.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void FunctionResultsCarryTheirValueAndNullResultsStayAbsent()
    {
        var result = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-1", JsonSerializer.SerializeToElement((object?)new { status = "ok" }))]);
        var empty = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-2", null)]);

        var resultPart = FrontendTranscriptMapper.ToMessage(result).Parts.Should().ContainSingle().Which;
        resultPart.Kind.Should().Be("tool_result");
        resultPart.CallId.Should().Be("call-1");
        resultPart.Value!.Value.GetProperty("status").GetString().Should().Be("ok");

        var emptyPart = FrontendTranscriptMapper.ToMessage(empty).Parts.Should().ContainSingle().Which;
        emptyPart.Value.Should().BeNull();
    }

    [Fact]
    public void UnsupportedResultValuesDegradeToAnAbsentValue()
    {
        // Delegates have no JSON representation on the source-generated object path;
        // the render must degrade instead of failing the transcript.
        var message = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-3", (Func<int>)(() => 42))]);

        var part = FrontendTranscriptMapper.ToMessage(message).Parts.Should().ContainSingle().Which;

        part.Kind.Should().Be("tool_result");
        part.Value.Should().BeNull();
    }

    [Fact]
    public void ProgressTextContentCarriesItsText()
    {
        var content = FrontendTranscriptMapper.ToProgressContent(new TextContent("working"));

        content.Kind.Should().Be("text");
        content.Text.Should().Be("working");
        content.Value.Should().BeNull();
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

        structured.Kind.Should().Be("json");
        structured.Value!.Value.GetArrayLength().Should().Be(2);
        degraded.Kind.Should().Be("json");
        degraded.Value!.Value.GetString().Should().Be("nan");
        unknown.Kind.Should().Be("unknown");
        unknown.Text.Should().BeNull();
        unknown.Value.Should().BeNull();
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

        wire.SessionId.Should().Be(sessionId.Value.ToString("N"));
        wire.Version.Should().Be(7);
        wire.Turns.Should().HaveCount(2);
        wire.Turns[0].RunId.Should().Be(runId.Value.ToString("N"));
        wire.Turns[0].Truncated.Should().BeTrue();
        wire.Turns[0].Model.Should().NotBeNull();
        wire.Turns[0].Model!.ProfileId.Should().Be("fast");
        wire.Turns[0].Model!.Provider.Should().Be("OpenAI");
        wire.Turns[0].Model!.Model.Should().Be("test-model");
        wire.Turns[0].Messages.Select(message => message.Role)
            .Should().Equal(["user", "assistant"]);
        // A turn without an identity (legacy row) carries no model block.
        wire.Turns[1].Model.Should().BeNull();
        wire.Turns[1].Truncated.Should().BeFalse();
    }
}
