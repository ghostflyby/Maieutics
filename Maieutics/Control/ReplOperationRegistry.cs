using System.Collections.Concurrent;

namespace Maieutics.Control;

/// <summary>
///     Tracks in-flight control channel operations by correlation id so they can be cancelled
///     through a <c>control.cancel</c> message or session teardown.
/// </summary>
internal sealed class ReplOperationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _operations = new(StringComparer.Ordinal);

    public bool TryRegister(string correlationId, CancellationTokenSource operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(operation);
        return _operations.TryAdd(correlationId, operation);
    }

    public bool TryCancel(string correlationId)
    {
        if (!_operations.TryGetValue(correlationId, out var operation)) return false;

        operation.Cancel();
        return true;
    }

    public void Remove(string correlationId)
    {
        _operations.TryRemove(correlationId, out _);
    }
}
