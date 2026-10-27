using System.Text.Json;

namespace Maieutics.Product.Tests;

/// <summary>Reads a frontend discovery file once it is published.
///
/// The producer publishes atomically (owner-only temp file, then rename), so the file's
/// appearance is a completion signal: a <c>FileSystemWatcher</c> on the parent directory
/// awaits it, and the existence wait has no timer behind it. Publication is not a
/// guarantee that this process can open the file yet — on Windows a freshly renamed file
/// can be briefly held by a scanner, and the failure surfaces as <see cref="IOException"/>
/// at open time. That lock is a condition outside the process with no completion signal
/// to await, so the open retries with an asynchronous backoff under its own budget — the
/// one irreducible retry; a file that never becomes readable still surfaces, naming the
/// last observed cause instead of a bare cancellation. Caller cancellation still
/// propagates untouched.</summary>
internal static class FrontendDiscoveryFile
{
    /// <summary>The backoff between open attempts while an external lock holds the
    /// published file. The lock's release has no waitable signal, so this is the one
    /// irreducible retry in the suite.</summary>
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>Awaits a readable, parseable discovery document within
    /// <paramref name="budget"/>. <paramref name="fault"/>, when supplied, is consulted on
    /// each open attempt and its non-null result is thrown instead of waiting out the
    /// budget (the smoke harness uses it to report a dead child immediately).</summary>
    internal static async Task<JsonElement> ReadAsync(
        string path,
        TimeSpan budget,
        CancellationToken cancellationToken,
        Func<string?>? fault = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        Exception? lastFailure = null;
        try
        {
            var directory = Path.GetDirectoryName(path) ?? ".";
            using var watcher = new FileSystemWatcher(directory, Path.GetFileName(path));
            watcher.EnableRaisingEvents = true;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
            // Pre-armed before the first open attempt, so a publication landing between a
            // failed open and the wait below is folded in instead of lost.
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Created += (_, _) => published.TrySetResult();
            watcher.Renamed += (_, _) => published.TrySetResult();
            watcher.Changed += (_, _) => published.TrySetResult();
            var awaitingPublication = !File.Exists(path);

            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (fault?.Invoke() is { } reported) throw new InvalidOperationException(reported);

                try
                {
                    // FileShare.ReadWrite: a concurrent reader (or a scanner that opens
                    // shared) must not turn this probe into a lock conflict.
                    await using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var document = await JsonDocument.ParseAsync(
                        stream,
                        cancellationToken: deadline.Token).ConfigureAwait(false);
                    return document.RootElement.Clone();
                }
                catch (Exception exception) when (
                    exception is FileNotFoundException or DirectoryNotFoundException or
                        IOException or UnauthorizedAccessException or JsonException)
                {
                    lastFailure = exception;
                }

                if (awaitingPublication)
                {
                    // The producer has not published yet: the appearance is the event, and
                    // the pre-armed source above carries any signal that raced the open.
                    await published.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                    published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    awaitingPublication = false;
                }
                else
                {
                    // Published but not openable: an external lock (a Windows scanner)
                    // holds it. Its release has no waitable signal, so the open retries
                    // with an asynchronous backoff — the one irreducible retry.
                    await Task.Delay(LockRetryDelay, deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"The frontend discovery file '{path}' was not readable within {budget.TotalSeconds:0.#}s.",
                lastFailure);
        }
    }
}
