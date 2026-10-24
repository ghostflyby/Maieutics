using System.Text.Json;

namespace Maieutics.Product.Tests;

/// <summary>Reads a frontend discovery file once it is published.
///
/// The producer publishes atomically (owner-only temp file, then rename), so "the file
/// exists" is a valid readiness signal — but it is not a guarantee that this process can
/// open it yet. On Windows a freshly renamed file can be briefly held by a scanner, and the
/// failure surfaces as <see cref="IOException"/> at open time rather than as a missing
/// file. That lock is a condition outside the process with no completion signal to await,
/// so the read retries with an asynchronous backoff under its own budget; a file that never
/// becomes readable still surfaces, naming the last observed cause instead of a bare
/// cancellation. Caller cancellation still propagates untouched.</summary>
internal static class FrontendDiscoveryFile
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>Awaits a readable, parseable discovery document within
    /// <paramref name="budget"/>. <paramref name="fault"/>, when supplied, is consulted on
    /// each retry and its non-null result is thrown instead of waiting out the budget (the
    /// smoke harness uses it to report a dead child immediately).</summary>
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

                await Task.Delay(RetryDelay, deadline.Token).ConfigureAwait(false);
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
