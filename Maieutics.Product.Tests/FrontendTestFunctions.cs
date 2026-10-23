using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Maieutics.Product.Tests;

internal static class FrontendTestFunctions
{
    internal static AIFunction CreateEchoFunction()
    {
        return AIFunctionFactory.Create(
            (string text) => JsonSerializer.SerializeToElement(
                text,
                HostIntegrationJsonContext.Default.String),
            new AIFunctionFactoryOptions
            {
                Name = "echo",
                Description = "Returns the supplied text.",
                SerializerOptions = HostIntegrationJsonContext.Default.Options
            });
    }
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class HostIntegrationJsonContext : JsonSerializerContext;
