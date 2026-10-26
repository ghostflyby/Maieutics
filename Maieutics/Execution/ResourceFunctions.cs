using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;
using Microsoft.Extensions.AI;

namespace Maieutics.Execution;

/// <summary>Model-callable tools over the virtual resource plane (ADR 0026). The
/// read entry stays <c>read_text</c> in <see cref="WorkspaceFunctions"/>; this class
/// adds discovery. Registered as built-in tools and as script tools, so Deno
/// children and plugins can enumerate the same catalog the model sees.</summary>
internal sealed class ResourceFunctions
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(ResourceJsonSerializerContext.Default.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    private readonly ResourceRegistry _registry;

    internal ResourceFunctions(ResourceRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        Functions =
        [
            AIFunctionFactory.Create(
                (Func<CancellationToken, ValueTask<ResourceCatalogResult>>)ListResourcesAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "list_resources",
                    Description =
                        "Lists virtual resources and resource templates exposed by registered " +
                        "providers, such as MCP servers. File reads use list_directory instead.",
                    SerializerOptions = SerializerOptions
                })
        ];
    }

    internal IReadOnlyList<AIFunction> Functions { get; }

    [Description("Lists virtual resources and resource templates from registered providers.")]
    private async ValueTask<ResourceCatalogResult> ListResourcesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var resources = ImmutableArray.CreateBuilder<ResourceCatalogEntry>();
            foreach (var provider in _registry.Providers)
                if (provider is IResourceCatalogProvider catalogProvider)
                    resources.AddRange(await catalogProvider.ListAsync(cancellationToken).ConfigureAwait(false));

            return new ResourceCatalogResult(resources.ToImmutable(), _registry.GetConflicts());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ResourceException exception)
        {
            throw new AgentToolException(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentToolException("resource_provider_failed", exception.Message);
        }
    }
}
