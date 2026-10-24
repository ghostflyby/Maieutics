using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Maieutics.Agent;
using Maieutics.Execution;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

/// <summary>Shared invocation helpers for the workspace tool tests: they serialize the
/// strict JSON schemas the tools expose and surface expected failures as typed values.</summary>
internal static class WorkspaceToolInvocations
{
    internal static AIFunction Function(WorkspaceFunctions functions, string name)
    {
        return functions.Functions.Single(function => function.Name == name);
    }

    private static AIFunctionArguments Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new AIFunctionArguments(document.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (object?)property.Value.Clone()));
    }

    internal static async ValueTask<ToolInvocation> InvokeAsync(AIFunction function, string json)
    {
        try
        {
            return new ToolInvocation(
                await function.InvokeAsync(Arguments(json), TestContext.Current.CancellationToken),
                null);
        }
        catch (AgentToolException exception)
        {
            return new ToolInvocation(null, exception);
        }
    }

    internal static AgentToolException ShouldFailure(ToolInvocation invocation)
    {
        Assert.NotNull(invocation.Failure);
        return Assert.IsType<AgentToolException>(invocation.Failure);
    }

    internal static T Result<T>(ToolInvocation invocation, JsonTypeInfo<T> jsonTypeInfo)
    {
        Assert.Null(invocation.Failure);
        var result = Assert.IsType<JsonElement>(invocation.Result);
        return result.Deserialize(jsonTypeInfo)
               ?? throw new InvalidOperationException("The tool returned an empty JSON result.");
    }

    internal sealed record ToolInvocation(object? Result, AgentToolException? Failure);
}
