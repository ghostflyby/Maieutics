using Maieutics.Agent;

namespace Maieutics.Providers;

/// <summary>
///     Optional interface that an <see cref="IConfiguredChatClientSource" /> may implement
///     to provide a list of models available from the provider API endpoint.
/// </summary>
internal interface IModelDiscoverySource
{
    /// <summary>Returns the list of models available from the provider API.</summary>
    /// <param name="cancellationToken">A cancellation token for the request.</param>
    /// <returns>The list of discovered model descriptors.</returns>
    ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
        CancellationToken cancellationToken = default);
}