namespace Maieutics.Processes;

/// <summary>Publishes the result of a synchronous start separately from the lifetime
/// of its launch thread. Linux delivers bubblewrap's parent-death signal when the thread
/// that forked it exits, so the process owner releases that thread after the child exits.</summary>
internal sealed class ProcessLaunchThread<T>(Task<T> ready, Task worker, TaskCompletionSource? parentLifetime)
{
    internal Task<T> Ready { get; } = ready;

    internal static ProcessLaunchThread<T> Start(
        Func<T> start, bool keepParentAlive, CancellationToken cancellationToken)
    {
        var ready = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentLifetime = keepParentAlive
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        var worker = Task.Factory.StartNew(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ready.SetResult(start());
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancellationToken &&
                                                               cancellationToken.IsCancellationRequested)
            {
                ready.SetCanceled(cancellationToken);
                return;
            }
            catch (Exception exception)
            {
                ready.SetException(exception);
                return;
            }

            parentLifetime?.Task.GetAwaiter().GetResult();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return new ProcessLaunchThread<T>(ready.Task, worker, parentLifetime);
    }

    internal void ReleaseParent() => parentLifetime?.TrySetResult();

    internal Task JoinAsync()
    {
        ReleaseParent();
        return worker;
    }
}
