using System.ClientModel.Primitives;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Providers.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace Maieutics.Product.Tests;

#pragma warning disable OPENAI001 // The OpenAI .NET Responses surface is currently marked experimental.

/// <summary>Covers the provider-neutral bridging the Responses apply_patch adapter performs: the
/// SDK supplies the structured operation, the runtime needs a function call, and the original
/// structured form has to survive a transcript round-trip for replay.</summary>
public sealed class ResponsesApplyPatchAdapterTests
{
    private const string CreateFileOperation =
        """{"type":"create_file","path":"docs/new.txt","diff":"+one\n+two\n"}""";

    [Theory]
    [InlineData("create_file", "docs/a.txt", "+hello\n", "*** Add File: docs/a.txt\n+hello\n")]
    [InlineData("update_file", "src/b.cs", "@@ ctx\n-old\n+new\n", "*** Update File: src/b.cs\n@@ ctx\n-old\n+new\n")]
    [InlineData("delete_file", "obsolete.txt", null, "*** Delete File: obsolete.txt\n")]
    public void RenderProducesTheV4ADocumentForEachOperation(
        string kind,
        string path,
        string? diff,
        string expectedBody)
    {
        var json = diff is null
            ? $$"""{"type":"{{kind}}","path":"{{path}}"}"""
            : $$"""{"type":"{{kind}}","path":"{{path}}","diff":{{JsonSerializer.Serialize(diff)}}}""";
        var operation = Read(json);

        var patch = ApplyPatchText.Render(operation);

        patch.Should().Be($"*** Begin Patch\n{expectedBody}*** End Patch\n");
    }

    [Fact]
    public void RenderCarriesAMoveTargetWhenTheOperationHasOne()
    {
        var operation = Read(
            """{"type":"update_file","path":"a.txt","move_to":"b.txt","diff":"@@ x\n-a\n+b\n"}""");

        ApplyPatchText.Render(operation).Should().Be(
            "*** Begin Patch\n*** Update File: a.txt\n*** Move to: b.txt\n@@ x\n-a\n+b\n*** End Patch\n");
    }

    [Fact]
    public void RenderedPatchIsAcceptedByTheRuntimeParser()
    {
        // The adapter renders text the runtime's own parser must accept; a mismatch between the
        // two would only surface at runtime, so pin it here.
        var operation = Read(
            $$"""{"type":"create_file","path":"docs/new.txt","diff":"+one\n+two\n"}""");
        var patch = ApplyPatchText.Render(operation);

        var document = Maieutics.Execution.ApplyPatchParser.Parse(patch);

        document.Files.Should().ContainSingle();
        document.Files[0].Kind.Should().Be("create_file");
        document.Files[0].Path.Should().Be("docs/new.txt");
        document.Files[0].Diff.Should().Be("one\ntwo\n");
    }

    [Fact]
    public void TheStructuredOperationSurvivesTheTranscriptJsonRoundTrip()
    {
        // Replay depends on this: the operation is preserved as JSON on the function call, and a
        // serialized SDK model would lose the extensible diff during the transcript's own
        // serialization. This pins the property that makes the JSON form necessary.
        var call = new FunctionCallContent("call_1", ApplyPatchFunctions.ToolName,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["patch"] = "*** Begin Patch\n*** Add File: docs/new.txt\n+one\n*** End Patch\n"
            });
        call.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [ResponsesApplyPatchChatClient.OperationProperty] =
                JsonDocument.Parse(CreateFileOperation).RootElement.Clone()
        };

        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        var json = JsonSerializer.Serialize(new[] { new ChatMessage(ChatRole.Assistant, [call]) }, options);
        var restored = JsonSerializer.Deserialize<ChatMessage[]>(json, options)!;
        var restoredCall = restored[0].Contents.OfType<FunctionCallContent>().Single();

        restoredCall.Name.Should().Be(ApplyPatchFunctions.ToolName);
        restoredCall.AdditionalProperties.Should().ContainKey(ResponsesApplyPatchChatClient.OperationProperty);
        var operationJson = (JsonElement)restoredCall.AdditionalProperties![
            ResponsesApplyPatchChatClient.OperationProperty]!;
        operationJson.GetProperty("type").GetString().Should().Be("create_file");
        operationJson.GetProperty("diff").GetString().Should().Be("+one\n+two\n");

        // And it still rebuilds the SDK operation, which is what the outbound rewrite needs.
        var rebuilt = Read(operationJson.GetRawText());
        rebuilt.Should().BeOfType<ApplyPatchCreateFileOperation>();
    }

    [Fact]
    public void TheVisibilitySetsNeverHideTheRuntimeFunctionOnResponses()
    {
        // The Responses adapter needs the local apply_patch function declared so the runtime
        // executes the projected call; only the general edit tools are hidden there.
        ApplyPatchFunctions.ResponsesHiddenToolNames.Should().Equal("write_text", "edit_text");
        ApplyPatchFunctions.ResponsesHiddenToolNames.Should().NotContain(ApplyPatchFunctions.ToolName);
        ApplyPatchFunctions.NonResponsesHiddenToolNames.Should().Equal(ApplyPatchFunctions.ToolName);
    }

    private static ApplyPatchOperation Read(string json)
    {
        return ModelReaderWriter.Read<ApplyPatchOperation>(
                   BinaryData.FromString(json),
                   new ModelReaderWriterOptions("J"),
                   OpenAIContext.Default)
               ?? throw new InvalidOperationException("The operation JSON did not deserialize.");
    }
}
#pragma warning restore OPENAI001
