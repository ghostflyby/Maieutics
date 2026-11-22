using System.Text.Json;
using System.Threading.Channels;

namespace Maieutics.Product.Tests;

/// <summary>Reads a frontend discovery file once it is published.
///
/// The producer publishes atomically (owner-only temp file, then rename), so the file's
/// appearance is a completion signal: a <c>FileSystemWatcher</c> on the parent directory
/// awaits it, and the existence wait has no timer behind it. An overflow means the
/// publication event may be lost, so it triggers a fresh probe; a permanently failed
/// watcher fails the wait with its original cause. Transient open/parse failures await
/// another notification under the same budget; the reader never polls for lock release.
/// A file that never becomes readable surfaces its last observed cause instead of bare
/// cancellation. Caller cancellation still propagates untouched.</summary>
internal static class FrontendDiscoveryFile
{
    /// <summary>Awaits a readable, parseable discovery document within
    /// <paramref name="budget"/>. <paramref name="fault"/>, when supplied, is consulted on
    /// each open attempt and its non-null result is thrown instead of waiting out the
    /// budget. The reader owns <paramref name="publicationWatcher"/> when supplied;
    /// tests use it to control notification loss without relying on filesystem timing.</summary>
    internal static async Task<JsonElement> ReadAsync(
        string path,
        TimeSpan budget,
        CancellationToken cancellationToken,
        Func<string?>? fault = null,
        FileSystemWatcher? publicationWatcher = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        Exception? lastFailure = null;
        try
        {
            var directory = Path.GetDirectoryName(path) ?? ".";
            using var watcher = publicationWatcher ?? new FileSystemWatcher(directory, Path.GetFileName(path));
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
            // Notifications request a probe of one authoritative file; repeated requests
            // coalesce. Overflow means a publication may have been lost, so probe again.
            var published = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropWrite,
                AllowSynchronousContinuations = false
            });
            var watcherFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Created += (_, _) => published.Writer.TryWrite(true);
            watcher.Renamed += (_, _) => published.Writer.TryWrite(true);
            watcher.Changed += (_, _) => published.Writer.TryWrite(true);
            watcher.Error += (_, args) =>
            {
                if (args.GetException() is not InternalBufferOverflowException)
                    watcherFailure.TrySetResult(args.GetException());
                published.Writer.TryWrite(true);
            };
            watcher.EnableRaisingEvents = true;

            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (watcherFailure.Task.IsCompletedSuccessfully)
                    throw new IOException($"Watching frontend discovery file '{path}' failed.",
                        await watcherFailure.Task.ConfigureAwait(false));
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

                await published.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
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
