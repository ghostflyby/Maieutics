namespace Maieutics;

/// <summary>Thread-safe lazily-computed asynchronous value. <see cref="Value"/> blocks the calling
/// thread until the factory task completes, which the composition root uses to resolve an
/// asynchronous singleton (the permission broker) inside a synchronous DI registration. The
/// factory runs at most once and the result is cached; a faulted factory keeps faulting.</summary>
internal sealed class AsyncLazy<T> where T : class
{
    private readonly Lazy<Task<T>> factory;

    internal AsyncLazy(Func<Task<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        this.factory = new Lazy<Task<T>>(() => System.Threading.Tasks.Task.Run(factory),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Blocks until the factory completes and returns the value. Safe to call from a
    /// synchronous DI registration because the broker factory binds its listener before returning.</summary>
    internal T Value => factory.Value.GetAwaiter().GetResult();
}
