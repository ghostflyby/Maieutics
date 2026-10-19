namespace Maieutics.Product.Tests;

/// <summary>Deletes test-owned temp directories, riding out the short-lived Windows file
/// locks that outlive the last SQLite handle (WAL teardown, antivirus and indexer scans).
/// The lock holder is an OS-external condition with no completion signal, so the retry
/// waits with an asynchronous backoff — never a blocking sleep — and a lock that truly
/// persists is still surfaced after the budget instead of being swallowed.</summary>
internal static class TempDirectoryCleanup
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public static async Task DeleteWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + Budget.Ticks / TimeSpan.TicksPerMillisecond;
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
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
