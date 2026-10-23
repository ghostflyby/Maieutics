namespace Maieutics.Testing;

/// <summary>One owner for throwaway temp areas: creates the directory under the suite's
/// <c>maieutics-&lt;purpose&gt;-&lt;guid&gt;</c> naming convention and deletes it on disposal
/// with reparse-point-safe recursion and a bounded retry that rides out short-lived OS-side
/// file locks (test principle 8: every resource has exactly one owner).</summary>
public sealed class TempArea : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TeardownBudget = Budget + TimeSpan.FromSeconds(5);

    private TempArea(string path)
    {
        Path = path;
    }

    /// <summary>The area's root path; everything below it is owned by this instance.</summary>
    public string Path { get; }

    /// <summary>Creates a fresh temp directory for the given purpose.</summary>
    public static TempArea Create(string purpose)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"maieutics-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new TempArea(path);
    }

    /// <summary>Deletes the area with the retrying, cause-naming teardown budget. The
    /// budget is independent of any test token: after an xUnit timeout that token is
    /// already canceled, and passing it would abort the cleanup and leak the tree.</summary>
    public Task DeleteAsync()
    {
        return DeleteAsync(Path);
    }

    /// <summary>Deletes the area, retrying within the caller's stage budget.</summary>
    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        return DeleteAsync(Path, cancellationToken);
    }

    /// <summary>Sync disposal: one reparse-safe delete attempt, no retry loop. Prefer
    /// <see cref="DisposeAsync" />, whose retries ride out short-lived OS locks.</summary>
    public void Dispose()
    {
        DeleteTree(Path);
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(DeleteAsync(Path));
    }

    /// <summary>Deletes a test-owned temp directory, riding out the short-lived Windows
    /// file locks that outlive the last SQLite handle (WAL teardown, antivirus and indexer
    /// scans). The lock holder is an OS-external condition with no completion signal, so
    /// the retry waits with an asynchronous backoff — never a blocking sleep — and a lock
    /// that truly persists is still surfaced after the budget, naming the last observed
    /// cause.</summary>
    public static async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)Budget.TotalMilliseconds;
        Exception? lastFailure = null;
        while (true)
        {
            try
            {
                DeleteTree(path);
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
    /// token.</summary>
    public static async Task DeleteAsync(string path)
    {
        // The source must outlive the whole retry loop, so await inside the using scope
        // rather than returning the inner task.
        using var cleanup = new CancellationTokenSource(TeardownBudget);
        await DeleteAsync(path, cleanup.Token).ConfigureAwait(false);
    }

    /// <summary>Recursive deletion that never descends into reparse points. .NET's
    /// recursive delete throws ("the parameter is incorrect") the moment it meets a
    /// junction on Windows — it does not follow it — so managed links are removed
    /// non-recursively first and the remaining tree is deleted afterwards.</summary>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var entry in Directory.EnumerateDirectories(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(entry, false);
            else
                DeleteTree(entry);
        }

        Directory.Delete(path, true);
    }
}
