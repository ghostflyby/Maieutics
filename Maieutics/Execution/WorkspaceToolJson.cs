using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Maieutics.Agent;
using Microsoft.Extensions.AI;

namespace Maieutics.Execution;

internal static class WorkspaceToolJson
{
    private const int MaximumSerializedResultBytes = 240 * 1_024;

    internal static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal static AgentToolOutcome Success<T>(T value, JsonTypeInfo<T> jsonTypeInfo)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, jsonTypeInfo);
        return bytes.Length <= MaximumSerializedResultBytes
            ? new AgentToolSuccess(ImmutableArray.Create<AIContent>(
                new DataContent(bytes, "application/json")))
            : new AgentToolFailure(
                "workspace_result_too_large",
                "The bounded workspace result still exceeds the Agent tool-result limit.");
    }

    internal static AgentToolFailure Failure(Exception exception) => exception switch
    {
        WorkspaceToolException workspace => new AgentToolFailure(workspace.Code, workspace.Message),
        JsonException => new AgentToolFailure(
            "workspace_invalid_arguments",
            "The tool arguments do not match the declared schema."),
        UnauthorizedAccessException => new AgentToolFailure(
            "workspace_access_denied",
            "The operating system denied access to the requested workspace path."),
        IOException => new AgentToolFailure(
            "workspace_io_error",
            "The workspace operation could not be completed because of an I/O error."),
        _ => throw new ArgumentOutOfRangeException(nameof(exception), exception, null)
    };
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ListDirectoryArguments))]
[JsonSerializable(typeof(ListDirectoryResult))]
[JsonSerializable(typeof(DirectoryCursor))]
[JsonSerializable(typeof(ReadTextArguments))]
[JsonSerializable(typeof(ReadTextResult))]
[JsonSerializable(typeof(SearchTextArguments))]
[JsonSerializable(typeof(SearchTextResult))]
internal sealed partial class WorkspaceToolJsonSerializerContext : JsonSerializerContext;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ListDirectoryArguments(string? Uri = null, string? Cursor = null, int? PageSize = null);

internal sealed record ListDirectoryResult(
    string Uri,
    ImmutableArray<WorkspaceDirectoryEntry> Entries,
    string? NextCursor);

internal sealed record WorkspaceDirectoryEntry(
    string Name,
    string Uri,
    string Kind,
    long? SizeBytes = null);

internal sealed record DirectoryCursor(string Uri, long WorkspaceVersion, string LastName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReadTextArguments(string? Uri = null, int? StartLine = null, int? MaxLines = null);

internal sealed record ReadTextResult(
    string Uri,
    int? StartLine,
    int? EndLine,
    string Text,
    bool Truncated,
    int? NextStartLine);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SearchTextArguments(
    string? Query = null,
    string? Uri = null,
    bool? Regex = null,
    bool? CaseSensitive = null,
    int? MaxResults = null);

internal sealed record SearchTextResult(
    string Uri,
    ImmutableArray<WorkspaceSearchMatch> Matches,
    bool Truncated,
    long ScannedBytes,
    int SkippedBinaryFiles,
    int SkippedLargeFiles,
    int SkippedSymbolicLinks,
    int SkippedNonRegularFiles);

internal sealed record WorkspaceSearchMatch(
    string Uri,
    int Line,
    int Column,
    string Preview);