namespace Maieutics.Product.Tests;

/// <summary>
///     Non-parallel collection for product tests that share process-wide state:
///     sockets bound to reserved ports, per-process environment variables, the
///     polling file-watcher flag, and in-proc composition-root hosts. Membership
///     is required for any test that mutates process-wide state.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProductIntegrationCollection
{
    public const string Name = "Product integration";
}
