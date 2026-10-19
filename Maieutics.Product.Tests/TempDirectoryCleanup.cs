namespace Maieutics.Product.Tests;

/// <summary>Deletes test-owned temp directories, riding out the short-lived Windows file
/// locks that outlive the last SQLite handle (WAL teardown, antivirus and indexer scans).
/// The lock holder is an OS-external condition with no completion signal, so the retry
/// waits with an asynchronous backoff — never a blocking sleep — and a lock that truly
/// persists is still surfaced after the budget, naming the last observed cause.</summary>
internal static class TempDirectoryCleanup
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public static async Task DeleteWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)Budget.TotalMilliseconds;
        Exception? lastFailure = null;
        while (true)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException &&
                Environment.TickCount64 < deadline)
            {
                lastFailure = exception;
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // The budget is spent while the tree is still locked. Report the actual
                // cause: a bare cancellation would hide whether it was a sharing violation
                // or a permission problem.
                throw new TimeoutException(
                    $"The temporary directory '{path}' stayed locked for {Budget.TotalSeconds:0.#}s.",
                    lastFailure ?? exception);
            }
        }
    }

    /// <summary>Teardown form: the retry budget is independent of the test's cancellation
    /// token. After an xUnit timeout that token is already canceled; passing it here would
    /// abort the cleanup and leak the tree, so teardown always gets its own bounded budget
    /// instead of the token of the test that just failed.</summary>
    public static async Task DeleteWithRetryAsync(string path)
    {
        // The source must outlive the whole retry loop, so await inside the using scope
        // rather than returning the inner task.
        using var cleanup = new CancellationTokenSource(Budget + TimeSpan.FromSeconds(5));
        await DeleteWithRetryAsync(path, cleanup.Token).ConfigureAwait(false);
    }
}
