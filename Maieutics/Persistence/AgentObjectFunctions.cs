using System.Text.Json;
using Maieutics.Agent;
using Microsoft.Extensions.AI;

namespace Maieutics.Persistence;

/// <summary>
///     The model-visible read path for stored objects: <c>object_fetch</c> returns a stored
///     JSON object as the structured tool result, so an oversized tool output can still be
///     consulted after its truncated preview entered the transcript. Enormous objects flow back
///     through the regular envelope path, which re-truncates to the same content address.
/// </summary>
internal sealed class AgentObjectFunctions(IAgentObjectStore store)
{
    public IReadOnlyList<AIFunction> Functions { get; } = [CreateFetchFunction(store)];

    private static AIFunction CreateFetchFunction(IAgentObjectStore store)
    {
        return AIFunctionFactory.Create(
            async (string sha256, CancellationToken cancellationToken) =>
                await FetchAsync(store, sha256, cancellationToken).ConfigureAwait(false),
            new AIFunctionFactoryOptions
            {
                Name = "object_fetch",
                Description =
                    "Returns a stored JSON object as structured data. Use this to read the full " +
                    "content of a tool result that was truncated (see its object.sha256 reference).",
            });
    }

    internal static async Task<JsonElement?> FetchAsync(
        IAgentObjectStore store,
        string sha256,
        CancellationToken cancellationToken)
    {
        ObjectStore.ValidateId(sha256);
        byte[] bytes;
        try
        {
            using var stream = store.Open(sha256);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }
        catch (FileNotFoundException exception)
        {
            throw new AgentToolException("object_not_found", exception.Message);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AgentToolException(
                "object_not_json",
                $"Object '{sha256}' is not valid JSON text and cannot be returned as a tool result.");
        }
    }
}
